using System.Collections.Generic;

namespace RadioChatter.Comms
{
    /// <summary>Remembers the most recently dispatched Tower and AWACS lines independently so a
    /// player can ask either station to repeat its last transmission.</summary>
    internal sealed class StationTransmissionHistory
    {
        private readonly Dictionary<VoiceStation, string> _lastText =
            new Dictionary<VoiceStation, string>(2);

        public void Record(VoiceStation station, string text)
        {
            if ((station != VoiceStation.Tower && station != VoiceStation.Awacs) ||
                string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            _lastText[station] = text;
        }

        public bool TryGet(VoiceStation station, out string text)
        {
            return _lastText.TryGetValue(station, out text) && !string.IsNullOrWhiteSpace(text);
        }

        public void Clear()
        {
            _lastText.Clear();
        }
    }
}
