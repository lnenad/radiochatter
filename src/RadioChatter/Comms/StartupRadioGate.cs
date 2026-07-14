using System.Collections.Generic;
using BepInEx.Logging;
using RadioChatter.Game;
using UnityEngine;

namespace RadioChatter.Comms
{
    /// <summary>Everything the startup gate needs to read from (or push into) the director.</summary>
    internal interface IStartupGateContext
    {
        bool TakeoffClearanceAnnounced { get; }
        bool AirborneAnnounced { get; }
        bool AwaitingAwacsCheckIn { get; }
        bool SpokenTowerReadbacksEnabled();
        /// <summary>The player looks like they are starting a ground/carrier takeoff.</summary>
        bool IsStartupTakeoffCandidate(Snapshot snapshot);
        bool HasQueuedTransmission(RadioEventType type);
        bool HasQueuedTransmission(RadioRole role);
        bool HasPendingReadback(TowerReadbackKind kind);
        /// <summary>Requeues one held wingman/mission line through the director's dedup rules.</summary>
        void QueueHeldWingmanLine(string text, float now, float availableAt);
        void ReleasePendingGroundSupportHails(float now);
    }

    /// <summary>The mission-start radio choreography: hold AWACS (and early wingman/mission
    /// comms) until the tower takeoff exchange — clearance, player readback, airborne handoff —
    /// has finished, then release the held lines in order. Implicit state machine:
    /// gate active → waiting for takeoff readback → waiting for airborne handoff →
    /// mission-comms grace window → released (or max-hold timeout at any point).</summary>
    internal sealed class StartupRadioGate
    {
        private const float StartupWingmanHoldFallbackSeconds = 10f;
        private const float StartupWingmanTakeoffReadbackMaxHoldSeconds = 65f;
        private const int MaxHeldStartupWingmanLines = 8;
        private const int MaxHeldStartupAwacsLines = 8;
        private const float StartupAwacsMaxHoldSeconds = 90f;
        private const float StartupStateGraceSeconds = 6f;
        private const float StartupMissionCommsGraceSeconds = 15f;

        private readonly Config _config;
        private readonly ManualLogSource _log;
        private readonly TransmissionQueue _queue;
        private readonly IRadioOutput _output;
        private readonly IStartupGateContext _context;

        private readonly List<string> _wingmanTexts = new List<string>(MaxHeldStartupWingmanLines);
        private readonly List<PendingTransmission> _heldAwacs = new List<PendingTransmission>(MaxHeldStartupAwacsLines);
        private bool _wingmanDecisionMade;
        private bool _wingmanHeld;
        private float _wingmanHeldAt;
        private float _takeoffClearanceQueuedAt = float.NaN;
        private bool _gateActive;
        private bool _awacsReleased;
        private float _gateStartedAt = float.NaN;
        private bool _missionCommsSeen;
        private float _takeoffSequenceDoneAt = float.NaN;

        public StartupRadioGate(
            Config config,
            ManualLogSource log,
            TransmissionQueue queue,
            IRadioOutput output,
            IStartupGateContext context)
        {
            _config = config;
            _log = log;
            _queue = queue;
            _output = output;
            _context = context;
        }

        public bool IsActive => _gateActive;

        public void Begin(Snapshot snapshot)
        {
            if (_awacsReleased)
                return;

            if (_gateActive)
            {
                if (float.IsNaN(_gateStartedAt))
                    _gateStartedAt = snapshot.Time;

                return;
            }

            if (_context.TakeoffClearanceAnnounced && IsWaitingForTakeoffReadback(snapshot.Time))
            {
                Start(snapshot.Time);
                return;
            }

            if (_context.IsStartupTakeoffCandidate(snapshot))
                Start(snapshot.Time);
        }

        private void Start(float now)
        {
            _gateActive = true;
            _gateStartedAt = now;
            ParkQueuedAwacs();
            _log.LogDebug("Startup radio gate active: tower/readback, mission comms, then AWACS.");
        }

        /// <summary>The automatic tower takeoff clearance was queued: remember when, and make
        /// sure AWACS is gated (or parked) behind the exchange.</summary>
        public void OnAutomaticTakeoffClearance(float now)
        {
            _takeoffClearanceQueuedAt = now;
            if (!_gateActive && !_awacsReleased)
                Start(now);
            else
                ParkQueuedAwacs();
        }

        /// <summary>A voice-granted clearance drives the same readback timing as the automatic
        /// one, but does not itself start the gate.</summary>
        public void NoteTakeoffClearanceQueued(float now)
        {
            _takeoffClearanceQueuedAt = now;
        }

        /// <summary>Handles an incoming wingman/mission comms line during startup: records that
        /// mission comms exist, and holds the line when the takeoff exchange is still pending.
        /// Returns true when the line was held (the caller queues it otherwise).</summary>
        public bool TryHoldMissionComms(Snapshot snapshot, string text, float now)
        {
            _missionCommsSeen = true;
            if (ShouldHoldWingman(snapshot))
            {
                _wingmanDecisionMade = true;
                HoldWingman(text, now);
                return true;
            }

            _wingmanDecisionMade = true;
            return false;
        }

        private bool ShouldHoldWingman(Snapshot snapshot)
        {
            if (_wingmanHeld)
                return true;

            if (_context.TakeoffClearanceAnnounced && IsWaitingForTakeoffReadback(snapshot.Time))
                return true;

            if (_wingmanDecisionMade)
                return false;

            if (!_config.TakeoffCalls.Value || _context.TakeoffClearanceAnnounced)
                return false;

            return _context.IsStartupTakeoffCandidate(snapshot) || StartupStateStillSettling(snapshot);
        }

        /// <summary>On the first in-mission polls the friendly airbase/HQ data may not be populated
        /// yet, which makes the takeoff-candidate check falsely negative. Treat the startup state as
        /// unsettled until bases appear or the grace period elapses, so the startup radio gate is not
        /// released before the tower sequence had a chance to start.</summary>
        private bool StartupStateStillSettling(Snapshot snapshot)
        {
            if (snapshot.FriendlyAirbases.Count > 0)
                return false;

            return float.IsNaN(_gateStartedAt) ||
                   snapshot.Time - _gateStartedAt < StartupStateGraceSeconds;
        }

        private void HoldWingman(string text, float now)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            if (!_wingmanHeld)
            {
                _wingmanHeld = true;
                _wingmanHeldAt = now;
                _log.LogDebug("Holding startup wingman/mission comms until after takeoff clearance and player readback.");
            }

            if (_wingmanTexts.Count >= MaxHeldStartupWingmanLines)
                _wingmanTexts.RemoveAt(0);

            _wingmanTexts.Add(text);
        }

        public void ReleaseWingmanFallback(Snapshot snapshot)
        {
            if (!_wingmanHeld)
                return;

            if (_context.TakeoffClearanceAnnounced)
            {
                if (IsWaitingForTakeoffReadback(snapshot.Time))
                    return;

                ReleaseHeldWingman(snapshot.Time, snapshot.Time);
                return;
            }

            if (!snapshot.Player.Grounded || snapshot.Time - _wingmanHeldAt >= StartupWingmanHoldFallbackSeconds)
                ReleaseHeldWingman(snapshot.Time, snapshot.Time);
        }

        private void ReleaseHeldWingman(float now, float availableAt)
        {
            if (!_wingmanHeld || _wingmanTexts.Count == 0)
                return;

            for (int i = 0; i < _wingmanTexts.Count; i++)
                _context.QueueHeldWingmanLine(_wingmanTexts[i], now, availableAt + i * 0.25f);

            _wingmanHeld = false;
            _wingmanTexts.Clear();
        }

        private bool IsWaitingForTakeoffReadback(float now)
        {
            if (!_context.TakeoffClearanceAnnounced || float.IsNaN(_takeoffClearanceQueuedAt))
                return false;

            if (now - _takeoffClearanceQueuedAt > StartupWingmanTakeoffReadbackMaxHoldSeconds)
                return false;

            if (_context.HasQueuedTransmission(RadioEventType.TowerTakeoff))
                return true;

            if (_context.SpokenTowerReadbacksEnabled() && _context.HasQueuedTransmission(RadioRole.Tower))
                return true;

            if (_output.HasAudioWork(RadioRole.Tower))
                return true;

            if (_context.SpokenTowerReadbacksEnabled() && _context.HasPendingReadback(TowerReadbackKind.Takeoff))
                return true;

            return _config.PlayerAcknowledgements.Value && _output.HasAudioWork(RadioRole.PlayerTower);
        }

        /// <summary>True while a ground takeoff is underway but Tower has not yet completed the
        /// airborne handoff to AWACS. Taking off from a field puts the player under Tower control;
        /// AWACS stays silent until Tower makes the airborne "switch to {awacs}" call and that
        /// exchange (including the player readback) has finished, so AWACS never steps on it.</summary>
        private bool IsWaitingForAirborneHandoff()
        {
            if (_context.AwaitingAwacsCheckIn)
                return true;

            if (!_context.TakeoffClearanceAnnounced)
                return false;

            if (!_context.AirborneAnnounced)
                return true;

            if (_context.HasQueuedTransmission(RadioEventType.TowerAirborne))
                return true;

            if (_context.SpokenTowerReadbacksEnabled() && _context.HasQueuedTransmission(RadioRole.Tower))
                return true;

            return _output.HasAudioWork(RadioRole.Tower) ||
                   _output.HasAudioWork(RadioRole.PlayerTower) ||
                   (_context.SpokenTowerReadbacksEnabled() && _context.HasPendingReadback(TowerReadbackKind.Handoff));
        }

        public bool ShouldHoldAwacs(float now, bool urgent)
        {
            if (urgent || !_gateActive || _awacsReleased)
                return false;

            return float.IsNaN(_gateStartedAt) ||
                   now - _gateStartedAt <= StartupAwacsMaxHoldSeconds;
        }

        public static bool IsAwacsHoldCandidate(PendingTransmission transmission)
        {
            return transmission.Role == RadioRole.Awacs &&
                   !transmission.BypassStartupHold &&
                   transmission.Type != RadioEventType.MissileThreat;
        }

        public void HoldAwacs(PendingTransmission transmission)
        {
            if (_heldAwacs.Count >= MaxHeldStartupAwacsLines)
                _heldAwacs.RemoveAt(0);

            _heldAwacs.Add(transmission);
            _log.LogDebug($"Holding startup AWACS line until after takeoff sequence: {transmission.Text}");
        }

        /// <summary>Drops held AWACS lines that no longer apply (e.g. contact calls after
        /// Winchester or a mission-role change).</summary>
        public void RemoveHeldAwacs(System.Predicate<PendingTransmission> match)
        {
            _heldAwacs.RemoveAll(match);
        }

        private void ParkQueuedAwacs()
        {
            if (!_gateActive || _awacsReleased)
                return;

            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                PendingTransmission transmission = _queue[i];
                if (!IsAwacsHoldCandidate(transmission))
                    continue;

                _queue.RemoveAt(i);
                HoldAwacs(transmission);
            }
        }

        public void TryRelease(Snapshot snapshot)
        {
            if (!_gateActive || _awacsReleased)
                return;

            float now = snapshot.Time;

            // A ground takeoff hands the player from Tower to AWACS. While the player is still on
            // the ground waiting to roll, keep the max-hold clock from expiring — a long taxi must
            // not release AWACS before the handoff, which cannot happen until the player is airborne.
            if (_context.TakeoffClearanceAnnounced && !_context.AirborneAnnounced && snapshot.Player.Grounded)
                _gateStartedAt = now;

            bool timedOut = !float.IsNaN(_gateStartedAt) &&
                            now - _gateStartedAt > StartupAwacsMaxHoldSeconds;

            if (!timedOut)
            {
                if (!_context.TakeoffClearanceAnnounced &&
                    (_context.IsStartupTakeoffCandidate(snapshot) || StartupStateStillSettling(snapshot)))
                    return;

                // Hold AWACS until Tower has handed the player off with the airborne "switch to
                // {awacs}" call and that exchange has finished playing.
                if (IsWaitingForAirborneHandoff())
                    return;

                if (IsWaitingForTakeoffReadback(now))
                    return;

                if (_wingmanHeld || _context.HasQueuedTransmission(RadioRole.Game) || _output.HasAudioWork(RadioRole.Game))
                    return;

                // The takeoff exchange is done and no mission comm is pending — but the first
                // scripted mission message often arrives a few seconds into the mission. Give
                // it a grace window so AWACS does not talk over comms that are about to start.
                if (!_missionCommsSeen)
                {
                    if (float.IsNaN(_takeoffSequenceDoneAt))
                        _takeoffSequenceDoneAt = now;

                    if (now - _takeoffSequenceDoneAt < StartupMissionCommsGraceSeconds)
                        return;
                }
            }

            for (int i = 0; i < _heldAwacs.Count; i++)
            {
                PendingTransmission transmission = _heldAwacs[i];
                transmission.CreatedAt = now;
                transmission.AvailableAt = now + i * 0.25f;
                transmission.ExpiresAt = transmission.AvailableAt + Mathf.Max(transmission.DisplaySeconds + 10f, 15f);
                _queue.Add(transmission);
            }

            _heldAwacs.Clear();
            _awacsReleased = true;
            _gateActive = false;
            _context.ReleasePendingGroundSupportHails(now);
            _log.LogDebug(timedOut
                ? "Startup radio gate released (timeout)."
                : "Startup radio gate released (takeoff sequence complete or not applicable).");
        }

        /// <summary>Per-aircraft reset: the gate re-arms for the next takeoff sequence.</summary>
        public void Reset()
        {
            _wingmanDecisionMade = false;
            _wingmanHeld = false;
            _wingmanTexts.Clear();
            _wingmanHeldAt = 0f;
            _takeoffClearanceQueuedAt = float.NaN;
            _gateActive = true;
            _awacsReleased = false;
            _gateStartedAt = float.NaN;
            _missionCommsSeen = false;
            _takeoffSequenceDoneAt = float.NaN;
            _heldAwacs.Clear();
        }
    }
}
