using System.Collections.Generic;
using RadioChatter.Game;

namespace RadioChatter.Comms
{
    /// <summary>Ambient friendly-flight chatter: watches friendly aircraft for state changes
    /// (lost, airborne, landed), collects short-lived candidates from combat events, and
    /// queues at most one low-priority line whenever the caller says the radio is idle.</summary>
    internal sealed class BattlefieldChatterFeed
    {
        /// <summary>Queues one chatter line; returns false when dedup/cooldowns rejected it.</summary>
        public delegate bool ChatterSink(string phraseKey, Dictionary<string, string> slots, float now, uint subjectId);

        private const float BattlefieldChatterIntervalSeconds = 18f;
        private const float BattlefieldChatterInitialDelaySeconds = 12f;
        private const float BattlefieldChatterCandidateLifetimeSeconds = 9f;
        private const int MaxBattlefieldChatterCandidates = 16;

        private readonly Config _config;
        private readonly ChatterSink _queueChatter;
        private readonly System.Action _cancelChatter;
        private readonly Dictionary<uint, FriendlyAircraftState> _friendlyAircraftStates = new Dictionary<uint, FriendlyAircraftState>(64);
        private readonly List<BattlefieldChatterCandidate> _candidates = new List<BattlefieldChatterCandidate>(MaxBattlefieldChatterCandidates);
        private float _nextChatterAt;
        private bool _tracking;

        public BattlefieldChatterFeed(Config config, ChatterSink queueChatter, System.Action cancelChatter)
        {
            _config = config;
            _queueChatter = queueChatter;
            _cancelChatter = cancelChatter;
        }

        public void Detect(Snapshot snapshot)
        {
            if (!_config.BattlefieldChatter.Value)
            {
                Disable();
                return;
            }

            bool initializing = !_tracking;
            if (initializing)
            {
                _tracking = true;
                _nextChatterAt = snapshot.Time + BattlefieldChatterInitialDelaySeconds;
            }

            for (int i = 0; i < snapshot.UnitLifecycles.Count; i++)
            {
                UnitLifecycleInfo unit = snapshot.UnitLifecycles[i];
                if (unit.Id == 0 || !unit.IsFriendly || !unit.IsAircraft || unit.IsPlayer)
                    continue;

                FriendlyAircraftState previous;
                if (!initializing && _friendlyAircraftStates.TryGetValue(unit.Id, out previous))
                {
                    if (!previous.Disabled && unit.Disabled)
                    {
                        AddCandidate(unit.Id, unit.DisplayName, "battlefield_lost", null, snapshot.Time, 4);
                    }
                    else if (!unit.Disabled && !previous.Disabled && previous.Grounded != unit.Grounded)
                    {
                        AddCandidate(unit.Id, unit.DisplayName,
                            unit.Grounded ? "battlefield_landed" : "battlefield_airborne", null, snapshot.Time, 1);
                    }
                }

                _friendlyAircraftStates[unit.Id] = new FriendlyAircraftState
                {
                    Disabled = unit.Disabled,
                    Grounded = unit.Grounded
                };
            }
        }

        public void AddCandidate(
            uint subjectId,
            string subjectName,
            string phraseKey,
            IDictionary<string, string> extraSlots,
            float now,
            int importance)
        {
            if (!_config.BattlefieldChatter.Value || subjectId == 0 || string.IsNullOrWhiteSpace(phraseKey))
                return;

            for (int i = _candidates.Count - 1; i >= 0; i--)
            {
                BattlefieldChatterCandidate existing = _candidates[i];
                if (existing.ExpiresAt < now)
                {
                    _candidates.RemoveAt(i);
                    continue;
                }

                if (existing.SubjectId == subjectId && existing.PhraseKey == phraseKey)
                    return;
            }

            if (_candidates.Count >= MaxBattlefieldChatterCandidates)
                _candidates.RemoveAt(0);

            Dictionary<string, string> slots = new Dictionary<string, string>
            {
                ["type"] = RadioText.SpokenUnitName(subjectName)
            };
            if (extraSlots != null)
            {
                foreach (KeyValuePair<string, string> slot in extraSlots)
                    slots[slot.Key] = slot.Value;
            }

            _candidates.Add(new BattlefieldChatterCandidate
            {
                SubjectId = subjectId,
                PhraseKey = phraseKey,
                Slots = slots,
                CreatedAt = now,
                ExpiresAt = now + BattlefieldChatterCandidateLifetimeSeconds,
                Importance = importance
            });
        }

        /// <summary>Queues the most important fresh candidate, but only while the radio is
        /// otherwise idle — the caller folds gate state, queued traffic, and audio work into
        /// <paramref name="radioIdle"/>, evaluated only after the cheap checks pass.</summary>
        public void TryQueue(float now, System.Func<bool> radioIdle)
        {
            if (!_config.BattlefieldChatter.Value || !_tracking)
                return;

            for (int i = _candidates.Count - 1; i >= 0; i--)
            {
                if (_candidates[i].ExpiresAt < now)
                    _candidates.RemoveAt(i);
            }

            if (_candidates.Count == 0 || now < _nextChatterAt || !radioIdle())
                return;

            int bestIndex = 0;
            for (int i = 1; i < _candidates.Count; i++)
            {
                BattlefieldChatterCandidate candidate = _candidates[i];
                BattlefieldChatterCandidate best = _candidates[bestIndex];
                if (candidate.Importance > best.Importance ||
                    candidate.Importance == best.Importance && candidate.CreatedAt < best.CreatedAt)
                {
                    bestIndex = i;
                }
            }

            BattlefieldChatterCandidate selected = _candidates[bestIndex];
            _candidates.RemoveAt(bestIndex);
            if (_queueChatter(selected.PhraseKey, selected.Slots, now, selected.SubjectId))
                _nextChatterAt = now + BattlefieldChatterIntervalSeconds;
        }

        public void Disable()
        {
            if (!_tracking && _candidates.Count == 0)
                return;

            Reset();
        }

        /// <summary>Unconditional clear (per-aircraft reset): tracking restarts on the next
        /// Detect and playing/queued chatter is cancelled.</summary>
        public void Reset()
        {
            _tracking = false;
            _nextChatterAt = 0f;
            _friendlyAircraftStates.Clear();
            _candidates.Clear();
            _cancelChatter();
        }

        private struct FriendlyAircraftState
        {
            public bool Disabled;
            public bool Grounded;
        }

        private struct BattlefieldChatterCandidate
        {
            public uint SubjectId;
            public string PhraseKey;
            public Dictionary<string, string> Slots;
            public float CreatedAt;
            public float ExpiresAt;
            public int Importance;
        }
    }
}
