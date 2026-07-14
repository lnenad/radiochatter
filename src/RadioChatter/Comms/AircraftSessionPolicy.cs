namespace RadioChatter.Comms
{
    /// <summary>Validates HUD aircraft-change notifications against the authoritative snapshot.
    /// FlightHud can be rebound to the same aircraft, or briefly cleared, without the player's
    /// airframe actually changing. Those notifications must not clear cockpit radio state.</summary>
    internal static class AircraftSessionPolicy
    {
        public static bool ShouldResetFromHudEvent(
            int currentAircraftId,
            int eventAircraftId,
            int snapshotAircraftId)
        {
            return eventAircraftId != 0 &&
                   eventAircraftId != currentAircraftId &&
                   eventAircraftId == snapshotAircraftId;
        }
    }

    internal static class FlightExitPolicy
    {
        /// <summary>A successful-sortie notification is authoritative even if the game disables
        /// the parked aircraft while the player exits it. Without this precedence, a normal exit
        /// is mistaken for an ejection and its immediate mayday interrupts the Tower greeting.</summary>
        public static bool IsNormalAirportExit(
            bool successfulSortie,
            bool aircraftDestroyed,
            bool grounded,
            bool nearAirbase)
        {
            if (successfulSortie)
                return true;

            return !aircraftDestroyed && grounded && nearAirbase;
        }
    }
}
