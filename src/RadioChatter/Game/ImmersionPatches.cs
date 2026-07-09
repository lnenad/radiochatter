using HarmonyLib;

namespace RadioChatter.Game
{
    [HarmonyPatch(typeof(global::ObjectiveOverlay), "UpdateOverlay")]
    internal static class ObjectiveOverlayUpdateOverlayPatch
    {
        private static bool Prefix(global::ObjectiveOverlay __instance)
        {
            Config cfg = Plugin.Cfg;
            if (cfg == null || !cfg.Enabled.Value || !cfg.HideObjectiveHudMarkers.Value)
                return true;

            __instance.HideOverlay();
            return false;
        }
    }

    /// <summary>
    /// Hides the friendly-airbase nav marker and distance label on the HUD.
    /// <c>AirbaseOverlay.LateUpdate</c> calls <c>DisplayMarkers(show)</c> (the only place that
    /// toggles the marker/label images) after <c>PositionMarkers</c> decides whether to show
    /// them; forcing <c>show</c> false hides them through the game's own path while the runway
    /// borders / glideslope landing aids and the taxi/landing clearance logic (in
    /// <c>UpdateNearestAirbase</c>) keep working.
    /// </summary>
    [HarmonyPatch(typeof(global::AirbaseOverlay), "DisplayMarkers")]
    internal static class AirbaseOverlayDisplayMarkersPatch
    {
        private static void Prefix(ref bool show)
        {
            Config cfg = Plugin.Cfg;
            if (cfg == null || !cfg.Enabled.Value || !cfg.HideAirbaseHudMarkers.Value)
                return;

            show = false;
        }
    }

    /// <summary>
    /// Drives the game's own tactical-map filter flags (MapOptions) while the immersion
    /// options are on. The map re-reads these every frame, so forcing the flag is enough;
    /// the player's original setting is restored when the option is turned off.
    /// </summary>
    internal static class ImmersionMapOptions
    {
        private static bool _forcingObjectives;
        private static bool _previousShowObjectives;

        public static void Tick(Config cfg)
        {
            if (cfg == null)
                return;

            global::MapOptions options = global::SceneSingleton<global::MapOptions>.i;
            if (options == null)
                return;

            bool enabled = cfg.Enabled.Value;

            if (enabled && cfg.HideMapObjectiveMarkers.Value)
            {
                if (!_forcingObjectives)
                {
                    _previousShowObjectives = options.showObjectives;
                    _forcingObjectives = true;
                }

                options.showObjectives = false;
            }
            else if (_forcingObjectives)
            {
                options.showObjectives = _previousShowObjectives;
                _forcingObjectives = false;
            }
        }
    }
}
