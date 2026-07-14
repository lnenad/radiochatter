using UnityEngine;

namespace RadioChatter.Audio
{
    /// <summary>Offline radio-effect DSP applied to TTS samples before they become an
    /// AudioClip: band-pass, soft saturation, low-level noise.</summary>
    internal sealed class RadioEffectProcessor
    {
        private const float HighPassCutoffHz = 450f;
        private const float LowPassCutoffHz = 2600f;
        private const float FilterQ = 0.707f;
        private const float SaturationGain = 1.85f;
        private const float OutputGain = 1.15f;
        private const float NoiseScale = 0.18f;
        private const float MaxEffectiveNoise = 0.008f;

        private readonly Config _config;
        private readonly System.Random _noise = new System.Random();

        public RadioEffectProcessor(Config config)
        {
            _config = config;
        }

        /// <summary>Returns the processed samples, or the input array unchanged when the
        /// effect is disabled.</summary>
        public float[] Process(float[] samples, int sampleRate)
        {
            if (_config == null || !_config.RadioEffectEnabled.Value)
                return samples;

            float[] processed = new float[samples.Length];
            int rate = Mathf.Max(1, sampleRate);
            BiquadFilter highPass = BiquadFilter.HighPass(HighPassCutoffHz, FilterQ, rate);
            BiquadFilter lowPass = BiquadFilter.LowPass(LowPassCutoffHz, FilterQ, rate);
            float noiseLevel = Mathf.Min(Mathf.Clamp(_config.NoiseLevel.Value, 0f, 0.2f) * NoiseScale, MaxEffectiveNoise);

            for (int i = 0; i < samples.Length; i++)
            {
                float filtered = lowPass.Process(highPass.Process(samples[i]));
                float shaped = (float)System.Math.Tanh(filtered * SaturationGain) * OutputGain;

                if (noiseLevel > 0f)
                    shaped += ((float)_noise.NextDouble() * 2f - 1f) * noiseLevel;

                processed[i] = Mathf.Clamp(shaped, -1f, 1f);
            }

            return processed;
        }
    }

    /// <summary>RBJ biquad filter, direct form II transposed.</summary>
    internal struct BiquadFilter
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
