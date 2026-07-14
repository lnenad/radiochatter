using System;
using System.Collections.Generic;

namespace RadioChatter.Comms
{
    /// <summary>A callout waiting for its turn on the radio.</summary>
    internal struct PendingTransmission
    {
        public RadioRole Role;
        public RadioEventType Type;
        public string Text;
        public int Priority;
        public float CreatedAt;
        public float AvailableAt;
        public float ExpiresAt;
        public float DisplaySeconds;
        public uint SubjectId;
        public bool BypassStartupHold;
    }

    /// <summary>The director's pending-callout queue plus its text-dedup and per-type
    /// cooldown logs. Selection is highest priority first, oldest first within a priority.
    /// Pure data structure — staleness and hold rules are supplied by the caller.</summary>
    internal sealed class TransmissionQueue
    {
        private readonly List<PendingTransmission> _items = new List<PendingTransmission>(32);
        private readonly Dictionary<string, float> _lastTextAt = new Dictionary<string, float>(64);
        private readonly List<string> _staleTextKeys = new List<string>(64);
        private readonly Dictionary<RadioEventType, float> _lastTypeQueuedAt = new Dictionary<RadioEventType, float>();
        private readonly float _duplicateWindowSeconds;

        public TransmissionQueue(float duplicateWindowSeconds)
        {
            _duplicateWindowSeconds = duplicateWindowSeconds;
        }

        public int Count => _items.Count;

        public PendingTransmission this[int index] => _items[index];

        public void Add(PendingTransmission item)
        {
            _items.Add(item);
        }

        public void RemoveAt(int index)
        {
            _items.RemoveAt(index);
        }

        public int RemoveAll(Predicate<PendingTransmission> match)
        {
            return _items.RemoveAll(match);
        }

        /// <summary>Clears queued items only; dedup/cooldown history survives so a purge
        /// cannot re-open the duplicate window.</summary>
        public void Clear()
        {
            _items.Clear();
        }

        /// <summary>Clears the dedup/cooldown logs. Per-sortie state: a respawned player
        /// should hear the standard calls again.</summary>
        public void ClearDedupLog()
        {
            _lastTextAt.Clear();
            _lastTypeQueuedAt.Clear();
        }

        public bool IsOnCooldown(RadioEventType type, float now, float cooldownSeconds)
        {
            float lastTypeTime;
            return cooldownSeconds > 0f &&
                   _lastTypeQueuedAt.TryGetValue(type, out lastTypeTime) &&
                   now - lastTypeTime < cooldownSeconds;
        }

        public bool IsDuplicate(string text, float now, float duplicateWindowSeconds)
        {
            float lastTextTime;
            return duplicateWindowSeconds > 0f &&
                   _lastTextAt.TryGetValue(text, out lastTextTime) &&
                   now - lastTextTime < duplicateWindowSeconds;
        }

        /// <summary>Records a queued line in the dedup/cooldown logs.</summary>
        public void MarkQueued(string text, RadioEventType type, float now)
        {
            PruneTextDedupLog(now);
            _lastTextAt[text] = now;
            _lastTypeQueuedAt[type] = now;
        }

        /// <summary>Removes and returns the best transmittable item: expired and stale items
        /// are dropped, held items are handed to <paramref name="hold"/> and removed, not-yet
        /// -available items stay queued. Returns false when nothing can transmit right now.</summary>
        public bool TrySelectNext(
            float now,
            Func<PendingTransmission, bool> isStale,
            Func<PendingTransmission, bool> shouldHold,
            Action<PendingTransmission> hold,
            out PendingTransmission next)
        {
            int bestIndex = -1;
            int bestPriority = int.MinValue;
            float oldest = float.PositiveInfinity;

            for (int i = _items.Count - 1; i >= 0; i--)
            {
                PendingTransmission item = _items[i];
                if (item.ExpiresAt < now)
                {
                    _items.RemoveAt(i);
                    continue;
                }

                if (isStale != null && isStale(item))
                {
                    _items.RemoveAt(i);
                    continue;
                }

                if (item.AvailableAt > now)
                    continue;

                if (shouldHold != null && shouldHold(item))
                {
                    _items.RemoveAt(i);
                    hold?.Invoke(item);
                    continue;
                }

                if (item.Priority > bestPriority || (item.Priority == bestPriority && item.CreatedAt < oldest))
                {
                    bestIndex = i;
                    bestPriority = item.Priority;
                    oldest = item.CreatedAt;
                }
            }

            if (bestIndex < 0)
            {
                next = default;
                return false;
            }

            next = _items[bestIndex];
            _items.RemoveAt(bestIndex);
            return true;
        }

        /// <summary>Dedup entries are only consulted within the duplicate window, so anything
        /// much older is dead weight; sweep once the table grows past a bound so a long sortie
        /// with lots of unique lines cannot grow it without limit.</summary>
        private void PruneTextDedupLog(float now)
        {
            const int pruneThreshold = 128;
            if (_lastTextAt.Count < pruneThreshold)
                return;

            _staleTextKeys.Clear();
            foreach (KeyValuePair<string, float> pair in _lastTextAt)
            {
                if (now - pair.Value >= _duplicateWindowSeconds * 2f)
                    _staleTextKeys.Add(pair.Key);
            }

            for (int i = 0; i < _staleTextKeys.Count; i++)
                _lastTextAt.Remove(_staleTextKeys[i]);
        }
    }
}
