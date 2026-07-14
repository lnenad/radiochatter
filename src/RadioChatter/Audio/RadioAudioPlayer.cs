using RadioChatter.Comms;
using RadioChatter.Speech;
using BepInEx.Logging;
using System.Collections.Generic;
using UnityEngine;

namespace RadioChatter.Audio
{
    /// <summary>Main-thread playback orchestrator: takes ready TTS clips from the pipeline,
    /// plays them on AudioSources with the radio effect, shows subtitles, and schedules the
    /// player's automatic replies. All cross-thread TTS state lives in TtsRequestPipeline.</summary>
    internal sealed class RadioAudioPlayer : IRadioOutput
    {
        private const float PendingSpeechSeconds = 45f;
        private const float ContactInfoLifetimeSeconds = 12f;
        private const float BattlefieldChatterLifetimeSeconds = 10f;

        private Config _config;
        private ManualLogSource _log;
        private PocketTtsClient _client;
        private SidecarSupervisor _sidecar;
        private GameObject _host;
        private readonly List<ActiveTransmission> _activeSources = new List<ActiveTransmission>(4);
        private readonly TtsRequestPipeline _pipeline = new TtsRequestPipeline();
        private readonly AcknowledgementQueue _acknowledgements = new AcknowledgementQueue();
        private readonly PlayerResponsePolicy _responsePolicy = new PlayerResponsePolicy();
        private readonly SubtitleOverlay _subtitles = new SubtitleOverlay();
        private readonly SidecarStatusHud _statusHud = new SidecarStatusHud();
        private readonly GroundSupportPlaybackGate _groundSupportPlaybackGate = new GroundSupportPlaybackGate();
        private RadioEffectProcessor _effect;
        private float _clock;
        private int _lastClockFrame = -1;
        private bool _paused;
        private bool _playbackLogged;
        private bool _pushToTalkActive;

        public void Initialize(Config config, GameObject host, ManualLogSource log)
        {
            _config = config;
            _log = log;
            _client = new PocketTtsClient(config);
            _sidecar = new SidecarSupervisor(config, log);
            _effect = new RadioEffectProcessor(config);
            _host = host;
            // The fallback is only invoked from the pipeline's main-thread entry points
            // (Submit/Tick), never from TTS callbacks, so touching the overlay is safe.
            _pipeline.Initialize(_client, _sidecar, log, ShowSubtitleFallback);
        }

        public void SetPushToTalkActive(bool active)
        {
            _pushToTalkActive = active;
        }

        public void Transmit(RadioRole role, RadioEventType type, string text, float displaySeconds)
        {
            if (_config == null || _client == null)
                return;

            // A support broadcast or direct ground exchange must never inherit an automatic
            // "copy" that was prepared for earlier traffic. Without this purge, the old reply
            // can be held behind the ground channel and play immediately after an ETA question.
            if (PlayerResponsePolicy.CancelsOutstandingAutomaticAcknowledgements(type, text))
                CancelOutstandingAutomaticAcknowledgements();

            // Positional AWACS info ages fast; if the clip cannot start playing before the
            // deadline (busy channel, slow TTS), it is dropped instead of played late.
            float deadline = _clock + ClipLifetimeSeconds(type);
            _pipeline.Submit(role, type, VoiceForRole(role), text, displaySeconds, deadline, _clock);
        }

        private static float ClipLifetimeSeconds(RadioEventType type)
        {
            switch (type)
            {
                case RadioEventType.NewContact:
                case RadioEventType.PictureUpdate:
                case RadioEventType.VectorToTarget:
                case RadioEventType.GroundSupportVector:
                    return ContactInfoLifetimeSeconds;
                case RadioEventType.BattlefieldChatter:
                    return BattlefieldChatterLifetimeSeconds;
                default:
                    return PendingSpeechSeconds;
            }
        }

        public void TransmitImmediate(RadioRole role, RadioEventType type, string text, float displaySeconds)
        {
            StopAll();
            Transmit(role, type, text, displaySeconds);
        }

        public void StopTransmissions(RadioEventType type)
        {
            _pipeline.PurgeType(type, _clock);

            for (int i = _activeSources.Count - 1; i >= 0; i--)
            {
                if (_activeSources[i].Type != type)
                    continue;

                StopAndDestroy(_activeSources[i]);
                _activeSources.RemoveAt(i);
            }
        }

        public bool HasAudioWork(RadioRole role)
        {
            return HasResponseWorkForRole(role);
        }

        public void ShowSubtitle(RadioRole role, string text, float displaySeconds)
        {
            if (_config == null || !_config.SubtitlesEnabled.Value || string.IsNullOrWhiteSpace(text))
                return;

            _subtitles.Add($"{SubtitleOverlay.PrefixForRole(role)} {text}", _clock, displaySeconds);
        }

        public void ClearReadbackPrompt(TowerReadbackKind kind)
        {
            _subtitles.ClearReadbackPrompt(kind);
        }

        public void ShowAwacsCheckInPrompt(string awacsCallsign, string playerCallsign)
        {
            if (_config == null || !_config.SubtitlesEnabled.Value)
                return;

            _subtitles.ShowAwacsCheckInPrompt(awacsCallsign, playerCallsign, _clock);
        }

        public void ClearAwacsCheckInPrompt()
        {
            _subtitles.ClearAwacsCheckInPrompt();
        }

        public void StopAll()
        {
            _pipeline.PurgeAll();
            _acknowledgements.Clear();

            for (int i = _activeSources.Count - 1; i >= 0; i--)
                StopAndDestroy(_activeSources[i]);

            _activeSources.Clear();
            _subtitles.Clear();
        }

        public void StopAllExcept(RadioEventType type)
        {
            foreach (RadioEventType candidate in System.Enum.GetValues(typeof(RadioEventType)))
            {
                if (candidate != type)
                    StopTransmissions(candidate);
            }

            _acknowledgements.Clear();
        }

        public void Tick()
        {
            _sidecar?.Tick();
            UpdateClock();
            _pipeline.Tick(_clock);

            if (_host == null || _paused)
                return;

            CleanupFinishedSources();
            UpdateActiveVolumes();
            ProcessPendingAcknowledgement();

            int maxConcurrent = _config != null ? Mathf.Clamp(_config.MaxConcurrentTransmissions.Value, 1, 6) : 3;
            while (_activeSources.Count < maxConcurrent)
            {
                ReadyClip clip;
                if (!_pipeline.TryTakeReady(_clock, CanPlayNow, out clip))
                    return;

                PlayClip(clip);
            }
        }

        private bool CanPlayNow(ReadyClip clip)
        {
            // Ground hails may reach the ready queue long after their director dispatch
            // because TTS is asynchronous. Enforce the interval here so the calls the player
            // hears remain separated even after a slow sidecar start or an audio backlog.
            if (clip.Type == RadioEventType.GroundSupportHail &&
                !_groundSupportPlaybackGate.CanStart(_clock))
            {
                return false;
            }

            return !IsRoleActive(clip.Role);
        }

        private void ShowSubtitleFallback(RadioRole role, string text, float displaySeconds)
        {
            if (_config != null && _config.SubtitlesEnabled.Value)
                _subtitles.Add($"{SubtitleOverlay.PrefixForRole(role)} {text}", _clock, displaySeconds);
        }

        /// <summary>Radio time only advances while the game is unpaused. Paused sources report
        /// isPlaying=false, so cleanup/expiry/acknowledgement logic must freeze with them or the
        /// whole backlog silently drains during a pause.</summary>
        private void UpdateClock()
        {
            if (Time.frameCount == _lastClockFrame)
                return;

            _lastClockFrame = Time.frameCount;
            _paused = AudioListener.pause || Time.timeScale <= 0.0001f;
            if (!_paused)
                _clock += Time.unscaledDeltaTime;
        }

        private void PlayClip(ReadyClip clip)
        {
            if (clip.Audio == null || clip.Audio.Samples == null || clip.Audio.Samples.Length == 0 || _host == null)
                return;

            // Purge once more at playback time in case unrelated radio traffic completed while
            // this clip was waiting for TTS or for its audio lane.
            if (PlayerResponsePolicy.CancelsOutstandingAutomaticAcknowledgements(clip.Type, clip.Text))
                CancelOutstandingAutomaticAcknowledgements();

            float[] samples;
            try
            {
                samples = _effect != null
                    ? _effect.Process(clip.Audio.Samples, clip.Audio.SampleRate)
                    : clip.Audio.Samples;
            }
            catch (System.Exception ex)
            {
                _log?.LogWarning($"Radio effect processing failed ({ex.GetType().Name}: {ex.Message}); playing raw TTS.");
                samples = clip.Audio.Samples;
            }

            if (samples == null || samples.Length == 0)
                return;

            AudioClip audioClip = AudioClip.Create("RadioChatterTTS", samples.Length, 1, clip.Audio.SampleRate, false);
            AudioSource source = null;
            try
            {
                audioClip.SetData(samples, 0);

                source = _host.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 0f;
                source.ignoreListenerPause = false;
                source.volume = VolumeForRole(clip.Role);
                source.clip = audioClip;
                source.Play();
            }
            catch
            {
                // The clip is only tracked once it reaches _activeSources; destroy it here or it
                // survives as an orphaned asset-type object.
                if (source != null)
                    Object.Destroy(source);
                Object.Destroy(audioClip);
                throw;
            }
            if (clip.Type == RadioEventType.GroundSupportHail)
                _groundSupportPlaybackGate.MarkStarted(_clock);
            TowerReadbackExpectation readbackExpectation;
            bool requiresSpokenReadback = TryGetSpokenTowerReadback(clip, out readbackExpectation);
            _activeSources.Add(new ActiveTransmission
            {
                Role = clip.Role,
                Type = clip.Type,
                Source = source,
                Clip = audioClip,
                Text = clip.Text,
                Response = ResponseFor(clip),
                RequiresSpokenReadback = requiresSpokenReadback
            });

            if (_config != null && _config.SubtitlesEnabled.Value)
            {
                _subtitles.Add($"{SubtitleOverlay.PrefixForRole(clip.Role)} {clip.Text}", _clock, clip.DisplaySeconds,
                    requiresSpokenReadback, readbackExpectation.Kind);
            }

            if (!_playbackLogged)
            {
                _playbackLogged = true;
                _log?.LogInfo($"RadioChatter voice playback started: {samples.Length} samples @ {clip.Audio.SampleRate} Hz, effect={_config != null && _config.RadioEffectEnabled.Value}");
            }
        }

        private void CleanupFinishedSources()
        {
            List<PlayerResponse> acknowledgements = null;

            for (int i = _activeSources.Count - 1; i >= 0; i--)
            {
                ActiveTransmission active = _activeSources[i];
                AudioSource source = active.Source;
                if (source != null && source.isPlaying)
                    continue;

                StopAndDestroy(active);
                _activeSources.RemoveAt(i);
                if (active.RequiresSpokenReadback)
                {
                    RadioEventBus.Enqueue(new RadioEvent
                    {
                        Type = RadioEventType.TowerReadbackRequired,
                        Text = active.Text
                    });
                }

                if (!string.IsNullOrEmpty(active.Response.Text))
                {
                    if (acknowledgements == null)
                        acknowledgements = new List<PlayerResponse>(2);

                    acknowledgements.Insert(0, active.Response);
                }
            }

            if (acknowledgements != null)
            {
                for (int i = 0; i < acknowledgements.Count; i++)
                    QueueAcknowledgement(acknowledgements[i]);
            }
        }

        private void UpdateActiveVolumes()
        {
            for (int i = 0; i < _activeSources.Count; i++)
            {
                AudioSource source = _activeSources[i].Source;
                if (source != null)
                    source.volume = VolumeForRole(_activeSources[i].Role);
            }
        }

        private float VolumeForRole(RadioRole role)
        {
            float volume = _config != null ? _config.Volume.Value : 0.8f;
            if (!_pushToTalkActive || IsPlayerRole(role) || _config == null)
                return volume;

            return volume * Mathf.Clamp01(_config.PushToTalkReceiveVolume.Value);
        }

        private bool IsRoleActive(RadioRole role)
        {
            for (int i = 0; i < _activeSources.Count; i++)
            {
                ActiveTransmission active = _activeSources[i];
                if (SameAudioLane(active.Role, role) && active.Source != null)
                    return true;
            }

            return false;
        }

        private string VoiceForRole(RadioRole role)
        {
            if (role == RadioRole.Tower)
                return _config.TowerVoice.Value;

            if (IsPlayerRole(role))
                return _config.PlayerVoice.Value;

            if (role == RadioRole.Game)
                return _config.WingmanVoice.Value;

            return _config.AwacsVoice.Value;
        }

        private void QueueAcknowledgement(PlayerResponse acknowledgement)
        {
            if (_config == null || !_config.PlayerAcknowledgements.Value)
                return;

            if (string.IsNullOrWhiteSpace(acknowledgement.Text))
                return;

            if (_acknowledgements.ContainsText(acknowledgement.Text))
                return;

            if (HasResponseWorkForRole(acknowledgement.Role))
                return;

            _acknowledgements.Enqueue(acknowledgement, _clock);
        }

        private void ProcessPendingAcknowledgement()
        {
            if (_acknowledgements.Count == 0)
                return;

            if (HasNonPlayerAudioWork())
            {
                _acknowledgements.Defer(_clock);
                return;
            }

            PlayerResponse acknowledgement;
            if (!_acknowledgements.TryDequeue(_clock, out acknowledgement))
                return;

            if (!string.IsNullOrWhiteSpace(acknowledgement.Text))
                Transmit(acknowledgement.Role, RadioEventType.PlayerAcknowledgement, acknowledgement.Text, 2f);
        }

        private bool HasNonPlayerAudioWork()
        {
            for (int i = 0; i < _activeSources.Count; i++)
            {
                ActiveTransmission active = _activeSources[i];
                if (!IsPlayerRole(active.Role) && active.Source != null)
                    return true;
            }

            return _pipeline.HasNonPlayerWork();
        }

        private bool HasResponseWorkForRole(RadioRole role)
        {
            if (_acknowledgements.HasForRole(role))
                return true;

            for (int i = 0; i < _activeSources.Count; i++)
            {
                ActiveTransmission active = _activeSources[i];
                if (active.Role == role && active.Source != null)
                    return true;
            }

            return _pipeline.HasWorkForRole(role);
        }

        private PlayerResponse ResponseFor(ReadyClip clip)
        {
            if (_config == null || !_config.PlayerAcknowledgements.Value)
                return default;

            return _responsePolicy.ResponseFor(clip.Role, clip.Type, clip.Text, SpokenTowerReadbacksEnabled());
        }

        private void CancelOutstandingAutomaticAcknowledgements()
        {
            _acknowledgements.Clear();

            // Responses are attached to active transmissions and normally enter the pending
            // queue when their clips finish. Clear those too so none can surface after a prompt.
            for (int i = 0; i < _activeSources.Count; i++)
            {
                ActiveTransmission active = _activeSources[i];
                active.Response = default;
                _activeSources[i] = active;
            }
        }

        private bool TryGetSpokenTowerReadback(ReadyClip clip, out TowerReadbackExpectation expectation)
        {
            expectation = default;
            if (clip.Role != RadioRole.Tower || !SpokenTowerReadbacksEnabled())
                return false;

            return TowerReadbackMatcher.TryCreate(clip.Text, out expectation);
        }

        private bool SpokenTowerReadbacksEnabled()
        {
            return _config != null &&
                   _config.VoiceCommandsEnabled.Value &&
                   _config.VoiceRequireTowerReadbacks.Value;
        }

        public void Shutdown()
        {
            _sidecar?.Shutdown();
            _sidecar = null;
            _client = null;
            _pipeline.Detach();

            StopAll();
        }

        public void DrawGui()
        {
            if (_config == null)
                return;

            if (_config.SidecarStatusDisplay.Value)
                _statusHud.Draw(_sidecar, _clock);

            if (_config.SubtitlesEnabled.Value)
                _subtitles.Draw(_clock);
        }

        private static bool SameAudioLane(RadioRole a, RadioRole b)
        {
            if (IsPlayerRole(a) && IsPlayerRole(b))
                return true;

            return a == b;
        }

        private static bool IsPlayerRole(RadioRole role)
        {
            return RadioRoles.IsPlayerRole(role);
        }

        private static void StopAndDestroy(ActiveTransmission active)
        {
            AudioSource source = active.Source;
            if (source != null)
            {
                source.Stop();
                source.clip = null;
                Object.Destroy(source);
            }

            if (active.Clip != null)
                Object.Destroy(active.Clip);
        }

        private struct ActiveTransmission
        {
            public RadioRole Role;
            public RadioEventType Type;
            public AudioSource Source;
            // Kept alongside Source: AudioClips are asset-type objects that outlive a destroyed
            // host GameObject, so destruction must not depend on reaching them via source.clip.
            public AudioClip Clip;
            public string Text;
            public PlayerResponse Response;
            public bool RequiresSpokenReadback;
        }
    }
}
