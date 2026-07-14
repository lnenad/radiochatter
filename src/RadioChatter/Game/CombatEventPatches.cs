using HarmonyLib;
using RadioChatter.Comms;

namespace RadioChatter.Game
{
    [HarmonyPatch(typeof(global::KillDisplay), "DisplayKill")]
    internal static class KillDisplayPatch
    {
        private static void Postfix(global::PersistentUnit killedUnit)
        {
            if (killedUnit == null)
                return;

            global::Unit unit = killedUnit.unit;
            RadioEventBus.Enqueue(new RadioEvent
            {
                Type = RadioEventType.PlayerKill,
                SubjectId = unit != null ? GameAdapter.PersistentId(unit) : killedUnit.id.Id,
                SubjectName = unit != null ? GameAdapter.DisplayName(unit) : killedUnit.unitName,
                SubjectIsAircraft = unit is global::Aircraft,
                SubjectIsMissile = unit is global::Missile,
                Position = unit != null ? GameAdapter.PositionOf(unit) : default
            });
        }
    }

    [HarmonyPatch(typeof(global::Unit), "ReportKilled")]
    internal static class UnitReportKilledPatch
    {
        private static void Postfix(global::Unit __instance)
        {
            if (__instance == null)
                return;

            global::Aircraft aircraft = __instance as global::Aircraft;
            if (aircraft != null && WeaponPatchHelpers.IsLocalPlayerAircraft(aircraft) && !SafeHasEjected(aircraft))
            {
                RadioEventBus.Enqueue(new RadioEvent
                {
                    Type = RadioEventType.PlayerAircraftDestroyed,
                    SubjectId = GameAdapter.PersistentId(__instance),
                    SubjectName = GameAdapter.DisplayName(__instance),
                    Position = GameAdapter.PositionOf(__instance)
                });
            }

            RadioEventBus.Enqueue(new RadioEvent
            {
                Type = RadioEventType.UnitDestroyed,
                SubjectId = GameAdapter.PersistentId(__instance),
                SubjectName = GameAdapter.RadioName(__instance),
                SubjectIsAircraft = __instance is global::Aircraft,
                SubjectIsMissile = __instance is global::Missile,
                SubjectIsFriendly = WeaponPatchHelpers.IsFriendlyAiAircraftIncludingDisabled(__instance),
                Position = GameAdapter.PositionOf(__instance)
            });
        }

        private static bool SafeHasEjected(global::Aircraft aircraft)
        {
            try { return aircraft != null && aircraft.HasEjected(); }
            catch { return false; }
        }
    }

    [HarmonyPatch(typeof(global::Aircraft), "LockedByMissile")]
    internal static class AircraftLockedByMissilePatch
    {
        private static void Postfix(global::Aircraft __instance, global::Missile missile)
        {
            if (__instance == null || missile == null)
                return;

            if (!global::GameManager.IsLocalAircraft(__instance))
            {
                if (WeaponPatchHelpers.IsFriendlyAiAircraft(__instance))
                {
                    RadioEventBus.Enqueue(new RadioEvent
                    {
                        Type = RadioEventType.FriendlyDefensiveCall,
                        SubjectId = GameAdapter.PersistentId(__instance),
                        SubjectName = GameAdapter.RadioName(__instance)
                    });
                }
                return;
            }

            MissileThreat threat = GameAdapter.ThreatFromMissile(__instance, missile);
            RadioEventBus.Enqueue(new RadioEvent
            {
                Type = RadioEventType.MissileThreat,
                SubjectId = threat.Id,
                SubjectName = GameAdapter.DisplayName(missile),
                SubjectIsMissile = true,
                Position = threat.Position,
                BearingDeg = threat.BearingFromPlayerDeg,
                DistanceM = threat.DistanceM
            });
        }
    }
}

