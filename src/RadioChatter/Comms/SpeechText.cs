using System;
using System.Text;

namespace RadioChatter.Comms
{
    /// <summary>Shared text helpers for matching speech transcripts and radio phraseology.
    /// One implementation on purpose: VoiceIntentParser and TowerReadbackMatcher previously
    /// carried diverging copies (one knew "niner", the other "oh"/"tree"/"fife"), so the same
    /// spoken digits could match in one grammar and miss in the other.</summary>
    internal static class SpeechText
    {
        /// <summary>Lowercase, strip everything but letters/digits to single spaces. This also
        /// folds transcription variants like "take-off" and "take off." onto "take off".</summary>
        public static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            StringBuilder builder = new StringBuilder(value.Length);
            bool lastWasSpace = true;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                    lastWasSpace = false;
                }
                else if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }
            }

            while (builder.Length > 0 && builder[builder.Length - 1] == ' ')
                builder.Length--;

            return builder.ToString();
        }

        /// <summary>Word-boundary Contains over the normalized (space-separated) text, so
        /// "land" does not match "island". Both text and needle are already normalized.</summary>
        public static int IndexOfWord(string text, string word, int start = 0)
        {
            if (word.Length == 0)
                return -1;

            while (start < text.Length)
            {
                int index = text.IndexOf(word, start, StringComparison.Ordinal);
                if (index < 0)
                    return -1;

                bool startOk = index == 0 || text[index - 1] == ' ';
                int end = index + word.Length;
                bool endOk = end == text.Length || text[end] == ' ';
                if (startOk && endOk)
                    return index;

                start = index + 1;
            }

            return -1;
        }

        /// <summary>Folds spoken digits onto numerals and removes spaces so "runway two seven"
        /// compares equal to "runway 27" regardless of how the transcriber wrote it.</summary>
        public static string FoldForCompare(string normalized)
        {
            string[] tokens = normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            StringBuilder builder = new StringBuilder(normalized.Length);
            for (int i = 0; i < tokens.Length; i++)
                builder.Append(FoldNumberToken(tokens[i]));

            return builder.ToString();
        }

        /// <summary>Covers the radio pronunciations ("niner", "tree", "fife") and common
        /// transcriptions ("oh") in one place.</summary>
        public static string FoldNumberToken(string token)
        {
            switch (token)
            {
                case "zero":
                case "oh": return "0";
                case "one": return "1";
                case "two": return "2";
                case "three":
                case "tree": return "3";
                case "four": return "4";
                case "five":
                case "fife": return "5";
                case "six": return "6";
                case "seven": return "7";
                case "eight": return "8";
                case "nine":
                case "niner": return "9";
                default: return token;
            }
        }
    }
}
