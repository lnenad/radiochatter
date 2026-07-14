using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace RadioChatter.Speech
{
    internal sealed class PocketTtsClient
    {
        private readonly Config _config;
        private readonly object _gate = new object();
        private readonly Dictionary<string, ClipData> _cache = new Dictionary<string, ClipData>();
        private readonly Queue<string> _cacheOrder = new Queue<string>();
        private readonly HashSet<string> _inFlight = new HashSet<string>();

        public PocketTtsClient(Config config)
        {
            _config = config;
        }

        public bool TryGetCached(string voice, string text, out ClipData clip)
        {
            string key = CacheKey(voice, text);
            lock (_gate)
                return _cache.TryGetValue(key, out clip);
        }

        public bool Request(string voice, string text, Action<ClipData> onSuccess, Action<string> onFailure)
        {
            string key = CacheKey(voice, text);
            lock (_gate)
            {
                if (_inFlight.Contains(key))
                    return false;

                _inFlight.Add(key);
            }

            Task.Run(() =>
            {
                try
                {
                    byte[] wav = PostSpeak(voice, text);
                    ClipData clip = WavParser.Parse(wav);
                    AddCache(key, clip);
                    onSuccess?.Invoke(clip);
                }
                catch (Exception ex)
                {
                    onFailure?.Invoke($"{ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    lock (_gate)
                        _inFlight.Remove(key);
                }
            });

            return true;
        }

        private byte[] PostSpeak(string voice, string text)
        {
            string url = SidecarHttp.BuildUrl(_config, "/speak");
            string body = "{\"text\":\"" + MiniJson.Escape(text) + "\",\"voice\":\"" + MiniJson.Escape(voice) + "\"}";

            // The sidecar generates one line at a time (~1.5s each) and queues
            // overlapping requests FIFO, so the timeout must cover a full burst of
            // MaxPendingSpeech (8) lines — ~12s — not just one generation. Timing out
            // earlier abandons the HTTP call while the sidecar keeps generating, and the
            // caller's retry then amplifies load on an already-busy sidecar. 12s stays
            // under the shortest queued-clip lifetime (15s), so a reply that arrives at
            // the deadline is still playable.
            return SidecarHttp.PostJson(url, body, "audio/wav", 12000);
        }

        private void AddCache(string key, ClipData clip)
        {
            int max = Math.Max(0, _config.CacheSize.Value);
            if (max == 0)
                return;

            lock (_gate)
            {
                if (_cache.ContainsKey(key))
                    return;

                _cache[key] = clip;
                _cacheOrder.Enqueue(key);

                while (_cache.Count > max && _cacheOrder.Count > 0)
                {
                    string old = _cacheOrder.Dequeue();
                    _cache.Remove(old);
                }
            }
        }

        private static string CacheKey(string voice, string text)
        {
            return voice + "\n" + text;
        }
    }

    internal sealed class ClipData
    {
        public int SampleRate;
        public float[] Samples;
    }

    internal static class WavParser
    {
        public static ClipData Parse(byte[] wav)
        {
            using (BinaryReader reader = new BinaryReader(new MemoryStream(wav)))
            {
                if (ReadFourCc(reader) != "RIFF")
                    throw new InvalidDataException("Missing RIFF header");

                reader.ReadInt32();
                if (ReadFourCc(reader) != "WAVE")
                    throw new InvalidDataException("Missing WAVE header");

                short audioFormat = 0;
                short channels = 0;
                int sampleRate = 0;
                short bitsPerSample = 0;
                byte[] data = null;

                while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
                {
                    string chunk = ReadFourCc(reader);
                    int size = reader.ReadInt32();
                    long next = reader.BaseStream.Position + size + (size & 1);

                    if (chunk == "fmt ")
                    {
                        audioFormat = reader.ReadInt16();
                        channels = reader.ReadInt16();
                        sampleRate = reader.ReadInt32();
                        reader.ReadInt32();
                        reader.ReadInt16();
                        bitsPerSample = reader.ReadInt16();
                    }
                    else if (chunk == "data")
                    {
                        data = reader.ReadBytes(size);
                    }

                    reader.BaseStream.Position = Math.Min(next, reader.BaseStream.Length);
                }

                if (data == null || channels <= 0 || sampleRate <= 0)
                    throw new InvalidDataException("Incomplete WAV data");

                if (audioFormat != 1 || bitsPerSample != 16)
                    throw new InvalidDataException($"Unsupported WAV format {audioFormat}/{bitsPerSample}");

                int bytesPerSample = bitsPerSample / 8;
                int frameCount = data.Length / (bytesPerSample * channels);
                float[] samples = new float[frameCount];

                for (int frame = 0; frame < frameCount; frame++)
                {
                    int frameOffset = frame * bytesPerSample * channels;
                    int sum = 0;
                    for (int channel = 0; channel < channels; channel++)
                    {
                        int offset = frameOffset + channel * bytesPerSample;
                        short value = (short)(data[offset] | (data[offset + 1] << 8));
                        sum += value;
                    }

                    samples[frame] = (sum / (float)channels) / 32768f;
                }

                return new ClipData { SampleRate = sampleRate, Samples = samples };
            }
        }

        private static string ReadFourCc(BinaryReader reader)
        {
            return Encoding.ASCII.GetString(reader.ReadBytes(4));
        }
    }
}
