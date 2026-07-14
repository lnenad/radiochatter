namespace RadioChatter.Comms
{
    /// <summary>Carrier detection and phrase routing kept free of game types so the behavior can
    /// be exercised by the standalone logic tests.</summary>
    internal static class CarrierCommsPolicy
    {
        public static bool IsCarrierAirbase(bool attachedAirbase, bool hasSkiJump, bool hasArrestor)
        {
            return attachedAirbase || hasSkiJump || hasArrestor;
        }

        public static string TakeoffPhraseKey(bool carrier)
        {
            return carrier ? "carrier_launch" : "tower_takeoff";
        }

        public static string AirbornePhraseKey(bool carrier)
        {
            return carrier ? "carrier_airborne" : "tower_airborne";
        }

        public static string ApproachPhraseKey(bool carrier)
        {
            return carrier ? "carrier_approach" : "tower_approach";
        }

        public static string FinalPhraseKey(bool carrier)
        {
            return carrier ? "carrier_recovery" : "tower_final";
        }

        public static string LandedPhraseKey(bool carrier)
        {
            return carrier ? "carrier_landed" : "tower_landed";
        }

        public static string ContinueInboundPhraseKey(bool carrier)
        {
            return carrier ? "carrier_continue_inbound" : "tower_continue_inbound";
        }
    }
}
