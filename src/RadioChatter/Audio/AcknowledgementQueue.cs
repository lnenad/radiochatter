using System.Collections.Generic;
using RadioChatter.Comms;

namespace RadioChatter.Audio
{
    /// <summary>Short queue of automatic player replies ("copy", tower readbacks) waiting
    /// for the channel to go quiet. Main-thread only; the caller decides when other audio
    /// work blocks release and calls Defer.</summary>
    internal sealed class AcknowledgementQueue
    {
        private const float DelaySeconds = 0.35f;
        private const int MaxPending = 8;

        private readonly Queue<PlayerResponse> _pending = new Queue<PlayerResponse>();
        private float _nextReadyAt;

        public int Count => _pending.Count;

        public void Enqueue(PlayerResponse response, float now)
        {
            bool wasEmpty = _pending.Count == 0;
            while (_pending.Count >= MaxPending)
                _pending.Dequeue();

            _pending.Enqueue(response);

            if (wasEmpty)
                _nextReadyAt = now + DelaySeconds;
        }

        public bool ContainsText(string text)
        {
            foreach (PlayerResponse pending in _pending)
            {
                if (string.Equals(pending.Text, text, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public bool HasForRole(RadioRole role)
        {
            foreach (PlayerResponse pending in _pending)
            {
                if (pending.Role == role)
                    return true;
            }

            return false;
        }

        /// <summary>Pushes the release window back while other traffic is talking.</summary>
        public void Defer(float now)
        {
            _nextReadyAt = now + DelaySeconds;
        }

        public bool TryDequeue(float now, out PlayerResponse response)
        {
            if (_pending.Count == 0 || now < _nextReadyAt)
            {
                response = default;
                return false;
            }

            response = _pending.Dequeue();

            if (_pending.Count > 0)
                _nextReadyAt = now + DelaySeconds;

            return true;
        }

        public void Clear()
        {
            _pending.Clear();
        }
    }
}
