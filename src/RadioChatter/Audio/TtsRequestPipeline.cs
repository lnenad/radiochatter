using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;
using RadioChatter.Comms;
using RadioChatter.Speech;

namespace RadioChatter.Audio
{
    /// <summary>A line waiting for TTS audio (sidecar not ready or saturated).</summary>
    internal struct PendingSpeech
    {
        public RadioRole Role;
        public RadioEventType Type;
        public string Voice;
        public string Text;
        public float DisplaySeconds;
        public float ExpiresAt;
    }

    /// <summary>A synthesized clip waiting for a free audio lane.</summary>
    internal struct ReadyClip
    {
        public RadioRole Role;
        public RadioEventType Type;
        public string Text;
        public float DisplaySeconds;
        public float ExpireAt;
        public ClipData Audio;
    }

    /// <summary>Owns the asynchronous half of the audio path: TTS requests, the retry queue
    /// for lines the sidecar could not take yet, and the ready-clip queue their results land
    /// in. This is the only audio class touched from background threads — TTS callbacks
    /// arrive on thread-pool threads — so every collection here is guarded by one gate and
    /// the purge generation is read/written with Interlocked. Everything downstream
    /// (AudioSources, subtitles, acknowledgements) stays main-thread-only in
    /// RadioAudioPlayer.</summary>
    internal sealed class TtsRequestPipeline
    {
        public delegate void SubtitleFallback(RadioRole role, string text, float displaySeconds);

        private const int MaxPendingSpeech = 8;

        private readonly object _gate = new object();
        private readonly List<ReadyClip> _ready = new List<ReadyClip>(8);
        private readonly Queue<PendingSpeech> _pendingSpeech = new Queue<PendingSpeech>();
        private readonly List<RadioRole> _inFlightRoles = new List<RadioRole>(8);
        private readonly Dictionary<RadioEventType, float> _typePurgedAt = new Dictionary<RadioEventType, float>();
        private int _audioGeneration;

        private PocketTtsClient _client;
        private SidecarSupervisor _sidecar;
        private ManualLogSource _log;
        private SubtitleFallback _subtitleFallback;
        private float _nextPendingSpeechLogTime;

        public void Initialize(PocketTtsClient client, SidecarSupervisor sidecar, ManualLogSource log, SubtitleFallback subtitleFallback)
        {
            _client = client;
            _sidecar = sidecar;
            _log = log;
            _subtitleFallback = subtitleFallback;
        }

        public void Detach()
        {
            _client = null;
            _sidecar = null;
        }

        /// <summary>Requests audio for a line, or parks it in the retry queue when the
        /// sidecar cannot take it yet. Lines that expire before audio arrives fall back to
        /// subtitles via the callback.</summary>
        public void Submit(RadioRole role, RadioEventType type, string voice, string text, float displaySeconds, float expireAt, float now)
        {
            if (!TryStartAudioRequest(role, type, voice, text, displaySeconds, expireAt, now))
                QueuePendingSpeech(role, type, voice, text, displaySeconds, expireAt, now);
        }

        /// <summary>Retries parked lines; call once per frame.</summary>
        public void Tick(float now)
        {
            if (_client == null)
                return;

            lock (_gate)
            {
                if (_pendingSpeech.Count == 0)
                    return;
            }

            bool canRequest = _sidecar == null || _sidecar.CanRequestAudio;
            int count;
            lock (_gate)
                count = _pendingSpeech.Count;

            for (int i = 0; i < count; i++)
            {
                PendingSpeech pending;
                lock (_gate)
                {
                    if (_pendingSpeech.Count == 0)
                        return;
                    pending = _pendingSpeech.Dequeue();
                }

                if (pending.ExpiresAt < now)
                {
                    // A line that could not get audio in time must not vanish silently:
                    // show its subtitle so the radio traffic still reaches the player.
                    _subtitleFallback?.Invoke(pending.Role, pending.Text, pending.DisplaySeconds);
                    _log?.LogInfo($"Voice audio unavailable in time; showed subtitle only: {pending.Text}");
                    continue;
                }

                if (!canRequest)
                {
                    lock (_gate)
                        _pendingSpeech.Enqueue(pending);
                    continue;
                }

                if (!TryStartAudioRequest(pending.Role, pending.Type, pending.Voice, pending.Text, pending.DisplaySeconds, pending.ExpiresAt, now))
                {
                    lock (_gate)
                        _pendingSpeech.Enqueue(pending);
                    canRequest = false; // stop issuing requests but keep sweeping expired lines
                }
            }
        }

        /// <summary>Drops queued/pending work of one type and invalidates in-flight TTS
        /// requests for it (their results are discarded on arrival).</summary>
        public void PurgeType(RadioEventType type, float now)
        {
            lock (_gate)
            {
                _typePurgedAt[type] = now;

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
        }

        /// <summary>Drops everything and invalidates every in-flight TTS request.</summary>
        public void PurgeAll()
        {
            Interlocked.Increment(ref _audioGeneration);

            lock (_gate)
            {
                _ready.Clear();
                _pendingSpeech.Clear();
                _inFlightRoles.Clear();
            }
        }

        /// <summary>Removes and returns the first non-expired ready clip the caller can play
        /// right now. The predicate runs under the pipeline gate — keep it cheap and
        /// lock-free (it only reads main-thread playback state).</summary>
        public bool TryTakeReady(float now, System.Func<ReadyClip, bool> canPlay, out ReadyClip clip)
        {
            lock (_gate)
            {
                for (int i = _ready.Count - 1; i >= 0; i--)
                {
                    if (_ready[i].ExpireAt < now)
                        _ready.RemoveAt(i);
                }

                for (int i = 0; i < _ready.Count; i++)
                {
                    if (!canPlay(_ready[i]))
                        continue;

                    clip = _ready[i];
                    _ready.RemoveAt(i);
                    return true;
                }
            }

            clip = default;
            return false;
        }

        public bool HasWorkForRole(RadioRole role)
        {
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

        public bool HasNonPlayerWork()
        {
            lock (_gate)
            {
                for (int i = 0; i < _ready.Count; i++)
                {
                    if (!RadioRoles.IsPlayerRole(_ready[i].Role))
                        return true;
                }

                foreach (PendingSpeech pending in _pendingSpeech)
                {
                    if (!RadioRoles.IsPlayerRole(pending.Role))
                        return true;
                }

                for (int i = 0; i < _inFlightRoles.Count; i++)
                {
                    if (!RadioRoles.IsPlayerRole(_inFlightRoles[i]))
                        return true;
                }
            }

            return false;
        }

        private bool TryStartAudioRequest(RadioRole role, RadioEventType type, string voice, string text, float displaySeconds, float expireAt, float now)
        {
            PocketTtsClient client = _client;
            if (client == null)
                return true; // shutting down; swallow the line

            if (client.TryGetCached(voice, text, out ClipData cached))
            {
                Enqueue(role, type, text, displaySeconds, expireAt, cached);
                return true;
            }

            if (_sidecar != null && !_sidecar.CanRequestAudio)
                return false;

            MarkAudioRequestStarted(role);
            int generation = Volatile.Read(ref _audioGeneration);
            float requestedAt = now;
            if (!client.Request(
                    voice,
                    text,
                    clip =>
                    {
                        MarkAudioRequestFinished(role);
                        if (generation == Volatile.Read(ref _audioGeneration) && !WasPurgedSince(type, requestedAt))
                            Enqueue(role, type, text, displaySeconds, expireAt, clip);
                    },
                    message =>
                    {
                        MarkAudioRequestFinished(role);
                        if (generation == Volatile.Read(ref _audioGeneration))
                            _sidecar?.ReportRequestFailure(message);
                    }))
            {
                MarkAudioRequestFinished(role);
                return false;
            }

            return true;
        }

        private void QueuePendingSpeech(RadioRole role, RadioEventType type, string voice, string text, float displaySeconds, float expireAt, float now)
        {
            lock (_gate)
            {
                foreach (PendingSpeech pending in _pendingSpeech)
                {
                    if (pending.Role == role && pending.Voice == voice && pending.Text == text)
                        return;
                }

                while (_pendingSpeech.Count >= MaxPendingSpeech)
                {
                    PendingSpeech dropped = _pendingSpeech.Dequeue();
                    _subtitleFallback?.Invoke(dropped.Role, dropped.Text, dropped.DisplaySeconds);
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
            }

            if (now >= _nextPendingSpeechLogTime)
            {
                _nextPendingSpeechLogTime = now + 10f;
                _log?.LogInfo("Pocket TTS is not ready yet; queued voice audio for retry.");
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

        private bool WasPurgedSince(RadioEventType type, float requestedAt)
        {
            lock (_gate)
            {
                float purgedAt;
                return _typePurgedAt.TryGetValue(type, out purgedAt) && purgedAt >= requestedAt;
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
    }
}
