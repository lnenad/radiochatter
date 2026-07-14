using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using RadioChatter.Game;
using RadioChatter.Speech;
using UnityEngine;

namespace RadioChatter.Comms
{
    internal sealed class CommsDirector : IGroundSupportCalloutSink, IStartupGateContext
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
        internal const int VoiceResponsePriority = 60;
        private const float VoiceResponseDuplicateWindowSeconds = 2f;
        private const int BattlefieldChatterPriority = 10;
        private readonly Config _config;
        private readonly IRadioOutput _output;
        private readonly ManualLogSource _log;
        private readonly PhraseEngine _phrases;
        private readonly List<RadioEvent> _patchedEvents = new List<RadioEvent>(16);
        private readonly TransmissionQueue _queue = new TransmissionQueue(DuplicateWindowSeconds);
        private readonly HashSet<uint> _announcedContacts = new HashSet<uint>();
        private readonly HashSet<uint> _warnedMissiles = new HashSet<uint>();
        private readonly HashSet<uint> _announcedPlayerKills = new HashSet<uint>();
        private readonly HashSet<uint> _announcedDestroyedUnits = new HashSet<uint>();
        private readonly Dictionary<uint, bool> _lastDisabledState = new Dictionary<uint, bool>(256);
        private readonly Dictionary<uint, ContactInfoRecord> _contactInfoLog = new Dictionary<uint, ContactInfoRecord>(64);
        private readonly Dictionary<string, float> _heldPlayerWeaponCalls = new Dictionary<string, float>(8);
        private readonly HashSet<string> _playerWeaponCallsSeenThisTick = new HashSet<string>();
        private readonly List<string> _stalePlayerWeaponCalls = new List<string>(8);
        private readonly StartupRadioGate _startupGate;
        private readonly InboundTracker _approachInbound = new InboundTracker();
        private readonly InboundTracker _rtbInbound = new InboundTracker();
        private readonly TowerReadbackSession _readbacks;
        private readonly BattlefieldChatterFeed _chatter;
        private readonly System.Func<bool> _radioIdleForChatter;
        private readonly GroundSupportCoordinator _groundSupport;
        private readonly StationTransmissionHistory _stationTransmissionHistory = new StationTransmissionHistory();

        private int _aircraftInstanceId;
        private int _homeAirbaseInstanceId;
        private bool _homeAirbaseIsCarrier;
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
        private bool _mpClientLogged;
        private bool _rtbFuelAnnounced;
        private bool _routineAwacsQuiet;
        private AwacsTrafficMode _awacsTrafficMode;
        private FlightMissionRole _flightMissionRole;
        private float _nextSnapshotLogTime;
        private float _suppressPictureUntil;
        private float _suppressRoutineAwacsUntil;
        private float _lastMissileWarnAt = float.NegativeInfinity;
        private bool _sessionActive;

        public CommsDirector(Config config, IRadioOutput output, ManualLogSource log)
        {
            _config = config;
            _output = output;
            _log = log;
            _phrases = new PhraseEngine(log);
            _groundSupport = new GroundSupportCoordinator(config, log, _queue, output, this);
            _startupGate = new StartupRadioGate(config, log, _queue, output, this);
            _readbacks = new TowerReadbackSession(config, log, output,
                (role, phraseKey, slots, now) =>
                    QueueVoiceResponse(role, RadioEventType.VoiceCommandResponse, phraseKey, slots, now),
                ApplyFailedReadbackEffect);
            _chatter = new BattlefieldChatterFeed(config,
                (phraseKey, slots, now, subjectId) =>
                    Queue(RadioRole.Game, RadioEventType.BattlefieldChatter, phraseKey, slots,
                        now, BattlefieldChatterPriority, 3f, 0f, false, subjectId),
                () => CancelTransmissions(RadioEventType.BattlefieldChatter));
            _radioIdleForChatter = () =>
                !_startupGate.IsActive && !_awaitingAwacsCheckIn &&
                _readbacks.Count == 0 && _queue.Count == 0 && !HasAnyRadioAudioWork();
        }

        // IStartupGateContext — the flight-sequence state and queue queries the startup gate
        // reads, plus the two actions it pushes back through the director.
        bool IStartupGateContext.TakeoffClearanceAnnounced => _takeoffClearanceAnnounced;
        bool IStartupGateContext.AirborneAnnounced => _airborneAnnounced;
        bool IStartupGateContext.AwaitingAwacsCheckIn => _awaitingAwacsCheckIn;

        bool IStartupGateContext.SpokenTowerReadbacksEnabled()
        {
            return SpokenTowerReadbacksEnabled();
        }

        bool IStartupGateContext.IsStartupTakeoffCandidate(Snapshot snapshot)
        {
            return IsStartupTakeoffCandidate(snapshot);
        }

        bool IStartupGateContext.HasQueuedTransmission(RadioEventType type)
        {
            return HasQueuedTransmission(type);
        }

        bool IStartupGateContext.HasQueuedTransmission(RadioRole role)
        {
            return HasQueuedTransmission(role);
        }

        bool IStartupGateContext.HasPendingReadback(TowerReadbackKind kind)
        {
            return _readbacks.Has(kind);
        }

        void IStartupGateContext.QueueHeldWingmanLine(string text, float now, float availableAt)
        {
            QueueText(RadioRole.Game, RadioEventType.InGameComms, text,
                now, 45, 4f, 0f, false, DuplicateWindowSeconds, availableAt);
        }

        void IStartupGateContext.ReleasePendingGroundSupportHails(float now)
        {
            ReleasePendingGroundSupportHails(now);
        }

        /// <summary>A failed (abandoned) readback pushes flight-sequence state back; routing it
        /// through this single handler keeps the cross-subsystem write explicit.</summary>
        private void ApplyFailedReadbackEffect(FailedReadbackEffect effect)
        {
            if (effect == FailedReadbackEffect.ReopenLandingSequence)
            {
                _finalAnnounced = false;
                _approachAnnounced = false;
            }
            else if (effect == FailedReadbackEffect.CancelAwacsCheckIn)
            {
                _awaitingAwacsCheckIn = false;
            }
        }

        // IGroundSupportCalloutSink — the narrow surface the ground-support coordinator
        // queues and reads through, so its callouts obey the director's dedup and
        // startup-hold rules.
        bool IGroundSupportCalloutSink.QueueCallout(
            RadioRole role,
            RadioEventType type,
            string phraseKey,
            IDictionary<string, string> slots,
            float now,
            int priority,
            float displaySeconds,
            uint subjectId,
            float duplicateWindowSeconds,
            bool bypassStartupHold)
        {
            return Queue(role, type, phraseKey, slots, now, priority, displaySeconds, 0f, false,
                subjectId: subjectId,
                duplicateWindowSecondsOverride: duplicateWindowSeconds,
                bypassStartupHold: bypassStartupHold);
        }

        void IGroundSupportCalloutSink.QueueVoiceResponse(RadioRole role, RadioEventType type, string phraseKey, IDictionary<string, string> slots, float now)
        {
            QueueVoiceResponse(role, type, phraseKey, slots, now);
        }

        void IGroundSupportCalloutSink.CancelVectorCalls()
        {
            CancelVectorCalls();
        }

        bool IGroundSupportCalloutSink.IsKnownDestroyed(uint subjectId)
        {
            return IsKnownDestroyed(subjectId);
        }

        bool IGroundSupportCalloutSink.ShouldHoldStartupAwacs(float now)
        {
            return _startupGate.ShouldHoldAwacs(now, false);
        }

        bool IGroundSupportCalloutSink.SuppressGroundSupportHails()
        {
            return SuppressGroundSupportHails();
        }

        bool? IGroundSupportCalloutSink.StableGrounded => _stableGrounded;

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
                // the mission itself continues. Suspend output, but retain state keyed to the
                // current airframe (Winchester, mission role, stable flight state). A later valid
                // snapshot resets it only if the authoritative aircraft instance id changed.
                SuspendForMissingPlayer();
                return;
            }

            _sessionActive = true;
            if (!_config.VoiceCommandsEnabled.Value)
                _awaitingAwacsCheckIn = false;

            if (!SpokenTowerReadbacksEnabled())
            {
                _readbacks.Clear();
                ClearTowerReadbackPrompts();
            }

            DrainPatchedEvents(snapshot);

            if (_aircraftInstanceId != snapshot.Player.AircraftInstanceId)
                ResetForAircraft(snapshot.Player.AircraftInstanceId);

            LogSnapshot(snapshot);
            _startupGate.Begin(snapshot);
            if (DetectPlayerDestroyed(snapshot))
                return;

            DetectDestroyedUnits(snapshot);
            _groundSupport.Update(snapshot);
            _chatter.Detect(snapshot);
            UpdateRoutineAwacsQuietState(snapshot);
            DetectContacts(snapshot);
            DetectMissiles(snapshot);
            DetectEjection(snapshot);
            DetectTower(snapshot);
            _startupGate.ReleaseWingmanFallback(snapshot);
            DetectApproach(snapshot);
            DetectRtb(snapshot);
            DetectVector(snapshot);
            DetectPicture(snapshot);
            _startupGate.TryRelease(snapshot);
            UpdateAwacsCheckInPrompt();
            _chatter.TryQueue(snapshot.Time, _radioIdleForChatter);
            ProcessQueue(snapshot.Time);
        }

        private void EndSession(bool preserveGroundSupport = false)
        {
            bool stopOutput = _sessionActive;
            bool preserveLandingGreeting = preserveGroundSupport && _successfulAirportLanding;
            _sessionActive = false;
            RadioEventBus.Clear();
            ResetSession(preserveGroundSupport);

            if (stopOutput)
            {
                if (preserveLandingGreeting)
                    _output.StopAllExcept(RadioEventType.TowerLanded);
                else
                    _output.StopAll();
            }
        }

        private void SuspendForMissingPlayer()
        {
            bool stopOutput = _sessionActive;
            _sessionActive = false;
            RadioEventBus.Clear();
            _queue.Clear();

            if (!stopOutput)
                return;

            if (_successfulAirportLanding)
                _output.StopAllExcept(RadioEventType.TowerLanded);
            else
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
                        if (AircraftSessionPolicy.ShouldResetFromHudEvent(
                                _aircraftInstanceId,
                                evt.PlayerAircraftInstanceId,
                                snapshot.Player.AircraftInstanceId))
                        {
                            ResetForAircraft(evt.PlayerAircraftInstanceId);
                            _log.LogDebug($"Player aircraft changed: {evt.SubjectName ?? "none"} #{evt.PlayerAircraftInstanceId}");
                        }
                        else
                        {
                            _log.LogDebug(
                                $"Ignored redundant/transient HUD aircraft change: event #{evt.PlayerAircraftInstanceId}, " +
                                $"current #{_aircraftInstanceId}, snapshot #{snapshot.Player.AircraftInstanceId}.");
                        }
                        break;

                    case RadioEventType.PlayerAircraftDestroyed:
                        StopAudioForPlayerDestroyed();
                        break;

                    case RadioEventType.UnitDestroyed:
                        if (evt.SubjectId != 0 && _announcedDestroyedUnits.Add(evt.SubjectId))
                            _log.LogInfo($"Event: unit destroyed: {evt.SubjectName}");

                        _groundSupport.HandleAttackerDestroyed(evt.SubjectId);

                        if (evt.SubjectIsFriendly && evt.SubjectIsAircraft)
                            _chatter.AddCandidate(evt.SubjectId, evt.SubjectName, "battlefield_lost", null, now, 4);
                        break;

                    case RadioEventType.GroundUnitUnderAttack:
                        _groundSupport.HandleGroundUnitUnderAttack(snapshot, evt, now);
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
                            Queue(RadioRole.Tower, RadioEventType.TowerLanded,
                                CarrierCommsPolicy.LandedPhraseKey(IsCarrierHome(snapshot)),
                                CommonSlots(), now, 70, 4f, 0f, false);
                        }
                        break;

                    case RadioEventType.TowerFinal:
                        if (_config.LandingCalls.Value && !_finalAnnounced && snapshot.InMission && snapshot.Player.Valid &&
                            !RequestDrivenComms())
                        {
                            _finalAnnounced = true;
                            bool carrier = IsCarrierHome(snapshot);
                            Queue(RadioRole.Tower, RadioEventType.TowerFinal,
                                CarrierCommsPolicy.FinalPhraseKey(carrier),
                                LandingClearanceSlots(snapshot, evt.Text, carrier), now, 80, 4f, 0f, false);
                        }
                        break;

                    case RadioEventType.InGameComms:
                        if (_config.InGameComms.Value && snapshot.InMission && snapshot.Player.Valid)
                        {
                            string text = RadioText.SanitizeGameComms(evt.Text);
                            if (!string.IsNullOrEmpty(text))
                            {
                                if (!_startupGate.TryHoldMissionComms(snapshot, text, now))
                                    QueueText(RadioRole.Game, evt.Type, text, now, 45, 4f, 1f, false);
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
                            _readbacks.Add(expectation, now);
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
                            _chatter.AddCandidate(evt.SubjectId, evt.SubjectName, "battlefield_weapon",
                                Slots("call", weaponCall), now, 2);
                        }
                        break;

                    case RadioEventType.FriendlyDefensiveCall:
                        _chatter.AddCandidate(evt.SubjectId, evt.SubjectName, "battlefield_defensive", null, now, 3);
                        break;
                }
            }

            _groundSupport.ResolveDestroyedThreats(snapshot.Time);
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

                    _groundSupport.HandleAttackerDestroyed(unit.Id);
                }

                _lastDisabledState[unit.Id] = unit.Disabled;
            }

            _groundSupport.ResolveDestroyedThreats(snapshot.Time);
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
            bool nearAirbase = false;
            if (!_successfulAirportLanding && !snapshot.Player.Destroyed && snapshot.Player.Grounded &&
                TryGetHomeBase(snapshot, out AirbaseInfo home, out float distanceM))
            {
                nearAirbase = IsNearAirbase(home, distanceM);
            }

            return FlightExitPolicy.IsNormalAirportExit(
                _successfulAirportLanding,
                snapshot.Player.Destroyed,
                snapshot.Player.Grounded,
                nearAirbase);
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

        private void ReleasePendingGroundSupportHails(float now)
        {
            if (_stableGrounded != false)
                return;

            _groundSupport.ReleasePendingHails(now);
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
                    _startupGate.OnAutomaticTakeoffClearance(snapshot.Time);

                    Queue(RadioRole.Tower, RadioEventType.TowerTakeoff,
                        CarrierCommsPolicy.TakeoffPhraseKey(home.IsCarrier),
                        TowerSlots(home), snapshot.Time, 70, 4f, 0f, false);
                }

                if (wasAirborne && nearBase && _config.LandingCalls.Value && !_landedAnnounced)
                {
                    _landedAnnounced = true;
                    Queue(RadioRole.Tower, RadioEventType.TowerLanded,
                        CarrierCommsPolicy.LandedPhraseKey(home.IsCarrier),
                        CommonSlots(), snapshot.Time, 70, 4f, 0f, false);
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
                    Queue(RadioRole.Tower, RadioEventType.TowerAirborne,
                        CarrierCommsPolicy.AirbornePhraseKey(home.IsCarrier),
                        CommonSlots(), snapshot.Time, 70, 4f, 0f, false);
                }
            }
        }

        private void DetectApproach(Snapshot snapshot)
        {
            if (snapshot.Player.Grounded || !_config.ApproachCalls.Value)
                return;

            if (!TryGetHomeBase(snapshot, out AirbaseInfo home, out float distanceM))
                return;

            bool closing = _approachInbound.UpdateClosing(distanceM, ApproachMinClosingMeters);

            if (distanceM > ApproachResetDistanceM)
            {
                _approachAnnounced = false;
                _finalAnnounced = false;
                _approachInbound.ResetInbound();
                return;
            }

            bool descending = snapshot.Player.Velocity.y < -1.5f;
            bool pointedAtBase = IsPointedAt(snapshot.Player.Position, snapshot.Player.HeadingDeg, home.Position, ApproachHeadingToleranceDeg);
            bool belowApproachCeiling = snapshot.Player.AltitudeAglM < ApproachMaxAltitudeAglM;
            bool approachIntent = pointedAtBase || descending || snapshot.Player.GearDown || distanceM < ApproachCloseRangeM;
            bool inbound = closing && belowApproachCeiling && approachIntent;
            _approachInbound.Track(inbound, snapshot.Time, ApproachInboundGraceSeconds);

            bool inboundLongEnough = _approachInbound.InboundFor(snapshot.Time, ApproachRequiredInboundSeconds);

            // The distance/inbound state above keeps updating even in request-driven mode so a
            // mid-flight toggle behaves; only the announcement itself is pull-only.
            if (!_approachAnnounced && distanceM < ApproachCallDistanceM && inboundLongEnough && !RequestDrivenComms())
            {
                _approachAnnounced = true;
                Queue(RadioRole.Tower, RadioEventType.TowerApproach,
                    CarrierCommsPolicy.ApproachPhraseKey(home.IsCarrier),
                    CommonSlots(), snapshot.Time, 70, 4f, 20f, false);
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
            _startupGate.RemoveHeldAwacs(item => IsContactInfoCall(item.Type));
            // New-contact calls are always unsolicited. Purge any request already handed to the
            // asynchronous audio layer so it cannot surface after Winchester or radio quiet.
            _output.StopTransmissions(RadioEventType.NewContact);
            if (_awacsTrafficMode == AwacsTrafficMode.Winchester)
                _groundSupport.StopHails();
            _rtbInbound.Reset();
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
                _rtbInbound.Reset();
                return;
            }

            bool closing = _rtbInbound.UpdateClosing(distanceM, RtbVectorMinClosingMeters);

            bool pointedAtHome = IsPointedAt(snapshot.Player.Position, snapshot.Player.HeadingDeg,
                home.Position, RtbVectorHeadingToleranceDeg);
            bool inbound = closing && pointedAtHome && snapshot.Player.SpeedMs > 60f;
            _rtbInbound.Track(inbound, snapshot.Time, RtbVectorInboundGraceSeconds);

            bool inboundLongEnough = _rtbInbound.InboundFor(snapshot.Time, RtbVectorRequiredInboundSeconds);

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
                _groundSupport.TryHandleTransmission(snapshot, text, now))
                return;

            if (SpokenTowerReadbacksEnabled() && _readbacks.TryHandle(text, intent, now))
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
                    if (!_groundSupport.TryRespondNamedVector(snapshot, text, now, callsign))
                        RespondVector(snapshot, now, callsign);
                    break;
                case VoiceIntentKind.RequestVectorGroundSupport:
                    _groundSupport.RespondVector(snapshot, text, now, callsign);
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
                case VoiceIntentKind.RequestRepeatLast:
                    RespondRepeatLast(intent.Station, now, callsign);
                    break;
                default:
                    if (intent.Station == VoiceStation.Tower)
                        QueueVoiceResponse(RadioRole.Tower, RadioEventType.VoiceCommandResponse, "say_again_tower", VoiceSlots(callsign), now);
                    else
                        QueueVoiceResponse(RadioRole.Awacs, RadioEventType.VoiceCommandResponse, "say_again_awacs", VoiceSlots(callsign), now);
                    break;
            }
        }

        private void RespondRepeatLast(VoiceStation requestedStation, float now, string callsign)
        {
            VoiceStation station = requestedStation == VoiceStation.Tower
                ? VoiceStation.Tower
                : VoiceStation.Awacs;
            RadioRole role = station == VoiceStation.Tower ? RadioRole.Tower : RadioRole.Awacs;

            if (_stationTransmissionHistory.TryGet(station, out string previousText))
            {
                QueueText(role, RadioEventType.VoiceCommandResponse, previousText,
                    now, VoiceResponsePriority, 4f, 0f, false,
                    duplicateWindowSeconds: 0f,
                    bypassStartupHold: true);
                _log.LogInfo($"{station} repeating last transmission for {callsign}: {previousText}");
                return;
            }

            QueueVoiceResponse(role, RadioEventType.VoiceCommandResponse,
                station == VoiceStation.Tower ? "tower_nothing_to_repeat" : "awacs_nothing_to_repeat",
                VoiceSlots(callsign), now);
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
                _groundSupport.StopHails();
            }
            else if (groundWasSuppressed)
            {
                _groundSupport.ReleasePendingHails(now);
            }

            if (SuppressAutomaticAirContacts())
            {
                _startupGate.RemoveHeldAwacs(item => item.Type == RadioEventType.NewContact);
                CancelTransmissions(RadioEventType.NewContact);
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
                _groundSupport.ReleasePendingHails(now);

            _log.LogInfo($"Flight mission role reset from {previous} to General: {reason}.");
        }

        private bool SuppressGroundSupportHails()
        {
            return FlightMissionRolePolicy.SuppressGroundSupportHails(_flightMissionRole) ||
                   _awacsTrafficMode == AwacsTrafficMode.Winchester;
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
                case FlightMissionRole.MaritimeStrike: return "maritime strike, anti-surface warfare";
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
                _groundSupport.StopHails();
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

        private void ClearTowerReadbackPrompts()
        {
            _output.ClearReadbackPrompt(TowerReadbackKind.Takeoff);
            _output.ClearReadbackPrompt(TowerReadbackKind.Landing);
            _output.ClearReadbackPrompt(TowerReadbackKind.Handoff);
        }

        private void PromptForMissingTowerReadbacks(float now)
        {
            if (!SpokenTowerReadbacksEnabled())
                return;

            bool towerBusy = HasQueuedTransmission(RadioRole.Tower) ||
                             _output.HasAudioWork(RadioRole.Tower);
            _readbacks.PromptForMissing(now, towerBusy);
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
                _readbacks.Has(TowerReadbackKind.Handoff))
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
                _startupGate.NoteTakeoffClearanceQueued(now);

                Dictionary<string, string> slots = TowerSlots(home);
                slots["callsign"] = callsign;
                QueueVoiceResponse(RadioRole.Tower, RadioEventType.TowerTakeoff,
                    CarrierCommsPolicy.TakeoffPhraseKey(home.IsCarrier), slots, now);
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
                    QueueVoiceResponse(RadioRole.Tower, RadioEventType.TowerFinal,
                        CarrierCommsPolicy.FinalPhraseKey(home.IsCarrier), slots, now);
                }
                else
                {
                    Dictionary<string, string> slots = VoiceSlots(callsign);
                    slots["range"] = RadioText.FormatRange(distanceM, snapshot.Units);
                    QueueVoiceResponse(RadioRole.Tower, RadioEventType.VoiceCommandResponse,
                        CarrierCommsPolicy.ContinueInboundPhraseKey(home.IsCarrier), slots, now);
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

        /// <summary>Drops queued calls of one type and stops its playing/pending audio —
        /// the standard way to retire a whole call type at once.</summary>
        private void CancelTransmissions(RadioEventType type)
        {
            _queue.RemoveAll(item => item.Type == type);
            _output.StopTransmissions(type);
        }

        private void CancelVectorCalls()
        {
            CancelTransmissions(RadioEventType.VectorToTarget);
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

            if (!urgent && _queue.IsOnCooldown(type, now, cooldownSeconds))
                return false;

            if (_queue.IsDuplicate(text, now, duplicateWindowSeconds))
                return false;

            // Real controller, player, threat, and mission traffic always wins. Purging here also
            // invalidates an in-flight ambient TTS request before it can become a playable clip.
            if (type != RadioEventType.BattlefieldChatter && priority > BattlefieldChatterPriority)
                CancelTransmissions(RadioEventType.BattlefieldChatter);

            _queue.MarkQueued(text, type, now);

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

            if (role == RadioRole.Awacs && !bypassStartupHold && _startupGate.ShouldHoldAwacs(now, urgent))
            {
                _startupGate.HoldAwacs(transmission);
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
                PendingTransmission transmission;
                if (!_queue.TrySelectNext(
                        now,
                        // Do not announce a contact that died while the call was waiting its turn.
                        item => IsContactInfoCall(item.Type) && IsKnownDestroyed(item.SubjectId),
                        item => StartupRadioGate.IsAwacsHoldCandidate(item) && _startupGate.ShouldHoldAwacs(now, false),
                        _startupGate.HoldAwacs,
                        out transmission))
                {
                    return;
                }

                _log.LogInfo($"[{transmission.Role}] {transmission.Text}");
                _output.Transmit(transmission.Role, transmission.Type, transmission.Text, transmission.DisplaySeconds);
                if (transmission.Role == RadioRole.Tower)
                    _stationTransmissionHistory.Record(VoiceStation.Tower, transmission.Text);
                else if (transmission.Role == RadioRole.Awacs)
                    _stationTransmissionHistory.Record(VoiceStation.Awacs, transmission.Text);
                if (transmission.Type == RadioEventType.GroundSupportHail)
                    _groundSupport.StartHailCooldown(now);
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

        private Dictionary<string, string> LandingClearanceSlots(Snapshot snapshot, string sourceText, bool carrier)
        {
            if (carrier)
                return CommonSlots();

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
                    _homeAirbaseIsCarrier = candidate.IsCarrier;
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
                _homeAirbaseIsCarrier = home.IsCarrier;
                return true;
            }

            return false;
        }

        private bool IsCarrierHome(Snapshot snapshot)
        {
            if (TryGetHomeBase(snapshot, out AirbaseInfo home, out float ignoredDistance))
                return home.IsCarrier;

            // Successful-sortie and exit events can arrive after the local-aircraft snapshot has
            // disappeared. Preserve the last confirmed home type so the welcome call still uses
            // carrier phraseology.
            return _homeAirbaseIsCarrier;
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
            ClearPlayerWeaponHoldState();
            _queue.Clear();
            _suppressPictureUntil = 0f;
            _suppressRoutineAwacsUntil = 0f;
            _lastMissileWarnAt = float.NegativeInfinity;
            _chatter.Disable();
            if (!preserveGroundSupport)
                _groundSupport.Disable();
        }

        private void ResetForAircraft(int aircraftInstanceId)
        {
            _aircraftInstanceId = aircraftInstanceId;
            // Dedup/cooldown logs are per-sortie state: a respawned player should hear the
            // standard calls again, and clearing here (not just in ResetSession) keeps
            // the text-dedup table from accumulating one entry per unique line across a
            // long session.
            _queue.ClearDedupLog();
            _contactInfoLog.Clear();
            _homeAirbaseInstanceId = 0;
            _homeAirbaseIsCarrier = false;
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
            _startupGate.Reset();
            _readbacks.Clear();
            ClearTowerReadbackPrompts();
            _stationTransmissionHistory.Clear();
            _approachInbound.Reset();
            _rtbFuelAnnounced = false;
            _rtbInbound.Reset();
            _routineAwacsQuiet = false;
            _awacsTrafficMode = AwacsTrafficMode.Normal;
            _flightMissionRole = FlightMissionRole.None;
            ClearPlayerWeaponHoldState();
            _queue.Clear();
            _groundSupport.ResetHailTimers();
            _suppressPictureUntil = 0f;
            _suppressRoutineAwacsUntil = 0f;
            _lastMissileWarnAt = float.NegativeInfinity;
            _chatter.Reset();
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

        private enum ContactAspect
        {
            Hot,
            Flanking,
            Cold
        }

        private enum AwacsTrafficMode
        {
            Normal,
            Quiet,
            Winchester
        }

        private struct ContactInfoRecord
        {
            public float CalledAt;
            public float RangeM;
            public ContactAspect Aspect;
        }
    }
}
