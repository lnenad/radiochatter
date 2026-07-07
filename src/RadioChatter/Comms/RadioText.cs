using System;
using System.Collections.Generic;
using System.Text;
using RadioChatter.Game;
using RadioChatter.Speech;
using UnityEngine;

namespace RadioChatter.Comms
{
    /// <summary>Pure text helpers that turn game strings and measurements into radio-friendly speech.
    /// Stateless; all mission logic lives in <see cref="CommsDirector"/>.</summary>
    internal static class RadioText
    {
        public static string FormatRange(float meters, UnitsSystem units)
        {
            if (units == UnitsSystem.Imperial)
                return $"{NumberSpeech.Natural(Mathf.Max(1, meters / 1852f))} nautical";

            return $"{NumberSpeech.Natural(Mathf.Max(1, meters / 1000f))} kilometers";
        }

        public static string FormatAltitude(float meters, UnitsSystem units)
        {
            if (units == UnitsSystem.Imperial)
                return $"angels {NumberSpeech.Natural(Mathf.Max(1, meters * 3.28084f / 1000f))}";

            int rounded = Mathf.Max(0, Mathf.RoundToInt(meters / 100f) * 100);
            return $"altitude {NumberSpeech.Natural(rounded)} meters";
        }

        public static string FormatRunway(AirbaseInfo home)
        {
            if (!string.IsNullOrEmpty(home.RunwayName))
                return " runway " + SpellDigits(home.RunwayName);

            if (!float.IsNaN(home.RunwayHeadingDeg))
                return " runway " + SpellDigits(NumberSpeech.HeadingRunway(home.RunwayHeadingDeg));

            return string.Empty;
        }

        public static string SpellDigits(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            List<string> words = new List<string>(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                switch (value[i])
                {
                    case '0': words.Add("zero"); break;
                    case '1': words.Add("one"); break;
                    case '2': words.Add("two"); break;
                    case '3': words.Add("three"); break;
                    case '4': words.Add("four"); break;
                    case '5': words.Add("five"); break;
                    case '6': words.Add("six"); break;
                    case '7': words.Add("seven"); break;
                    case '8': words.Add("eight"); break;
                    case '9': words.Add("niner"); break;
                    case 'L':
                    case 'l': words.Add("left"); break;
                    case 'R':
                    case 'r': words.Add("right"); break;
                    case 'C':
                    case 'c': words.Add("center"); break;
                }
            }

            return words.Count > 0 ? string.Join(" ", words.ToArray()) : value;
        }

        public static string SpokenUnitName(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
                return "target";

            if (displayName.Contains("SmallFighter1"))
                return "Vortex";
            if (displayName.Contains("Multirole1"))
                return "Ifrit";
            if (displayName.Contains("EW1"))
                return "Medusa";

            return displayName.Replace("_", " ").Replace("-", " ");
        }

        public static string SanitizeGameComms(string text)
        {
            return ExpandCompactUnits(StripRichTextAndCollapseWhitespace(text));
        }

        public static string FormatPlayerWeaponCall(string text)
        {
            string clean = StripRichTextAndCollapseWhitespace(text);
            if (string.IsNullOrEmpty(clean))
                return string.Empty;

            string[] words = clean.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return FormatPlayerWeaponWords(words, IsRepeatedGunsCall(words));
        }

        public static bool IsGunsCall(string text)
        {
            return text.StartsWith("GUNS!", StringComparison.OrdinalIgnoreCase);
        }

        public static string StripRichTextAndCollapseWhitespace(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            StringBuilder builder = new StringBuilder(text.Length);
            bool inTag = false;
            bool previousWhitespace = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '<')
                {
                    inTag = true;
                    continue;
                }

                if (c == '>')
                {
                    inTag = false;
                    continue;
                }

                if (inTag)
                    continue;

                if (char.IsWhiteSpace(c))
                {
                    if (!previousWhitespace && builder.Length > 0)
                    {
                        builder.Append(' ');
                        previousWhitespace = true;
                    }

                    continue;
                }

                builder.Append(c);
                previousWhitespace = false;
            }

            return builder.ToString().Trim();
        }

        public static string ExtractRunwayName(string text)
        {
            string clean = StripRichTextAndCollapseWhitespace(text);
            if (TryFindRunwayKeyword(clean, out int runwayIndex, out int keywordLength))
                return ReadRunwayName(clean, runwayIndex + keywordLength);

            if (TryFindLandingOnPhrase(clean, out int landingOnIndex))
                return ReadRunwayName(clean, landingOnIndex);

            return string.Empty;
        }

        public static int CountWords(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            int words = 0;
            bool inWord = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    inWord = false;
                    continue;
                }

                if (!inWord)
                {
                    words++;
                    inWord = true;
                }
            }

            return words;
        }

        private static string FormatPlayerWeaponWords(string[] words, bool exclaimEveryWord)
        {
            StringBuilder builder = new StringBuilder(words.Length * 8);
            for (int i = 0; i < words.Length; i++)
            {
                string word = words[i].Trim('!', '.', ',', ';', ':');
                if (string.IsNullOrEmpty(word))
                    continue;

                if (builder.Length > 0)
                    builder.Append(' ');

                builder.Append(word.ToLowerInvariant());
                if (exclaimEveryWord)
                    builder.Append('!');
            }

            if (!exclaimEveryWord && builder.Length > 0)
                builder.Append('!');

            return builder.ToString();
        }

        private static bool IsRepeatedGunsCall(string[] words)
        {
            if (words == null || words.Length < 2)
                return false;

            for (int i = 0; i < words.Length; i++)
            {
                string word = words[i].Trim('!', '.', ',', ';', ':');
                if (!string.Equals(word, "guns", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private static string ReadRunwayName(string text, int index)
        {
            int i = SkipRunwaySeparators(text, index);

            if (StartsWithKeywordAt(text, i, "runway", out int keywordEnd) ||
                StartsWithKeywordAt(text, i, "rwy", out keywordEnd))
            {
                i = SkipRunwaySeparators(text, keywordEnd);
            }

            StringBuilder runway = new StringBuilder(4);
            while (i < text.Length && char.IsDigit(text[i]) && runway.Length < 3)
            {
                runway.Append(text[i]);
                i++;
            }

            int sideIndex = SkipRunwaySeparators(text, i);

            if (runway.Length > 0 && sideIndex < text.Length && IsRunwaySideLetter(text[sideIndex]))
                runway.Append(text[sideIndex]);
            else if (runway.Length > 0 && TryReadRunwaySideWord(text, sideIndex, out char side))
                runway.Append(side);

            if (runway.Length > 0)
                return runway.ToString();

            int start = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i]))
                i++;

            return i > start ? text.Substring(start, i - start) : string.Empty;
        }

        private static int SkipRunwaySeparators(string text, int index)
        {
            while (index < text.Length && (char.IsWhiteSpace(text[index]) || text[index] == ':' || text[index] == '#' || text[index] == '-'))
                index++;

            return index;
        }

        private static bool TryFindLandingOnPhrase(string text, out int index)
        {
            int phraseIndex = text.IndexOf("cleared for landing on", StringComparison.OrdinalIgnoreCase);
            if (phraseIndex >= 0)
            {
                index = phraseIndex + "cleared for landing on".Length;
                return true;
            }

            index = -1;
            return false;
        }

        private static bool TryReadRunwaySideWord(string text, int index, out char side)
        {
            if (StartsWithKeywordAt(text, index, "left", out _))
            {
                side = 'L';
                return true;
            }

            if (StartsWithKeywordAt(text, index, "right", out _))
            {
                side = 'R';
                return true;
            }

            if (StartsWithKeywordAt(text, index, "center", out _) ||
                StartsWithKeywordAt(text, index, "centre", out _))
            {
                side = 'C';
                return true;
            }

            side = '\0';
            return false;
        }

        private static bool StartsWithKeywordAt(string text, int index, string keyword, out int end)
        {
            end = index + keyword.Length;
            if (index < 0 || end > text.Length)
                return false;

            for (int i = 0; i < keyword.Length; i++)
            {
                if (char.ToLowerInvariant(text[index + i]) != keyword[i])
                    return false;
            }

            bool beforeBoundary = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            bool afterBoundary = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            return beforeBoundary && afterBoundary;
        }

        private static bool TryFindRunwayKeyword(string text, out int index, out int length)
        {
            if (TryFindKeyword(text, "runway", out index))
            {
                length = "runway".Length;
                return true;
            }

            if (TryFindKeyword(text, "rwy", out index))
            {
                length = "rwy".Length;
                return true;
            }

            index = -1;
            length = 0;
            return false;
        }

        private static bool TryFindKeyword(string text, string keyword, out int index)
        {
            int searchStart = 0;
            while (searchStart < text.Length)
            {
                int found = text.IndexOf(keyword, searchStart, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                    break;

                int end = found + keyword.Length;
                bool beforeBoundary = found == 0 || !char.IsLetterOrDigit(text[found - 1]);
                bool afterBoundary = end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (beforeBoundary && afterBoundary)
                {
                    index = found;
                    return true;
                }

                searchStart = end;
            }

            index = -1;
            return false;
        }

        private static bool IsRunwaySideLetter(char c)
        {
            return c == 'L' || c == 'l' || c == 'R' || c == 'r' || c == 'C' || c == 'c';
        }

        private static string ExpandCompactUnits(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            StringBuilder builder = new StringBuilder(text.Length + 16);
            for (int i = 0; i < text.Length;)
            {
                if (!char.IsDigit(text[i]))
                {
                    builder.Append(text[i]);
                    i++;
                    continue;
                }

                int numberStart = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == ',' || text[i] == '.'))
                    i++;

                int numberEnd = i;
                int unitStart = i;
                while (unitStart < text.Length && text[unitStart] == ' ')
                    unitStart++;

                if (StartsWithUnit(text, unitStart, "ft", out int unitEnd) && IsUnitBoundary(text, unitEnd))
                {
                    string number = text.Substring(numberStart, numberEnd - numberStart);
                    builder.Append(number).Append(IsSingularNumber(number) ? " foot" : " feet");
                    i = unitEnd;
                    continue;
                }

                if (StartsWithUnit(text, unitStart, "m/s", out unitEnd) && IsUnitBoundary(text, unitEnd) ||
                    StartsWithUnit(text, unitStart, "mps", out unitEnd) && IsUnitBoundary(text, unitEnd))
                {
                    string number = text.Substring(numberStart, numberEnd - numberStart);
                    builder.Append(number).Append(" meters per second");
                    i = unitEnd;
                    continue;
                }

                if (StartsWithUnit(text, unitStart, "m", out unitEnd) && IsUnitBoundary(text, unitEnd))
                {
                    string number = text.Substring(numberStart, numberEnd - numberStart);
                    builder.Append(number).Append(IsSingularNumber(number) ? " meter" : " meters");
                    i = unitEnd;
                    continue;
                }

                builder.Append(text, numberStart, i - numberStart);
            }

            return builder.ToString();
        }

        private static bool StartsWithUnit(string text, int index, string unit, out int end)
        {
            end = index + unit.Length;
            if (index < 0 || end > text.Length)
                return false;

            for (int i = 0; i < unit.Length; i++)
            {
                if (char.ToLowerInvariant(text[index + i]) != unit[i])
                    return false;
            }

            return true;
        }

        private static bool IsUnitBoundary(string text, int index)
        {
            if (index >= text.Length)
                return true;

            char next = text[index];
            return !char.IsLetterOrDigit(next) && next != '/';
        }

        private static bool IsSingularNumber(string number)
        {
            return number == "1" || number == "1.0" || number == "1.00";
        }
    }
}
