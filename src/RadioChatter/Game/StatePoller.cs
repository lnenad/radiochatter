using BepInEx.Logging;
using RadioChatter.Comms;
using UnityEngine;

namespace RadioChatter.Game
{
    internal sealed class StatePoller
    {
        private readonly Snapshot _snapshot = new Snapshot();
        private IGameAdapter _adapter;
        private CommsDirector _director;
        private Config _config;
        private ManualLogSource _log;
        private float _nextPollTime;
        private float _nextErrorLogTime;
        private bool _firstTickLogged;

        public void Initialize(IGameAdapter adapter, CommsDirector director, Config config, ManualLogSource log)
        {
            _adapter = adapter;
            _director = director;
            _config = config;
            _log = log;
            _log.LogInfo("StatePoller initialized.");
        }

        public void Tick()
        {
            if (_adapter == null || _director == null || _config == null || !_config.Enabled.Value)
                return;

            float now = Time.unscaledTime;
            if (now < _nextPollTime)
                return;

            _nextPollTime = now + Mathf.Clamp(_config.PollIntervalSeconds.Value, 0.1f, 2f);

            try
            {
                if (_adapter.TryBuildSnapshot(_snapshot))
                {
                    if (!_firstTickLogged)
                    {
                        _firstTickLogged = true;
                        _log.LogInfo($"StatePoller first snapshot: mode={_snapshot.Mode}, inMission={_snapshot.InMission}, playerValid={_snapshot.Player.Valid}");
                    }

                    _director.Tick(_snapshot);
                }
            }
            catch (System.Exception ex)
            {
                if (now >= _nextErrorLogTime)
                {
                    _nextErrorLogTime = now + 10f;
                    _log.LogWarning($"RadioChatter state polling failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        public string DebugText()
        {
            string text = $"RadioChatter Debug\nMode: {_snapshot.Mode}  In mission: {_snapshot.InMission}\n";
            if (_snapshot.Player.Valid)
            {
                PlayerState p = _snapshot.Player;
                text += $"Player: {p.AircraftName} #{p.AircraftInstanceId}\n";
                text += $"Pos: {p.Position.x:0}, {p.Position.y:0}, {p.Position.z:0}  HDG {p.HeadingDeg:000}  SPD {p.SpeedMs:0} m/s\n";
                text += $"ALT: MSL {p.AltitudeMslM:0} m / AGL {p.AltitudeAglM:0} m  Gear {p.GearDown}  Grounded {p.Grounded}\n";

                if (TryNearestBase(out AirbaseInfo airbase, out float distanceM))
                    text += $"Nearest base: {airbase.Name} {distanceM / 1000f:0.0} km RWY {airbase.RunwayName ?? "--"}\n";

                if (_snapshot.HasSelectedTarget)
                {
                    ContactInfo t = _snapshot.SelectedTarget;
                    text += $"Target: {t.DisplayName} BRG {GPos.Bearing(p.Position, t.Position):000} RNG {GPos.Distance2D(p.Position, t.Position) / 1000f:0.0} km\n";
                }
            }
            else
            {
                text += "Player aircraft: unavailable\n";
            }

            text += $"Contacts: {_snapshot.Contacts.Count}  Airbases: {_snapshot.FriendlyAirbases.Count}  Missiles: {_snapshot.MissileThreats.Count}  Units: {_snapshot.UnitLifecycles.Count}";
            return text;
        }

        private bool TryNearestBase(out AirbaseInfo nearest, out float distanceM)
        {
            nearest = default;
            distanceM = float.PositiveInfinity;

            if (!_snapshot.Player.Valid)
                return false;

            for (int i = 0; i < _snapshot.FriendlyAirbases.Count; i++)
            {
                AirbaseInfo airbase = _snapshot.FriendlyAirbases[i];
                float distance = GPos.Distance2D(_snapshot.Player.Position, airbase.Position);
                if (distance < distanceM)
                {
                    nearest = airbase;
                    distanceM = distance;
                }
            }

            return !float.IsPositiveInfinity(distanceM);
        }
    }
}
