using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using RadioChatter.Comms;
using UnityEngine;

namespace RadioChatter.Speech
{
    /// <summary>Push-to-talk voice commands. Holding the PTT key records the microphone
    /// (main thread, Unity Microphone API); releasing it hands the samples to a background
    /// task that WAV-encodes them and asks the sidecar's /transcribe endpoint for text.
    /// The transcript is posted to the RadioEventBus as a PlayerVoiceCommand and handled
    /// by CommsDirector on the next poll tick.</summary>
    internal sealed class VoiceCommandController
    {
        private const int RequestedSampleRate = 16000;
        private const float MinCommandSeconds = 0.35f;
        private const int TranscribeTimeoutMs = 15000;

        private readonly Config _config;
        private readonly ManualLogSource _log;

        private AudioClip _clip;
        private string _device;
        private float _recordStartedAt;
        private bool _recording;
        private volatile bool _transcribing;
        private bool _keyWasHeld;
        private bool _micWarningLogged;
        private GUIStyle _style;

        public VoiceCommandController(Config config, ManualLogSource log)
        {
            _config = config;
            _log = log;
        }

        public bool IsPushToTalkHeld { get; private set; }

        public void Tick()
        {
            if (_config == null || !_config.VoiceCommandsEnabled.Value)
            {
                IsPushToTalkHeld = false;
                if (_recording)
                    CancelRecording();
                return;
            }

            bool held = UnityInput.Current.GetKey(_config.VoicePushToTalkKey.Value);
            IsPushToTalkHeld = held;

            if (_recording)
            {
                // Finish slightly before the non-looping clip fills so Microphone.GetPosition
                // is still live and reports a valid sample count.
                if (!held || Time.unscaledTime - _recordStartedAt >= MaxCommandSeconds())
                    FinishRecording();
            }
            else if (held && !_keyWasHeld && !_transcribing)
            {
                StartRecording();
            }

            _keyWasHeld = held;
        }

        public void DrawGui()
        {
            if (_config == null || !_config.VoiceCommandsEnabled.Value)
                return;

            string text;
            if (_recording)
                text = "● VOICE transmitting";
            else if (_transcribing)
                text = "VOICE processing...";
            else
                return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleLeft,
                    fontSize = 13
                };
            }

            _style.normal.textColor = _recording
                ? new Color(1f, 0.45f, 0.35f, 0.95f)
                : new Color(0.85f, 0.85f, 0.85f, 0.95f);

            Vector2 size = _style.CalcSize(new GUIContent(text));
            Rect rect = new Rect(Screen.width - size.x - 32f, Screen.height - 62f, size.x + 16f, 24f);
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(rect.x + 8f, rect.y + 2f, size.x, 20f), text, _style);
        }

        private float MaxCommandSeconds()
        {
            return Mathf.Clamp(_config.VoiceMaxCommandSeconds.Value, 2f, 20f);
        }

        private void StartRecording()
        {
            string configured = _config.VoiceMicrophoneDevice.Value;
            _device = string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();

            try
            {
                if (Microphone.devices == null || Microphone.devices.Length == 0)
                {
                    WarnOnce("No microphone found; voice commands are unavailable.");
                    return;
                }

                int lengthSec = Mathf.CeilToInt(MaxCommandSeconds()) + 1;
                _clip = Microphone.Start(_device, false, lengthSec, RequestedSampleRate);
                if (_clip == null)
                {
                    WarnOnce($"Microphone.Start failed for device '{_device ?? "default"}'.");
                    return;
                }

                _recording = true;
                _recordStartedAt = Time.unscaledTime;
            }
            catch (Exception ex)
            {
                _clip = null;
                _recording = false;
                WarnOnce($"Microphone start failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void CancelRecording()
        {
            _recording = false;
            AudioClip clip = _clip;
            _clip = null;

            try
            {
                Microphone.End(_device);
            }
            catch (Exception)
            {
                // Nothing actionable; the device may already be gone.
            }

            if (clip != null)
                UnityEngine.Object.Destroy(clip);
        }

        private void FinishRecording()
        {
            _recording = false;
            AudioClip clip = _clip;
            _clip = null;
            if (clip == null)
                return;

            int position = 0;
            try
            {
                position = Microphone.GetPosition(_device);
                Microphone.End(_device);
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"Microphone stop failed: {ex.GetType().Name}: {ex.Message}");
            }

            int channels = Mathf.Max(1, clip.channels);
            int frequency = clip.frequency;

            // If the position probe failed (device unplugged mid-utterance, driver quirk),
            // fall back to the wall-clock estimate of how much was recorded.
            int elapsedSamples = Mathf.Clamp(
                Mathf.RoundToInt((Time.unscaledTime - _recordStartedAt) * frequency), 0, clip.samples);
            int sampleCount = position > 0 ? Mathf.Min(position, clip.samples) : elapsedSamples;

            if (sampleCount < MinCommandSeconds * frequency)
            {
                UnityEngine.Object.Destroy(clip);
                return;
            }

            float[] frames = new float[sampleCount * channels];
            clip.GetData(frames, 0);
            UnityEngine.Object.Destroy(clip);

            string url = _config.SidecarUrl.Value.TrimEnd('/') + "/transcribe";
            string prompt = BuildPrompt();
            _transcribing = true;
            Task.Run(() => TranscribeAndDispatch(url, frames, frequency, channels, prompt));
        }

        private void TranscribeAndDispatch(string url, float[] frames, int frequency, int channels, string prompt)
        {
            try
            {
                byte[] wav = EncodeWavMono16(frames, frequency, channels);
                string transcript = PostTranscribe(url, wav, prompt);
                if (!SpeechTranscriptFilter.HasWords(transcript))
                {
                    _log?.LogDebug("Ignoring push-to-talk recording with no recognized speech.");
                    return;
                }

                RadioEventBus.Enqueue(new RadioEvent
                {
                    Type = RadioEventType.PlayerVoiceCommand,
                    Text = transcript.Trim()
                });
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"Voice command transcription failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _transcribing = false;
            }
        }

        /// <summary>Vocabulary bias for whisper: callsigns and the expected phraseology
        /// dramatically improve recognition of radio-speak on small models.</summary>
        private string BuildPrompt()
        {
            string prompt = "Air traffic control radio. " +
                   _config.PlayerCallsign.Value + ". " +
                   _config.AwacsCallsign.Value + ". " +
                   "Tower, this is " + _config.PlayerCallsign.Value + ", requesting takeoff. " +
                   "Request landing clearance. Inbound. " +
                   "Request picture. Bogey dope. Request vector to target. " +
                   "Vector to objective. Request objective list. " +
                   "Vector to home plate. Return to base. Radio check. Winchester. Radio quiet. Resume calls. " +
                   "Checking in, CAP as fragged. Checking in as close air support. " +
                   "Checking in, SEAD as fragged. Checking in, strike as fragged. " +
                   "Mission search and destroy. Mission general. " +
                   "Hammer four unable. Negative Anvil one. Unable to assist Ranger two. " +
                   _config.AwacsCallsign.Value + ", " + _config.PlayerCallsign.Value + ", airborne, checking in.";

            if (_config.VoiceRequireTowerReadbacks.Value)
            {
                prompt += " Cleared for takeoff runway two seven, " + _config.PlayerCallsign.Value + "." +
                          " Cleared to land runway two seven, " + _config.PlayerCallsign.Value + "." +
                          " Switching " + _config.AwacsCallsign.Value + ", " + _config.PlayerCallsign.Value + ".";
            }

            return prompt;
        }

        private static string PostTranscribe(string url, byte[] wav, string prompt)
        {
            byte[] payload = Encoding.UTF8.GetBytes(
                "{\"audio_b64\":\"" + Convert.ToBase64String(wav) + "\",\"prompt\":\"" + JsonEscape(prompt) + "\"}");

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "application/json";
            request.Timeout = TranscribeTimeoutMs;
            request.ReadWriteTimeout = TranscribeTimeoutMs;
            request.ContentLength = payload.Length;

            using (Stream stream = request.GetRequestStream())
                stream.Write(payload, 0, payload.Length);

            string body;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream responseStream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(responseStream, Encoding.UTF8))
                body = reader.ReadToEnd();

            return ExtractJsonString(body, "text");
        }

        private static byte[] EncodeWavMono16(float[] frames, int frequency, int channels)
        {
            int frameCount = frames.Length / channels;
            using (MemoryStream memory = new MemoryStream(44 + frameCount * 2))
            using (BinaryWriter writer = new BinaryWriter(memory))
            {
                int dataBytes = frameCount * 2;
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataBytes);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1);          // PCM
                writer.Write((short)1);          // mono
                writer.Write(frequency);
                writer.Write(frequency * 2);     // byte rate
                writer.Write((short)2);          // block align
                writer.Write((short)16);         // bits per sample
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataBytes);

                for (int frame = 0; frame < frameCount; frame++)
                {
                    float sum = 0f;
                    int offset = frame * channels;
                    for (int channel = 0; channel < channels; channel++)
                        sum += frames[offset + channel];

                    float sample = Mathf.Clamp(sum / channels, -1f, 1f);
                    writer.Write((short)Mathf.RoundToInt(sample * 32767f));
                }

                writer.Flush();
                return memory.ToArray();
            }
        }

        private static string JsonEscape(string value)
        {
            StringBuilder builder = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 32)
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            builder.Append(c);
                        break;
                }
            }
            return builder.ToString();
        }

        private static string ExtractJsonString(string json, string field)
        {
            string marker = "\"" + field + "\"";
            int index = json.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
                return null;

            index = json.IndexOf(':', index + marker.Length);
            if (index < 0)
                return null;

            index = json.IndexOf('"', index);
            if (index < 0)
                return null;

            StringBuilder builder = new StringBuilder(64);
            for (int i = index + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"')
                    return builder.ToString();

                if (c == '\\' && i + 1 < json.Length)
                {
                    char escape = json[++i];
                    switch (escape)
                    {
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'u':
                            if (i + 4 < json.Length)
                            {
                                builder.Append((char)Convert.ToInt32(json.Substring(i + 1, 4), 16));
                                i += 4;
                            }
                            break;
                        default: builder.Append(escape); break;
                    }
                }
                else
                {
                    builder.Append(c);
                }
            }

            return null;
        }

        private void WarnOnce(string message)
        {
            if (_micWarningLogged)
                return;

            _micWarningLogged = true;
            _log?.LogWarning(message);
        }
    }
}
