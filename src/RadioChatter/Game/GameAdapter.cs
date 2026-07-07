using System;
using UnityEngine;

namespace RadioChatter.Game
{
    internal sealed class GameAdapter : IGameAdapter
    {
        public bool TryBuildSnapshot(Snapshot snapshot)
        {
            snapshot.Clear();

            snapshot.Mode = ReadMode();
            snapshot.InMission = global::MissionManager.IsRunning &&
                                 (snapshot.Mode == GameMode.SinglePlayerOrHost || snapshot.Mode == GameMode.Editor);
            snapshot.Time = ReadMissionTime();
            snapshot.Units = global::PlayerSettings.unitSystem == global::PlayerSettings.UnitSystem.Imperial
                ? UnitsSystem.Imperial
                : UnitsSystem.Metric;

            if (!snapshot.InMission || snapshot.Mode == GameMode.MultiplayerClient)
                return true;

            global::Aircraft aircraft;
            if (!global::GameManager.GetLocalAircraft(out aircraft) || aircraft == null)
                return true;

            global::FactionHQ localHq;
            global::GameManager.GetLocalHQ(out localHq);

            FillPlayer(snapshot, aircraft);
            FillFriendlyAirbases(snapshot, localHq);
            FillUnits(snapshot, localHq);
            FillContacts(snapshot, localHq);
            FillSelectedTarget(snapshot, aircraft);
            FillMissileThreats(snapshot, aircraft);

            return true;
        }

        internal static uint PersistentId(global::Unit unit)
        {
            if (unit == null)
                return 0;

            uint id = unit.persistentID.Id;
            return id != 0 ? id : unchecked((uint)unit.GetInstanceID());
        }

        internal static uint PersistentId(global::Missile missile)
        {
            if (missile == null)
                return 0;

            uint id = missile.persistentID.Id;
            return id != 0 ? id : unchecked((uint)missile.GetInstanceID());
        }

        internal static string DisplayName(global::Unit unit)
        {
            if (unit == null)
                return "unknown";

            if (unit.definition != null && !string.IsNullOrEmpty(unit.definition.unitName))
                return unit.definition.unitName;

            if (!string.IsNullOrEmpty(unit.unitName))
                return unit.unitName;

            return unit.name;
        }

        internal static string BogeyName(global::Unit unit)
        {
            if (unit != null && unit.definition != null && !string.IsNullOrEmpty(unit.definition.bogeyName))
                return unit.definition.bogeyName;

            return DisplayName(unit);
        }

        internal static GPos PositionOf(global::Unit unit)
        {
            return ToGPos(global::GlobalPositionExtensions.GlobalPosition(unit));
        }

        internal static float HeadingOf(Transform transform)
        {
            return transform != null ? NormalizeHeading(transform.eulerAngles.y) : 0f;
        }

        internal static float BearingFrom(global::Unit from, global::Unit to)
        {
            return GPos.Bearing(PositionOf(from), PositionOf(to));
        }

        internal static MissileThreat ThreatFromMissile(global::Aircraft aircraft, global::Missile missile)
        {
            GPos playerPos = PositionOf(aircraft);
            GPos missilePos = PositionOf(missile);

            return new MissileThreat
            {
                Id = PersistentId(missile),
                Position = missilePos,
                BearingFromPlayerDeg = GPos.Bearing(playerPos, missilePos),
                DistanceM = GPos.Distance(playerPos, missilePos)
            };
        }

        private static void FillPlayer(Snapshot snapshot, global::Aircraft aircraft)
        {
            GPos pos = PositionOf(aircraft);
            Rigidbody rb = null;
            try { rb = aircraft.rb; } catch { }

            bool landed = false;
            try { landed = aircraft.IsLanded(); } catch { }

            bool destroyed = false;
            try { destroyed = aircraft.disabled || aircraft.unitState == global::Unit.UnitState.Destroyed; } catch { }

            snapshot.Player = new PlayerState
            {
                Valid = true,
                AircraftInstanceId = aircraft.GetInstanceID(),
                AircraftName = aircraft.definition != null && !string.IsNullOrEmpty(aircraft.definition.unitName)
                    ? aircraft.definition.unitName
                    : DisplayName(aircraft),
                Position = pos,
                Velocity = rb != null ? rb.velocity : Vector3.zero,
                HeadingDeg = HeadingOf(aircraft.transform),
                SpeedMs = aircraft.speed,
                AltitudeMslM = pos.y,
                AltitudeAglM = aircraft.radarAlt,
                GearDown = aircraft.gearDeployed,
                Grounded = landed || (aircraft.gearDeployed && aircraft.radarAlt < 2f && aircraft.speed < 45f),
                FuelFraction = SafeFuelLevel(aircraft),
                Ejected = aircraft.HasEjected(),
                Destroyed = destroyed
            };
        }

        private static void FillFriendlyAirbases(Snapshot snapshot, global::FactionHQ localHq)
        {
            if (localHq == null)
                return;

            foreach (global::Airbase airbase in localHq.GetAirbases())
            {
                if (airbase == null || airbase.disabled || airbase.CurrentHQ != localHq)
                    continue;

                float runwayHeading = float.NaN;
                string runwayName = null;

                if (airbase.runways != null && airbase.runways.Length > 0 && airbase.runways[0] != null)
                {
                    global::Airbase.Runway runway = airbase.runways[0];
                    Vector3 dir = runway.GetDirection(false);
                    runwayHeading = NormalizeHeading(Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg);
                    runwayName = runway.GetName(false);
                }

                Transform center = airbase.center != null ? airbase.center : airbase.transform;
                snapshot.FriendlyAirbases.Add(new AirbaseInfo
                {
                    InstanceId = airbase.GetInstanceID(),
                    Name = !string.IsNullOrEmpty(airbase.name) ? airbase.name : "Airbase",
                    Position = ToGPos(global::GlobalPositionExtensions.GlobalPosition(center)),
                    RadiusM = airbase.GetRadius(),
                    RunwayHeadingDeg = runwayHeading,
                    RunwayName = runwayName
                });
            }
        }

        private static void FillUnits(Snapshot snapshot, global::FactionHQ localHq)
        {
            if (global::UnitRegistry.allUnits == null)
                return;

            foreach (global::Unit unit in global::UnitRegistry.allUnits)
            {
                if (unit == null)
                    continue;

                snapshot.UnitLifecycles.Add(new UnitLifecycleInfo
                {
                    Id = PersistentId(unit),
                    DisplayName = DisplayName(unit),
                    Position = PositionOf(unit),
                    Disabled = unit.disabled || unit.unitState == global::Unit.UnitState.Destroyed,
                    IsAircraft = unit is global::Aircraft,
                    IsMissile = unit is global::Missile,
                    IsFriendly = localHq != null && unit.NetworkHQ == localHq,
                    IsPlayer = global::GameManager.IsLocalAircraft(unit)
                });
            }
        }

        private static void FillContacts(Snapshot snapshot, global::FactionHQ localHq)
        {
            if (localHq == null || localHq.trackingDatabase == null)
                return;

            foreach (var pair in localHq.trackingDatabase)
            {
                global::TrackingInfo tracking = pair.Value;
                if (tracking == null)
                    continue;

                global::Unit unit;
                if (!tracking.TryGetUnit(out unit) || unit == null || unit.disabled)
                    continue;

                if (unit.NetworkHQ == localHq || global::GameManager.IsLocalAircraft(unit))
                    continue;

                if (!(unit is global::Aircraft) || !tracking.Observed())
                    continue;

                snapshot.Contacts.Add(ContactFromUnit(unit, pair.Key.Id, ToGPos(tracking.GetPosition()), true, tracking.lastSpottedTime));
            }
        }

        private static void FillSelectedTarget(Snapshot snapshot, global::Aircraft aircraft)
        {
            if (aircraft.weaponManager == null)
                return;

            var targets = aircraft.weaponManager.GetTargetList();
            if (targets == null || targets.Count == 0)
                return;

            for (int i = targets.Count - 1; i >= 0; i--)
            {
                global::Unit target = targets[i];
                if (target == null || target.disabled)
                    continue;

                snapshot.HasSelectedTarget = true;
                snapshot.SelectedTarget = ContactFromUnit(target, PersistentId(target), PositionOf(target), true, snapshot.Time);
                return;
            }
        }

        private static void FillMissileThreats(Snapshot snapshot, global::Aircraft aircraft)
        {
            global::MissileWarning warning = aircraft.GetMissileWarningSystem();
            if (warning == null || warning.knownMissiles == null)
                return;

            foreach (global::Missile missile in warning.knownMissiles)
            {
                if (missile == null || missile.disabled)
                    continue;

                snapshot.MissileThreats.Add(ThreatFromMissile(aircraft, missile));
            }
        }

        private static ContactInfo ContactFromUnit(
            global::Unit unit,
            uint id,
            GPos position,
            bool observed,
            float lastSpottedTime)
        {
            Rigidbody rb = null;
            try { rb = unit.rb; } catch { }

            return new ContactInfo
            {
                Id = id != 0 ? id : PersistentId(unit),
                DisplayName = DisplayName(unit),
                BogeyName = BogeyName(unit),
                Position = position,
                Velocity = rb != null ? rb.velocity : Vector3.zero,
                HeadingDeg = HeadingOf(unit.transform),
                AltitudeMslM = position.y,
                IsAircraft = unit is global::Aircraft,
                IsMissile = unit is global::Missile,
                Observed = observed,
                LastSpottedTime = lastSpottedTime
            };
        }

        private static GPos ToGPos(global::GlobalPosition pos)
        {
            return new GPos(pos.x, pos.y, pos.z);
        }

        private static GameMode ReadMode()
        {
            switch (global::GameManager.gameState)
            {
                case global::GameState.SinglePlayer:
                    return GameMode.SinglePlayerOrHost;

                case global::GameState.Multiplayer:
                    global::NuclearOption.Networking.Player player;
                    if (global::GameManager.GetLocalPlayer<global::NuclearOption.Networking.Player>(out player) &&
                        player != null &&
                        !player.IsHostPlayer)
                    {
                        return GameMode.MultiplayerClient;
                    }

                    return GameMode.SinglePlayerOrHost;

                case global::GameState.Editor:
                    return GameMode.Editor;

                default:
                    return GameMode.Menu;
            }
        }

        private static float ReadMissionTime()
        {
            try
            {
                return global::MissionManager.i != null ? global::MissionManager.i.MissionTime : Time.timeSinceLevelLoad;
            }
            catch
            {
                return Time.timeSinceLevelLoad;
            }
        }

        private static float SafeFuelLevel(global::Aircraft aircraft)
        {
            try { return Mathf.Clamp01(aircraft.GetFuelLevel()); }
            catch { return 1f; }
        }

        private static float NormalizeHeading(float heading)
        {
            heading %= 360f;
            return heading < 0f ? heading + 360f : heading;
        }
    }
}
