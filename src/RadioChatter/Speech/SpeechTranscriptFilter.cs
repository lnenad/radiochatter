using System;
using System.Text;

namespace RadioChatter.Speech
{
    /// <summary>Prevents VAD-empty and explicit no-speech transcription results from becoming
    /// radio commands. Unknown spoken words still pass through so the station can say again.</summary>
    internal static class SpeechTranscriptFilter
    {
        private static readonly string[] NoSpeechMarkers =
        {
            "silence",
            "blank audio",
            "no audio",
            "no speech",
            "inaudible",
            "unintelligible",
            "background noise",
            "noise",
            "music"
        };

        public static bool HasWords(string transcript)
        {
            string normalized = Normalize(transcript);
            if (normalized.Length == 0)
                return false;

            for (int i = 0; i < NoSpeechMarkers.Length; i++)
            {
                if (string.Equals(normalized, NoSpeechMarkers[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
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
    }
}
