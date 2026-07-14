using System.Collections.Generic;
using System.Text;
using static RadioChatter.Comms.SpeechText;

namespace RadioChatter.Comms
{
    internal enum FlightMissionRole
    {
        None,
        Cap,
        Cas,
        Sead,
        Strike,
        MaritimeStrike,
        SearchAndDestroy
    }

    internal static class FlightMissionRolePolicy
    {
        public static bool SuppressGroundSupportHails(FlightMissionRole role)
        {
            return role == FlightMissionRole.Cap ||
                   role == FlightMissionRole.Sead ||
                   role == FlightMissionRole.Strike ||
                   role == FlightMissionRole.MaritimeStrike ||
                   role == FlightMissionRole.SearchAndDestroy;
        }

        public static bool SuppressAutomaticAirContacts(FlightMissionRole role)
        {
            return role == FlightMissionRole.Cas ||
                   role == FlightMissionRole.Sead ||
                   role == FlightMissionRole.Strike ||
                   role == FlightMissionRole.MaritimeStrike;
        }
    }

    internal enum VoiceIntentKind
    {
        Unknown,
        RequestTakeoff,
        RequestLanding,
        RequestPicture,
        RequestVector,
        RequestVectorGroundSupport,
        RequestVectorObjective,
        RequestObjectiveList,
        RequestVectorHome,
        DeclareWinchester,
        RequestAwacsQuiet,
        RequestAwacsResume,
        SetMissionRole,
        CheckIn,
        RadioCheck,
        RequestRepeatLast
    }

    internal enum VoiceStation
    {
        Unspecified,
        Tower,
        Awacs
    }

    internal struct VoiceIntent
    {
        public VoiceIntentKind Kind;
        public VoiceStation Station;
        /// <summary>Callsign the player identified themselves with ("this is Broadsword 1-1"),
        /// resolved to the configured callsign when it matches, otherwise prettified from the
        /// transcript. Empty when the player did not self-identify.</summary>
        public string Callsign;
        /// <summary>The utterance began by addressing a station ("tower ...", "overwatch ...").
        /// Proper-call enforcement keys off this, not off a station word appearing anywhere.</summary>
        public bool StationAddressed;
        /// <summary>The player self-identified with a callsign.</summary>
        public bool CallsignSpoken;
        /// <summary>Free-form words spoken after "objective", used to pick a specific objective
        /// by loose name match ("vector to objective radar site"). Empty = closest objective.</summary>
        public string ObjectiveQuery;
        /// <summary>Explicit mission role declared during an AWACS check-in or role update.</summary>
        public FlightMissionRole MissionRole;
    }

    /// <summary>Deterministic transcript-to-intent matcher. The command set is a small closed
    /// radio grammar, so ordered keyword rules over a normalized transcript beat any model:
    /// instant, testable, and a miss becomes an in-fiction "say again".</summary>
    internal static class VoiceIntentParser
    {
        /// <summary>Words that end a self-identification: the first command keyword after
        /// "this is <callsign>" (or after the addressed station) marks where the callsign stops.</summary>
        private static readonly HashSet<string> CallsignStopWords = new HashSet<string>
        {
            "request", "requesting", "requests", "ready", "cleared", "clearance",
            "radio", "comm", "comms", "mic", "how", "check", "checking",
            "takeoff", "take", "taxi", "departure", "launch",
            "land", "landing", "inbound", "approach", "full", "downwind", "overhead", "airborne", "recover", "recovery",
            "return", "returning", "rtb", "home", "base",
            "vector", "bearing", "steer", "heading", "intercept", "target", "bandit",
            "objective", "objectives", "list", "tasking", "status", "current", "active", "available", "all",
            "picture", "bogey", "bogie", "dope", "contacts", "threats", "situation", "scope",
            "quiet", "silence", "silent", "stop", "hold", "go", "cancel", "terminate", "minimize", "minimise",
            "resume", "restore", "normal", "emergency", "essential",
            "call", "calls", "callout", "callouts", "traffic",
            "winchester", "weapons", "ordnance", "ordinance", "dry",
            "mission", "role", "tasked", "tasking", "as", "fragged", "cap", "cas", "sead", "seed",
            "close", "combat", "patrol",
            "strike", "interdiction", "bomb", "bombing", "bomber", "ground",
            "maritime", "naval", "anti", "ship", "shipping", "hunter", "hunting", "surface", "warfare", "countersea", "war",
            "search", "destroy", "general", "multirole", "multi",
            "say", "need", "want", "give", "what", "on", "for", "with", "at", "to"
        };

        private static readonly string[] EmptyTokens = new string[0];

        public static VoiceIntent Parse(string transcript, string awacsCallsign, string configuredPlayerCallsign)
        {
            string text = Normalize(transcript);
            string awacs = Normalize(awacsCallsign);
            string[] tokens = text.Length == 0 ? EmptyTokens : text.Split(' ');

            int afterStation;
            VoiceStation leadingStation = DetectLeadingStation(tokens, awacs, out afterStation);

            VoiceIntent intent = new VoiceIntent
            {
                Kind = DetectKind(text),
                StationAddressed = leadingStation != VoiceStation.Unspecified,
                // A mid-sentence station mention still routes the reply even though it does not
                // count as a proper address.
                Station = leadingStation != VoiceStation.Unspecified ? leadingStation : DetectStation(text, awacs),
                Callsign = ResolveCallsign(ExtractCallsign(tokens, afterStation), configuredPlayerCallsign)
            };
            intent.CallsignSpoken = intent.Callsign.Length > 0;
            intent.ObjectiveQuery = intent.Kind == VoiceIntentKind.RequestVectorObjective
                ? ExtractObjectiveQuery(tokens)
                : string.Empty;
            if (intent.Kind == VoiceIntentKind.SetMissionRole)
                TryDetectMissionRole(text, out intent.MissionRole);

            return intent;
        }

        /// <summary>Natural negative replies used to turn down a ground-support request.
        /// This intentionally sits outside station/callsign grammar: "Hammer four, unable"
        /// needs only the ground callsign.</summary>
        public static bool IsGroundSupportDecline(string transcript)
        {
            string text = Normalize(transcript);
            return HasAny(text,
                "unable", "negative", "decline", "declining",
                "cannot assist", "cant assist", "cannot help", "cant help",
                "not able", "not available", "no can do");
        }

        /// <summary>True when the player explicitly uses "mission" as a cockpit mode command.
        /// This permits terse selections such as "mission search and destroy" while leaving
        /// ordinary AWACS requests subject to configured radio-discipline checks.</summary>
        public static bool ContainsMissionCommandWord(string transcript)
        {
            return IndexOfWord(Normalize(transcript), "mission") >= 0;
        }

        /// <summary>Station address at the very start of the utterance: "tower ...",
        /// "deck ...", "carrier ...", "awacs ...", or the configured AWACS callsign. Returns
        /// the token index after it.</summary>
        private static VoiceStation DetectLeadingStation(string[] tokens, string awacsCallsign, out int nextIndex)
        {
            nextIndex = 0;
            if (tokens.Length == 0)
                return VoiceStation.Unspecified;

            if (tokens[0] == "tower" || tokens[0] == "deck" || tokens[0] == "carrier")
            {
                nextIndex = tokens.Length > 1 && tokens[1] == "control" ? 2 : 1;
                return VoiceStation.Tower;
            }

            if (tokens[0] == "awacs")
            {
                nextIndex = 1;
                return VoiceStation.Awacs;
            }

            string[] awacsTokens = awacsCallsign.Length > 0 ? awacsCallsign.Split(' ') : null;
            if (awacsTokens != null && StartsWithSequence(tokens, awacsTokens))
            {
                nextIndex = awacsTokens.Length;
                return VoiceStation.Awacs;
            }

            return VoiceStation.Unspecified;
        }

        /// <summary>Pulls the self-identification out of "this is <callsign> ..." or
        /// "<station> <callsign> request ...". Returns normalized words, or empty.</summary>
        private static string ExtractCallsign(string[] tokens, int afterStationIndex)
        {
            if (tokens.Length == 0)
                return string.Empty;

            int start = -1;

            for (int i = 0; i + 1 < tokens.Length; i++)
            {
                if (tokens[i] == "this" && tokens[i + 1] == "is")
                {
                    start = i + 2;
                    break;
                }
            }

            // Only a *leading* station address is treated as "<station>, <callsign>, ...";
            // a station mentioned mid-sentence is part of the command, not an address.
            if (start < 0 && afterStationIndex > 0)
                start = afterStationIndex;

            if (start < 0 || start >= tokens.Length)
                return string.Empty;

            List<string> picked = new List<string>(4);
            bool hasAlpha = false;
            for (int i = start; i < tokens.Length && picked.Count < 5; i++)
            {
                if (CallsignStopWords.Contains(tokens[i]))
                    break;

                picked.Add(tokens[i]);
                for (int c = 0; c < tokens[i].Length; c++)
                {
                    if (char.IsLetter(tokens[i][c]))
                    {
                        hasAlpha = true;
                        break;
                    }
                }
            }

            if (picked.Count == 0 || !hasAlpha)
                return string.Empty;

            return string.Join(" ", picked.ToArray());
        }

        /// <summary>Spoken callsign matching the configured one (ignoring spacing, hyphens, and
        /// digit words vs digits) resolves to the configured spelling; anything else is kept as
        /// spoken so the tower plays along with whatever the player calls themselves.</summary>
        private static string ResolveCallsign(string spoken, string configured)
        {
            if (spoken.Length == 0)
                return string.Empty;

            string configuredNormalized = Normalize(configured);
            if (FoldForCompare(spoken) == FoldForCompare(configuredNormalized))
                return configured;

            return Prettify(spoken);
        }

        /// <summary>Words carrying no meaning when matching a spoken objective reference
        /// against objective names.</summary>
        private static readonly HashSet<string> QueryFillerWords = new HashSet<string>
        {
            "the", "a", "an", "to", "at", "of", "on", "for", "and", "please"
        };

        /// <summary>Everything spoken after "objective(s)" — the loose reference to a specific
        /// objective. Normalized words; empty when nothing follows.</summary>
        private static string ExtractObjectiveQuery(string[] tokens)
        {
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i] != "objective" && tokens[i] != "objectives")
                    continue;

                return i + 1 < tokens.Length
                    ? string.Join(" ", tokens, i + 1, tokens.Length - i - 1)
                    : string.Empty;
            }

            return string.Empty;
        }

        /// <summary>Loose spoken-name match: more than half of the meaningful spoken words
        /// must appear in the objective name (exact token or 4+ character prefix, digit words
        /// folded), so "radar site" finds "Destroy the radar site at Kowal" without the
        /// player reciting the name verbatim. Score is the number of matched words —
        /// callers pick the best-scoring candidate.</summary>
        public static bool LooseNameMatch(string spokenQuery, string name, out int score)
        {
            score = 0;

            string[] queryTokens = SignificantTokens(Normalize(spokenQuery));
            if (queryTokens.Length == 0)
                return false;

            string[] nameTokens = Normalize(name).Split(' ');
            for (int n = 0; n < nameTokens.Length; n++)
                nameTokens[n] = FoldNumberToken(nameTokens[n]);

            for (int q = 0; q < queryTokens.Length; q++)
            {
                for (int n = 0; n < nameTokens.Length; n++)
                {
                    if (TokensMatch(queryTokens[q], nameTokens[n]))
                    {
                        score++;
                        break;
                    }
                }
            }

            return score * 2 > queryTokens.Length;
        }

        /// <summary>Token-aware callsign lookup with digit-word folding, so "Anvil one" and
        /// "Anvil 1" identify the same persistent ground group without substring accidents.</summary>
        public static bool ContainsSpokenCallsign(string transcript, string callsign)
        {
            string normalizedTranscript = Normalize(transcript);
            string normalizedCallsign = Normalize(callsign);
            if (normalizedTranscript.Length == 0 || normalizedCallsign.Length == 0)
                return false;

            string[] transcriptTokens = normalizedTranscript.Split(' ');
            string[] callsignTokens = normalizedCallsign.Split(' ');
            if (callsignTokens.Length > transcriptTokens.Length)
                return false;

            for (int start = 0; start + callsignTokens.Length <= transcriptTokens.Length; start++)
            {
                bool match = true;
                for (int i = 0; i < callsignTokens.Length; i++)
                {
                    if (FoldNumberToken(transcriptTokens[start + i]) != FoldNumberToken(callsignTokens[i]))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                    return true;
            }

            return false;
        }

        private static string[] SignificantTokens(string normalized)
        {
            if (normalized.Length == 0)
                return EmptyTokens;

            string[] tokens = normalized.Split(' ');
            List<string> kept = new List<string>(tokens.Length);
            for (int i = 0; i < tokens.Length; i++)
            {
                if (!QueryFillerWords.Contains(tokens[i]))
                    kept.Add(FoldNumberToken(tokens[i]));
            }

            return kept.ToArray();
        }

        private static bool TokensMatch(string a, string b)
        {
            if (a == b)
                return true;

            // 4+ character prefix absorbs plural/inflection and transcription tails
            // ("radars" vs "radar", "kowal" vs "kowalski").
            string shorter = a.Length <= b.Length ? a : b;
            string longer = a.Length <= b.Length ? b : a;
            return shorter.Length >= 4 && longer.StartsWith(shorter, System.StringComparison.Ordinal);
        }

        /// <summary>"broad sword 1 1" → "Broad Sword 1-1": capitalized words, adjacent digit
        /// groups joined with a hyphen to match the usual callsign spelling.</summary>
        private static string Prettify(string normalized)
        {
            string[] tokens = normalized.Split(' ');
            StringBuilder builder = new StringBuilder(normalized.Length + 4);
            bool previousWasDigits = false;
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                bool isDigits = token.Length > 0 && char.IsDigit(token[0]);

                if (builder.Length > 0)
                    builder.Append(isDigits && previousWasDigits ? '-' : ' ');

                if (isDigits)
                    builder.Append(token);
                else
                {
                    builder.Append(char.ToUpperInvariant(token[0]));
                    if (token.Length > 1)
                        builder.Append(token, 1, token.Length - 1);
                }

                previousWasDigits = isDigits;
            }

            return builder.ToString();
        }

        private static bool StartsWithSequence(string[] tokens, string[] sequence)
        {
            if (sequence.Length == 0 || sequence.Length > tokens.Length)
                return false;

            for (int i = 0; i < sequence.Length; i++)
            {
                if (tokens[i] != sequence[i])
                    return false;
            }

            return true;
        }

        private static VoiceIntentKind DetectKind(string text)
        {
            if (text.Length == 0)
                return VoiceIntentKind.Unknown;

            FlightMissionRole ignoredRole;
            if (TryDetectMissionRole(text, out ignoredRole))
                return VoiceIntentKind.SetMissionRole;

            // Restore phrases must win over quiet phrases because "cancel radio quiet"
            // intentionally contains the word "quiet".
            if (HasAny(text,
                "resume calls", "resume callouts", "resume awacs", "restore calls", "restore callouts",
                "resume normal calls", "normal comms", "normal communications", "normal traffic", "radio normal",
                "cancel radio quiet", "cancel quiet comms", "terminate radio quiet"))
            {
                return VoiceIntentKind.RequestAwacsResume;
            }

            if (HasAny(text,
                "winchester", "win chester", "weapons dry", "out of weapons",
                "no weapons remaining", "out of ordnance", "out of ordinance"))
            {
                return VoiceIntentKind.DeclareWinchester;
            }

            if (HasAny(text,
                "radio quiet", "quiet radio", "quiet comms", "quiet communications",
                "quiet awacs", "minimize calls", "minimise calls", "minimize awacs", "minimise awacs",
                "stop callouts", "stop awacs calls", "stop awacs callouts", "hold awacs calls",
                "silence awacs", "awacs silence", "go quiet",
                "emergency traffic only", "essential traffic only"))
            {
                return VoiceIntentKind.RequestAwacsQuiet;
            }

            if (HasAny(text,
                "say again", "say that again", "please repeat", "repeat please",
                "repeat your last", "repeat last", "repeat that", "repeat transmission",
                "come again", "repeat"))
            {
                return VoiceIntentKind.RequestRepeatLast;
            }

            if (HasAny(text, "radio check", "comm check", "comms check", "mic check", "how do you read"))
                return VoiceIntentKind.RadioCheck;

            if (HasAny(text, "check in", "checking in", "checking on", "with you", "airborne"))
                return VoiceIntentKind.CheckIn;

            bool wantsHome = HasAny(text, "home", "base", "airfield", "airport", "field", "mother");
            if (HasAny(text, "rtb", "return to base", "returning to base"))
                return VoiceIntentKind.RequestVectorHome;

            bool wantsVector = HasAny(text, "vector", "bearing", "steer", "heading to", "intercept");
            if (wantsVector && wantsHome)
                return VoiceIntentKind.RequestVectorHome;

            if (wantsVector && HasAny(text, "support", "secondary"))
                return VoiceIntentKind.RequestVectorGroundSupport;

            // "vector to objective" / "request objective" — the mission objective, as opposed
            // to "vector to target", which is the locked/nearest contact. A plural or an
            // explicit list word asks for the whole board instead of a vector.
            if (HasAny(text, "objective", "objectives"))
            {
                bool wantsList = HasAny(text, "list", "all", "current", "active", "available", "tasking", "status", "what") ||
                                 (IndexOfWord(text, "objectives") >= 0 && !wantsVector);
                return wantsList ? VoiceIntentKind.RequestObjectiveList : VoiceIntentKind.RequestVectorObjective;
            }

            if (HasAny(text, "takeoff", "take off", "departure", "ready to taxi", "request taxi", "launch"))
                return VoiceIntentKind.RequestTakeoff;

            if (HasAny(text, "landing", "land", "inbound", "approach", "full stop", "downwind", "overhead", "recover", "recovery"))
                return VoiceIntentKind.RequestLanding;

            if (wantsVector || HasAny(text, "target", "bandit", "cut off"))
                return VoiceIntentKind.RequestVector;

            if (HasAny(text, "picture", "bogey", "bogie", "dope", "contacts", "threats", "situation", "scope"))
                return VoiceIntentKind.RequestPicture;

            return VoiceIntentKind.Unknown;
        }

        private static bool TryDetectMissionRole(string text, out FlightMissionRole role)
        {
            role = FlightMissionRole.None;
            bool hasRoleContext = HasAny(text,
                "check in", "checking in", "mission", "role", "tasked", "tasking", "as fragged");

            if (HasAny(text, "no mission", "mission general", "general mission", "mission multirole",
                "mission multi role", "cancel mission role", "clear mission role"))
            {
                role = FlightMissionRole.None;
                return true;
            }

            if (!hasRoleContext)
                return false;

            if (HasAny(text, "sead", "s e a d", "seed", "suppression of enemy air defenses",
                "suppression of enemy air defence"))
            {
                role = FlightMissionRole.Sead;
                return true;
            }

            if (HasAny(text, "cas", "c a s", "close air support"))
            {
                role = FlightMissionRole.Cas;
                return true;
            }

            if (HasAny(text, "cap", "c a p", "combat air patrol", "close air patrol"))
            {
                role = FlightMissionRole.Cap;
                return true;
            }

            if (HasAny(text,
                "maritime strike", "naval strike", "ship strike", "shipping strike",
                "anti ship", "anti shipping", "anti surface warfare", "asuw", "a s u w",
                "war at sea", "countersea", "counter sea", "ship attack", "attack ships",
                "ship hunter", "ship hunting"))
            {
                role = FlightMissionRole.MaritimeStrike;
                return true;
            }

            if (HasAny(text, "search and destroy", "search destroy", "seek and destroy"))
            {
                role = FlightMissionRole.SearchAndDestroy;
                return true;
            }

            if (HasAny(text,
                "strike", "interdiction", "air interdiction", "bomb", "bombing", "bomber",
                "bomb strike", "ground strike"))
            {
                role = FlightMissionRole.Strike;
                return true;
            }

            return false;
        }

        private static VoiceStation DetectStation(string text, string awacsCallsign)
        {
            int towerIndex = IndexOfWord(text, "tower");
            int deckIndex = IndexOfWord(text, "deck");
            int carrierIndex = IndexOfWord(text, "carrier");
            if (deckIndex >= 0 && (towerIndex < 0 || deckIndex < towerIndex))
                towerIndex = deckIndex;
            if (carrierIndex >= 0 && (towerIndex < 0 || carrierIndex < towerIndex))
                towerIndex = carrierIndex;
            int awacsIndex = awacsCallsign.Length > 0 ? IndexOfWord(text, awacsCallsign) : -1;
            if (awacsIndex < 0)
                awacsIndex = IndexOfWord(text, "awacs");

            if (towerIndex >= 0 && (awacsIndex < 0 || towerIndex < awacsIndex))
                return VoiceStation.Tower;

            if (awacsIndex >= 0)
                return VoiceStation.Awacs;

            return VoiceStation.Unspecified;
        }

        private static bool HasAny(string text, params string[] needles)
        {
            for (int i = 0; i < needles.Length; i++)
            {
                if (IndexOfWord(text, needles[i]) >= 0)
                    return true;
            }

            return false;
        }

    }
}
