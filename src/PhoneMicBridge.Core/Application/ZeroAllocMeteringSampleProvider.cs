using System;
using NAudio.Wave;
using PhoneMicBridge.Audio;

namespace PhoneMicBridge.Application
{
    /// <summary>
    /// Zero-allocation decorator around ISampleProvider.
    /// Reads samples directly into the downstream buffer and inspects Peak/RMS on a stack-allocated Span<float>.
    /// Generates zero heap allocations and eliminates GC pressure during high-throughput audio playback.
    /// </summary>
    public class ZeroAllocMeteringSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly AudioMeter _meter;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public ZeroAllocMeteringSampleProvider(ISampleProvider source, AudioMeter meter)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _meter = meter ?? throw new ArgumentNullException(nameof(meter));
        }

        public int Read(Span<float> buffer)
        {
            int samplesRead = _source.Read(buffer);
            if (samplesRead > 0)
            {
                _meter.UpdateFromSamples(buffer.Slice(0, samplesRead));
            }
            return samplesRead;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }
    }
}
