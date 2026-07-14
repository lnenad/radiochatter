using System;
using static RadioChatter.Comms.SpeechText;

namespace RadioChatter.Comms
{
    internal enum TowerReadbackKind
    {
        Takeoff,
        Landing,
        Handoff
    }

    internal struct TowerReadbackExpectation
    {
        public TowerReadbackKind Kind;
        public string Callsign;
        public string Runway;
        public string HandoffStation;
    }

    internal static class TowerReadbackTiming
    {
        /// <summary>The player's response window begins after Tower finishes speaking. Refreshing
        /// while correction audio is queued, synthesizing, or playing prevents that audio from
        /// consuming the retry timeout.</summary>
        public static float RefreshAwaitingSince(float awaitingSince, float now, bool towerBusy)
        {
            return towerBusy ? now : awaitingSince;
        }

        public static bool HasTimedOut(float awaitingSince, float now, float responseSeconds)
        {
            return now - awaitingSince >= responseSeconds;
        }
    }

    /// <summary>Builds and validates the critical parts of a Tower readback. Radio readbacks do
    /// not normally re-address Tower, so this is intentionally separate from proper-call parsing.</summary>
    internal static class TowerReadbackMatcher
    {
        public static bool TryCreate(string towerText, out TowerReadbackExpectation expectation)
        {
            expectation = default;
            string normalized = Normalize(towerText);
            if (normalized.Length == 0)
                return false;

            string callsign = LeadingCallsign(towerText);
            if (callsign.Length == 0)
                return false;

            if (HasTakeoffInstruction(normalized))
            {
                expectation = new TowerReadbackExpectation
                {
                    Kind = TowerReadbackKind.Takeoff,
                    Callsign = callsign,
                    Runway = WordSequenceAfter(normalized, "runway", null)
                };
                return true;
            }

            if (HasLandingInstruction(normalized))
            {
                expectation = new TowerReadbackExpectation
                {
                    Kind = TowerReadbackKind.Landing,
                    Callsign = callsign,
                    Runway = WordSequenceAfter(normalized, "runway", null)
                };
                return true;
            }

            string station = WordSequenceAfter(normalized, "switch", null);
            if (station.Length == 0)
                station = WordSequenceAfter(normalized, "contact", "on");

            if (station.Length == 0)
                return false;

            expectation = new TowerReadbackExpectation
            {
                Kind = TowerReadbackKind.Handoff,
                Callsign = callsign,
                HandoffStation = station
            };
            return true;
        }

        public static bool IsMatch(string transcript, TowerReadbackExpectation expectation)
        {
            string normalized = Normalize(transcript);
            if (normalized.Length == 0 || !ContainsFolded(normalized, expectation.Callsign))
                return false;

            switch (expectation.Kind)
            {
                case TowerReadbackKind.Takeoff:
                    return HasTakeoffInstruction(normalized) && HasExpectedRunway(normalized, expectation.Runway);

                case TowerReadbackKind.Landing:
                    return HasLandingInstruction(normalized) && HasExpectedRunway(normalized, expectation.Runway);

                case TowerReadbackKind.Handoff:
                    return HasAnyWord(normalized, "switch", "switching", "contact", "contacting") &&
                           ContainsFolded(normalized, expectation.HandoffStation);

                default:
                    return false;
            }
        }

        public static bool LooksLikeAttempt(string transcript, TowerReadbackExpectation expectation)
        {
            string normalized = Normalize(transcript);
            if (normalized.Length == 0)
                return true;

            if (HasAnyWord(normalized, "roger", "copy", "copied", "wilco", "affirmative", "understood"))
                return true;

            switch (expectation.Kind)
            {
                case TowerReadbackKind.Takeoff:
                    return HasAnyWord(normalized, "takeoff", "take off", "departure", "launch", "deck", "runway", "cleared", "clear");
                case TowerReadbackKind.Landing:
                    return HasAnyWord(normalized, "land", "landing", "recover", "recovery", "deck", "runway", "cleared", "clear", "full stop");
                case TowerReadbackKind.Handoff:
                    return HasAnyWord(normalized, "switch", "switching", "contact", "contacting", "button") ||
                           ContainsFolded(normalized, expectation.HandoffStation);
                default:
                    return false;
            }
        }

        private static bool HasTakeoffInstruction(string normalized)
        {
            return HasAnyWord(normalized, "clear", "cleared") && HasAnyWord(normalized, "takeoff", "take off", "launch");
        }

        private static bool HasLandingInstruction(string normalized)
        {
            return HasAnyWord(normalized, "clear", "cleared") && HasAnyWord(normalized, "land", "landing", "recover", "recovery");
        }

        private static bool HasExpectedRunway(string normalized, string expectedRunway)
        {
            if (string.IsNullOrEmpty(expectedRunway))
                return true;

            string spokenRunway = WordSequenceAfter(normalized, "runway", null);
            string foldedExpected = FoldForCompare(Normalize(expectedRunway));
            return spokenRunway.Length > 0 &&
                   FoldForCompare(spokenRunway).StartsWith(foldedExpected, StringComparison.Ordinal);
        }

        private static string LeadingCallsign(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            int comma = text.IndexOf(',');
            return comma > 0 ? Normalize(text.Substring(0, comma)) : string.Empty;
        }

        private static string WordSequenceAfter(string normalized, string keyword, string terminator)
        {
            int index = IndexOfWord(normalized, keyword);
            if (index < 0)
                return string.Empty;

            int start = index + keyword.Length;
            while (start < normalized.Length && normalized[start] == ' ')
                start++;

            if (start >= normalized.Length)
                return string.Empty;

            int end = normalized.Length;
            if (!string.IsNullOrEmpty(terminator))
            {
                int terminatorIndex = IndexOfWord(normalized, terminator, start);
                if (terminatorIndex >= 0)
                    end = terminatorIndex;
            }

            return normalized.Substring(start, end - start).Trim();
        }

        private static bool ContainsFolded(string normalizedHaystack, string phrase)
        {
            string foldedNeedle = FoldForCompare(Normalize(phrase));
            return foldedNeedle.Length > 0 && FoldForCompare(normalizedHaystack).Contains(foldedNeedle);
        }

        private static bool HasAnyWord(string normalized, params string[] words)
        {
            for (int i = 0; i < words.Length; i++)
            {
                if (IndexOfWord(normalized, words[i]) >= 0)
                    return true;
            }

            return false;
        }

    }
}
