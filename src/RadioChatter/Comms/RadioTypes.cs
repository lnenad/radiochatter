namespace RadioChatter.Comms
{
    // Kept separate from Events.cs so Unity-free policy code (and the logic tests)
    // can reference the radio enums without pulling in GPos/Snapshot.

    internal enum RadioRole
    {
        Tower,
        Awacs,
        Player,
        PlayerTower,
        PlayerFlight,
        PlayerAwacs,
        Game,
        System
    }

    internal enum RadioEventType
    {
        PlayerAircraftChanged,
        PlayerAircraftDestroyed,
        UnitDestroyed,
        PlayerKill,
        MissileThreat,
        SortieSuccessful,
        NewContact,
        TowerTakeoff,
        TowerAirborne,
        TowerApproach,
        TowerFinal,
        TowerLanded,
        RtbFuel,
        RtbVector,
        VectorToTarget,
        PictureUpdate,
        PlayerWeaponCall,
        PlayerDefensiveCall,
        PlayerEjectionCall,
        PlayerAcknowledgement,
        FriendlyWeaponCall,
        FriendlyDefensiveCall,
        BattlefieldChatter,
        GroundUnitUnderAttack,
        GroundSupportHail,
        GroundSupportAcknowledged,
        GroundSupportVector,
        GroundSupportDeclined,
        GroundSupportCompleted,
        GroundSupportCanceled,
        InGameComms,
        PlayerVoiceCommand,
        TowerReadbackRequired,
        VoiceCommandResponse
    }

    internal static class RadioRoles
    {
        public static bool IsPlayerRole(RadioRole role)
        {
            return role == RadioRole.Player ||
                   role == RadioRole.PlayerTower ||
                   role == RadioRole.PlayerFlight ||
                   role == RadioRole.PlayerAwacs;
        }
    }
}
