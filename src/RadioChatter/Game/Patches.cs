using System;
using System.Reflection;
using HarmonyLib;
using RadioChatter.Comms;
using UnityEngine;
using UnityEngine.UI;

namespace RadioChatter.Game
{
    internal static class RadioHudOverlay
    {
        private static GameObject _root;
        private static Text _text;
        private static bool _createdLogged;

        public static void Draw()
        {
            if (!RadioRuntime.Ready || !RadioRuntime.DebugOverlayEnabled)
            {
                if (_root != null)
                    _root.SetActive(false);
                return;
            }

            Ensure();
            if (_root == null || _text == null)
                return;

            _root.SetActive(true);
            _text.text = RadioRuntime.DebugText();
        }

        public static void Reset()
        {
            if (_root != null)
                UnityEngine.Object.Destroy(_root);

            _root = null;
            _text = null;
            _createdLogged = false;
        }

        private static void Ensure()
        {
            if (_root != null)
                return;

            _root = new GameObject("RadioChatter.DebugCanvas");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            Canvas canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32767;

            _root.AddComponent<CanvasScaler>();
            _root.AddComponent<GraphicRaycaster>();

            GameObject panel = new GameObject("Panel");
            panel.transform.SetParent(_root.transform, false);
            Image image = panel.AddComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0.78f);

            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            panelRect.anchoredPosition = new Vector2(20f, -20f);
            panelRect.sizeDelta = new Vector2(620f, 240f);

            GameObject textObject = new GameObject("Text");
            textObject.transform.SetParent(panel.transform, false);
            _text = textObject.AddComponent<Text>();
            _text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _text.fontSize = 16;
            _text.alignment = TextAnchor.UpperLeft;
            _text.color = Color.white;
            _text.horizontalOverflow = HorizontalWrapMode.Overflow;
            _text.verticalOverflow = VerticalWrapMode.Overflow;

            RectTransform textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(12f, 10f);
            textRect.offsetMax = new Vector2(-12f, -10f);

            RadioRuntime.LogInfoOnce(ref _createdLogged, "FlightHud-driven debug overlay created.");
        }
    }

    [HarmonyPatch(typeof(global::FlightHud), "Update")]
    internal static class FlightHudUpdatePatch
    {
        private static bool _logged;

        private static void Postfix()
        {
            RadioRuntime.LogInfoOnce(ref _logged, "FlightHud.Update patch active.");
            RadioRuntime.Tick();
            RadioHudOverlay.Draw();
        }
    }

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
                SubjectName = GameAdapter.DisplayName(__instance),
                SubjectIsAircraft = __instance is global::Aircraft,
                SubjectIsMissile = __instance is global::Missile,
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
            if (__instance == null || missile == null || !global::GameManager.IsLocalAircraft(__instance))
                return;

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

    [HarmonyPatch(typeof(global::FlightHud), "SetAircraft")]
    internal static class FlightHudSetAircraftPatch
    {
        private static void Postfix(global::Aircraft aircraft)
        {
            RadioEventBus.Enqueue(new RadioEvent
            {
                Type = RadioEventType.PlayerAircraftChanged,
                PlayerAircraftInstanceId = aircraft != null ? aircraft.GetInstanceID() : 0,
                SubjectName = aircraft != null ? GameAdapter.DisplayName(aircraft) : null,
                Position = aircraft != null ? GameAdapter.PositionOf(aircraft) : default
            });
        }
    }

    [HarmonyPatch(typeof(global::MissionMessages), "ShowMessage", new Type[] { typeof(string), typeof(bool), typeof(global::FactionHQ), typeof(bool) })]
    internal static class MissionMessagesShowMessagePatch
    {
        private static void Postfix(string message, bool playsound, global::FactionHQ faction, bool sendToClients)
        {
            if (string.IsNullOrWhiteSpace(message) || !IsForLocalFaction(faction))
                return;

            RadioEventBus.Enqueue(new RadioEvent
            {
                Type = LandingClearanceText.IsLandingClearance(message) ? RadioEventType.TowerFinal : RadioEventType.InGameComms,
                Text = message
            });
        }

        private static bool IsForLocalFaction(global::FactionHQ faction)
        {
            if (faction == null)
                return true;

            try
            {
                global::FactionHQ localHq;
                return global::GameManager.GetLocalHQ(out localHq) && localHq == faction;
            }
            catch
            {
                return false;
            }
        }
    }

    [HarmonyPatch(typeof(global::AircraftActionsReport), "ReportText", new Type[] { typeof(string), typeof(float) })]
    internal static class AircraftActionsReportReportTextPatch
    {
        private static void Postfix(string report)
        {
            if (!LandingClearanceText.IsLandingClearance(report))
                return;

            RadioEventBus.Enqueue(new RadioEvent
            {
                Type = RadioEventType.TowerFinal,
                Text = report
            });
        }
    }

    internal static class LandingClearanceText
    {
        public static bool IsLandingClearance(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            if (message.IndexOf("cleared for landing on", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return message.IndexOf("cleared to land", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   (message.IndexOf("runway", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("rwy", StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    [HarmonyPatch(typeof(global::Aircraft), "SuccessfulSortie")]
    internal static class AircraftSuccessfulSortiePatch
    {
        private static void Postfix(global::Aircraft __instance)
        {
            if (__instance == null || !global::GameManager.IsLocalAircraft(__instance))
                return;

            RadioEventBus.Enqueue(new RadioEvent
            {
                Type = RadioEventType.SortieSuccessful,
                PlayerAircraftInstanceId = __instance.GetInstanceID(),
                SubjectName = GameAdapter.DisplayName(__instance),
                Position = GameAdapter.PositionOf(__instance)
            });
        }
    }

    [HarmonyPatch(typeof(global::Weapon), "Fire", new Type[] { typeof(global::Unit), typeof(global::Unit), typeof(Vector3), typeof(global::WeaponStation), typeof(global::GlobalPosition) })]
    internal static class WeaponFirePatch
    {
        private static readonly FieldInfo WeaponInfoField = AccessTools.Field(typeof(global::Weapon), "info");

        private static void Postfix(global::Weapon __instance, global::Unit owner)
        {
            if (__instance == null || owner == null || !WeaponPatchHelpers.IsLocalPlayerAircraft(owner))
                return;

            global::WeaponInfo info = WeaponInfoField != null ? WeaponInfoField.GetValue(__instance) as global::WeaponInfo : null;
            string call = ClassifyPlayerWeaponCall(info);
            if (string.IsNullOrEmpty(call))
                return;

            RadioEventBus.Enqueue(new RadioEvent
            {
                Type = RadioEventType.PlayerWeaponCall,
                SubjectName = WeaponDisplayName(info),
                Text = call
            });
        }

        internal static string ClassifyPlayerWeaponCall(global::WeaponInfo info)
        {
            if (info == null)
                return null;

            string text = WeaponSearchText(info);

            if (info.gun || ContainsAny(text, "gun", "guns", "cannon", "gatling", "vulcan"))
                return "guns guns guns";

            if (info.bomb || info.glideBomb || ContainsAny(text, "bomb", "gbu", "jdam"))
                return "pickle";

            if (!info.missile)
                return null;

            if (IsScytheAam(info, text))
                return "fox three";

            if (IsAntiRadar(info, text))
                return "magnum";

            if (IsAirToAir(info, text))
            {
                if (IsInfraredAam(info, text))
                    return "fox two";

                if (IsSemiActiveRadarAam(text))
                    return "fox one";

                return "fox three";
            }

            if (IsAirToGround(info, text))
                return "rifle";

            return null;
        }

        private static bool IsAirToAir(global::WeaponInfo info, string text)
        {
            return info.effectiveness.antiAir > Mathf.Max(info.effectiveness.antiSurface, info.effectiveness.antiRadar) ||
                   ContainsAny(text, "air-to-air", "air to air", "aam", "sidewinder", "sparrow", "amraam", "interceptor");
        }

        private static bool IsScytheAam(global::WeaponInfo info, string text)
        {
            return text.IndexOf("scythe", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   (text.IndexOf("aam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("air-to-air", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("air to air", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    info.effectiveness.antiAir > 0.01f);
        }

        private static bool IsAirToGround(global::WeaponInfo info, string text)
        {
            return info.effectiveness.antiSurface > 0.01f ||
                   ContainsAny(text, "air-to-ground", "air to ground", "agm", "maverick", "anti-ship", "antiship");
        }

        private static bool IsAntiRadar(global::WeaponInfo info, string text)
        {
            return info.effectiveness.antiRadar > 0.01f ||
                   ContainsAny(text, "anti-radar", "antiradar", "anti radiation", "anti-radiation", "harm", "arm", "sead", "radar homing");
        }

        private static bool IsInfraredAam(global::WeaponInfo info, string text)
        {
            return info.targetRequirements.minIR > info.targetRequirements.minRadar ||
                   ContainsAny(text, "infrared", "heat", "ir ", " ir", "fox 2", "fox2", "sidewinder", "heater");
        }

        private static bool IsSemiActiveRadarAam(string text)
        {
            return ContainsAny(text, "semi-active", "semiactive", "sarh", "sparrow", "aim-7", "fox 1", "fox1");
        }

        private static string WeaponSearchText(global::WeaponInfo info)
        {
            return ((info.weaponName ?? string.Empty) + " " +
                    (info.shortName ?? string.Empty) + " " +
                    (info.description ?? string.Empty)).ToLowerInvariant();
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

        private static bool ContainsAny(string text, params string[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (text.IndexOf(values[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
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
                return;

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

        private static bool IsCurrentHudAircraft(global::Aircraft aircraft)
        {
            if (aircraft == null)
                return false;

            try
            {
                global::FlightHud hud = UnityEngine.Object.FindObjectOfType<global::FlightHud>();
                if (hud == null)
                    return false;

                FieldInfo aircraftField = AccessTools.Field(typeof(global::FlightHud), "aircraft");
                global::Aircraft hudAircraft = aircraftField != null ? aircraftField.GetValue(hud) as global::Aircraft : null;
                return hudAircraft == aircraft;
            }
            catch
            {
                return false;
            }
        }
    }
}
