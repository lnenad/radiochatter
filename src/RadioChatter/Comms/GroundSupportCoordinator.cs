using System.Collections.Generic;
using BepInEx.Logging;
using RadioChatter.Game;
using RadioChatter.Speech;
using UnityEngine;

namespace RadioChatter.Comms
{
    /// <summary>What the ground-support coordinator needs from the director: phrase-rendered
    /// queueing (with the director's dedup/startup-hold rules) and a few read-only queries.</summary>
    internal interface IGroundSupportCalloutSink
    {
        /// <summary>Queues a phrase-rendered callout (cooldown 0, not urgent).</summary>
        bool QueueCallout(
            RadioRole role,
            RadioEventType type,
            string phraseKey,
            IDictionary<string, string> slots,
            float now,
            int priority,
            float displaySeconds,
            uint subjectId,
            float duplicateWindowSeconds,
            bool bypassStartupHold);

        void QueueVoiceResponse(RadioRole role, RadioEventType type, string phraseKey, IDictionary<string, string> slots, float now);
        void CancelVectorCalls();
        bool IsKnownDestroyed(uint subjectId);
        bool ShouldHoldStartupAwacs(float now);
        bool SuppressGroundSupportHails();
        bool? StableGrounded { get; }
    }

    /// <summary>One friendly ground formation that has requested (or may request) support.</summary>
    internal sealed class GroundSupportGroup
    {
        public uint Id;
        public uint AnchorUnitId;
        public string Callsign;
        public GPos Position;
        public readonly HashSet<uint> MemberIds = new HashSet<uint>();
        public readonly GroundSupportThreatTracker Threats = new GroundSupportThreatTracker();
        public float LastAttackAt;
        public float LastSeenAliveAt;
        public float LastHailAt;
        public float NextHailAt;
        public float DismissedAt;
        public bool Pending;
        public bool Accepted;
        public bool Dismissed;
        public bool Resolved;
        public bool ThreatResolutionPending;
        public bool Closed;
    }

    /// <summary>Owns the ground-support request lifecycle: grouping attacked friendly ground
    /// units, hailing the player, accept/decline/switch via voice, vectors to the active or a
    /// named group, and resolving/closing groups as threats die or units are lost. Shares the
    /// director's transmission queue and audio output; everything phrase-rendered goes back
    /// through the sink so the director's dedup and startup-hold rules still apply.</summary>
    internal sealed class GroundSupportCoordinator
    {
        private const int GroundSupportHailPriority = 50;
        private const float GroundSupportLostGraceSeconds = 6f;
        private const float GroundSupportDuplicateSeconds = 10f;

        private static readonly string[] GroundSupportCallsigns =
        {
            "Anvil", "Hammer", "Bison", "Ranger", "Sentinel", "Nomad"
        };

        private readonly Config _config;
        private readonly ManualLogSource _log;
        private readonly TransmissionQueue _queue;
        private readonly IRadioOutput _output;
        private readonly IGroundSupportCalloutSink _sink;

        private readonly List<GroundSupportGroup> _groups = new List<GroundSupportGroup>(8);
        private readonly Dictionary<uint, GroundSupportGroup> _groupsByUnit = new Dictionary<uint, GroundSupportGroup>(32);
        private GroundSupportGroup _activeGroup;
        private int _nextCallsign;
        private float _nextHailAllowedAt;

        public GroundSupportCoordinator(
            Config config,
            ManualLogSource log,
            TransmissionQueue queue,
            IRadioOutput output,
            IGroundSupportCalloutSink sink)
        {
            _config = config;
            _log = log;
            _queue = queue;
            _output = output;
            _sink = sink;
        }

        public bool Enabled => _config.GroundSupportRequests.Value && _config.VoiceCommandsEnabled.Value;

        public void HandleGroundUnitUnderAttack(Snapshot snapshot, RadioEvent evt, float now)
        {
            if (!Enabled || evt.SubjectId == 0)
                return;

            // A fire/damage callback can race a destruction callback in the same frame. Never
            // open or refresh a task for an attacker already known to be destroyed.
            if (evt.AttackerId != 0 && _sink.IsKnownDestroyed(evt.AttackerId))
                return;

            GroundSupportGroup group;
            if (!_groupsByUnit.TryGetValue(evt.SubjectId, out group) || group.Closed)
                group = FindGroupNear(evt.Position);

            if (group == null)
            {
                group = new GroundSupportGroup
                {
                    Id = evt.SubjectId,
                    AnchorUnitId = evt.SubjectId,
                    Callsign = NextGroundSupportCallsign(),
                    Position = evt.Position,
                    Pending = true,
                    LastAttackAt = now,
                    LastSeenAliveAt = now
                };
                group.MemberIds.Add(evt.SubjectId);
                group.Threats.Track(evt.AttackerId);
                _groups.Add(group);
                _groupsByUnit[evt.SubjectId] = group;

                QueueHail(snapshot, group, now);
                _log.LogInfo($"Ground support request opened: {group.Callsign} ({evt.SubjectName}) group #{group.Id}.");
                return;
            }

            group.MemberIds.Add(evt.SubjectId);
            _groupsByUnit[evt.SubjectId] = group;
            float previousAttackAt = group.LastAttackAt;
            bool reopening = group.Dismissed &&
                             (group.Resolved ||
                              (now - group.DismissedAt >= _config.GroundSupportRepeatSeconds.Value &&
                               now - previousAttackAt >= _config.GroundSupportRepeatSeconds.Value));
            if (reopening)
            {
                group.Threats.Restart(evt.AttackerId);
                group.Resolved = false;
            }
            else
                group.Threats.Track(evt.AttackerId);

            group.LastAttackAt = now;
            group.LastSeenAliveAt = now;
            if (group.AnchorUnitId == evt.SubjectId || group.AnchorUnitId == 0)
                group.Position = evt.Position;

            // A declined/switched-away request stays quiet while attacks continue. Only a
            // genuinely new engagement after a full quiet interval may reuse the callsign.
            if (reopening)
            {
                group.Dismissed = false;
                group.Pending = true;
                QueueHail(snapshot, group, now);
                _log.LogInfo($"Ground support request reopened: {group.Callsign}.");
            }
        }

        private GroundSupportGroup FindGroupNear(GPos position)
        {
            float radius = _config.GroundSupportGroupRadiusM.Value;
            GroundSupportGroup nearest = null;
            float bestDistance = float.PositiveInfinity;

            for (int i = 0; i < _groups.Count; i++)
            {
                GroundSupportGroup candidate = _groups[i];
                if (candidate.Closed)
                    continue;

                float distance = GPos.Distance2D(candidate.Position, position);
                if (distance <= radius && distance < bestDistance)
                {
                    nearest = candidate;
                    bestDistance = distance;
                }
            }

            return nearest;
        }

        private string NextGroundSupportCallsign()
        {
            int index = _nextCallsign++;
            string stem = GroundSupportCallsigns[index % GroundSupportCallsigns.Length];
            int number = index / GroundSupportCallsigns.Length + 1;
            return stem + " " + number;
        }

        private bool QueueHail(Snapshot snapshot, GroundSupportGroup group, float now)
        {
            if (group == null || group.Closed || group.Accepted || group.Dismissed)
                return false;

            if (now < _nextHailAllowedAt)
            {
                group.NextHailAt = Mathf.Max(group.NextHailAt, _nextHailAllowedAt);
                return false;
            }

            if (GroundSupportHailGate.ShouldHold(
                    _sink.StableGrounded,
                    snapshot.Player.Grounded,
                    _sink.ShouldHoldStartupAwacs(now)))
            {
                group.NextHailAt = now + _config.GroundSupportRepeatSeconds.Value;
                return false;
            }

            if (_sink.SuppressGroundSupportHails())
            {
                group.NextHailAt = now + _config.GroundSupportRepeatSeconds.Value;
                return false;
            }

            bool queued = _sink.QueueCallout(RadioRole.Game, RadioEventType.GroundSupportHail,
                "ground_support_hail", Slots("ground_callsign", group.Callsign),
                now, GroundSupportHailPriority, 7f,
                subjectId: group.Id,
                duplicateWindowSeconds: 0f,
                bypassStartupHold: false);

            if (queued)
            {
                StartHailCooldown(now);
                group.LastHailAt = now;
                group.NextHailAt = now + _config.GroundSupportRepeatSeconds.Value;
            }

            return queued;
        }

        /// <summary>Blocks further audible hails for the gate interval; called when a hail is
        /// queued here or actually transmitted by the director.</summary>
        public void StartHailCooldown(float now)
        {
            _nextHailAllowedAt = Mathf.Max(_nextHailAllowedAt, GroundSupportHailGate.NextAllowedAt(now));

            for (int i = 0; i < _groups.Count; i++)
            {
                GroundSupportGroup group = _groups[i];
                if (group.Pending && !group.Closed && !group.Dismissed)
                    group.NextHailAt = Mathf.Max(group.NextHailAt, _nextHailAllowedAt);
            }
        }

        public void Update(Snapshot snapshot)
        {
            if (!Enabled)
            {
                Disable();
                return;
            }

            float now = snapshot.Time;
            float radius = _config.GroundSupportGroupRadiusM.Value;

            for (int g = 0; g < _groups.Count; g++)
            {
                GroundSupportGroup group = _groups[g];
                if (group.Closed)
                    continue;

                bool alive = false;
                bool anchorUpdated = false;
                for (int i = 0; i < snapshot.UnitLifecycles.Count; i++)
                {
                    UnitLifecycleInfo unit = snapshot.UnitLifecycles[i];
                    if (!unit.IsFriendly || !unit.IsGroundVehicle || unit.Disabled)
                        continue;

                    bool member = group.MemberIds.Contains(unit.Id);
                    if (!member && GPos.Distance2D(group.Position, unit.Position) > radius)
                        continue;

                    alive = true;
                    if (unit.Id == group.AnchorUnitId)
                    {
                        group.Position = unit.Position;
                        anchorUpdated = true;
                    }
                    else if (!anchorUpdated && member)
                    {
                        group.Position = unit.Position;
                    }
                }

                if (alive)
                {
                    group.LastSeenAliveAt = now;
                }
                else if (now - group.LastSeenAliveAt >= GroundSupportLostGraceSeconds)
                {
                    CloseGroup(group, now);
                    continue;
                }

                if (group.Pending && now >= group.NextHailAt)
                    QueueHail(snapshot, group, now);
            }
        }

        private void CloseGroup(GroundSupportGroup group, float now)
        {
            group.Closed = true;
            group.Pending = false;
            group.Accepted = false;
            RemoveQueuedGroundSupport(group.Id);
            CancelHailAudioAndRescheduleOthers(group, now);

            if (_activeGroup != group)
                return;

            _activeGroup = null;
            CancelTransmissions(RadioEventType.GroundSupportVector);
            _sink.QueueCallout(RadioRole.Awacs, RadioEventType.GroundSupportCanceled,
                "awacs_ground_support_lost", Slots(
                    "callsign", _config.PlayerCallsign.Value,
                    "awacs", _config.AwacsCallsign.Value,
                    "ground_callsign", group.Callsign),
                now, CommsDirector.VoiceResponsePriority, 4f,
                subjectId: group.Id,
                duplicateWindowSeconds: GroundSupportDuplicateSeconds,
                bypassStartupHold: true);
            _log.LogInfo($"Ground support task canceled: {group.Callsign} is no longer active.");
        }

        public void HandleAttackerDestroyed(uint attackerId)
        {
            if (attackerId == 0 || _groups.Count == 0)
                return;

            for (int i = 0; i < _groups.Count; i++)
            {
                GroundSupportGroup group = _groups[i];
                if (group.Closed || !group.Threats.MarkDestroyed(attackerId))
                    continue;

                // More attack events can follow the destruction event in this same update.
                // Finalize only after the whole batch has had a chance to add another attacker.
                group.ThreatResolutionPending = true;
            }
        }

        public void ResolveDestroyedThreats(float now)
        {
            for (int i = 0; i < _groups.Count; i++)
            {
                GroundSupportGroup group = _groups[i];
                if (!group.ThreatResolutionPending)
                    continue;

                group.ThreatResolutionPending = false;
                if (!group.Closed && group.Threats.Count == 0)
                    ResolveThreat(group, now);
            }
        }

        private void ResolveThreat(GroundSupportGroup group, float now)
        {
            bool playerWasSupporting = group.Accepted && _activeGroup == group;

            // Keep the mission-persistent group and callsign available for a genuinely new
            // engagement, but clear the current request/secondary immediately.
            group.Pending = false;
            group.Accepted = false;
            group.Dismissed = true;
            group.DismissedAt = now;
            group.Resolved = true;
            RemoveQueuedGroundSupport(group.Id);
            CancelHailAudioAndRescheduleOthers(group, now);
            _output.StopTransmissions(RadioEventType.GroundSupportAcknowledged);

            if (_activeGroup == group)
            {
                _activeGroup = null;
                CancelTransmissions(RadioEventType.GroundSupportVector);
            }

            if (playerWasSupporting)
            {
                _sink.QueueCallout(RadioRole.Game, RadioEventType.GroundSupportCompleted,
                    "ground_support_completed", Slots(
                        "callsign", _config.PlayerCallsign.Value,
                        "ground_callsign", group.Callsign),
                    now, CommsDirector.VoiceResponsePriority, 4f,
                    subjectId: group.Id,
                    duplicateWindowSeconds: 0f,
                    bypassStartupHold: true);
                _log.LogInfo($"Ground support task completed: {group.Callsign}; last tracked attacker destroyed.");
            }
            else
            {
                _log.LogInfo($"Ground support request resolved before acceptance: {group.Callsign}; last tracked attacker destroyed.");
            }
        }

        private bool QueueVector(Snapshot snapshot, GroundSupportGroup group, string playerCallsign, float now)
        {
            if (group == null || group.Closed)
                return false;

            return _sink.QueueCallout(RadioRole.Awacs, RadioEventType.GroundSupportVector,
                "awacs_ground_support_vector", Slots(
                    "callsign", playerCallsign,
                    "awacs", _config.AwacsCallsign.Value,
                    "ground_callsign", group.Callsign,
                    "bearing", NumberSpeech.Bearing(GPos.Bearing(snapshot.Player.Position, group.Position)),
                    "range", RadioText.FormatRange(GPos.Distance2D(snapshot.Player.Position, group.Position), snapshot.Units)),
                now, CommsDirector.VoiceResponsePriority, 5f,
                subjectId: group.Id,
                duplicateWindowSeconds: GroundSupportDuplicateSeconds,
                bypassStartupHold: true);
        }

        private bool QueueAcknowledgement(Snapshot snapshot, GroundSupportGroup group, string playerCallsign, float now, bool followup)
        {
            if (group == null || group.Closed)
                return false;

            Dictionary<string, string> slots = Slots(
                "callsign", playerCallsign,
                "ground_callsign", group.Callsign);
            if (!followup)
            {
                slots["bearing"] = NumberSpeech.Bearing(GPos.Bearing(snapshot.Player.Position, group.Position));
                slots["range"] = RadioText.FormatRange(
                    GPos.Distance2D(snapshot.Player.Position, group.Position), snapshot.Units);
            }

            return _sink.QueueCallout(RadioRole.Game, RadioEventType.GroundSupportAcknowledged,
                followup ? "ground_support_followup_ack" : "ground_support_acknowledged", slots,
                now, CommsDirector.VoiceResponsePriority, 4f,
                subjectId: group.Id,
                duplicateWindowSeconds: 0f,
                bypassStartupHold: true);
        }

        private void RemoveQueuedGroundSupport(uint groupId)
        {
            _queue.RemoveAll(item =>
                item.SubjectId == groupId &&
                (item.Type == RadioEventType.GroundSupportHail ||
                 item.Type == RadioEventType.GroundSupportAcknowledged ||
                 item.Type == RadioEventType.GroundSupportVector ||
                 item.Type == RadioEventType.GroundSupportDeclined ||
                 item.Type == RadioEventType.GroundSupportCompleted));
        }

        private void CancelHailAudioAndRescheduleOthers(GroundSupportGroup excluded, float now)
        {
            CancelTransmissions(RadioEventType.GroundSupportHail);

            // StopTransmissions invalidates same-frame TTS requests of that type. Requeue other
            // pending groups on a later poll, after the audio clock has advanced.
            float retryAt = now + Mathf.Max(0.25f, _config.PollIntervalSeconds.Value);
            for (int i = 0; i < _groups.Count; i++)
            {
                GroundSupportGroup group = _groups[i];
                if (group != excluded && group.Pending && !group.Closed && !group.Dismissed)
                    group.NextHailAt = Mathf.Min(group.NextHailAt, retryAt);
            }
        }

        public void Disable()
        {
            _nextHailAllowedAt = 0f;
            if (_groups.Count == 0 && _activeGroup == null)
                return;

            _groups.Clear();
            _groupsByUnit.Clear();
            _activeGroup = null;
            _nextCallsign = 0;
            _queue.RemoveAll(item =>
                item.Type == RadioEventType.GroundSupportHail ||
                item.Type == RadioEventType.GroundSupportAcknowledged ||
                item.Type == RadioEventType.GroundSupportVector ||
                item.Type == RadioEventType.GroundSupportDeclined ||
                item.Type == RadioEventType.GroundSupportCompleted ||
                item.Type == RadioEventType.GroundSupportCanceled);
            _output.StopTransmissions(RadioEventType.GroundSupportHail);
            _output.StopTransmissions(RadioEventType.GroundSupportAcknowledged);
            _output.StopTransmissions(RadioEventType.GroundSupportVector);
            _output.StopTransmissions(RadioEventType.GroundSupportDeclined);
            _output.StopTransmissions(RadioEventType.GroundSupportCompleted);
            _output.StopTransmissions(RadioEventType.GroundSupportCanceled);
        }

        public void StopHails()
        {
            CancelTransmissions(RadioEventType.GroundSupportHail);
        }

        /// <summary>Lets every pending group hail again on the next poll. The caller decides
        /// whether flight state permits it.</summary>
        public void ReleasePendingHails(float now)
        {
            for (int i = 0; i < _groups.Count; i++)
            {
                GroundSupportGroup group = _groups[i];
                if (group.Pending && !group.Closed && !group.Dismissed)
                    group.NextHailAt = Mathf.Min(group.NextHailAt, now);
            }
        }

        /// <summary>Per-aircraft reset: hail throttles restart, but groups and callsigns are
        /// mission-persistent and survive.</summary>
        public void ResetHailTimers()
        {
            _nextHailAllowedAt = 0f;
            for (int i = 0; i < _groups.Count; i++)
            {
                if (_groups[i].Pending)
                    _groups[i].NextHailAt = 0f;
            }
        }

        public bool TryHandleTransmission(Snapshot snapshot, string text, float now)
        {
            if (!Enabled)
                return false;

            bool decline = VoiceIntentParser.IsGroundSupportDecline(text);
            if (!decline && !VoiceIntentParser.ContainsSpokenCallsign(text, _config.PlayerCallsign.Value))
                return false;

            GroundSupportGroup selected = null;
            for (int i = 0; i < _groups.Count; i++)
            {
                GroundSupportGroup candidate = _groups[i];
                if (candidate.Closed || candidate.Dismissed || (!candidate.Pending && !candidate.Accepted))
                    continue;

                if (VoiceIntentParser.ContainsSpokenCallsign(text, candidate.Callsign))
                {
                    selected = candidate;
                    break;
                }
            }

            // The numeric suffix may be omitted while the stem is unique among available
            // groups: "Anvil, this is Broadsword one-one, inbound" selects Anvil 1. Once two
            // Anvil groups exist, the player must say the full numbered callsign.
            if (selected == null)
            {
                int stemMatches = 0;
                for (int i = 0; i < _groups.Count; i++)
                {
                    GroundSupportGroup candidate = _groups[i];
                    if (candidate.Closed || candidate.Dismissed || (!candidate.Pending && !candidate.Accepted))
                        continue;

                    string stem = CallsignStem(candidate.Callsign);
                    if (VoiceIntentParser.ContainsSpokenCallsign(text, stem))
                    {
                        selected = candidate;
                        stemMatches++;
                    }
                }

                if (stemMatches != 1)
                    selected = null;
            }

            if (selected == null)
                return false;

            if (decline)
            {
                Decline(selected, now);
                return true;
            }

            if (selected.Accepted && _activeGroup == selected)
            {
                QueueAcknowledgement(snapshot, selected, _config.PlayerCallsign.Value, now, true);
                _log.LogInfo($"Ground support follow-up addressed to {selected.Callsign}.");
                return true;
            }

            if (_activeGroup != null && _activeGroup != selected)
            {
                GroundSupportGroup previous = _activeGroup;
                previous.Accepted = false;
                previous.Pending = false;
                previous.Dismissed = true;
                previous.DismissedAt = now;
                previous.Resolved = false;
                RemoveQueuedGroundSupport(previous.Id);
                _log.LogInfo($"Ground support task switched from {previous.Callsign} to {selected.Callsign}.");
            }

            _activeGroup = selected;
            selected.Pending = false;
            selected.Accepted = true;
            selected.Dismissed = false;
            selected.Resolved = false;
            RemoveQueuedGroundSupport(selected.Id);
            CancelHailAudioAndRescheduleOthers(selected, now);

            // Future player-requested vectors now resolve to this group until another hail is accepted.
            _sink.CancelVectorCalls();
            CancelTransmissions(RadioEventType.GroundSupportVector);
            QueueAcknowledgement(snapshot, selected, _config.PlayerCallsign.Value, now, false);
            _log.LogInfo($"Ground support request accepted: {selected.Callsign}.");
            return true;
        }

        private void Decline(GroundSupportGroup group, float now)
        {
            bool wasActive = _activeGroup == group;
            group.Pending = false;
            group.Accepted = false;
            group.Dismissed = true;
            group.DismissedAt = now;
            group.Resolved = false;

            RemoveQueuedGroundSupport(group.Id);
            CancelHailAudioAndRescheduleOthers(group, now);

            if (wasActive)
            {
                _activeGroup = null;
                CancelTransmissions(RadioEventType.GroundSupportVector);
            }

            _sink.QueueCallout(RadioRole.Game, RadioEventType.GroundSupportDeclined,
                "ground_support_declined", Slots("ground_callsign", group.Callsign),
                now, CommsDirector.VoiceResponsePriority, 4f,
                subjectId: group.Id,
                duplicateWindowSeconds: 0f,
                bypassStartupHold: true);
            _log.LogInfo($"Ground support request declined: {group.Callsign}.");
        }

        private static string CallsignStem(string callsign)
        {
            if (string.IsNullOrEmpty(callsign))
                return string.Empty;

            int separator = callsign.IndexOf(' ');
            return separator > 0 ? callsign.Substring(0, separator) : callsign;
        }

        public bool TryRespondNamedVector(Snapshot snapshot, string text, float now, string callsign)
        {
            GroundSupportGroup group;
            bool mentioned;
            bool ambiguous;
            ResolveNamedGroup(text, out group, out mentioned, out ambiguous);
            if (!mentioned)
                return false;

            if (ambiguous)
            {
                _sink.QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse,
                    "awacs_ground_support_ambiguous", VoiceSlots(callsign), now);
            }
            else if (group == null)
            {
                _sink.QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse,
                    "awacs_no_ground_support", VoiceSlots(callsign), now);
            }
            else
            {
                QueueVector(snapshot, group, callsign, now);
            }

            return true;
        }

        public void RespondVector(Snapshot snapshot, string text, float now, string callsign)
        {
            if (TryRespondNamedVector(snapshot, text, now, callsign))
                return;

            GroundSupportGroup group = null;
            if (VoiceIntentParser.ContainsSpokenCallsign(text, "secondary"))
            {
                if (IsAvailableGroup(_activeGroup))
                    group = _activeGroup;
            }
            else if (VoiceIntentParser.ContainsSpokenCallsign(text, "last support request"))
            {
                // "last support request" means the group whose hail most recently reached the
                // radio queue, whether it is still pending or has already been accepted.
                group = MostRecentRequest();
            }
            else
            {
                _sink.QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse,
                    "awacs_ground_support_reference_required", VoiceSlots(callsign), now);
                return;
            }

            if (group != null)
            {
                QueueVector(snapshot, group, callsign, now);
            }
            else
            {
                _sink.QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse,
                    "awacs_no_ground_support", VoiceSlots(callsign), now);
            }
        }

        private void ResolveNamedGroup(string text, out GroundSupportGroup selected, out bool mentioned, out bool ambiguous)
        {
            selected = null;
            mentioned = false;
            ambiguous = false;

            // Prefer a full numbered reference. Requiring "to" prevents a player callsign such
            // as Ranger 1-1 from being mistaken for the destination of an ordinary vector call.
            for (int i = 0; i < _groups.Count; i++)
            {
                GroundSupportGroup candidate = _groups[i];
                if (!VoiceIntentParser.ContainsSpokenCallsign(text, "to " + candidate.Callsign))
                    continue;

                mentioned = true;
                if (IsAvailableGroup(candidate))
                    selected = candidate;
                return;
            }

            int matches = 0;
            for (int stemIndex = 0; stemIndex < GroundSupportCallsigns.Length; stemIndex++)
            {
                string stem = GroundSupportCallsigns[stemIndex];
                if (!VoiceIntentParser.ContainsSpokenCallsign(text, "to " + stem))
                    continue;

                mentioned = true;
                for (int i = 0; i < _groups.Count; i++)
                {
                    GroundSupportGroup candidate = _groups[i];
                    if (!IsAvailableGroup(candidate) ||
                        CallsignStem(candidate.Callsign) != stem)
                    {
                        continue;
                    }

                    selected = candidate;
                    matches++;
                }
            }

            if (matches > 1)
            {
                selected = null;
                ambiguous = true;
            }
        }

        private GroundSupportGroup MostRecentRequest()
        {
            GroundSupportGroup newest = null;
            float newestHail = float.NegativeInfinity;
            for (int i = 0; i < _groups.Count; i++)
            {
                GroundSupportGroup candidate = _groups[i];
                if (!IsAvailableGroup(candidate) || candidate.LastHailAt < newestHail)
                    continue;

                newest = candidate;
                newestHail = candidate.LastHailAt;
            }

            return newest;
        }

        private static bool IsAvailableGroup(GroundSupportGroup group)
        {
            return group != null && !group.Closed && !group.Dismissed && (group.Pending || group.Accepted);
        }

        private void CancelTransmissions(RadioEventType type)
        {
            _queue.RemoveAll(item => item.Type == type);
            _output.StopTransmissions(type);
        }

        private Dictionary<string, string> VoiceSlots(string callsign)
        {
            return Slots(
                "callsign", callsign,
                "awacs", _config.AwacsCallsign.Value);
        }

        private static Dictionary<string, string> Slots(params string[] pairs)
        {
            Dictionary<string, string> slots = new Dictionary<string, string>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                slots[pairs[i]] = pairs[i + 1];
            return slots;
        }
    }
}
