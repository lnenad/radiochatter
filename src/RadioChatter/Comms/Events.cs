using System.Collections.Generic;
using RadioChatter.Game;

namespace RadioChatter.Comms
{
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
        InGameComms
    }

    internal struct RadioEvent
    {
        public RadioEventType Type;
        public uint SubjectId;
        public string SubjectName;
        public bool SubjectIsAircraft;
        public bool SubjectIsMissile;
        public int PlayerAircraftInstanceId;
        public GPos Position;
        public float BearingDeg;
        public float DistanceM;
        public string Text;
    }

    internal interface IRadioOutput
    {
        void Transmit(RadioRole role, RadioEventType type, string text, float displaySeconds);
        void TransmitImmediate(RadioRole role, RadioEventType type, string text, float displaySeconds);
        /// <summary>Stops playing/pending audio of one event type, e.g. stale vector calls after a kill.</summary>
        void StopTransmissions(RadioEventType type);
        bool HasAudioWork(RadioRole role);
        void StopAll();
    }

    internal static class RadioEventBus
    {
        private static readonly object Gate = new object();
        private static readonly Queue<RadioEvent> Events = new Queue<RadioEvent>(32);

        public static void Enqueue(RadioEvent evt)
        {
            lock (Gate)
            {
                Events.Enqueue(evt);
            }
        }

        public static void Drain(List<RadioEvent> target)
        {
            lock (Gate)
            {
                while (Events.Count > 0)
                    target.Add(Events.Dequeue());
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                Events.Clear();
            }
        }
    }
}
