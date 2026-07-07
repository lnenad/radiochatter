using System;
using System.Collections.Generic;
using UnityEngine;

namespace RadioChatter.Game
{
    /// <summary>Absolute world position (floating-origin safe). Mirrors the game's GlobalPosition
    /// without leaking the game type outside the adapter.</summary>
    public struct GPos
    {
        public float x, y, z;

        public GPos(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

        public static float Distance(in GPos a, in GPos b)
        {
            float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public static float Distance2D(in GPos a, in GPos b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Compass bearing (deg, 0..360, 0 = +Z/north) from a to b.</summary>
        public static float Bearing(in GPos a, in GPos b)
        {
            float deg = Mathf.Atan2(b.x - a.x, b.z - a.z) * Mathf.Rad2Deg;
            return deg < 0f ? deg + 360f : deg;
        }
    }

    public enum UnitsSystem { Metric, Imperial }

    public enum GameMode { Menu, SinglePlayerOrHost, MultiplayerClient, Editor }

    public struct PlayerState
    {
        public bool Valid;
        public int AircraftInstanceId;   // changes on respawn / new airframe
        public string AircraftName;      // display name
        public GPos Position;
        public Vector3 Velocity;
        public float HeadingDeg;         // nose heading
        public float SpeedMs;            // TAS m/s
        public float AltitudeMslM;
        public float AltitudeAglM;
        public bool GearDown;
        public bool Grounded;            // gear down + ~0 AGL (debounced by detectors, raw here)
        public float FuelFraction;       // 0..1
        public bool Ejected;
        public bool Destroyed;
    }

    public struct ContactInfo
    {
        public uint Id;
        public string DisplayName;       // e.g. "FS-20B Vortex"
        public string BogeyName;         // generic name for radio use
        public GPos Position;
        public Vector3 Velocity;
        public float HeadingDeg;
        public float AltitudeMslM;
        public bool IsAircraft;
        public bool IsMissile;
        public bool Observed;            // currently spotted vs stale track
        public float LastSpottedTime;
    }

    public struct AirbaseInfo
    {
        public int InstanceId;
        public string Name;
        public GPos Position;
        public float RadiusM;
        public float RunwayHeadingDeg;   // NaN when no runway data
        public string RunwayName;        // e.g. "09", may be null
    }

    public struct MissileThreat
    {
        public uint Id;
        public GPos Position;
        public float BearingFromPlayerDeg;
        public float DistanceM;
    }

    public struct UnitLifecycleInfo
    {
        public uint Id;
        public string DisplayName;
        public GPos Position;
        public bool Disabled;
        public bool IsAircraft;
        public bool IsMissile;
        public bool IsFriendly;
        public bool IsPlayer;
    }

    /// <summary>Per-tick view of game state. One instance is reused (main thread only);
    /// lists retain capacity to keep steady-state allocations at zero.</summary>
    public sealed class Snapshot
    {
        public float Time;               // mission time seconds
        public GameMode Mode;
        public bool InMission;
        public UnitsSystem Units;
        public PlayerState Player;
        public readonly List<ContactInfo> Contacts = new List<ContactInfo>(64);        // detected enemy contacts
        public readonly List<AirbaseInfo> FriendlyAirbases = new List<AirbaseInfo>(8);
        public readonly List<MissileThreat> MissileThreats = new List<MissileThreat>(8);
        public readonly List<UnitLifecycleInfo> UnitLifecycles = new List<UnitLifecycleInfo>(256);
        public bool HasSelectedTarget;
        public ContactInfo SelectedTarget;

        public void Clear()
        {
            InMission = false;
            Player = default;
            Contacts.Clear();
            FriendlyAirbases.Clear();
            MissileThreats.Clear();
            UnitLifecycles.Clear();
            HasSelectedTarget = false;
            SelectedTarget = default;
        }
    }
}
