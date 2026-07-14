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
            string url = _config.SidecarUrl.Value.TrimEnd('/') + "/speak";
            byte[] payload = Encoding.UTF8.GetBytes(
                "{\"text\":\"" + JsonEscape(text) + "\",\"voice\":\"" + JsonEscape(voice) + "\"}");

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "audio/wav";
            // The sidecar generates one line at a time (~1.5s each) and queues
            // overlapping requests FIFO, so the timeout must cover a full burst of
            // MaxPendingSpeech lines, not just one generation. Requests that would
            // outlive the shortest clip lifetime (10s) are not worth waiting for.
            request.Timeout = 6000;
            request.ReadWriteTimeout = 6000;
            request.ContentLength = payload.Length;

            using (Stream stream = request.GetRequestStream())
                stream.Write(payload, 0, payload.Length);

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream responseStream = response.GetResponseStream())
            using (MemoryStream memory = new MemoryStream())
            {
                responseStream.CopyTo(memory);
                return memory.ToArray();
            }
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
