using System;
using System.Reflection;
using HarmonyLib;
using RadioChatter.Comms;
using UnityEngine;

namespace RadioChatter.Game
{
    [HarmonyPatch(typeof(global::Weapon), "Fire", new Type[] { typeof(global::Unit), typeof(global::Unit), typeof(Vector3), typeof(global::WeaponStation), typeof(global::GlobalPosition) })]
    internal static class WeaponFirePatch
    {
        private static readonly FieldInfo WeaponInfoField = AccessTools.Field(typeof(global::Weapon), "info");

        private static void Postfix(global::Weapon __instance, global::Unit owner)
        {
            if (__instance == null || owner == null)
                return;

            bool localPlayer = WeaponPatchHelpers.IsLocalPlayerAircraft(owner);
            bool friendlyAi = !localPlayer && WeaponPatchHelpers.IsFriendlyAiAircraft(owner);
            if (!localPlayer && !friendlyAi)
                return;

            global::WeaponInfo info = WeaponInfoField != null ? WeaponInfoField.GetValue(__instance) as global::WeaponInfo : null;
            string call = ClassifyPlayerWeaponCall(info);
            if (string.IsNullOrEmpty(call))
                return;

            RadioEventBus.Enqueue(new RadioEvent
            {
                Type = localPlayer ? RadioEventType.PlayerWeaponCall : RadioEventType.FriendlyWeaponCall,
                SubjectId = friendlyAi ? GameAdapter.PersistentId(owner) : 0,
                SubjectName = friendlyAi ? GameAdapter.RadioName(owner) : WeaponDisplayName(info),
                Text = call
            });
        }

        internal static string ClassifyPlayerWeaponCall(global::WeaponInfo info)
        {
            if (info == null)
                return null;

            return WeaponBrevityClassifier.Classify(new WeaponBrevityProfile
            {
                IsGun = info.gun,
                IsBomb = info.bomb,
                IsGlideBomb = info.glideBomb,
                IsMissile = info.missile,
                AntiAirEffectiveness = info.effectiveness.antiAir,
                AntiSurfaceEffectiveness = info.effectiveness.antiSurface,
                AntiRadarEffectiveness = info.effectiveness.antiRadar,
                MinimumInfraredSignal = info.targetRequirements.minIR,
                MinimumRadarSignal = info.targetRequirements.minRadar,
                WeaponName = info.weaponName,
                ShortName = info.shortName,
                Description = info.description
            });
        }

        internal static string WeaponDisplayName(global::WeaponInfo info)
        {
            if (info == null)
                return "unknown weapon";

            if (!string.IsNullOrEmpty(info.shortName))
                return info.shortName;

            if (!string.IsNullOrEmpty(info.weaponName))
                return info.weaponName;

            return "unknown weapon";
        }
    }

    [HarmonyPatch(typeof(global::WeaponManager), "Fire")]
    internal static class WeaponManagerFirePatch
    {
        private static readonly FieldInfo AircraftField = AccessTools.Field(typeof(global::WeaponManager), "aircraft");
        private static readonly FieldInfo CurrentWeaponStationField = AccessTools.Field(typeof(global::WeaponManager), "currentWeaponStation");

        private static void Prefix(global::WeaponManager __instance, ref WeaponFireState __state)
        {
            __state = default;

            if (__instance == null)
                return;

            global::Aircraft aircraft = AircraftField != null ? AircraftField.GetValue(__instance) as global::Aircraft : null;
            if (!WeaponPatchHelpers.IsLocalPlayerAircraft(aircraft))
                return;

            global::WeaponStation station = CurrentWeaponStationField != null ? CurrentWeaponStationField.GetValue(__instance) as global::WeaponStation : null;
            if (station == null || station.WeaponInfo == null)
                return;

            try
            {
                if (station.SafetyIsOn(aircraft) || !station.Ready() || station.SalvoInProgress)
                    return;
            }
            catch
            {
                return;
            }

            string call = WeaponFirePatch.ClassifyPlayerWeaponCall(station.WeaponInfo);
            if (string.IsNullOrEmpty(call))
                return;

            __state = new WeaponFireState
            {
                Armed = true,
                Text = call,
                SubjectName = WeaponFirePatch.WeaponDisplayName(station.WeaponInfo)
            };
        }

        private static void Postfix(WeaponFireState __state)
        {
            if (!__state.Armed)
                return;

            RadioEventBus.Enqueue(new RadioEvent
            {
                Type = RadioEventType.PlayerWeaponCall,
                SubjectName = __state.SubjectName,
                Text = __state.Text
            });
        }

        private struct WeaponFireState
        {
            public bool Armed;
            public string Text;
            public string SubjectName;
        }
    }

    [HarmonyPatch(typeof(global::WeaponManager), "FireGuns")]
    internal static class WeaponManagerFireGunsPatch
    {
        private static readonly FieldInfo AircraftField = AccessTools.Field(typeof(global::WeaponManager), "aircraft");

        private static void Postfix(global::WeaponManager __instance)
        {
            if (__instance == null)
                return;

            global::Aircraft aircraft = AircraftField != null ? AircraftField.GetValue(__instance) as global::Aircraft : null;
            if (!WeaponPatchHelpers.IsLocalPlayerAircraft(aircraft))
            {
                if (WeaponPatchHelpers.IsFriendlyAiAircraft(aircraft))
                {
                    RadioEventBus.Enqueue(new RadioEvent
                    {
                        Type = RadioEventType.FriendlyWeaponCall,
                        SubjectId = GameAdapter.PersistentId(aircraft),
                        SubjectName = GameAdapter.RadioName(aircraft),
                        Text = "guns guns guns"
                    });
                }
                return;
            }

            RadioEventBus.Enqueue(new RadioEvent
            {
                Type = RadioEventType.PlayerWeaponCall,
                SubjectName = "guns",
                Text = "guns guns guns"
            });
        }
    }

    internal static class WeaponPatchHelpers
    {
        public static bool IsLocalPlayerAircraft(global::Unit unit)
        {
            if (unit == null)
                return false;

            try
            {
                if (global::GameManager.IsLocalAircraft(unit))
                    return true;
            }
            catch
            {
            }

            return IsCurrentHudAircraft(unit as global::Aircraft);
        }

        public static bool IsFriendlyAiAircraft(global::Unit unit)
        {
            return IsFriendlyAiAircraft(unit, true);
        }

        public static bool IsFriendlyAiAircraftIncludingDisabled(global::Unit unit)
        {
            return IsFriendlyAiAircraft(unit, false);
        }

        private static bool IsFriendlyAiAircraft(global::Unit unit, bool requireActive)
        {
            if (unit == null || !(unit is global::Aircraft) ||
                Plugin.Cfg == null || !Plugin.Cfg.BattlefieldChatter.Value ||
                IsLocalPlayerAircraft(unit))
                return false;

            try
            {
                global::FactionHQ localHq;
                global::GameManager.GetLocalHQ(out localHq);
                return localHq != null && unit.NetworkHQ == localHq && (!requireActive || !unit.disabled);
            }
            catch
            {
                return false;
            }
        }

        // This runs inside weapon-fire/kill/damage hooks, so the FlightHud lookup and the
        // reflected field must both be cached: FindObjectOfType per shot is a measurable
        // per-frame cost in combat. The hud reference is seeded by FlightHud.SetAircraft
        // and only re-discovered (at most once per frame) after Unity destroys it.
        private static readonly FieldInfo HudAircraftField = AccessTools.Field(typeof(global::FlightHud), "aircraft");
        private static global::FlightHud _cachedHud;
        private static int _lastHudSearchFrame = -1;

        public static void CacheHud(global::FlightHud hud)
        {
            _cachedHud = hud;
        }

        private static bool IsCurrentHudAircraft(global::Aircraft aircraft)
        {
            if (aircraft == null)
                return false;

            try
            {
                global::FlightHud hud = _cachedHud;
                if (hud == null && Time.frameCount != _lastHudSearchFrame)
                {
                    _lastHudSearchFrame = Time.frameCount;
                    hud = UnityEngine.Object.FindObjectOfType<global::FlightHud>();
                    _cachedHud = hud;
                }

                if (hud == null || HudAircraftField == null)
                    return false;

                global::Aircraft hudAircraft = HudAircraftField.GetValue(hud) as global::Aircraft;
                return hudAircraft == aircraft;
            }
            catch
            {
                return false;
            }
        }
    }
}
