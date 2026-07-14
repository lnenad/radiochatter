using HarmonyLib;
using RadioChatter.Comms;

namespace RadioChatter.Game
{
    [HarmonyPatch(typeof(global::FlightHud), "Update")]
    internal static class FlightHudUpdatePatch
    {
        private static bool _logged;

        private static void Postfix()
        {
            RadioRuntime.LogInfoOnce(ref _logged, "FlightHud.Update patch active.");
            RadioRuntime.Tick();
        }
    }

    [HarmonyPatch(typeof(global::FlightHud), "SetAircraft")]
    internal static class FlightHudSetAircraftPatch
    {
        private static void Postfix(global::FlightHud __instance, global::Aircraft aircraft)
        {
            WeaponPatchHelpers.CacheHud(__instance);
            RadioEventBus.Enqueue(new RadioEvent
            {
                Type = RadioEventType.PlayerAircraftChanged,
                PlayerAircraftInstanceId = aircraft != null ? aircraft.GetInstanceID() : 0,
                SubjectName = aircraft != null ? GameAdapter.DisplayName(aircraft) : null,
                Position = aircraft != null ? GameAdapter.PositionOf(aircraft) : default
            });
        }
    }
}

