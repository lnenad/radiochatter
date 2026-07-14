using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using RadioChatter.Game;
using RadioChatter.Speech;
using UnityEngine;

namespace RadioChatter.Comms
{
    internal sealed class CommsDirector
    {
        private const int StableTicksRequired = 2;
        private const float DuplicateWindowSeconds = 60f;
        private const float PictureSuppressAfterContactSeconds = 20f;
        private const float MinDisplaySeconds = 3f;
        private const float MaxDisplaySeconds = 10f;
        private const float ReadBaseSeconds = 1.25f;
        private const float ReadSecondsPerWord = 0.38f;
        private const float ApproachResetDistanceM = 35000f;
        private const float ApproachCallDistanceM = 16000f;
        private const float ApproachRequiredInboundSeconds = 22f;
        private const float ApproachHeadingToleranceDeg = 75f;
        private const float ApproachInboundGraceSeconds = 6f;
        private const float ApproachMaxAltitudeAglM = 3500f;
        private const float ApproachCloseRangeM = 12000f;
        private const float ApproachMinClosingMeters = 15f;
        private const float CloseTargetBearingOmitMeters = 2f * 1852f;
        private const float VectorSuppressDistanceM = 4000f;
        private const float PlayerWeaponDuplicateSeconds = 1.5f;
        private const float PlayerGunsDuplicateSeconds = 5f;
        private const float PlayerWeaponReleaseSeconds = 2f;
        private const float PlayerDefensiveDuplicateSeconds = 6f;
        private const float MissileInterruptDebounceSeconds = 2.5f;
        private const float MissileRoutineSuppressSeconds = 8f;
        private const float StartupWingmanHoldFallbackSeconds = 10f;
        private const float StartupWingmanTakeoffReadbackMaxHoldSeconds = 65f;
        private const int MaxHeldStartupWingmanLines = 8;
        private const int MaxHeldStartupAwacsLines = 8;
        private const float StartupAwacsMaxHoldSeconds = 90f;
        private const float StartupStateGraceSeconds = 6f;
        private const float StartupMissionCommsGraceSeconds = 15f;
        private const float NearAirbaseMinimumM = 1500f;
        private const float NearAirbaseRadiusBufferM = 2500f;
        private const float RtbFuelBingoFraction = 0.18f;
        private const float RtbVectorMinDistanceM = 18000f;
        private const float RtbVectorMaxDistanceM = 90000f;
        private const float RtbVectorHeadingToleranceDeg = 55f;
        private const float RtbVectorRequiredInboundSeconds = 18f;
        private const float RtbVectorInboundGraceSeconds = 6f;
        private const float RtbVectorMinClosingMeters = 20f;
        private const float RtbVectorCooldownSeconds = 90f;
        private const int MaxTransmissionsPerTick = 4;
        private const int VoiceResponsePriority = 60;
        private const float VoiceResponseDuplicateWindowSeconds = 2f;
        private const float TowerReadbackResponseSeconds = 10f;
        private const int TowerReadbackMaxAttempts = 2;
        private const float BattlefieldChatterIntervalSeconds = 18f;
        private const float BattlefieldChatterInitialDelaySeconds = 12f;
        private const float BattlefieldChatterCandidateLifetimeSeconds = 9f;
        private const int BattlefieldChatterPriority = 10;
        private const int MaxBattlefieldChatterCandidates = 16;
        private const int GroundSupportHailPriority = 50;
        private const float GroundSupportLostGraceSeconds = 6f;
        private const float GroundSupportDuplicateSeconds = 10f;

        private static readonly string[] GroundSupportCallsigns =
        {
            "Anvil", "Hammer", "Bison", "Ranger", "Sentinel", "Nomad"
        };

        private readonly Config _config;
        private readonly IRadioOutput _output;
        private readonly ManualLogSource _log;
        private readonly PhraseEngine _phrases;
        private readonly List<RadioEvent> _patchedEvents = new List<RadioEvent>(16);
        private readonly List<PendingTransmission> _queue = new List<PendingTransmission>(32);
        private readonly HashSet<uint> _announcedContacts = new HashSet<uint>();
        private readonly HashSet<uint> _warnedMissiles = new HashSet<uint>();
        private readonly HashSet<uint> _announcedPlayerKills = new HashSet<uint>();
        private readonly HashSet<uint> _announcedDestroyedUnits = new HashSet<uint>();
        private readonly Dictionary<uint, bool> _lastDisabledState = new Dictionary<uint, bool>(256);
        private readonly Dictionary<string, float> _lastTextAt = new Dictionary<string, float>(64);
        private readonly Dictionary<RadioEventType, float> _lastTypeQueuedAt = new Dictionary<RadioEventType, float>();
        private readonly Dictionary<uint, ContactInfoRecord> _contactInfoLog = new Dictionary<uint, ContactInfoRecord>(64);
        private readonly Dictionary<string, float> _heldPlayerWeaponCalls = new Dictionary<string, float>(8);
        private readonly HashSet<string> _playerWeaponCallsSeenThisTick = new HashSet<string>();
        private readonly List<string> _stalePlayerWeaponCalls = new List<string>(8);
        private readonly List<string> _startupWingmanTexts = new List<string>(MaxHeldStartupWingmanLines);
        private readonly List<PendingTransmission> _startupAwacsTransmissions = new List<PendingTransmission>(MaxHeldStartupAwacsLines);
        private readonly List<PendingTowerReadback> _pendingTowerReadbacks = new List<PendingTowerReadback>(3);
        private readonly Dictionary<uint, FriendlyAircraftState> _friendlyAircraftStates = new Dictionary<uint, FriendlyAircraftState>(64);
        private readonly List<BattlefieldChatterCandidate> _battlefieldChatterCandidates = new List<BattlefieldChatterCandidate>(MaxBattlefieldChatterCandidates);
        private readonly List<GroundSupportGroup> _groundSupportGroups = new List<GroundSupportGroup>(8);
        private readonly Dictionary<uint, GroundSupportGroup> _groundSupportByUnit = new Dictionary<uint, GroundSupportGroup>(32);

        private int _aircraftInstanceId;
        private int _homeAirbaseInstanceId;
        private bool? _stableGrounded;
        private int _groundedTicks;
        private int _airborneTicks;
        private bool _takeoffClearanceAnnounced;
        private bool _airborneAnnounced;
        private bool _awaitingAwacsCheckIn;
        private bool _approachAnnounced;
        private bool _finalAnnounced;
        private bool _landedAnnounced;
        private bool _successfulAirportLanding;
        private bool _ejectionAnnounced;
        private bool _destroyedAudioStopped;
        private bool _startupWingmanDecisionMade;
        private bool _startupWingmanHeld;
        private float _startupWingmanHeldAt;
        private float _takeoffClearanceQueuedAt = float.NaN;
        private bool _startupRadioGateActive;
        private bool _startupAwacsReleased;
        private float _startupRadioGateStartedAt = float.NaN;
        private bool _startupMissionCommsSeen;
        private float _startupTakeoffSequenceDoneAt = float.NaN;
        private bool _mpClientLogged;
        private float _previousHomeDistance = float.NaN;
        private float _approachInboundStartedAt = float.NaN;
        private float _approachLastInboundAt = float.NaN;
        private bool _rtbFuelAnnounced;
        private float _previousRtbHomeDistance = float.NaN;
        private float _rtbInboundStartedAt = float.NaN;
        private float _rtbLastInboundAt = float.NaN;
        private bool _routineAwacsQuiet;
        private AwacsTrafficMode _awacsTrafficMode;
        private FlightMissionRole _flightMissionRole;
        private float _nextSnapshotLogTime;
        private float _suppressPictureUntil;
        private float _suppressRoutineAwacsUntil;
        private float _lastMissileWarnAt = float.NegativeInfinity;
        private float _nextBattlefieldChatterAt;
        private bool _battlefieldChatterTracking;
        private GroundSupportGroup _activeGroundSupportGroup;
        private int _nextGroundSupportCallsign;
        private float _nextGroundSupportHailAllowedAt;
        private bool _sessionActive;

        public CommsDirector(Config config, IRadioOutput output, ManualLogSource log)
        {
            _config = config;
            _output = output;
            _log = log;
            _phrases = new PhraseEngine(log);
        }

        public void Tick(Snapshot snapshot)
        {
            if (!_config.Enabled.Value)
            {
                EndSession();
                return;
            }

            if (snapshot.Mode == GameMode.MultiplayerClient)
            {
                if (!_mpClientLogged)
                {
                    _mpClientLogged = true;
                    _log.LogInfo("RadioChatter disabled in multiplayer client mode.");
                }

                EndSession();
                return;
            }

            _mpClientLogged = false;

            if (!snapshot.InMission)
            {
                EndSession();
                return;
            }

            if (!snapshot.Player.Valid)
            {
                // Aircraft changes can briefly leave the host without a local aircraft while
                // the mission itself continues. Preserve only mission-level ground callsigns;
                // all aircraft/radio state still resets exactly as before.
                EndSession(true);
                return;
            }

            _sessionActive = true;
            if (!_config.VoiceCommandsEnabled.Value)
                _awaitingAwacsCheckIn = false;

            if (!SpokenTowerReadbacksEnabled())
            {
                _pendingTowerReadbacks.Clear();
                ClearTowerReadbackPrompts();
            }

            DrainPatchedEvents(snapshot);

            if (_aircraftInstanceId != snapshot.Player.AircraftInstanceId)
                ResetForAircraft(snapshot.Player.AircraftInstanceId);

            LogSnapshot(snapshot);
            BeginStartupRadioGate(snapshot);
            if (DetectPlayerDestroyed(snapshot))
                return;

            DetectDestroyedUnits(snapshot);
            UpdateGroundSupportRequests(snapshot);
            DetectBattlefieldChatter(snapshot);
            UpdateRoutineAwacsQuietState(snapshot);
            DetectContacts(snapshot);
            DetectMissiles(snapshot);
            DetectEjection(snapshot);
            DetectTower(snapshot);
            ReleaseHeldStartupWingmanFallback(snapshot);
            DetectApproach(snapshot);
            DetectRtb(snapshot);
            DetectVector(snapshot);
            DetectPicture(snapshot);
            TryReleaseHeldStartupAwacs(snapshot);
            UpdateAwacsCheckInPrompt();
            TryQueueBattlefieldChatter(snapshot.Time);
            ProcessQueue(snapshot.Time);
        }

        private void EndSession(bool preserveGroundSupport = false)
        {
            bool stopOutput = _sessionActive;
            _sessionActive = false;
            RadioEventBus.Clear();
            ResetSession(preserveGroundSupport);

            if (stopOutput)
                _output.StopAll();
        }

        private void DrainPatchedEvents(Snapshot snapshot)
        {
            _patchedEvents.Clear();
            _playerWeaponCallsSeenThisTick.Clear();
            RadioEventBus.Drain(_patchedEvents);
            ReleaseStalePlayerWeaponCalls(snapshot.Time);

            for (int i = 0; i < _patchedEvents.Count; i++)
            {
                RadioEvent evt = _patchedEvents[i];
                float now = snapshot.Time;

                switch (evt.Type)
                {
                    case RadioEventType.PlayerAircraftChanged:
                        ResetForAircraft(evt.PlayerAircraftInstanceId);
                        _log.LogDebug($"Player aircraft changed: {evt.SubjectName ?? "none"} #{evt.PlayerAircraftInstanceId}");
                        break;

                    case RadioEventType.PlayerAircraftDestroyed:
                        StopAudioForPlayerDestroyed();
                        break;

                    case RadioEventType.UnitDestroyed:
                        if (evt.SubjectId != 0 && _announcedDestroyedUnits.Add(evt.SubjectId))
                            _log.LogInfo($"Event: unit destroyed: {evt.SubjectName}");

                        HandleGroundSupportAttackerDestroyed(evt.SubjectId);

                        if (evt.SubjectIsFriendly && evt.SubjectIsAircraft)
                            AddBattlefieldChatterCandidate(evt.SubjectId, evt.SubjectName, "battlefield_lost", null, now, 4);
                        break;

                    case RadioEventType.GroundUnitUnderAttack:
                        HandleGroundUnitUnderAttack(snapshot, evt, now);
                        break;

                    case RadioEventType.PlayerKill:
                        if (_config.SplashCalls.Value && evt.SubjectId != 0 && _announcedPlayerKills.Add(evt.SubjectId))
                        {
                            CancelVectorCalls();
                            string key = evt.SubjectIsAircraft ? "awacs_splash_air" : "awacs_splash_ground";
                            Queue(RadioRole.Awacs, evt.Type, key, Slots(
                                "callsign", _config.PlayerCallsign.Value,
                                "awacs", _config.AwacsCallsign.Value,
                                "type", RadioText.SpokenUnitName(evt.SubjectName)), now, 60, 4f, 15f, false);
                        }
                        break;

                    case RadioEventType.MissileThreat:
                        if (evt.SubjectId != 0 && _warnedMissiles.Add(evt.SubjectId))
                            AnnounceMissileThreat(snapshot, evt.BearingDeg, now);
                        break;

                    case RadioEventType.SortieSuccessful:
                        _successfulAirportLanding = true;
                        if (_config.LandingCalls.Value && !_landedAnnounced)
                        {
                            _landedAnnounced = true;
                            Queue(RadioRole.Tower, RadioEventType.TowerLanded, "tower_landed", CommonSlots(), now, 70, 4f, 0f, false);
                        }
                        break;

                    case RadioEventType.TowerFinal:
                        if (_config.LandingCalls.Value && !_finalAnnounced && snapshot.InMission && snapshot.Player.Valid &&
                            !RequestDrivenComms())
                        {
                            _finalAnnounced = true;
                            Queue(RadioRole.Tower, RadioEventType.TowerFinal, "tower_final",
                                LandingClearanceSlots(snapshot, evt.Text), now, 80, 4f, 0f, false);
                        }
                        break;

                    case RadioEventType.InGameComms:
                        if (_config.InGameComms.Value && snapshot.InMission && snapshot.Player.Valid)
                        {
                            string text = RadioText.SanitizeGameComms(evt.Text);
                            if (!string.IsNullOrEmpty(text))
                            {
                                _startupMissionCommsSeen = true;
                                if (ShouldHoldStartupWingman(snapshot))
                                {
                                    _startupWingmanDecisionMade = true;
                                    HoldStartupWingman(text, now);
                                }
                                else
                                {
                                    _startupWingmanDecisionMade = true;
                                    QueueText(RadioRole.Game, evt.Type, text, now, 45, 4f, 1f, false);
                                }
                            }
                        }
                        break;

                    case RadioEventType.PlayerVoiceCommand:
                        if (_config.Enabled.Value && _config.VoiceCommandsEnabled.Value &&
                            snapshot.Mode != GameMode.MultiplayerClient &&
                            snapshot.InMission && snapshot.Player.Valid)
                        {
                            HandleVoiceCommand(snapshot, evt.Text, now);
                        }
                        break;

                    case RadioEventType.TowerReadbackRequired:
                        if (SpokenTowerReadbacksEnabled() &&
                            TowerReadbackMatcher.TryCreate(evt.Text, out TowerReadbackExpectation expectation))
                        {
                            AddPendingTowerReadback(expectation, now);
                            _log.LogInfo($"Awaiting spoken Tower {expectation.Kind.ToString().ToLowerInvariant()} readback from {expectation.Callsign}.");
                        }
                        break;

                    case RadioEventType.PlayerWeaponCall:
                        if (_config.PlayerWeaponCalls.Value && snapshot.InMission && snapshot.Player.Valid)
                        {
                            string text = RadioText.FormatPlayerWeaponCall(evt.Text);
                            if (!string.IsNullOrEmpty(text))
                            {
                                if (SuppressHeldPlayerWeaponCall(text, now))
                                    break;

                                float duplicateWindow = RadioText.IsGunsCall(text) ? PlayerGunsDuplicateSeconds : PlayerWeaponDuplicateSeconds;
                                QueueText(RadioRole.Player, evt.Type, text, now, 65, 2.5f, 0.5f, false, duplicateWindow);
                                _log.LogDebug($"Player weapon call: {text} ({evt.SubjectName ?? "unknown weapon"})");
                            }
                        }
                        break;

                    case RadioEventType.FriendlyWeaponCall:
                        string weaponCall = RadioText.FormatPlayerWeaponCall(evt.Text);
                        if (!string.IsNullOrEmpty(weaponCall))
                        {
                            AddBattlefieldChatterCandidate(evt.SubjectId, evt.SubjectName, "battlefield_weapon",
                                Slots("call", weaponCall), now, 2);
                        }
                        break;

                    case RadioEventType.FriendlyDefensiveCall:
                        AddBattlefieldChatterCandidate(evt.SubjectId, evt.SubjectName, "battlefield_defensive", null, now, 3);
                        break;
                }
            }

            ResolveDestroyedGroundSupportThreats(snapshot.Time);
            ReleaseStalePlayerWeaponCalls(snapshot.Time);
            PromptForMissingTowerReadbacks(snapshot.Time);
        }

        private bool SuppressHeldPlayerWeaponCall(string text, float now)
        {
            // WeaponManager.Fire can repeat while the trigger is held; one phrase per hold is enough.
            _playerWeaponCallsSeenThisTick.Add(text);

            if (_heldPlayerWeaponCalls.ContainsKey(text))
            {
                _heldPlayerWeaponCalls[text] = now;
                return true;
            }

            _heldPlayerWeaponCalls[text] = now;
            return false;
        }

        private void ReleaseStalePlayerWeaponCalls(float now)
        {
            if (_heldPlayerWeaponCalls.Count == 0)
                return;

            _stalePlayerWeaponCalls.Clear();
            float releaseSeconds = Mathf.Max(PlayerWeaponReleaseSeconds, Mathf.Clamp(_config.PollIntervalSeconds.Value, 0.1f, 2f) * 1.5f);

            foreach (KeyValuePair<string, float> entry in _heldPlayerWeaponCalls)
            {
                if (_playerWeaponCallsSeenThisTick.Contains(entry.Key))
                    continue;

                if (now - entry.Value > releaseSeconds)
                    _stalePlayerWeaponCalls.Add(entry.Key);
            }

            for (int i = 0; i < _stalePlayerWeaponCalls.Count; i++)
                _heldPlayerWeaponCalls.Remove(_stalePlayerWeaponCalls[i]);
        }

        private bool GroundSupportEnabled()
        {
            return _config.GroundSupportRequests.Value && _config.VoiceCommandsEnabled.Value;
        }

        private void HandleGroundUnitUnderAttack(Snapshot snapshot, RadioEvent evt, float now)
        {
            if (!GroundSupportEnabled() || evt.SubjectId == 0)
                return;

            // A fire/damage callback can race a destruction callback in the same frame. Never
            // open or refresh a task for an attacker already known to be destroyed.
            if (evt.AttackerId != 0 && IsKnownDestroyed(evt.AttackerId))
                return;

            GroundSupportGroup group;
            if (!_groundSupportByUnit.TryGetValue(evt.SubjectId, out group) || group.Closed)
                group = FindGroundSupportGroupNear(evt.Position);

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
                _groundSupportGroups.Add(group);
                _groundSupportByUnit[evt.SubjectId] = group;

                QueueGroundSupportHail(snapshot, group, now);
                _log.LogInfo($"Ground support request opened: {group.Callsign} ({evt.SubjectName}) group #{group.Id}.");
                return;
            }

            group.MemberIds.Add(evt.SubjectId);
            _groundSupportByUnit[evt.SubjectId] = group;
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
                QueueGroundSupportHail(snapshot, group, now);
                _log.LogInfo($"Ground support request reopened: {group.Callsign}.");
            }
        }

        private GroundSupportGroup FindGroundSupportGroupNear(GPos position)
        {
            float radius = _config.GroundSupportGroupRadiusM.Value;
            GroundSupportGroup nearest = null;
            float bestDistance = float.PositiveInfinity;

            for (int i = 0; i < _groundSupportGroups.Count; i++)
            {
                GroundSupportGroup candidate = _groundSupportGroups[i];
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
            int index = _nextGroundSupportCallsign++;
            string stem = GroundSupportCallsigns[index % GroundSupportCallsigns.Length];
            int number = index / GroundSupportCallsigns.Length + 1;
            return stem + " " + number;
        }

        private bool QueueGroundSupportHail(Snapshot snapshot, GroundSupportGroup group, float now)
        {
            if (group == null || group.Closed || group.Accepted || group.Dismissed)
                return false;

            if (now < _nextGroundSupportHailAllowedAt)
            {
                group.NextHailAt = Mathf.Max(group.NextHailAt, _nextGroundSupportHailAllowedAt);
                return false;
            }

            if (GroundSupportHailGate.ShouldHold(
                    _stableGrounded,
                    snapshot.Player.Grounded,
                    ShouldHoldStartupAwacs(now, false)))
            {
                group.NextHailAt = now + _config.GroundSupportRepeatSeconds.Value;
                return false;
            }

            if (SuppressGroundSupportHails())
            {
                group.NextHailAt = now + _config.GroundSupportRepeatSeconds.Value;
                return false;
            }

            bool queued = Queue(RadioRole.Game, RadioEventType.GroundSupportHail, "ground_support_hail", Slots(
                "ground_callsign", group.Callsign),
                now, GroundSupportHailPriority, 7f, 0f, false,
                subjectId: group.Id,
                duplicateWindowSecondsOverride: 0f);

            if (queued)
            {
                StartGroundSupportHailCooldown(now);
                group.LastHailAt = now;
                group.NextHailAt = now + _config.GroundSupportRepeatSeconds.Value;
            }

            return queued;
        }

        private void StartGroundSupportHailCooldown(float now)
        {
            _nextGroundSupportHailAllowedAt = Mathf.Max(
                _nextGroundSupportHailAllowedAt,
                GroundSupportHailGate.NextAllowedAt(now));

            for (int i = 0; i < _groundSupportGroups.Count; i++)
            {
                GroundSupportGroup group = _groundSupportGroups[i];
                if (group.Pending && !group.Closed && !group.Dismissed)
                    group.NextHailAt = Mathf.Max(group.NextHailAt, _nextGroundSupportHailAllowedAt);
            }
        }

        private void UpdateGroundSupportRequests(Snapshot snapshot)
        {
            if (!GroundSupportEnabled())
            {
                DisableGroundSupportRequests();
                return;
            }

            float now = snapshot.Time;
            float radius = _config.GroundSupportGroupRadiusM.Value;

            for (int g = 0; g < _groundSupportGroups.Count; g++)
            {
                GroundSupportGroup group = _groundSupportGroups[g];
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
                    CloseGroundSupportGroup(group, now);
                    continue;
                }

                if (group.Pending && now >= group.NextHailAt)
                    QueueGroundSupportHail(snapshot, group, now);
            }
        }

        private void CloseGroundSupportGroup(GroundSupportGroup group, float now)
        {
            group.Closed = true;
            group.Pending = false;
            group.Accepted = false;
            RemoveQueuedGroundSupport(group.Id);
            CancelGroundSupportHailAudioAndRescheduleOthers(group, now);

            if (_activeGroundSupportGroup != group)
                return;

            _activeGroundSupportGroup = null;
            _queue.RemoveAll(item => item.Type == RadioEventType.GroundSupportVector);
            _output.StopTransmissions(RadioEventType.GroundSupportVector);
            Queue(RadioRole.Awacs, RadioEventType.GroundSupportCanceled, "awacs_ground_support_lost", Slots(
                "callsign", _config.PlayerCallsign.Value,
                "awacs", _config.AwacsCallsign.Value,
                "ground_callsign", group.Callsign), now, VoiceResponsePriority, 4f, 0f, false,
                subjectId: group.Id,
                duplicateWindowSecondsOverride: GroundSupportDuplicateSeconds,
                bypassStartupHold: true);
            _log.LogInfo($"Ground support task canceled: {group.Callsign} is no longer active.");
        }

        private void HandleGroundSupportAttackerDestroyed(uint attackerId)
        {
            if (attackerId == 0 || _groundSupportGroups.Count == 0)
                return;

            for (int i = 0; i < _groundSupportGroups.Count; i++)
            {
                GroundSupportGroup group = _groundSupportGroups[i];
                if (group.Closed || !group.Threats.MarkDestroyed(attackerId))
                    continue;

                // More attack events can follow the destruction event in this same update.
                // Finalize only after the whole batch has had a chance to add another attacker.
                group.ThreatResolutionPending = true;
            }
        }

        private void ResolveDestroyedGroundSupportThreats(float now)
        {
            for (int i = 0; i < _groundSupportGroups.Count; i++)
            {
                GroundSupportGroup group = _groundSupportGroups[i];
                if (!group.ThreatResolutionPending)
                    continue;

                group.ThreatResolutionPending = false;
                if (!group.Closed && group.Threats.Count == 0)
                    ResolveGroundSupportThreat(group, now);
            }
        }

        private void ResolveGroundSupportThreat(GroundSupportGroup group, float now)
        {
            bool playerWasSupporting = group.Accepted && _activeGroundSupportGroup == group;

            // Keep the mission-persistent group and callsign available for a genuinely new
            // engagement, but clear the current request/secondary immediately.
            group.Pending = false;
            group.Accepted = false;
            group.Dismissed = true;
            group.DismissedAt = now;
            group.Resolved = true;
            RemoveQueuedGroundSupport(group.Id);
            CancelGroundSupportHailAudioAndRescheduleOthers(group, now);
            _output.StopTransmissions(RadioEventType.GroundSupportAcknowledged);

            if (_activeGroundSupportGroup == group)
            {
                _activeGroundSupportGroup = null;
                _queue.RemoveAll(item => item.Type == RadioEventType.GroundSupportVector);
                _output.StopTransmissions(RadioEventType.GroundSupportVector);
            }

            if (playerWasSupporting)
            {
                Queue(RadioRole.Game, RadioEventType.GroundSupportCompleted,
                    "ground_support_completed", Slots(
                        "callsign", _config.PlayerCallsign.Value,
                        "ground_callsign", group.Callsign),
                    now, VoiceResponsePriority, 4f, 0f, false,
                    subjectId: group.Id,
                    duplicateWindowSecondsOverride: 0f,
                    bypassStartupHold: true);
                _log.LogInfo($"Ground support task completed: {group.Callsign}; last tracked attacker destroyed.");
            }
            else
            {
                _log.LogInfo($"Ground support request resolved before acceptance: {group.Callsign}; last tracked attacker destroyed.");
            }
        }

        private bool QueueGroundSupportVector(
            Snapshot snapshot,
            GroundSupportGroup group,
            string playerCallsign,
            float now)
        {
            if (group == null || group.Closed)
                return false;

            return Queue(RadioRole.Awacs, RadioEventType.GroundSupportVector,
                "awacs_ground_support_vector", Slots(
                    "callsign", playerCallsign,
                    "awacs", _config.AwacsCallsign.Value,
                    "ground_callsign", group.Callsign,
                    "bearing", NumberSpeech.Bearing(GPos.Bearing(snapshot.Player.Position, group.Position)),
                    "range", RadioText.FormatRange(GPos.Distance2D(snapshot.Player.Position, group.Position), snapshot.Units)),
                now, VoiceResponsePriority, 5f, 0f, false,
                subjectId: group.Id,
                duplicateWindowSecondsOverride: GroundSupportDuplicateSeconds,
                bypassStartupHold: true);
        }

        private bool QueueGroundSupportAcknowledgement(
            Snapshot snapshot,
            GroundSupportGroup group,
            string playerCallsign,
            float now,
            bool followup)
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

            return Queue(RadioRole.Game, RadioEventType.GroundSupportAcknowledged,
                followup ? "ground_support_followup_ack" : "ground_support_acknowledged", slots,
                now, VoiceResponsePriority, 4f, 0f, false,
                subjectId: group.Id,
                duplicateWindowSecondsOverride: 0f,
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

        private void CancelGroundSupportHailAudioAndRescheduleOthers(GroundSupportGroup excluded, float now)
        {
            _queue.RemoveAll(item => item.Type == RadioEventType.GroundSupportHail);
            _output.StopTransmissions(RadioEventType.GroundSupportHail);

            // StopTransmissions invalidates same-frame TTS requests of that type. Requeue other
            // pending groups on a later poll, after the audio clock has advanced.
            float retryAt = now + Mathf.Max(0.25f, _config.PollIntervalSeconds.Value);
            for (int i = 0; i < _groundSupportGroups.Count; i++)
            {
                GroundSupportGroup group = _groundSupportGroups[i];
                if (group != excluded && group.Pending && !group.Closed && !group.Dismissed)
                    group.NextHailAt = Mathf.Min(group.NextHailAt, retryAt);
            }
        }

        private void DisableGroundSupportRequests()
        {
            _nextGroundSupportHailAllowedAt = 0f;
            if (_groundSupportGroups.Count == 0 && _activeGroundSupportGroup == null)
                return;

            _groundSupportGroups.Clear();
            _groundSupportByUnit.Clear();
            _activeGroundSupportGroup = null;
            _nextGroundSupportCallsign = 0;
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

        private void DetectDestroyedUnits(Snapshot snapshot)
        {
            for (int i = 0; i < snapshot.UnitLifecycles.Count; i++)
            {
                UnitLifecycleInfo unit = snapshot.UnitLifecycles[i];
                if (unit.Id == 0 || unit.IsMissile || unit.IsPlayer)
                    continue;

                bool wasDisabled;
                if (_lastDisabledState.TryGetValue(unit.Id, out wasDisabled) && !wasDisabled && unit.Disabled)
                {
                    if (_announcedDestroyedUnits.Add(unit.Id))
                        _log.LogInfo($"Event: unit destroyed by polling: {unit.DisplayName}");

                    HandleGroundSupportAttackerDestroyed(unit.Id);
                }

                _lastDisabledState[unit.Id] = unit.Disabled;
            }

            ResolveDestroyedGroundSupportThreats(snapshot.Time);
        }

        private void DetectContacts(Snapshot snapshot)
        {
            if (!_config.NewContactCalls.Value || _routineAwacsQuiet || SuppressAutomaticAirContacts())
                return;

            for (int i = 0; i < snapshot.Contacts.Count; i++)
            {
                ContactInfo contact = snapshot.Contacts[i];
                if (!contact.Observed || contact.Id == 0 || !_announcedContacts.Add(contact.Id))
                    continue;

                QueueContact(snapshot, contact, RadioEventType.NewContact, "awacs_new_contact", 50, 5f, 5f);
                _suppressPictureUntil = Mathf.Max(_suppressPictureUntil, snapshot.Time + PictureSuppressAfterContactSeconds);
            }
        }

        private void DetectMissiles(Snapshot snapshot)
        {
            if (!_config.MissileWarnings.Value && !_config.PlayerDefensiveCalls.Value)
                return;

            for (int i = 0; i < snapshot.MissileThreats.Count; i++)
            {
                MissileThreat threat = snapshot.MissileThreats[i];
                if (threat.Id == 0 || !_warnedMissiles.Add(threat.Id))
                    continue;

                AnnounceMissileThreat(snapshot, threat.BearingFromPlayerDeg, snapshot.Time);
            }
        }

        /// <summary>Announces a fresh missile threat with top priority. A live missile trumps
        /// routine chatter, so whatever AWACS/tower call is mid-transmission is cut off and the
        /// pending queue is purged (the calls below are urgent) before the warning goes out.
        /// Closely-spaced launches queue behind one another instead of stomping the last warning.</summary>
        private void AnnounceMissileThreat(Snapshot snapshot, float missileBearingDeg, float now)
        {
            if (now - _lastMissileWarnAt > MissileInterruptDebounceSeconds)
                _output.StopAll();

            _lastMissileWarnAt = now;

            // Keep routine AWACS info (vector/picture) quiet through the immediate defensive moment.
            _suppressRoutineAwacsUntil = Mathf.Max(_suppressRoutineAwacsUntil, now + MissileRoutineSuppressSeconds);
            _suppressPictureUntil = Mathf.Max(_suppressPictureUntil, now + MissileRoutineSuppressSeconds);

            string breakCall = EscapeBreakCall(missileBearingDeg, snapshot.Player.HeadingDeg);

            if (_config.PlayerDefensiveCalls.Value)
                QueueText(RadioRole.Player, RadioEventType.PlayerDefensiveCall, "missile, " + breakCall + "!",
                    now, 100, 2.5f, 0f, true, PlayerDefensiveDuplicateSeconds);

            if (_config.MissileWarnings.Value)
                Queue(RadioRole.Awacs, RadioEventType.MissileThreat, "awacs_missile", Slots(
                    "callsign", _config.PlayerCallsign.Value,
                    "bearing", NumberSpeech.Bearing(missileBearingDeg),
                    "break", breakCall), now, 100, 3f, 0f, true);
        }

        /// <summary>The defensive break to escape an incoming missile: turn to drive the threat onto
        /// the 3/9 line (the beam) with the smaller turn. A threat ahead of the wingline is beamed by
        /// breaking away from it; a threat behind the wingline by breaking toward its side.</summary>
        private static string EscapeBreakCall(float missileBearingDeg, float playerHeadingDeg)
        {
            float relative = Mathf.DeltaAngle(playerHeadingDeg, missileBearingDeg); // + = threat to the right
            bool breakRight = relative >= 0f ? relative > 90f : relative > -90f;
            return breakRight ? "break right" : "break left";
        }

        private void DetectEjection(Snapshot snapshot)
        {
            if (_ejectionAnnounced || !snapshot.Player.Ejected)
                return;

            _ejectionAnnounced = true;
            ResetMissionRoleToGeneral("ejection", snapshot.Time, false);

            if (!_config.PlayerEjectionCalls.Value)
                return;

            if (IsNormalAirportExitEjection(snapshot))
            {
                _log.LogDebug("Suppressing player ejection mayday for normal airport exit.");
                return;
            }

            _queue.Clear();
            _output.TransmitImmediate(RadioRole.Player, RadioEventType.PlayerEjectionCall, "mayday! mayday! ejecting!", 3f);
            _log.LogInfo("[Player] mayday! mayday! ejecting!");
        }

        private bool IsNormalAirportExitEjection(Snapshot snapshot)
        {
            if (snapshot.Player.Destroyed)
                return false;

            bool safelyOnGround = snapshot.Player.Grounded ||
                                  (_successfulAirportLanding && snapshot.Player.AltitudeAglM < 5f && snapshot.Player.SpeedMs < 45f);
            if (!safelyOnGround)
                return false;

            if (!TryGetHomeBase(snapshot, out AirbaseInfo home, out float distanceM))
                return false;

            return IsNearAirbase(home, distanceM);
        }

        private bool DetectPlayerDestroyed(Snapshot snapshot)
        {
            if (!snapshot.Player.Destroyed || snapshot.Player.Ejected)
                return false;

            ResetMissionRoleToGeneral("aircraft destroyed", snapshot.Time, false);

            if (!_destroyedAudioStopped)
                StopAudioForPlayerDestroyed();

            return true;
        }

        private void StopAudioForPlayerDestroyed()
        {
            if (_destroyedAudioStopped)
                return;

            _destroyedAudioStopped = true;
            _queue.Clear();
            string text = $"{_config.AwacsCallsign.Value}, {_config.PlayerCallsign.Value} is down, no chute";
            _output.TransmitImmediate(RadioRole.Awacs, RadioEventType.PlayerAircraftDestroyed, text, 4f);
            _log.LogInfo($"[Awacs] {text}");
        }

        private void BeginStartupRadioGate(Snapshot snapshot)
        {
            if (_startupAwacsReleased)
                return;

            if (_startupRadioGateActive)
            {
                if (float.IsNaN(_startupRadioGateStartedAt))
                    _startupRadioGateStartedAt = snapshot.Time;

                return;
            }

            if (_takeoffClearanceAnnounced && IsWaitingForTakeoffReadback(snapshot.Time))
            {
                StartStartupRadioGate(snapshot.Time);
                return;
            }

            if (IsStartupTakeoffCandidate(snapshot))
                StartStartupRadioGate(snapshot.Time);
        }

        private void StartStartupRadioGate(float now)
        {
            _startupRadioGateActive = true;
            _startupRadioGateStartedAt = now;
            ParkQueuedStartupAwacs();
            _log.LogDebug("Startup radio gate active: tower/readback, mission comms, then AWACS.");
        }

        private bool ShouldHoldStartupWingman(Snapshot snapshot)
        {
            if (_startupWingmanHeld)
                return true;

            if (_takeoffClearanceAnnounced && IsWaitingForTakeoffReadback(snapshot.Time))
                return true;

            if (_startupWingmanDecisionMade)
                return false;

            if (!_config.TakeoffCalls.Value || _takeoffClearanceAnnounced)
                return false;

            return IsStartupTakeoffCandidate(snapshot) || StartupStateStillSettling(snapshot);
        }

        /// <summary>On the first in-mission polls the friendly airbase/HQ data may not be populated
        /// yet, which makes the takeoff-candidate check falsely negative. Treat the startup state as
        /// unsettled until bases appear or the grace period elapses, so the startup radio gate is not
        /// released before the tower sequence had a chance to start.</summary>
        private bool StartupStateStillSettling(Snapshot snapshot)
        {
            if (snapshot.FriendlyAirbases.Count > 0)
                return false;

            return float.IsNaN(_startupRadioGateStartedAt) ||
                   snapshot.Time - _startupRadioGateStartedAt < StartupStateGraceSeconds;
        }

        private bool IsStartupTakeoffCandidate(Snapshot snapshot)
        {
            if (!snapshot.InMission || !snapshot.Player.Valid)
                return false;

            if (!_config.TakeoffCalls.Value || _takeoffClearanceAnnounced)
                return false;

            if (!TryGetHomeBase(snapshot, out AirbaseInfo home, out float distanceM))
                return false;

            bool nearBase = IsNearAirbase(home, distanceM);
            if (!nearBase)
                return false;

            return snapshot.Player.Grounded ||
                   snapshot.Player.GearDown && snapshot.Player.AltitudeAglM < 30f && snapshot.Player.SpeedMs < 150f ||
                   snapshot.Player.AltitudeAglM < 8f && snapshot.Player.SpeedMs < 80f;
        }

        private void HoldStartupWingman(string text, float now)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            if (!_startupWingmanHeld)
            {
                _startupWingmanHeld = true;
                _startupWingmanHeldAt = now;
                _log.LogDebug("Holding startup wingman/mission comms until after takeoff clearance and player readback.");
            }

            if (_startupWingmanTexts.Count >= MaxHeldStartupWingmanLines)
                _startupWingmanTexts.RemoveAt(0);

            _startupWingmanTexts.Add(text);
        }

        private void ReleaseHeldStartupWingmanFallback(Snapshot snapshot)
        {
            if (!_startupWingmanHeld)
                return;

            if (_takeoffClearanceAnnounced)
            {
                if (IsWaitingForTakeoffReadback(snapshot.Time))
                    return;

                ReleaseHeldStartupWingman(snapshot.Time, snapshot.Time);
                return;
            }

            if (!snapshot.Player.Grounded || snapshot.Time - _startupWingmanHeldAt >= StartupWingmanHoldFallbackSeconds)
                ReleaseHeldStartupWingman(snapshot.Time, snapshot.Time);
        }

        private void ReleaseHeldStartupWingman(float now, float availableAt)
        {
            if (!_startupWingmanHeld || _startupWingmanTexts.Count == 0)
                return;

            for (int i = 0; i < _startupWingmanTexts.Count; i++)
            {
                QueueText(RadioRole.Game, RadioEventType.InGameComms, _startupWingmanTexts[i],
                    now, 45, 4f, 0f, false, DuplicateWindowSeconds, availableAt + i * 0.25f);
            }

            _startupWingmanHeld = false;
            _startupWingmanTexts.Clear();
        }

        private bool IsWaitingForTakeoffReadback(float now)
        {
            if (!_takeoffClearanceAnnounced || float.IsNaN(_takeoffClearanceQueuedAt))
                return false;

            if (now - _takeoffClearanceQueuedAt > StartupWingmanTakeoffReadbackMaxHoldSeconds)
                return false;

            if (HasQueuedTransmission(RadioEventType.TowerTakeoff))
                return true;

            if (SpokenTowerReadbacksEnabled() && HasQueuedTransmission(RadioRole.Tower))
                return true;

            if (_output.HasAudioWork(RadioRole.Tower))
                return true;

            if (SpokenTowerReadbacksEnabled() && HasPendingTowerReadback(TowerReadbackKind.Takeoff))
                return true;

            return _config.PlayerAcknowledgements.Value && _output.HasAudioWork(RadioRole.PlayerTower);
        }

        /// <summary>True while a ground takeoff is underway but Tower has not yet completed the
        /// airborne handoff to AWACS. Taking off from a field puts the player under Tower control;
        /// AWACS stays silent until Tower makes the airborne "switch to {awacs}" call and that
        /// exchange (including the player readback) has finished, so AWACS never steps on it.</summary>
        private bool IsWaitingForAirborneHandoff()
        {
            if (_awaitingAwacsCheckIn)
                return true;

            if (!_takeoffClearanceAnnounced)
                return false;

            if (!_airborneAnnounced)
                return true;

            if (HasQueuedTransmission(RadioEventType.TowerAirborne))
                return true;

            if (SpokenTowerReadbacksEnabled() && HasQueuedTransmission(RadioRole.Tower))
                return true;

            return _output.HasAudioWork(RadioRole.Tower) ||
                   _output.HasAudioWork(RadioRole.PlayerTower) ||
                   (SpokenTowerReadbacksEnabled() && HasPendingTowerReadback(TowerReadbackKind.Handoff));
        }

        private void DetectBattlefieldChatter(Snapshot snapshot)
        {
            if (!_config.BattlefieldChatter.Value)
            {
                DisableBattlefieldChatter();
                return;
            }

            bool initializing = !_battlefieldChatterTracking;
            if (initializing)
            {
                _battlefieldChatterTracking = true;
                _nextBattlefieldChatterAt = snapshot.Time + BattlefieldChatterInitialDelaySeconds;
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
                        AddBattlefieldChatterCandidate(unit.Id, unit.DisplayName, "battlefield_lost", null, snapshot.Time, 4);
                    }
                    else if (!unit.Disabled && !previous.Disabled && previous.Grounded != unit.Grounded)
                    {
                        AddBattlefieldChatterCandidate(unit.Id, unit.DisplayName,
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

        private void AddBattlefieldChatterCandidate(
            uint subjectId,
            string subjectName,
            string phraseKey,
            IDictionary<string, string> extraSlots,
            float now,
            int importance)
        {
            if (!_config.BattlefieldChatter.Value || subjectId == 0 || string.IsNullOrWhiteSpace(phraseKey))
                return;

            for (int i = _battlefieldChatterCandidates.Count - 1; i >= 0; i--)
            {
                BattlefieldChatterCandidate existing = _battlefieldChatterCandidates[i];
                if (existing.ExpiresAt < now)
                {
                    _battlefieldChatterCandidates.RemoveAt(i);
                    continue;
                }

                if (existing.SubjectId == subjectId && existing.PhraseKey == phraseKey)
                    return;
            }

            if (_battlefieldChatterCandidates.Count >= MaxBattlefieldChatterCandidates)
                _battlefieldChatterCandidates.RemoveAt(0);

            Dictionary<string, string> slots = Slots("type", RadioText.SpokenUnitName(subjectName));
            if (extraSlots != null)
            {
                foreach (KeyValuePair<string, string> slot in extraSlots)
                    slots[slot.Key] = slot.Value;
            }

            _battlefieldChatterCandidates.Add(new BattlefieldChatterCandidate
            {
                SubjectId = subjectId,
                PhraseKey = phraseKey,
                Slots = slots,
                CreatedAt = now,
                ExpiresAt = now + BattlefieldChatterCandidateLifetimeSeconds,
                Importance = importance
            });
        }

        private void TryQueueBattlefieldChatter(float now)
        {
            if (!_config.BattlefieldChatter.Value || !_battlefieldChatterTracking)
                return;

            for (int i = _battlefieldChatterCandidates.Count - 1; i >= 0; i--)
            {
                if (_battlefieldChatterCandidates[i].ExpiresAt < now)
                    _battlefieldChatterCandidates.RemoveAt(i);
            }

            if (_battlefieldChatterCandidates.Count == 0 || now < _nextBattlefieldChatterAt ||
                _startupRadioGateActive || _awaitingAwacsCheckIn || _pendingTowerReadbacks.Count > 0 ||
                _queue.Count > 0 || HasAnyRadioAudioWork())
            {
                return;
            }

            int bestIndex = 0;
            for (int i = 1; i < _battlefieldChatterCandidates.Count; i++)
            {
                BattlefieldChatterCandidate candidate = _battlefieldChatterCandidates[i];
                BattlefieldChatterCandidate best = _battlefieldChatterCandidates[bestIndex];
                if (candidate.Importance > best.Importance ||
                    candidate.Importance == best.Importance && candidate.CreatedAt < best.CreatedAt)
                {
                    bestIndex = i;
                }
            }

            BattlefieldChatterCandidate selected = _battlefieldChatterCandidates[bestIndex];
            _battlefieldChatterCandidates.RemoveAt(bestIndex);
            if (Queue(RadioRole.Game, RadioEventType.BattlefieldChatter, selected.PhraseKey, selected.Slots,
                now, BattlefieldChatterPriority, 3f, 0f, false, selected.SubjectId))
            {
                _nextBattlefieldChatterAt = now + BattlefieldChatterIntervalSeconds;
            }
        }

        private bool HasAnyRadioAudioWork()
        {
            return _output.HasAudioWork(RadioRole.Tower) ||
                   _output.HasAudioWork(RadioRole.Awacs) ||
                   _output.HasAudioWork(RadioRole.Player) ||
                   _output.HasAudioWork(RadioRole.PlayerTower) ||
                   _output.HasAudioWork(RadioRole.PlayerFlight) ||
                   _output.HasAudioWork(RadioRole.PlayerAwacs) ||
                   _output.HasAudioWork(RadioRole.Game) ||
                   _output.HasAudioWork(RadioRole.System);
        }

        private void DisableBattlefieldChatter()
        {
            if (!_battlefieldChatterTracking && _battlefieldChatterCandidates.Count == 0)
                return;

            _battlefieldChatterTracking = false;
            _nextBattlefieldChatterAt = 0f;
            _friendlyAircraftStates.Clear();
            _battlefieldChatterCandidates.Clear();
            _queue.RemoveAll(item => item.Type == RadioEventType.BattlefieldChatter);
            _output.StopTransmissions(RadioEventType.BattlefieldChatter);
        }

        private bool HasQueuedTransmission(RadioEventType type)
        {
            for (int i = 0; i < _queue.Count; i++)
            {
                if (_queue[i].Type == type)
                    return true;
            }

            return false;
        }

        private bool HasQueuedTransmission(RadioRole role)
        {
            for (int i = 0; i < _queue.Count; i++)
            {
                if (_queue[i].Role == role)
                    return true;
            }

            return false;
        }

        private bool ShouldHoldStartupAwacs(float now, bool urgent)
        {
            if (urgent || !_startupRadioGateActive || _startupAwacsReleased)
                return false;

            return float.IsNaN(_startupRadioGateStartedAt) ||
                   now - _startupRadioGateStartedAt <= StartupAwacsMaxHoldSeconds;
        }

        private static bool IsStartupAwacsHoldCandidate(PendingTransmission transmission)
        {
            return transmission.Role == RadioRole.Awacs &&
                   !transmission.BypassStartupHold &&
                   transmission.Type != RadioEventType.MissileThreat;
        }

        private void HoldStartupAwacs(PendingTransmission transmission)
        {
            if (_startupAwacsTransmissions.Count >= MaxHeldStartupAwacsLines)
                _startupAwacsTransmissions.RemoveAt(0);

            _startupAwacsTransmissions.Add(transmission);
            _log.LogDebug($"Holding startup AWACS line until after takeoff sequence: {transmission.Text}");
        }

        private void ParkQueuedStartupAwacs()
        {
            if (!_startupRadioGateActive || _startupAwacsReleased)
                return;

            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                PendingTransmission transmission = _queue[i];
                if (!IsStartupAwacsHoldCandidate(transmission))
                    continue;

                _queue.RemoveAt(i);
                HoldStartupAwacs(transmission);
            }
        }

        private void TryReleaseHeldStartupAwacs(Snapshot snapshot)
        {
            if (!_startupRadioGateActive || _startupAwacsReleased)
                return;

            float now = snapshot.Time;

            // A ground takeoff hands the player from Tower to AWACS. While the player is still on
            // the ground waiting to roll, keep the max-hold clock from expiring — a long taxi must
            // not release AWACS before the handoff, which cannot happen until the player is airborne.
            if (_takeoffClearanceAnnounced && !_airborneAnnounced && snapshot.Player.Grounded)
                _startupRadioGateStartedAt = now;

            bool timedOut = !float.IsNaN(_startupRadioGateStartedAt) &&
                            now - _startupRadioGateStartedAt > StartupAwacsMaxHoldSeconds;

            if (!timedOut)
            {
                if (!_takeoffClearanceAnnounced &&
                    (IsStartupTakeoffCandidate(snapshot) || StartupStateStillSettling(snapshot)))
                    return;

                // Hold AWACS until Tower has handed the player off with the airborne "switch to
                // {awacs}" call and that exchange has finished playing.
                if (IsWaitingForAirborneHandoff())
                    return;

                if (IsWaitingForTakeoffReadback(now))
                    return;

                if (_startupWingmanHeld || HasQueuedTransmission(RadioRole.Game) || _output.HasAudioWork(RadioRole.Game))
                    return;

                // The takeoff exchange is done and no mission comm is pending — but the first
                // scripted mission message often arrives a few seconds into the mission. Give
                // it a grace window so AWACS does not talk over comms that are about to start.
                if (!_startupMissionCommsSeen)
                {
                    if (float.IsNaN(_startupTakeoffSequenceDoneAt))
                        _startupTakeoffSequenceDoneAt = now;

                    if (now - _startupTakeoffSequenceDoneAt < StartupMissionCommsGraceSeconds)
                        return;
                }
            }

            for (int i = 0; i < _startupAwacsTransmissions.Count; i++)
            {
                PendingTransmission transmission = _startupAwacsTransmissions[i];
                transmission.CreatedAt = now;
                transmission.AvailableAt = now + i * 0.25f;
                transmission.ExpiresAt = transmission.AvailableAt + Mathf.Max(transmission.DisplaySeconds + 10f, 15f);
                _queue.Add(transmission);
            }

            _startupAwacsTransmissions.Clear();
            _startupAwacsReleased = true;
            _startupRadioGateActive = false;
            ReleasePendingGroundSupportHails(now);
            _log.LogDebug(timedOut
                ? "Startup radio gate released (timeout)."
                : "Startup radio gate released (takeoff sequence complete or not applicable).");
        }

        private void ReleasePendingGroundSupportHails(float now)
        {
            if (_stableGrounded != false)
                return;

            for (int i = 0; i < _groundSupportGroups.Count; i++)
            {
                GroundSupportGroup group = _groundSupportGroups[i];
                if (group.Pending && !group.Closed && !group.Dismissed)
                    group.NextHailAt = Mathf.Min(group.NextHailAt, now);
            }
        }

        /// <summary>With voice commands on and RequestDriven set, clearances and AWACS info are
        /// pull, not push: no automatic takeoff/approach/landing clearances and no periodic
        /// picture/vector/RTB-advisory calls — the player keys up and asks. Event-driven calls
        /// stay automatic: new contacts, missile warnings, splashes, bingo fuel, the airborne
        /// handoff, and welcome-home. The startup radio gate still holds AWACS while the player
        /// sits on the ramp (up to the usual 90 s cap).</summary>
        private bool RequestDrivenComms()
        {
            return _config.VoiceCommandsEnabled.Value && _config.VoiceRequestDriven.Value;
        }

        private bool SpokenTowerReadbacksEnabled()
        {
            return _config.VoiceCommandsEnabled.Value && _config.VoiceRequireTowerReadbacks.Value;
        }

        private void DetectTower(Snapshot snapshot)
        {
            AirbaseInfo home;
            float distanceM;
            bool hasHomeBase = TryGetHomeBase(snapshot, out home, out distanceM);
            bool nearBase = hasHomeBase && IsNearAirbase(home, distanceM);

            if (snapshot.Player.Grounded)
            {
                _groundedTicks++;
                _airborneTicks = 0;
            }
            else
            {
                _airborneTicks++;
                _groundedTicks = 0;
            }

            if (_stableGrounded != true && _groundedTicks >= StableTicksRequired)
            {
                bool wasAirborne = _stableGrounded == false;
                _stableGrounded = true;

                if (wasAirborne)
                {
                    _awaitingAwacsCheckIn = false;
                    ResetMissionRoleToGeneral("landing", snapshot.Time, true);
                }

                if (!wasAirborne && nearBase && _config.TakeoffCalls.Value && !_takeoffClearanceAnnounced &&
                    !RequestDrivenComms())
                {
                    _takeoffClearanceAnnounced = true;
                    _takeoffClearanceQueuedAt = snapshot.Time;
                    if (!_startupRadioGateActive && !_startupAwacsReleased)
                        StartStartupRadioGate(snapshot.Time);
                    else
                        ParkQueuedStartupAwacs();

                    Queue(RadioRole.Tower, RadioEventType.TowerTakeoff, "tower_takeoff",
                        TowerSlots(home), snapshot.Time, 70, 4f, 0f, false);
                }

                if (wasAirborne && nearBase && _config.LandingCalls.Value && !_landedAnnounced)
                {
                    _landedAnnounced = true;
                    Queue(RadioRole.Tower, RadioEventType.TowerLanded, "tower_landed", CommonSlots(), snapshot.Time, 70, 4f, 0f, false);
                }
            }
            else if (_stableGrounded != false && _airborneTicks >= StableTicksRequired)
            {
                bool wasGrounded = _stableGrounded == true;
                _stableGrounded = false;
                _successfulAirportLanding = false;
                ReleasePendingGroundSupportHails(snapshot.Time);

                if (wasGrounded)
                    ResetMissionRoleToGeneral("takeoff", snapshot.Time, true);

                if (wasGrounded && nearBase && _config.TakeoffCalls.Value && !_airborneAnnounced)
                {
                    _airborneAnnounced = true;
                    _awaitingAwacsCheckIn = _config.VoiceCommandsEnabled.Value;
                    Queue(RadioRole.Tower, RadioEventType.TowerAirborne, "tower_airborne", CommonSlots(), snapshot.Time, 70, 4f, 0f, false);
                }
            }
        }

        private void DetectApproach(Snapshot snapshot)
        {
            if (snapshot.Player.Grounded || !_config.ApproachCalls.Value)
                return;

            if (!TryGetHomeBase(snapshot, out AirbaseInfo home, out float distanceM))
                return;

            bool closing = float.IsNaN(_previousHomeDistance) || distanceM < _previousHomeDistance - ApproachMinClosingMeters;
            _previousHomeDistance = distanceM;

            if (distanceM > ApproachResetDistanceM)
            {
                _approachAnnounced = false;
                _finalAnnounced = false;
                _approachInboundStartedAt = float.NaN;
                _approachLastInboundAt = float.NaN;
                return;
            }

            bool descending = snapshot.Player.Velocity.y < -1.5f;
            bool pointedAtBase = IsPointedAt(snapshot.Player.Position, snapshot.Player.HeadingDeg, home.Position, ApproachHeadingToleranceDeg);
            bool belowApproachCeiling = snapshot.Player.AltitudeAglM < ApproachMaxAltitudeAglM;
            bool approachIntent = pointedAtBase || descending || snapshot.Player.GearDown || distanceM < ApproachCloseRangeM;
            bool inbound = closing && belowApproachCeiling && approachIntent;

            if (inbound)
            {
                if (float.IsNaN(_approachInboundStartedAt))
                    _approachInboundStartedAt = snapshot.Time;

                _approachLastInboundAt = snapshot.Time;
            }
            else if (float.IsNaN(_approachLastInboundAt) || snapshot.Time - _approachLastInboundAt > ApproachInboundGraceSeconds)
            {
                _approachInboundStartedAt = float.NaN;
                _approachLastInboundAt = float.NaN;
            }

            bool inboundLongEnough = !float.IsNaN(_approachInboundStartedAt) &&
                                     snapshot.Time - _approachInboundStartedAt >= ApproachRequiredInboundSeconds;

            // The distance/inbound state above keeps updating even in request-driven mode so a
            // mid-flight toggle behaves; only the announcement itself is pull-only.
            if (!_approachAnnounced && distanceM < ApproachCallDistanceM && inboundLongEnough && !RequestDrivenComms())
            {
                _approachAnnounced = true;
                Queue(RadioRole.Tower, RadioEventType.TowerApproach, "tower_approach", CommonSlots(), snapshot.Time, 70, 4f, 20f, false);
            }
        }

        /// <summary>Quiet automatic combat information only from an explicit player command.
        /// Weapon state and flight direction are deliberately not treated as intent.
        /// This never gates urgent calls or direct voice-command responses.</summary>
        private void UpdateRoutineAwacsQuietState(Snapshot snapshot)
        {
            bool quiet = _awacsTrafficMode == AwacsTrafficMode.Quiet ||
                         _awacsTrafficMode == AwacsTrafficMode.Winchester;
            if (quiet == _routineAwacsQuiet)
                return;

            _routineAwacsQuiet = quiet;
            if (!quiet)
            {
                ReleasePendingGroundSupportHails(snapshot.Time);
                _log.LogDebug("Routine AWACS automatic callouts restored.");
                return;
            }

            // Forced/player-requested contact calls use BypassStartupHold and are preserved.
            _queue.RemoveAll(item => IsContactInfoCall(item.Type) && !item.BypassStartupHold);
            _startupAwacsTransmissions.RemoveAll(item => IsContactInfoCall(item.Type));
            if (_awacsTrafficMode == AwacsTrafficMode.Winchester)
                StopGroundSupportHails();
            _previousRtbHomeDistance = float.NaN;
            _rtbInboundStartedAt = float.NaN;
            _rtbLastInboundAt = float.NaN;
            string reason = _awacsTrafficMode == AwacsTrafficMode.Quiet
                ? "player-requested radio quiet"
                : "player-declared Winchester";
            _log.LogInfo($"Routine AWACS automatic callouts quieted: {reason}.");
        }

        private void DetectRtb(Snapshot snapshot)
        {
            if (!_config.RtbCalls.Value || snapshot.Player.Grounded || snapshot.Player.Ejected)
                return;

            if (!TryGetHomeBase(snapshot, out AirbaseInfo home, out float distanceM))
                return;

            if (!_rtbFuelAnnounced && snapshot.Player.FuelFraction > 0f && snapshot.Player.FuelFraction <= RtbFuelBingoFraction)
            {
                _rtbFuelAnnounced = true;
                Queue(RadioRole.Awacs, RadioEventType.RtbFuel, "awacs_rtb_fuel", Slots(
                    "callsign", _config.PlayerCallsign.Value,
                    "awacs", _config.AwacsCallsign.Value,
                    "bearing", NumberSpeech.Bearing(GPos.Bearing(snapshot.Player.Position, home.Position)),
                    "range", RadioText.FormatRange(distanceM, snapshot.Units)), snapshot.Time, 55, 4f, 300f, false);
            }

            // The bingo-fuel warning above stays automatic. The periodic "continue RTB" vector
            // is pull-only in request-driven mode and is omitted while routine AWACS is quiet.
            if (RequestDrivenComms() || _routineAwacsQuiet)
                return;

            bool inRtbVectorRange = distanceM >= RtbVectorMinDistanceM && distanceM <= RtbVectorMaxDistanceM;
            if (!inRtbVectorRange)
            {
                _previousRtbHomeDistance = float.NaN;
                _rtbInboundStartedAt = float.NaN;
                _rtbLastInboundAt = float.NaN;
                return;
            }

            bool closing = float.IsNaN(_previousRtbHomeDistance) ||
                           distanceM < _previousRtbHomeDistance - RtbVectorMinClosingMeters;
            _previousRtbHomeDistance = distanceM;

            bool pointedAtHome = IsPointedAt(snapshot.Player.Position, snapshot.Player.HeadingDeg,
                home.Position, RtbVectorHeadingToleranceDeg);
            bool inbound = closing && pointedAtHome && snapshot.Player.SpeedMs > 60f;

            if (inbound)
            {
                if (float.IsNaN(_rtbInboundStartedAt))
                    _rtbInboundStartedAt = snapshot.Time;

                _rtbLastInboundAt = snapshot.Time;
            }
            else if (float.IsNaN(_rtbLastInboundAt) ||
                     snapshot.Time - _rtbLastInboundAt > RtbVectorInboundGraceSeconds)
            {
                _rtbInboundStartedAt = float.NaN;
                _rtbLastInboundAt = float.NaN;
            }

            bool inboundLongEnough = !float.IsNaN(_rtbInboundStartedAt) &&
                                     snapshot.Time - _rtbInboundStartedAt >= RtbVectorRequiredInboundSeconds;

            if (!inboundLongEnough)
                return;

            Queue(RadioRole.Awacs, RadioEventType.RtbVector, "awacs_rtb_vector", Slots(
                "callsign", _config.PlayerCallsign.Value,
                "awacs", _config.AwacsCallsign.Value,
                "bearing", NumberSpeech.Bearing(GPos.Bearing(snapshot.Player.Position, home.Position)),
                "range", RadioText.FormatRange(distanceM, snapshot.Units)), snapshot.Time, 35, 4f, RtbVectorCooldownSeconds, false);
        }

        private void DetectVector(Snapshot snapshot)
        {
            if (!_config.VectorToTargetCalls.Value || !snapshot.HasSelectedTarget ||
                RequestDrivenComms() || _routineAwacsQuiet)
                return;

            if (snapshot.Time < _suppressRoutineAwacsUntil)
                return;

            float rangeM = GPos.Distance2D(snapshot.Player.Position, snapshot.SelectedTarget.Position);
            if (rangeM <= VectorSuppressDistanceM)
                return;

            QueueContact(snapshot, snapshot.SelectedTarget, RadioEventType.VectorToTarget, "awacs_vector", 40, 5f, _config.VectorIntervalSeconds.Value);
        }

        private void DetectPicture(Snapshot snapshot)
        {
            if (!_config.PictureUpdateCalls.Value || snapshot.Contacts.Count == 0 ||
                RequestDrivenComms() || _routineAwacsQuiet)
                return;

            if (snapshot.Time < _suppressPictureUntil || snapshot.Time < _suppressRoutineAwacsUntil)
                return;

            // The vector call owns the selected target; picture covers the remaining threats.
            uint excludeId = _config.VectorToTargetCalls.Value && snapshot.HasSelectedTarget
                ? snapshot.SelectedTarget.Id
                : 0;

            if (!TryNearestContact(snapshot, excludeId, out ContactInfo contact))
                return;

            QueueContact(snapshot, contact, RadioEventType.PictureUpdate, "awacs_picture", 30, 5f, _config.PictureIntervalSeconds.Value);
        }

        private void HandleVoiceCommand(Snapshot snapshot, string transcript, float now)
        {
            string text = transcript == null ? string.Empty : transcript.Trim();
            if (!SpeechTranscriptFilter.HasWords(text))
                return;

            VoiceIntent intent = VoiceIntentParser.Parse(text, _config.AwacsCallsign.Value, _config.PlayerCallsign.Value);
            string callsign = string.IsNullOrEmpty(intent.Callsign) ? _config.PlayerCallsign.Value : intent.Callsign;
            _log.LogInfo($"Voice command: \"{text}\" -> {intent.Kind} ({intent.Station}, callsign \"{callsign}\")");

            if (text.Length > 0 && _config.VoiceShowRecognizedText.Value)
                _output.ShowSubtitle(RadioRole.Player, text, 4f);

            // Ground traffic is addressed directly rather than through Tower/AWACS format.
            // Acceptances require both callsigns; a decline needs the ground callsign plus
            // unmistakable negative wording such as "unable".
            if ((intent.Station == VoiceStation.Unspecified || VoiceIntentParser.IsGroundSupportDecline(text)) &&
                TryHandleGroundSupportTransmission(snapshot, text, now))
                return;

            if (TryHandleTowerReadback(text, intent, now))
                return;

            // Radio discipline: a proper call is "<station>, [this is] <callsign>, <request>".
            // A malformed call gets a corrective reply instead of an answer.
            // "mission <role>" is itself an explicit mode-selection command. Keep it usable as
            // a terse cockpit control even when ordinary radio requests require full phraseology.
            bool explicitMissionCommand = intent.Kind == VoiceIntentKind.SetMissionRole &&
                                          VoiceIntentParser.ContainsMissionCommandWord(text);
            bool explicitStateDeclaration = intent.Kind == VoiceIntentKind.DeclareWinchester;
            if (_config.VoiceRequireProperCalls.Value && !explicitMissionCommand && !explicitStateDeclaration)
            {
                if (!intent.StationAddressed)
                {
                    QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse, "radio_format_unaddressed", VoiceSlots(callsign), now);
                    return;
                }

                if (!intent.CallsignSpoken)
                {
                    if (intent.Station == VoiceStation.Tower)
                        QueueVoiceResponse(RadioRole.Tower, RadioEventType.VoiceCommandResponse, "radio_format_no_callsign_tower", VoiceSlots(callsign), now);
                    else
                        QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse, "radio_format_no_callsign_awacs", VoiceSlots(callsign), now);
                    return;
                }
            }

            switch (intent.Kind)
            {
                case VoiceIntentKind.RequestTakeoff:
                    RespondTakeoff(snapshot, now, callsign);
                    break;
                case VoiceIntentKind.RequestLanding:
                    RespondLanding(snapshot, now, callsign);
                    break;
                case VoiceIntentKind.RequestPicture:
                    RespondPicture(snapshot, now, callsign);
                    break;
                case VoiceIntentKind.RequestVector:
                    if (!TryRespondNamedGroundSupportVector(snapshot, text, now, callsign))
                        RespondVector(snapshot, now, callsign);
                    break;
                case VoiceIntentKind.RequestVectorGroundSupport:
                    RespondGroundSupportVector(snapshot, text, now, callsign);
                    break;
                case VoiceIntentKind.RequestVectorObjective:
                    RespondVectorObjective(snapshot, now, callsign, intent.ObjectiveQuery);
                    break;
                case VoiceIntentKind.RequestObjectiveList:
                    RespondObjectiveList(snapshot, now, callsign);
                    break;
                case VoiceIntentKind.RequestVectorHome:
                    RespondVectorHome(snapshot, now, callsign);
                    break;
                case VoiceIntentKind.DeclareWinchester:
                    SetAwacsTrafficMode(AwacsTrafficMode.Winchester, now, callsign);
                    break;
                case VoiceIntentKind.RequestAwacsQuiet:
                    SetAwacsTrafficMode(AwacsTrafficMode.Quiet, now, callsign);
                    break;
                case VoiceIntentKind.RequestAwacsResume:
                    SetAwacsTrafficMode(AwacsTrafficMode.Normal, now, callsign);
                    break;
                case VoiceIntentKind.SetMissionRole:
                    RespondMissionRoleCheckIn(snapshot, intent.MissionRole, now, callsign);
                    break;
                case VoiceIntentKind.CheckIn:
                    if (intent.Station == VoiceStation.Tower)
                        QueueVoiceResponse(RadioRole.Tower, RadioEventType.VoiceCommandResponse, "say_again_tower", VoiceSlots(callsign), now);
                    else
                        RespondAwacsCheckIn(snapshot, now, callsign);
                    break;
                case VoiceIntentKind.RadioCheck:
                    if (intent.Station == VoiceStation.Tower)
                        QueueVoiceResponse(RadioRole.Tower, RadioEventType.VoiceCommandResponse, "radio_check_tower", VoiceSlots(callsign), now);
                    else
                        QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse, "radio_check_awacs", VoiceSlots(callsign), now);
                    break;
                default:
                    if (intent.Station == VoiceStation.Tower)
                        QueueVoiceResponse(RadioRole.Tower, RadioEventType.VoiceCommandResponse, "say_again_tower", VoiceSlots(callsign), now);
                    else
                        QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse, "say_again_awacs", VoiceSlots(callsign), now);
                    break;
            }
        }

        private void RespondMissionRoleCheckIn(
            Snapshot snapshot,
            FlightMissionRole role,
            float now,
            string callsign)
        {
            bool groundWasSuppressed = SuppressGroundSupportHails();
            _flightMissionRole = role;
            _awaitingAwacsCheckIn = false;
            _output.ClearAwacsCheckInPrompt();

            if (SuppressGroundSupportHails())
            {
                _queue.RemoveAll(item => item.Type == RadioEventType.GroundSupportHail);
                _output.StopTransmissions(RadioEventType.GroundSupportHail);
            }
            else if (groundWasSuppressed)
            {
                for (int i = 0; i < _groundSupportGroups.Count; i++)
                {
                    GroundSupportGroup group = _groundSupportGroups[i];
                    if (group.Pending && !group.Closed && !group.Dismissed)
                        group.NextHailAt = Mathf.Min(group.NextHailAt, now);
                }
            }

            if (SuppressAutomaticAirContacts())
            {
                _queue.RemoveAll(item => item.Type == RadioEventType.NewContact);
                _startupAwacsTransmissions.RemoveAll(item => item.Type == RadioEventType.NewContact);
                _output.StopTransmissions(RadioEventType.NewContact);
            }

            Dictionary<string, string> slots = VoiceSlots(callsign);
            if (role == FlightMissionRole.Sead)
            {
                RadarEmitterInfo emitter;
                if (TryNearestRadarEmitter(snapshot, out emitter))
                {
                    slots["bearing"] = NumberSpeech.Bearing(GPos.Bearing(snapshot.Player.Position, emitter.Position));
                    slots["range"] = RadioText.FormatRange(
                        GPos.Distance2D(snapshot.Player.Position, emitter.Position), snapshot.Units);
                    slots["type"] = RadioText.SpokenUnitName(emitter.DisplayName);
                    QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse,
                        "awacs_mission_sead", slots, now);
                }
                else
                {
                    QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse,
                        "awacs_mission_sead_clean", slots, now);
                }
            }
            else if (role == FlightMissionRole.None)
            {
                QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse,
                    "awacs_mission_general", slots, now);
            }
            else
            {
                slots["mission"] = MissionRoleSpeech(role);
                QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse,
                    "awacs_mission_check_in", slots, now);
            }

            _log.LogInfo($"Flight mission role set to {role}; ground hails suppressed={SuppressGroundSupportHails()}, new-contact calls suppressed={SuppressAutomaticAirContacts()}.");
        }

        private void ResetMissionRoleToGeneral(string reason, float now, bool releaseGroundHails)
        {
            if (_flightMissionRole == FlightMissionRole.None)
                return;

            bool groundWasSuppressed = SuppressGroundSupportHails();
            FlightMissionRole previous = _flightMissionRole;
            _flightMissionRole = FlightMissionRole.None;

            if (releaseGroundHails && groundWasSuppressed)
            {
                for (int i = 0; i < _groundSupportGroups.Count; i++)
                {
                    GroundSupportGroup group = _groundSupportGroups[i];
                    if (group.Pending && !group.Closed && !group.Dismissed)
                        group.NextHailAt = Mathf.Min(group.NextHailAt, now);
                }
            }

            _log.LogInfo($"Flight mission role reset from {previous} to General: {reason}.");
        }

        private bool SuppressGroundSupportHails()
        {
            return FlightMissionRolePolicy.SuppressGroundSupportHails(_flightMissionRole) ||
                   _awacsTrafficMode == AwacsTrafficMode.Winchester;
        }

        private void StopGroundSupportHails()
        {
            _queue.RemoveAll(item => item.Type == RadioEventType.GroundSupportHail);
            _output.StopTransmissions(RadioEventType.GroundSupportHail);
        }

        private bool SuppressAutomaticAirContacts()
        {
            return FlightMissionRolePolicy.SuppressAutomaticAirContacts(_flightMissionRole);
        }

        private static string MissionRoleSpeech(FlightMissionRole role)
        {
            switch (role)
            {
                case FlightMissionRole.Cap: return "combat air patrol";
                case FlightMissionRole.Cas: return "close air support";
                case FlightMissionRole.Strike: return "strike";
                case FlightMissionRole.SearchAndDestroy: return "search and destroy";
                default: return "general mission";
            }
        }

        private static bool TryNearestRadarEmitter(Snapshot snapshot, out RadarEmitterInfo nearest)
        {
            nearest = default;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < snapshot.RadarEmitters.Count; i++)
            {
                RadarEmitterInfo candidate = snapshot.RadarEmitters[i];
                float distance = GPos.Distance2D(snapshot.Player.Position, candidate.Position);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                nearest = candidate;
            }

            return !float.IsPositiveInfinity(bestDistance);
        }

        private void SetAwacsTrafficMode(AwacsTrafficMode mode, float now, string callsign)
        {
            _awacsTrafficMode = mode;
            if (SuppressGroundSupportHails())
                StopGroundSupportHails();
            else
                ReleasePendingGroundSupportHails(now);

            string phraseKey = mode == AwacsTrafficMode.Winchester
                ? "awacs_winchester"
                : mode == AwacsTrafficMode.Quiet
                    ? "awacs_radio_quiet"
                    : "awacs_radio_resumed";
            QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse,
                phraseKey, VoiceSlots(callsign), now);
            _log.LogInfo($"AWACS traffic mode set to {mode} by player voice command.");
        }

        private bool TryHandleGroundSupportTransmission(Snapshot snapshot, string text, float now)
        {
            if (!GroundSupportEnabled())
                return false;

            bool decline = VoiceIntentParser.IsGroundSupportDecline(text);
            if (!decline && !VoiceIntentParser.ContainsSpokenCallsign(text, _config.PlayerCallsign.Value))
                return false;

            GroundSupportGroup selected = null;
            for (int i = 0; i < _groundSupportGroups.Count; i++)
            {
                GroundSupportGroup candidate = _groundSupportGroups[i];
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
                for (int i = 0; i < _groundSupportGroups.Count; i++)
                {
                    GroundSupportGroup candidate = _groundSupportGroups[i];
                    if (candidate.Closed || candidate.Dismissed || (!candidate.Pending && !candidate.Accepted))
                        continue;

                    string stem = GroundSupportCallsignStem(candidate.Callsign);
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
                DeclineGroundSupport(selected, now);
                return true;
            }

            if (selected.Accepted && _activeGroundSupportGroup == selected)
            {
                QueueGroundSupportAcknowledgement(snapshot, selected, _config.PlayerCallsign.Value, now, true);
                _log.LogInfo($"Ground support follow-up addressed to {selected.Callsign}.");
                return true;
            }

            if (_activeGroundSupportGroup != null && _activeGroundSupportGroup != selected)
            {
                GroundSupportGroup previous = _activeGroundSupportGroup;
                previous.Accepted = false;
                previous.Pending = false;
                previous.Dismissed = true;
                previous.DismissedAt = now;
                previous.Resolved = false;
                RemoveQueuedGroundSupport(previous.Id);
                _log.LogInfo($"Ground support task switched from {previous.Callsign} to {selected.Callsign}.");
            }

            _activeGroundSupportGroup = selected;
            selected.Pending = false;
            selected.Accepted = true;
            selected.Dismissed = false;
            selected.Resolved = false;
            RemoveQueuedGroundSupport(selected.Id);
            CancelGroundSupportHailAudioAndRescheduleOthers(selected, now);

            // Future player-requested vectors now resolve to this group until another hail is accepted.
            CancelVectorCalls();
            _queue.RemoveAll(item => item.Type == RadioEventType.GroundSupportVector);
            _output.StopTransmissions(RadioEventType.GroundSupportVector);
            QueueGroundSupportAcknowledgement(snapshot, selected, _config.PlayerCallsign.Value, now, false);
            _log.LogInfo($"Ground support request accepted: {selected.Callsign}.");
            return true;
        }

        private void DeclineGroundSupport(GroundSupportGroup group, float now)
        {
            bool wasActive = _activeGroundSupportGroup == group;
            group.Pending = false;
            group.Accepted = false;
            group.Dismissed = true;
            group.DismissedAt = now;
            group.Resolved = false;

            RemoveQueuedGroundSupport(group.Id);
            CancelGroundSupportHailAudioAndRescheduleOthers(group, now);

            if (wasActive)
            {
                _activeGroundSupportGroup = null;
                _queue.RemoveAll(item => item.Type == RadioEventType.GroundSupportVector);
                _output.StopTransmissions(RadioEventType.GroundSupportVector);
            }

            Queue(RadioRole.Game, RadioEventType.GroundSupportDeclined,
                "ground_support_declined", Slots("ground_callsign", group.Callsign),
                now, VoiceResponsePriority, 4f, 0f, false,
                subjectId: group.Id,
                duplicateWindowSecondsOverride: 0f,
                bypassStartupHold: true);
            _log.LogInfo($"Ground support request declined: {group.Callsign}.");
        }

        private static string GroundSupportCallsignStem(string callsign)
        {
            if (string.IsNullOrEmpty(callsign))
                return string.Empty;

            int separator = callsign.IndexOf(' ');
            return separator > 0 ? callsign.Substring(0, separator) : callsign;
        }

        private void RespondAwacsCheckIn(Snapshot snapshot, float now, string callsign)
        {
            if (_flightMissionRole != FlightMissionRole.None)
            {
                RespondMissionRoleCheckIn(snapshot, _flightMissionRole, now, callsign);
                return;
            }

            _awaitingAwacsCheckIn = false;
            _output.ClearAwacsCheckInPrompt();
            QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse,
                "awacs_check_in", VoiceSlots(callsign), now);
            _log.LogInfo($"AWACS check-in complete for {callsign}.");
        }

        private bool TryHandleTowerReadback(string text, VoiceIntent intent, float now)
        {
            if (!SpokenTowerReadbacksEnabled() || _pendingTowerReadbacks.Count == 0)
                return false;

            for (int i = 0; i < _pendingTowerReadbacks.Count; i++)
            {
                TowerReadbackExpectation expectation = _pendingTowerReadbacks[i].Expectation;
                if (!TowerReadbackMatcher.IsMatch(text, expectation))
                    continue;

                _pendingTowerReadbacks.RemoveAt(i);
                _output.ClearReadbackPrompt(expectation.Kind);
                _log.LogInfo($"Accepted spoken Tower {expectation.Kind.ToString().ToLowerInvariant()} readback: \"{text}\"");
                return true;
            }

            // A command explicitly sent to AWACS remains an AWACS command even while Tower is
            // waiting. Otherwise only readback-like speech (or a Tower-addressed transmission)
            // is intercepted, so unrelated unaddressed commands retain their existing behavior.
            if (intent.Station == VoiceStation.Awacs)
                return false;

            PendingTowerReadback pendingReadback = _pendingTowerReadbacks[0];
            TowerReadbackExpectation pending = pendingReadback.Expectation;
            if (intent.Station != VoiceStation.Tower && !TowerReadbackMatcher.LooksLikeAttempt(text, pending))
                return false;

            HandleFailedTowerReadback(0, now, true, text);
            return true;
        }

        private void AddPendingTowerReadback(TowerReadbackExpectation expectation, float now)
        {
            for (int i = _pendingTowerReadbacks.Count - 1; i >= 0; i--)
            {
                if (_pendingTowerReadbacks[i].Expectation.Kind == expectation.Kind)
                    _pendingTowerReadbacks.RemoveAt(i);
            }

            _pendingTowerReadbacks.Add(new PendingTowerReadback
            {
                Expectation = expectation,
                AwaitingSince = now
            });
        }

        private bool HasPendingTowerReadback(TowerReadbackKind kind)
        {
            for (int i = 0; i < _pendingTowerReadbacks.Count; i++)
            {
                if (_pendingTowerReadbacks[i].Expectation.Kind == kind)
                    return true;
            }

            return false;
        }

        private void PromptForMissingTowerReadbacks(float now)
        {
            if (!SpokenTowerReadbacksEnabled())
                return;

            for (int i = _pendingTowerReadbacks.Count - 1; i >= 0; i--)
            {
                PendingTowerReadback pending = _pendingTowerReadbacks[i];
                if (now - pending.AwaitingSince < TowerReadbackResponseSeconds)
                    continue;

                HandleFailedTowerReadback(i, now, false, null);
            }
        }

        private void HandleFailedTowerReadback(int index, float now, bool incorrect, string transcript)
        {
            PendingTowerReadback pending = _pendingTowerReadbacks[index];
            TowerReadbackExpectation expectation = pending.Expectation;
            pending.FailedAttempts++;

            if (pending.FailedAttempts >= TowerReadbackMaxAttempts)
            {
                _pendingTowerReadbacks.RemoveAt(index);
                _output.ClearReadbackPrompt(expectation.Kind);
                ApplyFailedReadbackState(expectation.Kind);

                Dictionary<string, string> finalSlots = VoiceSlots(expectation.Callsign);
                finalSlots["outcome"] = FailedReadbackOutcome(expectation.Kind);
                QueueVoiceResponse(RadioRole.Tower, RadioEventType.VoiceCommandResponse,
                    "tower_readback_failed", finalSlots, now);

                _log.LogInfo($"Tower stopped waiting for {expectation.Kind.ToString().ToLowerInvariant()} readback after {TowerReadbackMaxAttempts} failed attempts.");
                return;
            }

            Dictionary<string, string> slots = VoiceSlots(expectation.Callsign);
            if (incorrect)
            {
                QueueVoiceResponse(RadioRole.Tower, RadioEventType.VoiceCommandResponse,
                    "tower_readback_incorrect", slots, now);
                _log.LogInfo($"Rejected incomplete Tower {expectation.Kind.ToString().ToLowerInvariant()} readback: \"{transcript}\"");
            }
            else
            {
                slots["instruction"] = ReadbackInstruction(expectation.Kind);
                QueueVoiceResponse(RadioRole.Tower, RadioEventType.VoiceCommandResponse,
                    "tower_readback_missing", slots, now);
                _log.LogInfo($"Tower requested missing {expectation.Kind.ToString().ToLowerInvariant()} readback from {expectation.Callsign}.");
            }

            pending.AwaitingSince = now;
            _pendingTowerReadbacks[index] = pending;
        }

        private void ApplyFailedReadbackState(TowerReadbackKind kind)
        {
            if (kind == TowerReadbackKind.Landing)
            {
                _finalAnnounced = false;
                _approachAnnounced = false;
            }
            else if (kind == TowerReadbackKind.Handoff)
            {
                _awaitingAwacsCheckIn = false;
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

        private void ClearTowerReadbackPrompts()
        {
            _output.ClearReadbackPrompt(TowerReadbackKind.Takeoff);
            _output.ClearReadbackPrompt(TowerReadbackKind.Landing);
            _output.ClearReadbackPrompt(TowerReadbackKind.Handoff);
        }

        private void UpdateAwacsCheckInPrompt()
        {
            if (!_awaitingAwacsCheckIn)
            {
                _output.ClearAwacsCheckInPrompt();
                return;
            }

            // Do not ask the player to report to AWACS until Tower's handoff and either the
            // automatic or player-spoken handoff readback have completely left the radio lane.
            if (HasQueuedTransmission(RadioEventType.TowerAirborne) ||
                _output.HasAudioWork(RadioRole.Tower) ||
                _output.HasAudioWork(RadioRole.PlayerTower) ||
                HasPendingTowerReadback(TowerReadbackKind.Handoff))
            {
                return;
            }

            _output.ShowAwacsCheckInPrompt(_config.AwacsCallsign.Value, _config.PlayerCallsign.Value);
        }

        private Dictionary<string, string> VoiceSlots(string callsign)
        {
            Dictionary<string, string> slots = CommonSlots();
            slots["callsign"] = callsign;
            return slots;
        }

        private void RespondTakeoff(Snapshot snapshot, float now, string callsign)
        {
            if (TryGetHomeBase(snapshot, out AirbaseInfo home, out float distanceM) &&
                IsNearAirbase(home, distanceM) && snapshot.Player.Grounded)
            {
                // A voice-granted clearance drives the same state as the automatic one, so the
                // startup gate and the auto tower sequence stay consistent.
                _takeoffClearanceAnnounced = true;
                _takeoffClearanceQueuedAt = now;

                Dictionary<string, string> slots = TowerSlots(home);
                slots["callsign"] = callsign;
                QueueVoiceResponse(RadioRole.Tower, RadioEventType.TowerTakeoff, "tower_takeoff", slots, now);
            }
            else
            {
                QueueVoiceResponse(RadioRole.Tower, RadioEventType.VoiceCommandResponse, "tower_unable", VoiceSlots(callsign), now);
            }
        }

        private void RespondLanding(Snapshot snapshot, float now, string callsign)
        {
            if (!snapshot.Player.Grounded && TryGetHomeBase(snapshot, out AirbaseInfo home, out float distanceM))
            {
                if (distanceM < ApproachResetDistanceM)
                {
                    _finalAnnounced = true;
                    _approachAnnounced = true;
                    Dictionary<string, string> slots = TowerSlots(home);
                    slots["callsign"] = callsign;
                    QueueVoiceResponse(RadioRole.Tower, RadioEventType.TowerFinal, "tower_final", slots, now);
                }
                else
                {
                    Dictionary<string, string> slots = VoiceSlots(callsign);
                    slots["range"] = RadioText.FormatRange(distanceM, snapshot.Units);
                    QueueVoiceResponse(RadioRole.Tower, RadioEventType.VoiceCommandResponse, "tower_continue_inbound", slots, now);
                }
            }
            else
            {
                QueueVoiceResponse(RadioRole.Tower, RadioEventType.VoiceCommandResponse, "tower_unable", VoiceSlots(callsign), now);
            }
        }

        private void RespondPicture(Snapshot snapshot, float now, string callsign)
        {
            if (TryNearestContact(snapshot, 0, out ContactInfo contact))
                QueueContact(snapshot, contact, RadioEventType.PictureUpdate, "awacs_picture", VoiceResponsePriority, 5f, 0f, force: true, callsignOverride: callsign);
            else
                QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse, "awacs_picture_clean", VoiceSlots(callsign), now);
        }

        private bool TryRespondNamedGroundSupportVector(Snapshot snapshot, string text, float now, string callsign)
        {
            GroundSupportGroup group;
            bool mentioned;
            bool ambiguous;
            ResolveNamedGroundSupportGroup(text, out group, out mentioned, out ambiguous);
            if (!mentioned)
                return false;

            if (ambiguous)
            {
                QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse,
                    "awacs_ground_support_ambiguous", VoiceSlots(callsign), now);
            }
            else if (group == null)
            {
                QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse,
                    "awacs_no_ground_support", VoiceSlots(callsign), now);
            }
            else
            {
                QueueGroundSupportVector(snapshot, group, callsign, now);
            }

            return true;
        }

        private void RespondGroundSupportVector(Snapshot snapshot, string text, float now, string callsign)
        {
            if (TryRespondNamedGroundSupportVector(snapshot, text, now, callsign))
                return;

            GroundSupportGroup group = null;
            if (VoiceIntentParser.ContainsSpokenCallsign(text, "secondary"))
            {
                if (IsAvailableGroundSupportGroup(_activeGroundSupportGroup))
                    group = _activeGroundSupportGroup;
            }
            else if (VoiceIntentParser.ContainsSpokenCallsign(text, "last support request"))
            {
                // "last support request" means the group whose hail most recently reached the
                // radio queue, whether it is still pending or has already been accepted.
                group = MostRecentGroundSupportRequest();
            }
            else
            {
                QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse,
                    "awacs_ground_support_reference_required", VoiceSlots(callsign), now);
                return;
            }

            if (group != null)
            {
                QueueGroundSupportVector(snapshot, group, callsign, now);
            }
            else
            {
                QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse,
                    "awacs_no_ground_support", VoiceSlots(callsign), now);
            }
        }

        private void ResolveNamedGroundSupportGroup(
            string text,
            out GroundSupportGroup selected,
            out bool mentioned,
            out bool ambiguous)
        {
            selected = null;
            mentioned = false;
            ambiguous = false;

            // Prefer a full numbered reference. Requiring "to" prevents a player callsign such
            // as Ranger 1-1 from being mistaken for the destination of an ordinary vector call.
            for (int i = 0; i < _groundSupportGroups.Count; i++)
            {
                GroundSupportGroup candidate = _groundSupportGroups[i];
                if (!VoiceIntentParser.ContainsSpokenCallsign(text, "to " + candidate.Callsign))
                    continue;

                mentioned = true;
                if (IsAvailableGroundSupportGroup(candidate))
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
                for (int i = 0; i < _groundSupportGroups.Count; i++)
                {
                    GroundSupportGroup candidate = _groundSupportGroups[i];
                    if (!IsAvailableGroundSupportGroup(candidate) ||
                        GroundSupportCallsignStem(candidate.Callsign) != stem)
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

        private GroundSupportGroup MostRecentGroundSupportRequest()
        {
            GroundSupportGroup newest = null;
            float newestHail = float.NegativeInfinity;
            for (int i = 0; i < _groundSupportGroups.Count; i++)
            {
                GroundSupportGroup candidate = _groundSupportGroups[i];
                if (!IsAvailableGroundSupportGroup(candidate) || candidate.LastHailAt < newestHail)
                    continue;

                newest = candidate;
                newestHail = candidate.LastHailAt;
            }

            return newest;
        }

        private static bool IsAvailableGroundSupportGroup(GroundSupportGroup group)
        {
            return group != null && !group.Closed && !group.Dismissed && (group.Pending || group.Accepted);
        }

        private void RespondVector(Snapshot snapshot, float now, string callsign)
        {
            if (snapshot.HasSelectedTarget)
                QueueContact(snapshot, snapshot.SelectedTarget, RadioEventType.VectorToTarget, "awacs_vector", VoiceResponsePriority, 5f, 0f, force: true, callsignOverride: callsign);
            else if (TryNearestContact(snapshot, 0, out ContactInfo contact))
                QueueContact(snapshot, contact, RadioEventType.VectorToTarget, "awacs_vector", VoiceResponsePriority, 5f, 0f, force: true, callsignOverride: callsign);
            else
                QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse, "awacs_no_target", VoiceSlots(callsign), now);
        }

        private void RespondVectorObjective(Snapshot snapshot, float now, string callsign, string query)
        {
            List<ObjectiveInfo> objectives = snapshot.Objectives;
            if (objectives.Count == 0)
            {
                QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse, "awacs_no_objective", VoiceSlots(callsign), now);
                return;
            }

            int bestIndex = -1;
            if (!string.IsNullOrEmpty(query))
            {
                // Loose match against every objective name; best word-overlap wins, closest
                // breaks ties. A reference that matches nothing gets a corrective reply
                // rather than a silent fallback to the wrong objective.
                int bestScore = 0;
                for (int i = 0; i < objectives.Count; i++)
                {
                    int score;
                    if (!VoiceIntentParser.LooseNameMatch(query, objectives[i].Name, out score))
                        continue;

                    if (bestIndex < 0 || score > bestScore ||
                        (score == bestScore && objectives[i].DistanceM < objectives[bestIndex].DistanceM))
                    {
                        bestScore = score;
                        bestIndex = i;
                    }
                }

                if (bestIndex < 0)
                {
                    QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse, "awacs_objective_unknown", VoiceSlots(callsign), now);
                    return;
                }
            }
            else
            {
                for (int i = 0; i < objectives.Count; i++)
                {
                    if (!objectives[i].HasPosition)
                        continue;

                    if (bestIndex < 0 || objectives[i].DistanceM < objectives[bestIndex].DistanceM)
                        bestIndex = i;
                }

                if (bestIndex < 0)
                {
                    QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse, "awacs_unable", VoiceSlots(callsign), now);
                    return;
                }
            }

            ObjectiveInfo objective = objectives[bestIndex];
            if (!objective.HasPosition)
            {
                QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse, "awacs_unable", VoiceSlots(callsign), now);
                return;
            }

            Dictionary<string, string> slots = VoiceSlots(callsign);
            slots["objective"] = objective.Name;
            slots["bearing"] = NumberSpeech.Bearing(GPos.Bearing(snapshot.Player.Position, objective.Position));
            slots["range"] = RadioText.FormatRange(GPos.Distance2D(snapshot.Player.Position, objective.Position), snapshot.Units);
            QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse, "awacs_vector_objective", slots, now);
        }

        private void RespondObjectiveList(Snapshot snapshot, float now, string callsign)
        {
            if (snapshot.Objectives.Count == 0)
            {
                QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse, "awacs_no_objective", VoiceSlots(callsign), now);
                return;
            }

            const int maxListed = 5;
            List<ObjectiveInfo> sorted = new List<ObjectiveInfo>(snapshot.Objectives);
            sorted.Sort((a, b) => a.DistanceM.CompareTo(b.DistanceM));

            int listed = sorted.Count < maxListed ? sorted.Count : maxListed;
            StringBuilder builder = new StringBuilder(96);
            for (int i = 0; i < listed; i++)
            {
                if (i > 0)
                    builder.Append("; ");

                builder.Append(sorted[i].Name);
                if (sorted[i].HasPosition)
                {
                    builder.Append(", bearing ").Append(NumberSpeech.Bearing(GPos.Bearing(snapshot.Player.Position, sorted[i].Position)));
                    builder.Append(", ").Append(RadioText.FormatRange(GPos.Distance2D(snapshot.Player.Position, sorted[i].Position), snapshot.Units));
                }
            }

            if (sorted.Count > listed)
                builder.Append("; and ").Append(sorted.Count - listed).Append(" more");

            Dictionary<string, string> slots = VoiceSlots(callsign);
            slots["objectives"] = builder.ToString();
            QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse, "awacs_objective_list", slots, now);
        }

        private void RespondVectorHome(Snapshot snapshot, float now, string callsign)
        {
            if (TryGetHomeBase(snapshot, out AirbaseInfo home, out float distanceM))
            {
                QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse, "awacs_vector_home", Slots(
                    "callsign", callsign,
                    "awacs", _config.AwacsCallsign.Value,
                    "bearing", NumberSpeech.Bearing(GPos.Bearing(snapshot.Player.Position, home.Position)),
                    "range", RadioText.FormatRange(distanceM, snapshot.Units)), now);
            }
            else
            {
                QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse, "awacs_unable", VoiceSlots(callsign), now);
            }
        }

        private void QueueVoiceResponse(RadioRole role, RadioEventType type, string phraseKey, IDictionary<string, string> slots, float now)
        {
            QueueText(role, type, _phrases.Render(phraseKey, slots), now, VoiceResponsePriority, 4f, 0f, false,
                duplicateWindowSeconds: VoiceResponseDuplicateWindowSeconds, bypassStartupHold: true);
        }

        private void QueueContact(
            Snapshot snapshot,
            ContactInfo contact,
            RadioEventType type,
            string phraseKey,
            int priority,
            float duration,
            float cooldown,
            bool force = false,
            string callsignOverride = null)
        {
            float bearing = GPos.Bearing(snapshot.Player.Position, contact.Position);
            float rangeM = GPos.Distance2D(snapshot.Player.Position, contact.Position);
            float contactToPlayer = GPos.Bearing(contact.Position, snapshot.Player.Position);
            float aspectDiff = AbsDelta(contact.HeadingDeg, contactToPlayer);
            ContactAspect aspectCategory = aspectDiff < 45f ? ContactAspect.Hot : aspectDiff > 135f ? ContactAspect.Cold : ContactAspect.Flanking;

            if (!force && !ShouldConveyContactInfo(contact.Id, snapshot.Time, rangeM, aspectCategory))
                return;

            string aspect = aspectCategory == ContactAspect.Hot ? "hot" : aspectCategory == ContactAspect.Cold ? "cold" : "flanking";
            string bearingClause = rangeM < CloseTargetBearingOmitMeters ? string.Empty : ", bearing " + NumberSpeech.Bearing(bearing);
            string altitudeClause = contact.IsAircraft ? ", " + RadioText.FormatAltitude(contact.AltitudeMslM, snapshot.Units) : string.Empty;

            bool queued = Queue(RadioRole.Awacs, type, phraseKey, Slots(
                "callsign", callsignOverride ?? _config.PlayerCallsign.Value,
                "awacs", _config.AwacsCallsign.Value,
                "bearing", NumberSpeech.Bearing(bearing),
                "bearing_clause", bearingClause,
                "range", RadioText.FormatRange(rangeM, snapshot.Units),
                "altitude", RadioText.FormatAltitude(contact.AltitudeMslM, snapshot.Units),
                "altitude_clause", altitudeClause,
                "aspect", aspect,
                "type", RadioText.SpokenUnitName(contact.DisplayName)), snapshot.Time, priority, duration, cooldown, false,
                subjectId: contact.Id,
                duplicateWindowSecondsOverride: force ? VoiceResponseDuplicateWindowSeconds : (float?)null,
                bypassStartupHold: force);

            if (queued && contact.Id != 0)
            {
                _contactInfoLog[contact.Id] = new ContactInfoRecord
                {
                    CalledAt = snapshot.Time,
                    RangeM = rangeM,
                    Aspect = aspectCategory
                };
            }
        }

        /// <summary>All AWACS informational calls about one contact (new contact, picture, vector)
        /// share a cooldown so they do not repeat the same facts back to back. Early repeats are
        /// allowed only when the situation changed: the contact turned hot, or its range moved by
        /// about a quarter.</summary>
        private bool ShouldConveyContactInfo(uint contactId, float now, float rangeM, ContactAspect aspect)
        {
            if (contactId == 0)
                return true;

            ContactInfoRecord last;
            if (!_contactInfoLog.TryGetValue(contactId, out last))
                return true;

            if (now - last.CalledAt >= _config.ContactInfoCooldownSeconds.Value)
                return true;

            if (aspect == ContactAspect.Hot && last.Aspect != ContactAspect.Hot)
                return true;

            return Mathf.Abs(rangeM - last.RangeM) >= Mathf.Max(2000f, last.RangeM * 0.25f);
        }

        private void CancelVectorCalls()
        {
            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                if (_queue[i].Type == RadioEventType.VectorToTarget)
                    _queue.RemoveAt(i);
            }

            _output.StopTransmissions(RadioEventType.VectorToTarget);
        }

        private static bool IsContactInfoCall(RadioEventType type)
        {
            return type == RadioEventType.NewContact ||
                   type == RadioEventType.PictureUpdate ||
                   type == RadioEventType.VectorToTarget;
        }

        private bool IsKnownDestroyed(uint subjectId)
        {
            return subjectId != 0 &&
                   (_announcedDestroyedUnits.Contains(subjectId) || _announcedPlayerKills.Contains(subjectId));
        }

        private void RemoveQueuedContactCalls(uint subjectId)
        {
            if (subjectId == 0)
                return;

            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                if (IsContactInfoCall(_queue[i].Type) && _queue[i].SubjectId == subjectId)
                    _queue.RemoveAt(i);
            }
        }

        private bool Queue(
            RadioRole role,
            RadioEventType type,
            string phraseKey,
            IDictionary<string, string> slots,
            float now,
            int priority,
            float displaySeconds,
            float cooldownSeconds,
            bool urgent,
            uint subjectId = 0,
            float? duplicateWindowSecondsOverride = null,
            bool bypassStartupHold = false)
        {
            return QueueText(role, type, _phrases.Render(phraseKey, slots), now, priority, displaySeconds, cooldownSeconds, urgent,
                duplicateWindowSeconds: duplicateWindowSecondsOverride ?? DuplicateWindowSeconds,
                subjectId: subjectId,
                bypassStartupHold: bypassStartupHold);
        }

        private bool QueueText(
            RadioRole role,
            RadioEventType type,
            string text,
            float now,
            int priority,
            float displaySeconds,
            float cooldownSeconds,
            bool urgent,
            float duplicateWindowSeconds = DuplicateWindowSeconds,
            float availableAt = float.NegativeInfinity,
            uint subjectId = 0,
            bool bypassStartupHold = false)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            if (!urgent && cooldownSeconds > 0f)
            {
                float lastTypeTime;
                if (_lastTypeQueuedAt.TryGetValue(type, out lastTypeTime) && now - lastTypeTime < cooldownSeconds)
                    return false;
            }

            float lastTextTime;
            if (duplicateWindowSeconds > 0f &&
                _lastTextAt.TryGetValue(text, out lastTextTime) &&
                now - lastTextTime < duplicateWindowSeconds)
                return false;

            // Real controller, player, threat, and mission traffic always wins. Purging here also
            // invalidates an in-flight ambient TTS request before it can become a playable clip.
            if (type != RadioEventType.BattlefieldChatter && priority > BattlefieldChatterPriority)
            {
                _queue.RemoveAll(item => item.Type == RadioEventType.BattlefieldChatter);
                _output.StopTransmissions(RadioEventType.BattlefieldChatter);
            }

            _lastTextAt[text] = now;
            _lastTypeQueuedAt[type] = now;

            if (urgent)
            {
                _queue.RemoveAll(item => item.Priority < priority);
            }

            float effectiveDisplaySeconds = CalculateDisplaySeconds(text, displaySeconds);
            float effectiveAvailableAt = Mathf.Max(now, availableAt);
            PendingTransmission transmission = new PendingTransmission
            {
                Role = role,
                Type = type,
                Text = text,
                Priority = priority,
                CreatedAt = now,
                AvailableAt = effectiveAvailableAt,
                ExpiresAt = effectiveAvailableAt + Mathf.Max(effectiveDisplaySeconds + 10f, 15f),
                DisplaySeconds = effectiveDisplaySeconds,
                SubjectId = subjectId,
                BypassStartupHold = bypassStartupHold
            };

            if (role == RadioRole.Awacs && !bypassStartupHold && ShouldHoldStartupAwacs(now, urgent))
            {
                HoldStartupAwacs(transmission);
                return true;
            }

            _queue.Add(transmission);
            return true;
        }

        private void ProcessQueue(float now)
        {
            if (_queue.Count == 0)
                return;

            int transmitted = 0;
            while (_queue.Count > 0 && transmitted < MaxTransmissionsPerTick)
            {
                int bestIndex = -1;
                int bestPriority = int.MinValue;
                float oldest = float.PositiveInfinity;

                for (int i = _queue.Count - 1; i >= 0; i--)
                {
                    PendingTransmission item = _queue[i];
                    if (item.ExpiresAt < now)
                    {
                        _queue.RemoveAt(i);
                        continue;
                    }

                    // Do not announce a contact that died while the call was waiting its turn.
                    if (IsContactInfoCall(item.Type) && IsKnownDestroyed(item.SubjectId))
                    {
                        _queue.RemoveAt(i);
                        continue;
                    }

                    if (item.AvailableAt > now)
                        continue;

                    if (IsStartupAwacsHoldCandidate(item) && ShouldHoldStartupAwacs(now, false))
                    {
                        _queue.RemoveAt(i);
                        HoldStartupAwacs(item);
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
                    return;

                PendingTransmission transmission = _queue[bestIndex];
                _queue.RemoveAt(bestIndex);
                _log.LogInfo($"[{transmission.Role}] {transmission.Text}");
                _output.Transmit(transmission.Role, transmission.Type, transmission.Text, transmission.DisplaySeconds);
                if (transmission.Type == RadioEventType.GroundSupportHail)
                    StartGroundSupportHailCooldown(now);
                transmitted++;

                // One informational call per contact per burst; queued repeats are now redundant.
                if (IsContactInfoCall(transmission.Type))
                    RemoveQueuedContactCalls(transmission.SubjectId);
            }
        }

        private Dictionary<string, string> CommonSlots()
        {
            return Slots(
                "callsign", _config.PlayerCallsign.Value,
                "awacs", _config.AwacsCallsign.Value);
        }

        private Dictionary<string, string> TowerSlots(AirbaseInfo home)
        {
            Dictionary<string, string> slots = CommonSlots();
            slots["runway"] = RadioText.FormatRunway(home);
            return slots;
        }

        private Dictionary<string, string> LandingClearanceSlots(Snapshot snapshot, string sourceText)
        {
            string runwayName = RadioText.ExtractRunwayName(sourceText);
            if (!string.IsNullOrEmpty(runwayName))
            {
                Dictionary<string, string> slots = CommonSlots();
                slots["runway"] = " runway " + RadioText.SpeakRunway(runwayName);
                return slots;
            }

            AirbaseInfo home;
            float ignoredDistance;
            if (TryGetHomeBase(snapshot, out home, out ignoredDistance))
                return TowerSlots(home);

            Dictionary<string, string> fallback = CommonSlots();
            fallback["runway"] = string.Empty;
            return fallback;
        }

        private static Dictionary<string, string> Slots(params string[] pairs)
        {
            Dictionary<string, string> slots = new Dictionary<string, string>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                slots[pairs[i]] = pairs[i + 1];
            return slots;
        }

        private bool TryNearestContact(Snapshot snapshot, uint excludeId, out ContactInfo nearest)
        {
            nearest = default;
            float bestDistance = float.PositiveInfinity;

            for (int i = 0; i < snapshot.Contacts.Count; i++)
            {
                ContactInfo contact = snapshot.Contacts[i];
                if (!contact.Observed || (excludeId != 0 && contact.Id == excludeId))
                    continue;

                float distance = GPos.Distance2D(snapshot.Player.Position, contact.Position);
                if (contact.IsAircraft)
                    distance *= 0.75f;

                if (distance < bestDistance)
                {
                    nearest = contact;
                    bestDistance = distance;
                }
            }

            return !float.IsPositiveInfinity(bestDistance);
        }

        private bool TryGetHomeBase(Snapshot snapshot, out AirbaseInfo home, out float distanceM)
        {
            home = default;
            distanceM = float.PositiveInfinity;

            if (snapshot.FriendlyAirbases.Count == 0)
                return false;

            if (_homeAirbaseInstanceId != 0)
            {
                for (int i = 0; i < snapshot.FriendlyAirbases.Count; i++)
                {
                    AirbaseInfo candidate = snapshot.FriendlyAirbases[i];
                    if (candidate.InstanceId != _homeAirbaseInstanceId)
                        continue;

                    home = candidate;
                    distanceM = GPos.Distance2D(snapshot.Player.Position, candidate.Position);
                    return true;
                }
            }

            for (int i = 0; i < snapshot.FriendlyAirbases.Count; i++)
            {
                AirbaseInfo candidate = snapshot.FriendlyAirbases[i];
                float distance = GPos.Distance2D(snapshot.Player.Position, candidate.Position);
                if (distance < distanceM)
                {
                    home = candidate;
                    distanceM = distance;
                }
            }

            if (!float.IsPositiveInfinity(distanceM))
            {
                _homeAirbaseInstanceId = home.InstanceId;
                return true;
            }

            return false;
        }

        private static bool IsNearAirbase(AirbaseInfo home, float distanceM)
        {
            return distanceM <= Mathf.Max(NearAirbaseMinimumM, home.RadiusM + NearAirbaseRadiusBufferM);
        }

        private static bool IsPointedAt(GPos origin, float headingDeg, GPos target, float toleranceDeg)
        {
            return AbsDelta(headingDeg, GPos.Bearing(origin, target)) <= toleranceDeg;
        }

        private void ResetSession(bool preserveGroundSupport = false)
        {
            ResetForAircraft(0);
            _announcedContacts.Clear();
            _warnedMissiles.Clear();
            _announcedPlayerKills.Clear();
            _announcedDestroyedUnits.Clear();
            _lastDisabledState.Clear();
            _lastTextAt.Clear();
            _lastTypeQueuedAt.Clear();
            _contactInfoLog.Clear();
            ClearPlayerWeaponHoldState();
            _queue.Clear();
            _suppressPictureUntil = 0f;
            _suppressRoutineAwacsUntil = 0f;
            _lastMissileWarnAt = float.NegativeInfinity;
            DisableBattlefieldChatter();
            if (!preserveGroundSupport)
                DisableGroundSupportRequests();
        }

        private void ResetForAircraft(int aircraftInstanceId)
        {
            _aircraftInstanceId = aircraftInstanceId;
            _homeAirbaseInstanceId = 0;
            _stableGrounded = null;
            _groundedTicks = 0;
            _airborneTicks = 0;
            _takeoffClearanceAnnounced = false;
            _airborneAnnounced = false;
            _awaitingAwacsCheckIn = false;
            _output.ClearAwacsCheckInPrompt();
            _approachAnnounced = false;
            _finalAnnounced = false;
            _landedAnnounced = false;
            _successfulAirportLanding = false;
            _ejectionAnnounced = false;
            _destroyedAudioStopped = false;
            _startupWingmanDecisionMade = false;
            _startupWingmanHeld = false;
            _startupWingmanTexts.Clear();
            _startupWingmanHeldAt = 0f;
            _takeoffClearanceQueuedAt = float.NaN;
            _startupRadioGateActive = true;
            _startupAwacsReleased = false;
            _startupRadioGateStartedAt = float.NaN;
            _startupMissionCommsSeen = false;
            _startupTakeoffSequenceDoneAt = float.NaN;
            _startupAwacsTransmissions.Clear();
            _pendingTowerReadbacks.Clear();
            ClearTowerReadbackPrompts();
            _previousHomeDistance = float.NaN;
            _approachInboundStartedAt = float.NaN;
            _approachLastInboundAt = float.NaN;
            _rtbFuelAnnounced = false;
            _previousRtbHomeDistance = float.NaN;
            _rtbInboundStartedAt = float.NaN;
            _rtbLastInboundAt = float.NaN;
            _routineAwacsQuiet = false;
            _awacsTrafficMode = AwacsTrafficMode.Automatic;
            _flightMissionRole = FlightMissionRole.None;
            _nextGroundSupportHailAllowedAt = 0f;
            ClearPlayerWeaponHoldState();
            _queue.Clear();
            for (int i = 0; i < _groundSupportGroups.Count; i++)
            {
                if (_groundSupportGroups[i].Pending)
                    _groundSupportGroups[i].NextHailAt = 0f;
            }
            _suppressPictureUntil = 0f;
            _suppressRoutineAwacsUntil = 0f;
            _lastMissileWarnAt = float.NegativeInfinity;
            _battlefieldChatterTracking = false;
            _nextBattlefieldChatterAt = 0f;
            _friendlyAircraftStates.Clear();
            _battlefieldChatterCandidates.Clear();
            _output.StopTransmissions(RadioEventType.BattlefieldChatter);
        }

        private void ClearPlayerWeaponHoldState()
        {
            _heldPlayerWeaponCalls.Clear();
            _playerWeaponCallsSeenThisTick.Clear();
            _stalePlayerWeaponCalls.Clear();
        }

        private void LogSnapshot(Snapshot snapshot)
        {
            if (snapshot.Time < _nextSnapshotLogTime)
                return;

            _nextSnapshotLogTime = snapshot.Time + 5f;
            _log.LogDebug($"Snapshot: player={snapshot.Player.AircraftName} pos=({snapshot.Player.Position.x:0},{snapshot.Player.Position.y:0},{snapshot.Player.Position.z:0}) hdg={snapshot.Player.HeadingDeg:0} speed={snapshot.Player.SpeedMs:0} contacts={snapshot.Contacts.Count} emitters={snapshot.RadarEmitters.Count} missiles={snapshot.MissileThreats.Count} bases={snapshot.FriendlyAirbases.Count}");
        }

        private static float CalculateDisplaySeconds(string text, float requestedSeconds)
        {
            int wordCount = RadioText.CountWords(text);
            float readingSeconds = ReadBaseSeconds + wordCount * ReadSecondsPerWord;
            return Mathf.Max(requestedSeconds, Mathf.Clamp(readingSeconds, MinDisplaySeconds, MaxDisplaySeconds));
        }

        private static float AbsDelta(float a, float b)
        {
            return Mathf.Abs(Mathf.DeltaAngle(a, b));
        }

        private struct PendingTransmission
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

        private struct PendingTowerReadback
        {
            public TowerReadbackExpectation Expectation;
            public float AwaitingSince;
            public int FailedAttempts;
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

        private sealed class GroundSupportGroup
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

        private enum ContactAspect
        {
            Hot,
            Flanking,
            Cold
        }

        private enum AwacsTrafficMode
        {
            Automatic,
            Quiet,
            Winchester,
            Normal
        }

        private struct ContactInfoRecord
        {
            public float CalledAt;
            public float RangeM;
            public ContactAspect Aspect;
        }
    }
}
