using System;
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

        public void UpdateFromRawBuffer(byte[] buffer, int bytesRecorded, WaveFormat format, float gain = 1.0f)
        {
            if (bytesRecorded <= 0) return;

            float sum = 0;
            float max = 0;
            int count = 0;

            if (format.BitsPerSample == 32)
            {
                int floatCount = bytesRecorded / 4;
                for (int i = 0; i < floatCount; i++)
                {
                    float sample = BitConverter.ToSingle(buffer, i * 4) * gain;
                    float abs = Math.Abs(sample);
                    if (abs > max) max = abs;
                    sum += sample * sample;
                    count++;
                }
            }
            else if (format.BitsPerSample == 16)
            {
                int shortCount = bytesRecorded / 2;
                for (int i = 0; i < shortCount; i++)
                {
                    short val = BitConverter.ToInt16(buffer, i * 2);
                    float sample = (val / 32768.0f) * gain;
                    float abs = Math.Abs(sample);
                    if (abs > max) max = abs;
                    sum += sample * sample;
                    count++;
                }
            }

            Peak = max;
            Rms = count > 0 ? (float)Math.Sqrt(sum / count) : 0f;
        }
    }
}
