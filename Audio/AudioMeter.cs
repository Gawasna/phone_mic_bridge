using System;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PhoneMicBridge.Audio
{
    public class AudioMeter
    {
        public float Peak { get; private set; }
        public float Rms { get; private set; }

        public ISampleProvider Wrap(ISampleProvider source)
        {
            var metering = new MeteringSampleProvider(source);
            metering.StreamVolume += (s, e) =>
            {
                if (e.MaxSampleValues != null && e.MaxSampleValues.Length > 0)
                {
                    Peak = e.MaxSampleValues[0];
                }
            };
            return metering;
        }

        /// <summary>
        /// Zero-allocation hot path audio inspection using Span and MemoryMarshal.Cast.
        /// Eliminates all temporary allocations and per-sample BitConverter overhead.
        /// </summary>
        public void UpdateFromRawBuffer(ReadOnlySpan<byte> buffer, WaveFormat format, float gain = 1.0f)
        {
            if (buffer.IsEmpty) return;

            float sum = 0f;
            float max = 0f;

            if (format.BitsPerSample == 32)
            {
                ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(buffer);
                for (int i = 0; i < samples.Length; i++)
                {
                    float sample = samples[i] * gain;
                    float abs = Math.Abs(sample);
                    if (abs > max) max = abs;
                    sum += sample * sample;
                }

                Peak = max;
                Rms = samples.Length > 0 ? MathF.Sqrt(sum / samples.Length) : 0f;
            }
            else if (format.BitsPerSample == 16)
            {
                ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(buffer);
                for (int i = 0; i < samples.Length; i++)
                {
                    float sample = (samples[i] / 32768.0f) * gain;
                    float abs = Math.Abs(sample);
                    if (abs > max) max = abs;
                    sum += sample * sample;
                }

                Peak = max;
                Rms = samples.Length > 0 ? MathF.Sqrt(sum / samples.Length) : 0f;
            }
        }

        /// <summary>
        /// Overload accepting byte array and length for backward compatibility with NAudio event buffers.
        /// </summary>
        public void UpdateFromRawBuffer(byte[] buffer, int bytesRecorded, WaveFormat format, float gain = 1.0f)
        {
            if (buffer == null || bytesRecorded <= 0) return;
            UpdateFromRawBuffer(buffer.AsSpan(0, bytesRecorded), format, gain);
        }
    }
}
