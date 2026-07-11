using NAudio.Wave;
using Scribe.Core.Audio;
using Xunit;

namespace Scribe.Core.Tests;

public class MonoDownmixSampleProviderTests
{
    [Fact]
    public void Averages_stereo_channels_into_mono()
    {
        // Interleaved stereo: L=1.0, R=0.0 per frame → mono should be 0.5.
        float[] interleaved = [1f, 0f, 1f, 0f, 1f, 0f, 1f, 0f];
        var source = new ArraySampleProvider(interleaved, sampleRate: 16000, channels: 2);

        var mono = new MonoDownmixSampleProvider(source);
        float[] output = new float[8];
        int read = mono.Read(output, 0, output.Length);

        Assert.Equal(1, mono.WaveFormat.Channels);
        Assert.Equal(16000, mono.WaveFormat.SampleRate);
        Assert.Equal(4, read);
        for (int i = 0; i < read; i++)
        {
            Assert.Equal(0.5f, output[i], precision: 5);
        }
    }

    [Fact]
    public void Passes_mono_source_through_unchanged()
    {
        float[] samples = [0.1f, -0.2f, 0.3f, -0.4f];
        var source = new ArraySampleProvider(samples, sampleRate: 16000, channels: 1);

        var mono = new MonoDownmixSampleProvider(source);
        float[] output = new float[4];
        int read = mono.Read(output, 0, output.Length);

        Assert.Equal(4, read);
        Assert.Equal(samples, output);
    }

    [Fact]
    public void ReadAll_returns_every_sample_when_the_initial_buffer_must_grow()
    {
        float[] samples = Enumerable.Range(0, 40_000).Select(index => index / 40_000f).ToArray();
        var source = new ArraySampleProvider(samples, sampleRate: 16_000, channels: 1);

        var output = AudioCaptureService.ReadAll(source);

        Assert.Equal(samples, output);
    }

    private sealed class ArraySampleProvider(float[] data, int sampleRate, int channels) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int available = Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }
}
