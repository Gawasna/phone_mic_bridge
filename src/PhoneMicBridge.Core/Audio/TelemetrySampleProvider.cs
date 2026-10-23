using System;
using NAudio.Wave;

namespace PhoneMicBridge.Audio
{
    /// <summary>
    /// Wraps an ISampleProvider to track byte throughput for telemetry.
    /// Used to monitor bidirectional HFP traffic for SCO stability diagnosis.
    /// </summary>
    public class TelemetrySampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly Action<long> _onBytesRead;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public TelemetrySampleProvider(ISampleProvider source, Action<long> onBytesRead)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _onBytesRead = onBytesRead ?? throw new ArgumentNullException(nameof(onBytesRead));
        }

        public int Read(Span<float> buffer)
        {
            int samplesRead = _source.Read(buffer);
            if (samplesRead > 0)
            {
                long bytesRead = samplesRead * sizeof(float);
                _onBytesRead(bytesRead);
            }
            return samplesRead;
        }
    }
}
