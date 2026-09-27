using System.Buffers;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Scribe.Core.Audio;

namespace Scribe.Core.Tests;

public sealed class AudioResamplingEquivalenceTests
{
    [Theory]
    [MemberData(nameof(ResampleCases))]
    public void ResampleToTarget_matches_release_050_read_boundaries_exactly(int sampleRate, int channels, int seconds)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        var raw = BuildRawFloatAudio(sampleRate, channels, seconds);

        var expected = ResampleLikeRelease050(raw, format);
        var actual = AudioCaptureService.ResampleToTarget(raw, raw.Length, format);

        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index], actual[index]);
        }
    }

    public static TheoryData<int, int, int> ResampleCases() => new()
    {
        { 44_100, 1, 12 },
        { 44_100, 2, 12 },
        { 48_000, 1, 12 },
        { 48_000, 2, 12 },
        { 44_100, 1, 55 },
        { 44_100, 2, 55 },
        { 48_000, 1, 55 },
        { 48_000, 2, 55 },
    };

    private static byte[] BuildRawFloatAudio(int sampleRate, int channels, int seconds)
    {
        var random = new Random(HashCode.Combine(sampleRate, channels, seconds));
        var samples = new float[sampleRate * channels * seconds];
        for (var frame = 0; frame < sampleRate * seconds; frame++)
        {
            var seededNoise = ((float)random.NextDouble() - 0.5f) * 0.02f;
            var tone = MathF.Sin(2 * MathF.PI * 440 * frame / sampleRate) * 0.35f;
            for (var channel = 0; channel < channels; channel++)
            {
                samples[(frame * channels) + channel] = (tone * (1f - channel * 0.1f)) + seededNoise;
            }
        }

        return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }

    private static float[] ResampleLikeRelease050(byte[] bytes, WaveFormat format)
    {
        var rawStream = new RawSourceWaveStream(bytes, 0, bytes.Length, format);
        ISampleProvider source = rawStream.ToSampleProvider();
        ISampleProvider mono = format.Channels == 1
            ? source
            : new MonoDownmixSampleProvider(source);
        ISampleProvider resampled = mono.WaveFormat.SampleRate == 16_000
            ? mono
            : new WdlResamplingSampleProvider(mono, 16_000);

        return ReadAllLikeRelease050(resampled);
    }

    private static float[] ReadAllLikeRelease050(ISampleProvider provider)
    {
        var pool = ArrayPool<float>.Shared;
        var samples = pool.Rent(provider.WaveFormat.SampleRate);
        var count = 0;
        try
        {
            while (true)
            {
                if (count == samples.Length)
                {
                    var expanded = pool.Rent(checked(samples.Length * 2));
                    samples.AsSpan(0, count).CopyTo(expanded);
                    samples.AsSpan().Clear();
                    pool.Return(samples);
                    samples = expanded;
                }

                var read = provider.Read(samples.AsSpan(count));
                if (read <= 0)
                {
                    return samples.AsSpan(0, count).ToArray();
                }

                count += read;
            }
        }
        finally
        {
            samples.AsSpan().Clear();
            pool.Return(samples);
        }
    }
}
