using System;
using HarmonyLib;
using RadioChatter.Comms;

namespace RadioChatter.Game
{
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
}

