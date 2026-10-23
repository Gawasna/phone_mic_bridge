using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PhoneMicBridge.Audio
{
    public static class AudioResampler
    {
        public static ISampleProvider NormalizeToDestination(ISampleProvider source, int targetSampleRate, int targetChannels = 1)
        {
            ISampleProvider provider = source;

            // Channel downmix or upmix
            if (targetChannels == 1 && provider.WaveFormat.Channels > 1)
            {
                provider = provider.ToMono();
            }
            else if (targetChannels > 1 && provider.WaveFormat.Channels == 1)
            {
                provider = provider.ToStereo();
            }

            // Resample if necessary
            if (provider.WaveFormat.SampleRate != targetSampleRate)
            {
                provider = new WdlResamplingSampleProvider(provider, targetSampleRate);
            }

            return provider;
        }
    }
}
