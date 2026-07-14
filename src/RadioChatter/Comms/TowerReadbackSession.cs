using System.Collections.Generic;
using BepInEx.Logging;

namespace RadioChatter.Comms
{
    /// <summary>The state a failed (abandoned) readback forces back on the director's flight
    /// sequence, returned explicitly so the coupling is visible at the call site.</summary>
    internal enum FailedReadbackEffect
    {
        None,
        /// <summary>Landing clearance withdrawn — the approach/final sequence may re-announce.</summary>
        ReopenLandingSequence,
        /// <summary>Handoff unconfirmed — stop waiting for the AWACS check-in.</summary>
        CancelAwacsCheckIn
    }

    /// <summary>Tracks tower clearances awaiting a spoken player readback: matching attempts,
    /// re-prompting after the response window, and abandoning the clearance after too many
    /// failures. The caller gates on the spoken-readbacks config and applies any
    /// FailedReadbackEffect to its flight state.</summary>
    internal sealed class TowerReadbackSession
    {
        public delegate void VoiceResponseSink(RadioRole role, string phraseKey, Dictionary<string, string> slots, float now);

        private const float TowerReadbackResponseSeconds = 10f;
        private const int TowerReadbackMaxAttempts = 2;

        private readonly Config _config;
        private readonly ManualLogSource _log;
        private readonly IRadioOutput _output;
        private readonly VoiceResponseSink _queueVoiceResponse;
        private readonly System.Action<FailedReadbackEffect> _applyFailedReadbackEffect;
        private readonly List<PendingTowerReadback> _pending = new List<PendingTowerReadback>(3);

        public TowerReadbackSession(
            Config config,
            ManualLogSource log,
            IRadioOutput output,
            VoiceResponseSink queueVoiceResponse,
            System.Action<FailedReadbackEffect> applyFailedReadbackEffect)
        {
            _config = config;
            _log = log;
            _output = output;
            _queueVoiceResponse = queueVoiceResponse;
            _applyFailedReadbackEffect = applyFailedReadbackEffect;
        }

        public int Count => _pending.Count;

        public void Add(TowerReadbackExpectation expectation, float now)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].Expectation.Kind == expectation.Kind)
                    _pending.RemoveAt(i);
            }

            _pending.Add(new PendingTowerReadback
            {
                Expectation = expectation,
                AwaitingSince = now
            });
        }

        public bool Has(TowerReadbackKind kind)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].Expectation.Kind == kind)
                    return true;
            }

            return false;
        }

        public void Clear()
        {
            _pending.Clear();
        }

        public bool TryHandle(string text, VoiceIntent intent, float now)
        {
            if (_pending.Count == 0)
                return false;

            // "Say again" asks Tower to repeat the instruction; it is not an attempted readback
            // and must not consume one of the player's allowed attempts.
            if (intent.Kind == VoiceIntentKind.RequestRepeatLast)
                return false;

            for (int i = 0; i < _pending.Count; i++)
            {
                TowerReadbackExpectation expectation = _pending[i].Expectation;
                if (!TowerReadbackMatcher.IsMatch(text, expectation))
                    continue;

                _pending.RemoveAt(i);
                _output.ClearReadbackPrompt(expectation.Kind);
                _log.LogInfo($"Accepted spoken Tower {expectation.Kind.ToString().ToLowerInvariant()} readback: \"{text}\"");
                return true;
            }

            // A command explicitly sent to AWACS remains an AWACS command even while Tower is
            // waiting. Otherwise only readback-like speech (or a Tower-addressed transmission)
            // is intercepted, so unrelated unaddressed commands retain their existing behavior.
            if (intent.Station == VoiceStation.Awacs)
                return false;

            TowerReadbackExpectation pending = _pending[0].Expectation;
            if (intent.Station != VoiceStation.Tower && !TowerReadbackMatcher.LooksLikeAttempt(text, pending))
                return false;

            HandleFailed(0, now, true, text);
            return true;
        }

        /// <summary>Ages the response windows and re-prompts (or abandons) overdue readbacks.
        /// While Tower audio is queued/playing the window is refreshed instead of consumed.</summary>
        public void PromptForMissing(float now, bool towerBusy)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingTowerReadback pending = _pending[i];
                pending.AwaitingSince = TowerReadbackTiming.RefreshAwaitingSince(
                    pending.AwaitingSince, now, towerBusy);
                if (towerBusy)
                {
                    _pending[i] = pending;
                    continue;
                }

                if (!TowerReadbackTiming.HasTimedOut(
                        pending.AwaitingSince, now, TowerReadbackResponseSeconds))
                {
                    continue;
                }

                HandleFailed(i, now, false, null);
            }
        }

        private void HandleFailed(int index, float now, bool incorrect, string transcript)
        {
            PendingTowerReadback pending = _pending[index];
            TowerReadbackExpectation expectation = pending.Expectation;
            pending.FailedAttempts++;

            if (pending.FailedAttempts >= TowerReadbackMaxAttempts)
            {
                _pending.RemoveAt(index);
                _output.ClearReadbackPrompt(expectation.Kind);
                _applyFailedReadbackEffect(EffectFor(expectation.Kind));

                Dictionary<string, string> finalSlots = VoiceSlots(expectation.Callsign);
                finalSlots["outcome"] = FailedReadbackOutcome(expectation.Kind);
                _queueVoiceResponse(RadioRole.Tower, "tower_readback_failed", finalSlots, now);

                _log.LogInfo($"Tower stopped waiting for {expectation.Kind.ToString().ToLowerInvariant()} readback after {TowerReadbackMaxAttempts} failed attempts.");
                return;
            }

            Dictionary<string, string> slots = VoiceSlots(expectation.Callsign);
            if (incorrect)
            {
                _queueVoiceResponse(RadioRole.Tower, "tower_readback_incorrect", slots, now);
                _log.LogInfo($"Rejected incomplete Tower {expectation.Kind.ToString().ToLowerInvariant()} readback: \"{transcript}\"");
            }
            else
            {
                slots["instruction"] = ReadbackInstruction(expectation.Kind);
                _queueVoiceResponse(RadioRole.Tower, "tower_readback_missing", slots, now);
                _log.LogInfo($"Tower requested missing {expectation.Kind.ToString().ToLowerInvariant()} readback from {expectation.Callsign}.");
            }

            pending.AwaitingSince = now;
            _pending[index] = pending;
        }

        private static FailedReadbackEffect EffectFor(TowerReadbackKind kind)
        {
            switch (kind)
            {
                case TowerReadbackKind.Landing:
                    return FailedReadbackEffect.ReopenLandingSequence;
                case TowerReadbackKind.Handoff:
                    return FailedReadbackEffect.CancelAwacsCheckIn;
                default:
                    return FailedReadbackEffect.None;
            }
        }

        private static string FailedReadbackOutcome(TowerReadbackKind kind)
        {
            switch (kind)
            {
                case TowerReadbackKind.Takeoff:
                    return "cancel takeoff clearance, hold position";
                case TowerReadbackKind.Landing:
                    return "go around";
                case TowerReadbackKind.Handoff:
                    return "handoff unconfirmed, radio failure suspected";
                default:
                    return "radio failure suspected";
            }
        }

        private static string ReadbackInstruction(TowerReadbackKind kind)
        {
            switch (kind)
            {
                case TowerReadbackKind.Takeoff:
                    return "the takeoff clearance";
                case TowerReadbackKind.Landing:
                    return "the landing clearance";
                case TowerReadbackKind.Handoff:
                    return "the handoff instruction";
                default:
                    return "the instruction";
            }
        }

        private Dictionary<string, string> VoiceSlots(string callsign)
        {
            return new Dictionary<string, string>
            {
                ["callsign"] = callsign,
                ["awacs"] = _config.AwacsCallsign.Value
            };
        }

        private struct PendingTowerReadback
        {
            public TowerReadbackExpectation Expectation;
            public float AwaitingSince;
            public int FailedAttempts;
        }
    }
}
