using System.Buffers;
using BenchmarkDotNet.Attributes;
using NAudio.Wave;
using Scribe.Core.Audio;

namespace Scribe.Benchmarks;

/// <summary>
/// Cost of wiping <see cref="AudioCaptureService.ReadAll"/>'s pooled scratch before it goes back to
/// the shared pool, which otherwise keeps the last dictation's resampled audio for the next renter.
/// The baseline arm is the loop as it was before the wipe, verbatim.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Audio")]
public class ReadAllScratchBenchmarks
{
    private const int SampleRate = 16_000;

    private float[] _samples = [];
    private ArraySampleProvider _provider = null!;

    [Params(2, 10, 30)]
    public int Seconds { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _samples = new float[SampleRate * Seconds];
        for (var index = 0; index < _samples.Length; index++)
        {
            _samples[index] = MathF.Sin(2 * MathF.PI * 220 * index / SampleRate) * 0.25f;
        }

        _provider = new ArraySampleProvider(_samples, SampleRate);
    }

    [Benchmark(Baseline = true)]
    public float[] WithoutWipe()
    {
        _provider.Reset();
        return ReadAllWithoutWipe(_provider);
    }

    [Benchmark]
    public float[] Shipping()
    {
        _provider.Reset();
        return AudioCaptureService.ReadAll(_provider);
    }

    private static float[] ReadAllWithoutWipe(ISampleProvider provider)
    {
        var samples = ArrayPool<float>.Shared.Rent(provider.WaveFormat.SampleRate);
        var count = 0;
        try
        {
            while (true)
            {
                if (count == samples.Length)
                {
                    var expanded = ArrayPool<float>.Shared.Rent(checked(samples.Length * 2));
                    samples.AsSpan(0, count).CopyTo(expanded);
                    ArrayPool<float>.Shared.Return(samples);
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
            ArrayPool<float>.Shared.Return(samples);
        }
    }

    private sealed class ArraySampleProvider(float[] samples, int sampleRate) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(Span<float> buffer)
        {
            var available = Math.Min(buffer.Length, samples.Length - _position);
            samples.AsSpan(_position, available).CopyTo(buffer);
            _position += available;
            return available;
        }

        public void Reset() => _position = 0;
    }
}
