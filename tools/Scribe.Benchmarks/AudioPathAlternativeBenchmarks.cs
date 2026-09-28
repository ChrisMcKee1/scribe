using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using NAudio.Wave;
using Scribe.Core.Audio;
using Scribe.Core.Transcription;

namespace Scribe.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("Audio")]
public class AudioPathAlternativeBenchmarks
{
    private readonly NonPoolingFloatPool _pool = new();
    private byte[] _stereoFloatRaw = [];
    private byte[] _monoFloatRaw = [];
    private float[] _samples16k = [];
    private ArraySampleProvider _readAllSource = new([]);
    private WaveFormat _stereoFloatFormat = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
    private WaveFormat _monoFloatFormat = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);

    [Params(12, 55)]
    public int Seconds { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _stereoFloatFormat = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        _monoFloatFormat = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);
        _stereoFloatRaw = BuildInterleavedFloatRaw(Seconds, channels: 2);
        _monoFloatRaw = BuildInterleavedFloatRaw(Seconds, channels: 1);
        _samples16k = BuildSamples(Seconds, 16_000);
        _readAllSource = new ArraySampleProvider(_samples16k);
    }

    [Benchmark(Baseline = true)]
    public CaptureSignalReport OldSignalAnalyzeStereoFloat() =>
        OldAnalyzeFloat(MemoryMarshal.Cast<byte, float>(_stereoFloatRaw), channels: 2, sampleRate: 48_000);

    [Benchmark]
    public CaptureSignalReport OptimizedSignalAnalyzeStereoFloat() =>
        CaptureSignalAnalyzer.Analyze(_stereoFloatRaw, _stereoFloatFormat);

    [Benchmark]
    public CaptureSignalReport OldSignalAnalyzeMonoFloat() =>
        OldAnalyzeFloat(MemoryMarshal.Cast<byte, float>(_monoFloatRaw), channels: 1, sampleRate: 48_000);

    [Benchmark]
    public CaptureSignalReport OptimizedSignalAnalyzeMonoFloat() =>
        CaptureSignalAnalyzer.Analyze(_monoFloatRaw, _monoFloatFormat);

    [Benchmark]
    public float[] OldReadAllGrowth()
    {
        _readAllSource.Reset();
        return OldReadAll(_readAllSource, _pool);
    }

    // The shipping read at 16 kHz: requests capped by RequestLimit (4,096 samples at a multiple of 16 kHz), the scratch
    // grown by doubling as before. Same source and non-pooling pool as OldReadAllGrowth, so the two arms differ only in
    // the request bound. perf-051 measured its estimated first rent here, which was dropped when it was reconciled with
    // perf/memory-optimizer: after that merge this arm's call bound the requests to the capture length instead.
    [Benchmark]
    public float[] BoundedRequestReadAll()
    {
        _readAllSource.Reset();
        return AudioCaptureService.ReadAll(_readAllSource, _pool, AudioCaptureService.RequestLimit(_readAllSource.WaveFormat));
    }

    [Benchmark]
    public float OldScalarPeak() =>
        ScalarPeak(MemoryMarshal.Cast<byte, float>(_stereoFloatRaw));

    [Benchmark]
    public float OptimizedComputePeak() =>
        AudioCaptureService.ComputePeak(_stereoFloatRaw, _stereoFloatFormat);

    [Benchmark]
    public float VectorPeakTrial() =>
        VectorPeak(MemoryMarshal.Cast<byte, float>(_stereoFloatRaw));

    [Benchmark]
    public IReadOnlyList<(int Start, int Length)> CurrentChunkPlan() =>
        TranscriptionChunker.Plan(_samples16k, 16_000);

    private static byte[] BuildInterleavedFloatRaw(int seconds, int channels)
    {
        var samples = BuildSamples(seconds, 48_000 * channels);
        for (var index = 0; index < samples.Length; index += channels)
        {
            var frame = index / channels;
            var value = MathF.Sin(2 * MathF.PI * 220 * frame / 48_000f) * 0.45f;
            for (var channel = 0; channel < channels; channel++)
            {
                samples[index + channel] = channel == 0 ? value : value * 0.9f;
            }
        }

        return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }

    private static float[] BuildSamples(int seconds, int sampleRate)
    {
        var samples = new float[seconds * sampleRate];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = MathF.Sin(2 * MathF.PI * 220 * index / sampleRate) * 0.45f;
        }

        return samples;
    }

    private static CaptureSignalReport OldAnalyzeFloat(ReadOnlySpan<float> samples, int channels, int sampleRate)
    {
        if (samples.Length < channels)
        {
            return new CaptureSignalReport(channels, sampleRate, 0, 0, 0, 0, 0, []);
        }

        var peaks = new float[channels];
        var sumSquares = new double[channels];
        var counts = new long[channels];
        double sum = 0;
        long clipped = 0;
        long nearSilent = 0;

        for (var i = 0; i < samples.Length; i++)
        {
            var value = samples[i];
            var channel = i % channels;
            var magnitude = Math.Abs(value);

            if (magnitude > peaks[channel]) peaks[channel] = magnitude;
            sumSquares[channel] += value * (double)value;
            counts[channel]++;
            sum += value;

            if (magnitude >= CaptureSignalAnalyzer.ClipThreshold) clipped++;
            if (magnitude < CaptureSignalAnalyzer.NearSilenceThreshold) nearSilent++;
        }

        var perChannel = new List<ChannelLevel>(channels);
        for (var c = 0; c < channels; c++)
        {
            var rms = counts[c] == 0 ? 0f : (float)Math.Sqrt(sumSquares[c] / counts[c]);
            perChannel.Add(new ChannelLevel(c, peaks[c], rms));
        }

        var totalSquares = sumSquares.Sum();
        var overallRms = (float)Math.Sqrt(totalSquares / samples.Length);

        return new CaptureSignalReport(
            channels,
            sampleRate,
            peaks.Max(),
            overallRms,
            clipped / (double)samples.Length,
            nearSilent / (double)samples.Length,
            (float)(sum / samples.Length),
            perChannel);
    }

    private static float[] OldReadAll(ISampleProvider provider, ArrayPool<float> pool)
    {
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

    private static float VectorPeak(ReadOnlySpan<float> samples)
    {
        if (!Vector.IsHardwareAccelerated || samples.Length < Vector<float>.Count)
        {
            return ScalarPeak(samples);
        }

        var vectorPeak = Vector<float>.Zero;
        var index = 0;
        var lastVectorStart = samples.Length - Vector<float>.Count;
        for (; index <= lastVectorStart; index += Vector<float>.Count)
        {
            vectorPeak = Vector.Max(vectorPeak, Vector.Abs(new Vector<float>(samples.Slice(index, Vector<float>.Count))));
        }

        var peak = 0f;
        for (var lane = 0; lane < Vector<float>.Count; lane++)
        {
            if (vectorPeak[lane] > peak)
            {
                peak = vectorPeak[lane];
            }
        }

        for (; index < samples.Length; index++)
        {
            var magnitude = Math.Abs(samples[index]);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return Math.Clamp(peak, 0f, 1f);
    }

    private static float ScalarPeak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            var magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return Math.Clamp(peak, 0f, 1f);
    }

    private sealed class ArraySampleProvider(float[] samples) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(16_000, 1);

        public int Read(Span<float> buffer)
        {
            var available = Math.Min(buffer.Length, samples.Length - _position);
            samples.AsSpan(_position, available).CopyTo(buffer);
            _position += available;
            return available;
        }

        public void Reset() => _position = 0;
    }

    private sealed class NonPoolingFloatPool : ArrayPool<float>
    {
        public override float[] Rent(int minimumLength) => new float[minimumLength];

        public override void Return(float[] array, bool clearArray = false)
        {
        }
    }
}
