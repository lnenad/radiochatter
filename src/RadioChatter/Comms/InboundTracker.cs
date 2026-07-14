namespace RadioChatter.Comms
{
    /// <summary>Debounced "flying toward a point" detector shared by the approach and RTB
    /// callouts: the caller reports whether this tick looks inbound; the tracker requires the
    /// state to persist for a duration, forgiving brief dropouts within a grace window.</summary>
    internal sealed class InboundTracker
    {
        private float _previousDistance = float.NaN;
        private float _inboundStartedAt = float.NaN;
        private float _lastInboundAt = float.NaN;

        /// <summary>True when the distance shrank by at least <paramref name="minClosingMeters"/>
        /// since the previous sample (or on the first sample).</summary>
        public bool UpdateClosing(float distanceM, float minClosingMeters)
        {
            bool closing = float.IsNaN(_previousDistance) || distanceM < _previousDistance - minClosingMeters;
            _previousDistance = distanceM;
            return closing;
        }

        /// <summary>Advances the inbound timer, dropping it once the condition has lapsed for
        /// longer than the grace window.</summary>
        public void Track(bool inbound, float now, float graceSeconds)
        {
            if (inbound)
            {
                if (float.IsNaN(_inboundStartedAt))
                    _inboundStartedAt = now;

                _lastInboundAt = now;
            }
            else if (float.IsNaN(_lastInboundAt) || now - _lastInboundAt > graceSeconds)
            {
                _inboundStartedAt = float.NaN;
                _lastInboundAt = float.NaN;
            }
        }

        public bool InboundFor(float now, float requiredSeconds)
        {
            return !float.IsNaN(_inboundStartedAt) && now - _inboundStartedAt >= requiredSeconds;
        }

        /// <summary>Clears the inbound timer but keeps the distance history (used when the
        /// player leaves the detection envelope without invalidating the closing baseline).</summary>
        public void ResetInbound()
        {
            _inboundStartedAt = float.NaN;
            _lastInboundAt = float.NaN;
        }

        public void Reset()
        {
            _previousDistance = float.NaN;
            ResetInbound();
        }
    }
}
