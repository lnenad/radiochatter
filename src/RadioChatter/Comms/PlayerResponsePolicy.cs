using System;

namespace RadioChatter.Comms
{
    /// <summary>A synthesized line the player pilot speaks in reply to a finished
    /// transmission: a tower readback or a generic acknowledgement.</summary>
    internal struct PlayerResponse
    {
        public RadioRole Role;
        public string Text;
    }

    /// <summary>Phraseology policy for automatic player replies: which transmissions get a
    /// response at all, what a tower clearance readback says, and which generic
    /// acknowledgement to pick. Pure comms logic — lives next to TowerReadbackMatcher so the
    /// two clearance grammars are maintained together (TowerReadbackFor builds the spoken
    /// readback; TowerReadbackMatcher validates the player's own spoken attempt).</summary>
    internal sealed class PlayerResponsePolicy
    {
        private static readonly string[] PlayerAcknowledgements =
        {
            "roger that",
            "copy",
            "copy that",
            "wilco",
            "understood",
            "affirmative"
        };

        /// <summary>Clearance phrases a readback repeats from the start of the phrase to the
        /// end of the tower line. Keep in sync with TowerReadbackMatcher's instruction sets.</summary>
        private static readonly string[] ClearancePhrases =
        {
            "cleared for takeoff",
            "cleared for launch",
            "cleared to land",
            "cleared to recover",
            "cleared for recovery"
        };

        private readonly Random _random = new Random();
        private int _lastAcknowledgementIndex = -1;

        /// <summary>The player's reply for a finished transmission; default (empty Text) when
        /// no automatic reply is appropriate. Caller gates on its own config (player
        /// acknowledgements enabled).</summary>
        public PlayerResponse ResponseFor(RadioRole role, RadioEventType type, string text, bool spokenReadbacksEnabled)
        {
            // Broadcast traffic and questions require either no answer or an actual player
            // transmission. A synthetic generic acknowledgement is inappropriate for both.
            if (SuppressesAutomaticAcknowledgements(type, text))
                return default;

            if (RadioRoles.IsPlayerRole(role) || role == RadioRole.System)
                return default;

            text = text ?? string.Empty;
            if (text.IndexOf("missile", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("defend", StringComparison.OrdinalIgnoreCase) >= 0 ||
                IsPlayerDownCall(text))
            {
                return default;
            }

            if (role == RadioRole.Tower)
            {
                string towerReadback = TowerReadbackFor(text);
                if (!string.IsNullOrEmpty(towerReadback))
                {
                    TowerReadbackExpectation ignored;
                    if (spokenReadbacksEnabled && TowerReadbackMatcher.TryCreate(text, out ignored))
                        return default;

                    return new PlayerResponse
                    {
                        Role = RadioRole.PlayerTower,
                        Text = towerReadback
                    };
                }

                return default;
            }

            RadioRole responseRole = role == RadioRole.Awacs ? RadioRole.PlayerAwacs : RadioRole.PlayerFlight;
            return new PlayerResponse
            {
                Role = responseRole,
                Text = PickAcknowledgement()
            };
        }

        public static bool SuppressesAutomaticAcknowledgements(RadioEventType type, string text)
        {
            return type == RadioEventType.BattlefieldChatter ||
                   CancelsOutstandingAutomaticAcknowledgements(type, text);
        }

        public static bool CancelsOutstandingAutomaticAcknowledgements(RadioEventType type, string text)
        {
            if (type == RadioEventType.GroundSupportHail ||
                type == RadioEventType.GroundSupportAcknowledged ||
                type == RadioEventType.GroundSupportVector ||
                type == RadioEventType.GroundSupportDeclined ||
                type == RadioEventType.GroundSupportCompleted ||
                type == RadioEventType.GroundSupportCanceled)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(text))
                return false;

            // Do not answer questions such as "what is your ETA?" with a random "copy" even
            // if a future question arrives under a newly added event type.
            return text.IndexOf('?') >= 0 ||
                   text.IndexOf("what is your E T A", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("what is your ETA", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The spoken readback for a tower clearance line, or null when the line
        /// carries no readback-worthy instruction.</summary>
        public static string TowerReadbackFor(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string callsign = ExtractLeadingCallsign(text);

            for (int i = 0; i < ClearancePhrases.Length; i++)
            {
                int index = text.IndexOf(ClearancePhrases[i], StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                    return AppendCallsign(CleanRadioPhrase(text.Substring(index)), callsign);
            }

            if (TryExtractSwitchStation(text, out string station))
                return AppendCallsign("switching " + station, callsign);

            return null;
        }

        private static bool TryExtractSwitchStation(string text, out string station)
        {
            station = null;

            int switchIndex = text.IndexOf("switch ", StringComparison.OrdinalIgnoreCase);
            if (switchIndex >= 0)
            {
                station = CleanRadioPhrase(text.Substring(switchIndex + "switch ".Length));
                return !string.IsNullOrEmpty(station);
            }

            int contactIndex = text.IndexOf("contact ", StringComparison.OrdinalIgnoreCase);
            if (contactIndex < 0)
                return false;

            int stationStart = contactIndex + "contact ".Length;
            int stationEnd = IndexOfAnyTerminator(text, stationStart, ",", ".", " on ");
            station = CleanRadioPhrase(text.Substring(stationStart, stationEnd - stationStart));
            return !string.IsNullOrEmpty(station);
        }

        private static int IndexOfAnyTerminator(string text, int start, params string[] terminators)
        {
            int best = text.Length;
            for (int i = 0; i < terminators.Length; i++)
            {
                int index = text.IndexOf(terminators[i], start, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && index < best)
                    best = index;
            }

            return best;
        }

        private static string ExtractLeadingCallsign(string text)
        {
            int comma = text.IndexOf(',');
            if (comma <= 0)
                return string.Empty;

            return CleanRadioPhrase(text.Substring(0, comma));
        }

        private static string AppendCallsign(string phrase, string callsign)
        {
            phrase = CleanRadioPhrase(phrase);
            if (string.IsNullOrEmpty(phrase))
                return null;

            if (string.IsNullOrEmpty(callsign))
                return phrase;

            return phrase + ", " + callsign;
        }

        private static string CleanRadioPhrase(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            return text.Trim().Trim(',', '.', ';', ':').Trim();
        }

        private static bool IsPlayerDownCall(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return text.IndexOf(" is down", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   text.IndexOf("no chute", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string PickAcknowledgement()
        {
            if (PlayerAcknowledgements.Length == 0)
                return "roger";

            int index = _random.Next(PlayerAcknowledgements.Length);
            if (PlayerAcknowledgements.Length > 1 && index == _lastAcknowledgementIndex)
                index = (index + 1) % PlayerAcknowledgements.Length;

            _lastAcknowledgementIndex = index;
            return PlayerAcknowledgements[index];
        }
    }
}
