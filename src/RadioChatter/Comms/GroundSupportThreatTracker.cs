using System.Collections.Generic;

namespace RadioChatter.Comms
{
    /// <summary>Tracks the distinct hostile ground units responsible for one spatial support
    /// request. A request is complete only after the last known attacker is destroyed.</summary>
    internal sealed class GroundSupportThreatTracker
    {
        private readonly HashSet<uint> _attackerIds = new HashSet<uint>();

        public int Count => _attackerIds.Count;

        public void Track(uint attackerId)
        {
            if (attackerId != 0)
                _attackerIds.Add(attackerId);
        }

        public void Restart(uint attackerId)
        {
            _attackerIds.Clear();
            Track(attackerId);
        }

        public bool MarkDestroyed(uint attackerId)
        {
            return attackerId != 0 && _attackerIds.Remove(attackerId) && _attackerIds.Count == 0;
        }
    }

    internal static class GroundSupportHailGate
    {
        public const float GlobalCooldownSeconds = 20f;

        /// <summary>Unsolicited support requests are for airborne players and must not jump ahead
        /// of Tower/AWACS startup sequencing. A nullable stable state is held until confirmed.</summary>
        public static bool ShouldHold(bool? stableGrounded, bool playerGrounded, bool startupAwacsHold)
        {
            return playerGrounded || stableGrounded != false || startupAwacsHold;
        }

        public static float NextAllowedAt(float transmissionTime)
        {
            return transmissionTime + GlobalCooldownSeconds;
        }

    }

    /// <summary>Enforces the ground-support interval at the point where audio actually starts.
    /// Director dispatch times can precede playback while TTS is loading or another clip owns the
    /// audio lane, so they cannot by themselves guarantee player-heard spacing.</summary>
    internal sealed class GroundSupportPlaybackGate
    {
        private float _nextAllowedAt = float.NegativeInfinity;

        public bool CanStart(float now)
        {
            return now >= _nextAllowedAt;
        }

        public void MarkStarted(float now)
        {
            _nextAllowedAt = GroundSupportHailGate.NextAllowedAt(now);
        }
    }
}
