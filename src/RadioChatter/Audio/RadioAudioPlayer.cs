using RadioChatter.Comms;
using RadioChatter.Speech;
using BepInEx.Logging;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace RadioChatter.Audio
{
    internal sealed class RadioAudioPlayer : IRadioOutput
    {
        private const float HighPassCutoffHz = 450f;
        private const float LowPassCutoffHz = 2600f;
        private const float FilterQ = 0.707f;
        private const float SaturationGain = 1.85f;
        private const float OutputGain = 1.15f;
        private const float NoiseScale = 0.18f;
        private const float MaxEffectiveNoise = 0.008f;
        private const float PendingSpeechSeconds = 45f;
        private const float ContactInfoLifetimeSeconds = 12f;
        private const float AcknowledgementDelaySeconds = 0.35f;
        private const float ReadyBannerSeconds = 5f;
        private const int MaxPendingSpeech = 8;
        private const int MaxPendingAcknowledgements = 8;
        private const int MaxActiveSubtitles = 3;
        private static readonly string[] PlayerAcknowledgements =
        {
            "roger that",
            "copy",
            "copy that",
            "wilco",
            "understood",
            "affirmative"
        };

        private Config _config;
        private ManualLogSource _log;
        private PocketTtsClient _client;
        private SidecarSupervisor _sidecar;
        private GameObject _host;
        private readonly object _gate = new object();
        private readonly List<ReadyClip> _ready = new List<ReadyClip>(8);
        private readonly List<ActiveTransmission> _activeSources = new List<ActiveTransmission>(4);
        private readonly Queue<PendingSpeech> _pendingSpeech = new Queue<PendingSpeech>();
        private readonly Queue<PendingAcknowledgement> _pendingAcknowledgements = new Queue<PendingAcknowledgement>();
        private readonly List<RadioRole> _inFlightRoles = new List<RadioRole>(8);
        private readonly Dictionary<RadioEventType, float> _typePurgedAt = new Dictionary<RadioEventType, float>();
        private readonly List<SubtitleLine> _subtitles = new List<SubtitleLine>(MaxActiveSubtitles);
        private readonly System.Random _noise = new System.Random();
        private readonly System.Random _ackRandom = new System.Random();
        private float _nextPendingSpeechLogTime;
        private float _nextAcknowledgementReadyAt;
        private float _clock;
        private int _lastClockFrame = -1;
        private bool _paused;
        private int _lastAcknowledgementIndex = -1;
        private int _audioGeneration;
        private bool _playbackLogged;
        private GUIStyle _style;
        private GUIStyle _statusStyle;
        private SidecarSupervisor.SidecarStatus _lastSidecarStatus = SidecarSupervisor.SidecarStatus.Unknown;
        private float _sidecarReadyAt = float.NaN;

        public void Initialize(Config config, GameObject host, ManualLogSource log)
        {
            _config = config;
            _log = log;
            _client = new PocketTtsClient(config);
            _sidecar = new SidecarSupervisor(config, log);
            _host = host;
        }

        public void Transmit(RadioRole role, RadioEventType type, string text, float displaySeconds)
        {
            if (_config == null || _client == null)
                return;

            // Positional AWACS info ages fast; if the clip cannot start playing before the
            // deadline (busy channel, slow TTS), it is dropped instead of played late.
            float deadline = _clock + ClipLifetimeSeconds(type);
            string voice = VoiceForRole(role);
            if (!TryStartAudioRequest(role, type, voice, text, displaySeconds, deadline))
                QueuePendingSpeech(role, type, voice, text, displaySeconds, deadline);
        }

        private static float ClipLifetimeSeconds(RadioEventType type)
        {
            switch (type)
            {
                case RadioEventType.NewContact:
                case RadioEventType.PictureUpdate:
                case RadioEventType.VectorToTarget:
                    return ContactInfoLifetimeSeconds;
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
            lock (_gate)
            {
                _typePurgedAt[type] = _clock;

                for (int i = _ready.Count - 1; i >= 0; i--)
                {
                    if (_ready[i].Type == type)
                        _ready.RemoveAt(i);
                }

                int pendingCount = _pendingSpeech.Count;
                for (int i = 0; i < pendingCount; i++)
                {
                    PendingSpeech pending = _pendingSpeech.Dequeue();
                    if (pending.Type != type)
                        _pendingSpeech.Enqueue(pending);
                }
            }

            for (int i = _activeSources.Count - 1; i >= 0; i--)
            {
                if (_activeSources[i].Type != type)
                    continue;

                StopAndDestroySource(_activeSources[i].Source);
                _activeSources.RemoveAt(i);
            }
        }

        private bool WasPurgedSince(RadioEventType type, float requestedAt)
        {
            lock (_gate)
            {
                float purgedAt;
                return _typePurgedAt.TryGetValue(type, out purgedAt) && purgedAt >= requestedAt;
            }
        }

        public bool HasAudioWork(RadioRole role)
        {
            return HasResponseWorkForRole(role);
        }

        public void StopAll()
        {
            _audioGeneration++;

            lock (_gate)
            {
                _ready.Clear();
                _pendingSpeech.Clear();
                _inFlightRoles.Clear();
            }

            _pendingAcknowledgements.Clear();
            for (int i = _activeSources.Count - 1; i >= 0; i--)
                StopAndDestroySource(_activeSources[i].Source);

            _activeSources.Clear();
            _subtitles.Clear();
        }

        public void Tick()
        {
            _sidecar?.Tick();
            UpdateClock();
            TryStartPendingSpeech();

            if (_host == null || _paused)
                return;

            CleanupFinishedSources();
            UpdateActiveVolumes();
            ProcessPendingAcknowledgement();

            int maxConcurrent = _config != null ? Mathf.Clamp(_config.MaxConcurrentTransmissions.Value, 1, 6) : 3;
            while (_activeSources.Count < maxConcurrent)
            {
                ReadyClip clip;
                lock (_gate)
                {
                    for (int i = _ready.Count - 1; i >= 0; i--)
                    {
                        if (_ready[i].ExpireAt < _clock)
                            _ready.RemoveAt(i);
                    }

                    int readyIndex = FindPlayableReadyClipIndex();
                    if (readyIndex < 0)
                        return;

                    clip = _ready[readyIndex];
                    _ready.RemoveAt(readyIndex);
                }

                PlayClip(clip);
            }
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

            float[] samples;
            try
            {
                samples = PrepareSamples(clip);
            }
            catch (System.Exception ex)
            {
                _log?.LogWarning($"Radio effect processing failed ({ex.GetType().Name}: {ex.Message}); playing raw TTS.");
                samples = clip.Audio.Samples;
            }

            if (samples == null || samples.Length == 0)
                return;

            AudioClip audioClip = AudioClip.Create("RadioChatterTTS", samples.Length, 1, clip.Audio.SampleRate, false);
            audioClip.SetData(samples, 0);

            AudioSource source = _host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.ignoreListenerPause = false;
            source.volume = _config != null ? _config.Volume.Value : 0.8f;
            source.clip = audioClip;
            source.Play();
            _activeSources.Add(new ActiveTransmission
            {
                Role = clip.Role,
                Type = clip.Type,
                Source = source,
                Text = clip.Text,
                Response = ResponseFor(clip)
            });

            if (_config != null && _config.SubtitlesEnabled.Value)
                AddSubtitle($"{PrefixForRole(clip.Role)} {clip.Text}", clip.DisplaySeconds);

            if (!_playbackLogged)
            {
                _playbackLogged = true;
                _log?.LogInfo($"RadioChatter voice playback started: {samples.Length} samples @ {clip.Audio.SampleRate} Hz, effect={_config != null && _config.RadioEffectEnabled.Value}");
            }
        }

        private void CleanupFinishedSources()
        {
            List<PendingAcknowledgement> acknowledgements = null;

            for (int i = _activeSources.Count - 1; i >= 0; i--)
            {
                ActiveTransmission active = _activeSources[i];
                AudioSource source = active.Source;
                if (source != null && source.isPlaying)
                    continue;

                StopAndDestroySource(source);
                _activeSources.RemoveAt(i);
                if (!string.IsNullOrEmpty(active.Response.Text))
                {
                    if (acknowledgements == null)
                        acknowledgements = new List<PendingAcknowledgement>(2);

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
            float volume = _config != null ? _config.Volume.Value : 0.8f;
            for (int i = 0; i < _activeSources.Count; i++)
            {
                AudioSource source = _activeSources[i].Source;
                if (source != null)
                    source.volume = volume;
            }
        }

        private void Enqueue(RadioRole role, RadioEventType type, string text, float displaySeconds, float expireAt, ClipData clip)
        {
            lock (_gate)
            {
                _ready.Add(new ReadyClip
                {
                    Role = role,
                    Type = type,
                    Text = text,
                    DisplaySeconds = displaySeconds,
                    ExpireAt = expireAt,
                    Audio = clip
                });
            }

            _sidecar?.ReportRequestSuccess();
        }

        private int FindPlayableReadyClipIndex()
        {
            for (int i = 0; i < _ready.Count; i++)
            {
                if (!IsRoleActive(_ready[i].Role))
                    return i;
            }

            return -1;
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

        private void OnTtsFailure(string message)
        {
            _sidecar?.ReportRequestFailure(message);
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

        private bool TryStartAudioRequest(RadioRole role, RadioEventType type, string voice, string text, float displaySeconds, float expireAt)
        {
            if (_client.TryGetCached(voice, text, out ClipData cached))
            {
                Enqueue(role, type, text, displaySeconds, expireAt, cached);
                return true;
            }

            if (_sidecar != null && !_sidecar.CanRequestAudio)
                return false;

            MarkAudioRequestStarted(role);
            int generation = _audioGeneration;
            float requestedAt = _clock;
            if (!_client.Request(
                    voice,
                    text,
                    clip =>
                    {
                        MarkAudioRequestFinished(role);
                        if (generation == _audioGeneration && !WasPurgedSince(type, requestedAt))
                            Enqueue(role, type, text, displaySeconds, expireAt, clip);
                    },
                    message =>
                    {
                        MarkAudioRequestFinished(role);
                        if (generation == _audioGeneration)
                            OnTtsFailure(message);
                    }))
            {
                MarkAudioRequestFinished(role);
                return false;
            }

            return true;
        }

        private void QueuePendingSpeech(RadioRole role, RadioEventType type, string voice, string text, float displaySeconds, float expireAt)
        {
            foreach (PendingSpeech pending in _pendingSpeech)
            {
                if (pending.Role == role && pending.Voice == voice && pending.Text == text)
                    return;
            }

            while (_pendingSpeech.Count >= MaxPendingSpeech)
            {
                PendingSpeech dropped = _pendingSpeech.Dequeue();
                if (_config != null && _config.SubtitlesEnabled.Value)
                    AddSubtitle($"{PrefixForRole(dropped.Role)} {dropped.Text}", dropped.DisplaySeconds);
            }

            _pendingSpeech.Enqueue(new PendingSpeech
            {
                Role = role,
                Type = type,
                Voice = voice,
                Text = text,
                DisplaySeconds = displaySeconds,
                ExpiresAt = expireAt
            });

            if (_clock >= _nextPendingSpeechLogTime)
            {
                _nextPendingSpeechLogTime = _clock + 10f;
                _log?.LogInfo("Pocket TTS is not ready yet; queued voice audio for retry.");
            }
        }

        private void TryStartPendingSpeech()
        {
            if (_config == null || _client == null || _pendingSpeech.Count == 0)
                return;

            bool canRequest = _sidecar == null || _sidecar.CanRequestAudio;
            int count = _pendingSpeech.Count;
            float now = _clock;
            for (int i = 0; i < count; i++)
            {
                PendingSpeech pending = _pendingSpeech.Dequeue();
                if (pending.ExpiresAt < now)
                {
                    // A line that could not get audio in time must not vanish silently:
                    // show its subtitle so the radio traffic still reaches the player.
                    if (_config.SubtitlesEnabled.Value)
                        AddSubtitle($"{PrefixForRole(pending.Role)} {pending.Text}", pending.DisplaySeconds);
                    _log?.LogInfo($"Voice audio unavailable in time; showed subtitle only: {pending.Text}");
                    continue;
                }

                if (!canRequest)
                {
                    _pendingSpeech.Enqueue(pending);
                    continue;
                }

                if (!TryStartAudioRequest(pending.Role, pending.Type, pending.Voice, pending.Text, pending.DisplaySeconds, pending.ExpiresAt))
                {
                    _pendingSpeech.Enqueue(pending);
                    canRequest = false; // stop issuing requests but keep sweeping expired lines
                }
            }
        }

        private void MarkAudioRequestStarted(RadioRole role)
        {
            lock (_gate)
                _inFlightRoles.Add(role);
        }

        private void MarkAudioRequestFinished(RadioRole role)
        {
            lock (_gate)
            {
                for (int i = 0; i < _inFlightRoles.Count; i++)
                {
                    if (_inFlightRoles[i] == role)
                    {
                        _inFlightRoles.RemoveAt(i);
                        return;
                    }
                }
            }
        }

        private void QueueAcknowledgement(PendingAcknowledgement acknowledgement)
        {
            if (_config == null || !_config.PlayerAcknowledgements.Value)
                return;

            if (string.IsNullOrWhiteSpace(acknowledgement.Text))
                return;

            if (HasPendingResponseText(acknowledgement.Text))
                return;

            if (HasResponseWorkForRole(acknowledgement.Role))
                return;

            bool wasEmpty = _pendingAcknowledgements.Count == 0;
            while (_pendingAcknowledgements.Count >= MaxPendingAcknowledgements)
                _pendingAcknowledgements.Dequeue();

            _pendingAcknowledgements.Enqueue(acknowledgement);

            if (wasEmpty)
                _nextAcknowledgementReadyAt = _clock + AcknowledgementDelaySeconds;
        }

        private void ProcessPendingAcknowledgement()
        {
            if (_pendingAcknowledgements.Count == 0)
                return;

            if (HasNonPlayerAudioWork())
            {
                _nextAcknowledgementReadyAt = _clock + AcknowledgementDelaySeconds;
                return;
            }

            if (_clock < _nextAcknowledgementReadyAt)
                return;

            PendingAcknowledgement acknowledgement = _pendingAcknowledgements.Dequeue();
            if (!string.IsNullOrWhiteSpace(acknowledgement.Text))
                Transmit(acknowledgement.Role, RadioEventType.PlayerAcknowledgement, acknowledgement.Text, 2f);

            if (_pendingAcknowledgements.Count > 0)
                _nextAcknowledgementReadyAt = _clock + AcknowledgementDelaySeconds;
        }

        private bool HasNonPlayerAudioWork()
        {
            for (int i = 0; i < _activeSources.Count; i++)
            {
                ActiveTransmission active = _activeSources[i];
                if (!IsPlayerRole(active.Role) && active.Source != null)
                    return true;
            }

            lock (_gate)
            {
                for (int i = 0; i < _ready.Count; i++)
                {
                    if (!IsPlayerRole(_ready[i].Role))
                        return true;
                }

                foreach (PendingSpeech pending in _pendingSpeech)
                {
                    if (!IsPlayerRole(pending.Role))
                        return true;
                }

                for (int i = 0; i < _inFlightRoles.Count; i++)
                {
                    if (!IsPlayerRole(_inFlightRoles[i]))
                        return true;
                }
            }

            return false;
        }

        private bool HasPendingResponseText(string text)
        {
            foreach (PendingAcknowledgement pending in _pendingAcknowledgements)
            {
                if (string.Equals(pending.Text, text, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private bool HasResponseWorkForRole(RadioRole role)
        {
            foreach (PendingAcknowledgement pending in _pendingAcknowledgements)
            {
                if (pending.Role == role)
                    return true;
            }

            for (int i = 0; i < _activeSources.Count; i++)
            {
                ActiveTransmission active = _activeSources[i];
                if (active.Role == role && active.Source != null)
                    return true;
            }

            lock (_gate)
            {
                for (int i = 0; i < _ready.Count; i++)
                {
                    if (_ready[i].Role == role)
                        return true;
                }

                foreach (PendingSpeech pending in _pendingSpeech)
                {
                    if (pending.Role == role)
                        return true;
                }

                for (int i = 0; i < _inFlightRoles.Count; i++)
                {
                    if (_inFlightRoles[i] == role)
                        return true;
                }
            }

            return false;
        }

        private PendingAcknowledgement ResponseFor(ReadyClip clip)
        {
            if (_config == null || !_config.PlayerAcknowledgements.Value)
                return default;

            if (IsPlayerRole(clip.Role) || clip.Role == RadioRole.System)
                return default;

            string text = clip.Text ?? string.Empty;
            if (text.IndexOf("missile", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("defend", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                IsPlayerDownCall(text))
            {
                return default;
            }

            if (clip.Role == RadioRole.Tower)
            {
                string towerReadback = TowerReadbackFor(text);
                if (!string.IsNullOrEmpty(towerReadback))
                {
                    return new PendingAcknowledgement
                    {
                        Role = RadioRole.PlayerTower,
                        Text = towerReadback,
                        IsReadback = true
                    };
                }

                return default;
            }

            RadioRole responseRole = clip.Role == RadioRole.Awacs ? RadioRole.PlayerAwacs : RadioRole.PlayerFlight;
            return new PendingAcknowledgement
            {
                Role = responseRole,
                Text = PickAcknowledgement(),
                IsReadback = false
            };
        }

        private static string TowerReadbackFor(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string callsign = ExtractLeadingCallsign(text);

            int takeoffIndex = text.IndexOf("cleared for takeoff", System.StringComparison.OrdinalIgnoreCase);
            if (takeoffIndex >= 0)
                return AppendCallsign(CleanRadioPhrase(text.Substring(takeoffIndex)), callsign);

            int landingIndex = text.IndexOf("cleared to land", System.StringComparison.OrdinalIgnoreCase);
            if (landingIndex >= 0)
                return AppendCallsign(CleanRadioPhrase(text.Substring(landingIndex)), callsign);

            if (TryExtractSwitchStation(text, out string station))
                return AppendCallsign("switching " + station, callsign);

            return null;
        }

        private static bool TryExtractSwitchStation(string text, out string station)
        {
            station = null;

            int switchIndex = text.IndexOf("switch ", System.StringComparison.OrdinalIgnoreCase);
            if (switchIndex >= 0)
            {
                station = CleanRadioPhrase(text.Substring(switchIndex + "switch ".Length));
                return !string.IsNullOrEmpty(station);
            }

            int contactIndex = text.IndexOf("contact ", System.StringComparison.OrdinalIgnoreCase);
            if (contactIndex < 0)
                return false;

            int stationStart = contactIndex + "contact ".Length;
            int stationEnd = IndexOfAnyTerminator(text, stationStart, ",", ".", " on ");
            station = CleanRadioPhrase(text.Substring(stationStart, stationEnd - stationStart));
            return !string.IsNullOrEmpty(station);
        }

        private static int IndexOfAnyTerminator(string text, int start, params string[] terminators)
        {
            int best = text.Length;
            for (int i = 0; i < terminators.Length; i++)
            {
                int index = text.IndexOf(terminators[i], start, System.StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && index < best)
                    best = index;
            }

            return best;
        }

        private static string ExtractLeadingCallsign(string text)
        {
            int comma = text.IndexOf(',');
            if (comma <= 0)
                return string.Empty;

            return CleanRadioPhrase(text.Substring(0, comma));
        }

        private static string AppendCallsign(string phrase, string callsign)
        {
            phrase = CleanRadioPhrase(phrase);
            if (string.IsNullOrEmpty(phrase))
                return null;

            if (string.IsNullOrEmpty(callsign))
                return phrase;

            return phrase + ", " + callsign;
        }

        private static string CleanRadioPhrase(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            return text.Trim().Trim(',', '.', ';', ':').Trim();
        }

        private static bool IsPlayerDownCall(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return text.IndexOf(" is down", System.StringComparison.OrdinalIgnoreCase) >= 0 &&
                   text.IndexOf("no chute", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string PickAcknowledgement()
        {
            if (PlayerAcknowledgements.Length == 0)
                return "roger";

            int index = _ackRandom.Next(PlayerAcknowledgements.Length);
            if (PlayerAcknowledgements.Length > 1 && index == _lastAcknowledgementIndex)
                index = (index + 1) % PlayerAcknowledgements.Length;

            _lastAcknowledgementIndex = index;
            return PlayerAcknowledgements[index];
        }

        private float[] PrepareSamples(ReadyClip clip)
        {
            if (_config == null || !_config.RadioEffectEnabled.Value)
                return clip.Audio.Samples;

            float[] processed = new float[clip.Audio.Samples.Length];
            int sampleRate = Mathf.Max(1, clip.Audio.SampleRate);
            BiquadFilter highPass = BiquadFilter.HighPass(HighPassCutoffHz, FilterQ, sampleRate);
            BiquadFilter lowPass = BiquadFilter.LowPass(LowPassCutoffHz, FilterQ, sampleRate);
            float noiseLevel = Mathf.Min(Mathf.Clamp(_config.NoiseLevel.Value, 0f, 0.2f) * NoiseScale, MaxEffectiveNoise);

            for (int i = 0; i < clip.Audio.Samples.Length; i++)
            {
                float filtered = lowPass.Process(highPass.Process(clip.Audio.Samples[i]));
                float shaped = (float)System.Math.Tanh(filtered * SaturationGain) * OutputGain;

                if (noiseLevel > 0f)
                    shaped += ((float)_noise.NextDouble() * 2f - 1f) * noiseLevel;

                processed[i] = Mathf.Clamp(shaped, -1f, 1f);
            }

            return processed;
        }

        public void Shutdown()
        {
            _sidecar?.Shutdown();
            _sidecar = null;
            _client = null;

            lock (_gate)
            {
                _ready.Clear();
                _pendingSpeech.Clear();
                _inFlightRoles.Clear();
            }

            StopAll();
        }

        public void DrawGui()
        {
            if (_config == null)
                return;

            DrawSidecarStatus();

            if (!_config.SubtitlesEnabled.Value)
                return;

            PruneSubtitles(_clock);
            if (_subtitles.Count == 0)
                return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 18,
                    wordWrap = true
                };
                _style.normal.textColor = Color.white;
            }

            string subtitle = BuildSubtitleText();
            float width = Mathf.Min(Screen.width - 40f, 900f);
            float labelWidth = width - 24f;
            float labelHeight = _style.CalcHeight(new GUIContent(subtitle), labelWidth);
            float height = Mathf.Clamp(labelHeight + 16f, 70f, 220f);
            Rect rect = new Rect((Screen.width - width) * 0.5f, Screen.height - height - 40f, width, height);
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(rect.x + 12f, rect.y + 8f, labelWidth, rect.height - 16f), subtitle, _style);
        }

        private void DrawSidecarStatus()
        {
            if (_sidecar == null || !_config.SidecarStatusDisplay.Value)
                return;

            SidecarSupervisor.SidecarStatus status = _sidecar.Status;
            if (status != _lastSidecarStatus)
            {
                if (status == SidecarSupervisor.SidecarStatus.Available)
                    _sidecarReadyAt = _clock;
                _lastSidecarStatus = status;
            }

            string text;
            switch (status)
            {
                case SidecarSupervisor.SidecarStatus.Available:
                    if (float.IsNaN(_sidecarReadyAt) || _clock - _sidecarReadyAt > ReadyBannerSeconds)
                        return;
                    text = "Radio voice: ready";
                    break;
                case SidecarSupervisor.SidecarStatus.Starting:
                    text = "Radio voice: loading TTS model...";
                    break;
                case SidecarSupervisor.SidecarStatus.Unavailable:
                    text = "Radio voice: sidecar unavailable - subtitles only";
                    break;
                default:
                    text = "Radio voice: connecting to sidecar...";
                    break;
            }

            if (_statusStyle == null)
            {
                _statusStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleLeft,
                    fontSize = 13
                };
                _statusStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f, 0.95f);
            }

            Vector2 size = _statusStyle.CalcSize(new GUIContent(text));
            Rect rect = new Rect(Screen.width - size.x - 32f, Screen.height - 34f, size.x + 16f, 24f);
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(rect.x + 8f, rect.y + 2f, size.x, 20f), text, _statusStyle);
        }

        private void AddSubtitle(string text, float displaySeconds)
        {
            PruneSubtitles(_clock);
            while (_subtitles.Count >= MaxActiveSubtitles)
                _subtitles.RemoveAt(0);

            _subtitles.Add(new SubtitleLine
            {
                Text = text,
                Until = _clock + Mathf.Max(1f, displaySeconds)
            });
        }

        private void PruneSubtitles(float now)
        {
            for (int i = _subtitles.Count - 1; i >= 0; i--)
            {
                if (now > _subtitles[i].Until)
                    _subtitles.RemoveAt(i);
            }
        }

        private string BuildSubtitleText()
        {
            StringBuilder builder = new StringBuilder(160);
            for (int i = 0; i < _subtitles.Count; i++)
            {
                if (i > 0)
                    builder.Append('\n');
                builder.Append(_subtitles[i].Text);
            }

            return builder.ToString();
        }

        private static string PrefixForRole(RadioRole role)
        {
            if (role == RadioRole.Tower)
                return "[TWR]";

            if (role == RadioRole.Awacs)
                return "[AWACS]";

            if (role == RadioRole.Player)
                return "[PILOT]";

            if (role == RadioRole.PlayerTower)
                return "[PLAYER-TWR]";

            if (role == RadioRole.PlayerFlight)
                return "[PLAYER-FLIGHT]";

            if (role == RadioRole.PlayerAwacs)
                return "[PLAYER-AWACS]";

            if (role == RadioRole.Game)
                return "[COMMS]";

            return "[RadioChatter]";
        }

        private static bool SameAudioLane(RadioRole a, RadioRole b)
        {
            if (IsPlayerRole(a) && IsPlayerRole(b))
                return true;

            return a == b;
        }

        private static bool IsPlayerRole(RadioRole role)
        {
            return role == RadioRole.Player ||
                   role == RadioRole.PlayerTower ||
                   role == RadioRole.PlayerFlight ||
                   role == RadioRole.PlayerAwacs;
        }

        private static void StopAndDestroySource(AudioSource source)
        {
            if (source == null)
                return;

            AudioClip clip = source.clip;
            source.Stop();
            source.clip = null;
            if (clip != null)
                Object.Destroy(clip);
            Object.Destroy(source);
        }

        private struct PendingSpeech
        {
            public RadioRole Role;
            public RadioEventType Type;
            public string Voice;
            public string Text;
            public float DisplaySeconds;
            public float ExpiresAt;
        }

        private struct PendingAcknowledgement
        {
            public RadioRole Role;
            public string Text;
            public bool IsReadback;
        }

        private struct ReadyClip
        {
            public RadioRole Role;
            public RadioEventType Type;
            public string Text;
            public float DisplaySeconds;
            public float ExpireAt;
            public ClipData Audio;
        }

        private struct ActiveTransmission
        {
            public RadioRole Role;
            public RadioEventType Type;
            public AudioSource Source;
            public string Text;
            public PendingAcknowledgement Response;
        }

        private struct SubtitleLine
        {
            public string Text;
            public float Until;
        }

        private struct BiquadFilter
        {
            private float _b0;
            private float _b1;
            private float _b2;
            private float _a1;
            private float _a2;
            private float _z1;
            private float _z2;

            public static BiquadFilter HighPass(float cutoffHz, float q, int sampleRate)
            {
                float cutoff = ClampCutoff(cutoffHz, sampleRate);
                float omega = 2f * Mathf.PI * cutoff / sampleRate;
                float sin = Mathf.Sin(omega);
                float cos = Mathf.Cos(omega);
                float alpha = sin / (2f * Mathf.Max(0.001f, q));

                float b0 = (1f + cos) * 0.5f;
                float b1 = -(1f + cos);
                float b2 = (1f + cos) * 0.5f;
                float a0 = 1f + alpha;
                float a1 = -2f * cos;
                float a2 = 1f - alpha;
                return FromCoefficients(b0, b1, b2, a0, a1, a2);
            }

            public static BiquadFilter LowPass(float cutoffHz, float q, int sampleRate)
            {
                float cutoff = ClampCutoff(cutoffHz, sampleRate);
                float omega = 2f * Mathf.PI * cutoff / sampleRate;
                float sin = Mathf.Sin(omega);
                float cos = Mathf.Cos(omega);
                float alpha = sin / (2f * Mathf.Max(0.001f, q));

                float b0 = (1f - cos) * 0.5f;
                float b1 = 1f - cos;
                float b2 = (1f - cos) * 0.5f;
                float a0 = 1f + alpha;
                float a1 = -2f * cos;
                float a2 = 1f - alpha;
                return FromCoefficients(b0, b1, b2, a0, a1, a2);
            }

            public float Process(float input)
            {
                float output = _b0 * input + _z1;
                _z1 = _b1 * input - _a1 * output + _z2;
                _z2 = _b2 * input - _a2 * output;
                return output;
            }

            private static BiquadFilter FromCoefficients(float b0, float b1, float b2, float a0, float a1, float a2)
            {
                float invA0 = 1f / a0;
                return new BiquadFilter
                {
                    _b0 = b0 * invA0,
                    _b1 = b1 * invA0,
                    _b2 = b2 * invA0,
                    _a1 = a1 * invA0,
                    _a2 = a2 * invA0
                };
            }

            private static float ClampCutoff(float cutoffHz, int sampleRate)
            {
                float max = Mathf.Max(20f, sampleRate * 0.45f);
                return Mathf.Clamp(cutoffHz, 20f, max);
            }
        }
    }
}
