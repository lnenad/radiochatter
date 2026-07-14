using System;
using RadioChatter.Comms;

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
            string normalized = SpeechText.Normalize(transcript);
            if (normalized.Length == 0)
                return false;

            for (int i = 0; i < NoSpeechMarkers.Length; i++)
            {
                if (string.Equals(normalized, NoSpeechMarkers[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }
    }
}
