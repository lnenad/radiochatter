using System;

namespace RadioChatter.Comms
{
    /// <summary>Game-agnostic weapon facts used to choose the pilot's radio brevity call.</summary>
    internal struct WeaponBrevityProfile
    {
        public bool IsGun;
        public bool IsBomb;
        public bool IsGlideBomb;
        public bool IsMissile;
        public float AntiAirEffectiveness;
        public float AntiSurfaceEffectiveness;
        public float AntiRadarEffectiveness;
        public float MinimumInfraredSignal;
        public float MinimumRadarSignal;
        public string WeaponName;
        public string ShortName;
        public string Description;
    }

    /// <summary>Maps weapon characteristics to standard release calls without depending on
    /// game assemblies, which keeps the classification rules independently testable.</summary>
    internal static class WeaponBrevityClassifier
    {
        public static string Classify(WeaponBrevityProfile weapon)
        {
            string text = SearchText(weapon);

            if (weapon.IsGun || ContainsAny(text, "gun", "guns", "cannon", "gatling", "vulcan"))
                return "guns guns guns";

            if (weapon.IsBomb || weapon.IsGlideBomb || ContainsAny(text, "bomb", "gbu", "jdam"))
                return "pickle";

            if (!weapon.IsMissile)
                return null;

            if (IsScytheAam(weapon, text))
                return "fox three";

            if (IsAntiRadar(weapon))
                return "magnum";

            if (IsAirToAir(weapon, text))
            {
                if (IsInfraredAam(weapon, text))
                    return "fox two";

                if (ContainsAny(text, "semi-active", "semiactive", "sarh", "sparrow", "aim-7", "fox 1", "fox1"))
                    return "fox one";

                return "fox three";
            }

            if (IsAirToGround(weapon, text))
                return "rifle";

            return null;
        }

        private static bool IsAirToAir(WeaponBrevityProfile weapon, string text)
        {
            return weapon.AntiAirEffectiveness > Math.Max(
                       weapon.AntiSurfaceEffectiveness, weapon.AntiRadarEffectiveness) ||
                   ContainsAny(text, "air-to-air", "air to air", "aam", "sidewinder", "sparrow", "amraam", "interceptor");
        }

        private static bool IsScytheAam(WeaponBrevityProfile weapon, string text)
        {
            return text.IndexOf("scythe", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   (text.IndexOf("aam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("air-to-air", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("air to air", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    weapon.AntiAirEffectiveness > 0.01f);
        }

        private static bool IsAirToGround(WeaponBrevityProfile weapon, string text)
        {
            return weapon.AntiSurfaceEffectiveness > 0.01f ||
                   ContainsAny(text, "air-to-ground", "air to ground", "agm", "maverick", "anti-ship", "antiship");
        }

        private static bool IsAntiRadar(WeaponBrevityProfile weapon)
        {
            // The ARAD is the game's only anti-radiation missile that warrants "magnum".
            // Match its name so generic anti-radar effectiveness does not misclassify weapons.
            string name = (weapon.WeaponName ?? string.Empty) + " " + (weapon.ShortName ?? string.Empty);
            return name.IndexOf("arad", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsInfraredAam(WeaponBrevityProfile weapon, string text)
        {
            return weapon.MinimumInfraredSignal > weapon.MinimumRadarSignal ||
                   ContainsAny(text, "infrared", "heat", "ir ", " ir", "fox 2", "fox2", "sidewinder", "heater");
        }

        private static string SearchText(WeaponBrevityProfile weapon)
        {
            return (weapon.WeaponName ?? string.Empty) + " " +
                   (weapon.ShortName ?? string.Empty) + " " +
                   (weapon.Description ?? string.Empty);
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
}
