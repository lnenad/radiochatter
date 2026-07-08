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

    }
}
