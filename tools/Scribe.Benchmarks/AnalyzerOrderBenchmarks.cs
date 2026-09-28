using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using NAudio.Wave;
using Scribe.Core.Audio;

namespace Scribe.Benchmarks;

/// <summary>
/// The capture signal analyzer every stop runs before the conversion, on the shipping code
/// (<see cref="CaptureSignalAnalyzer.Analyze"/>) against the single loop 0.5.0 shipped for every channel count, kept
/// here verbatim (<c>Original</c>, the baseline). One and two channels, 32-bit float at 48 kHz, for the median dictation
/// (9.5 s) and a long one (55 s). The setup refuses to run unless both give the same report, bit for bit, on this input;
/// <c>CaptureSignalAnalyzerDifferentialTests</c> is the full proof, on x64 and on Arm64. The shipping analyzer is the
/// original loop again (AUDIO-O-06's faster loops were backed out when their RMS differed on Arm64), so both arms now
/// measure the same code; keep this class as the harness for the next attempt.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Audio")]
public class AnalyzerOrderBenchmarks
{
    private byte[] _raw = [];
    private WaveFormat _format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);

    [Params(1, 2)]
    public int Channels { get; set; }

    [Params(9.5, 55.0)]
    public double Seconds { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, Channels);
        var second = SyntheticCapture.OneSecondOfPackets(_format).SelectMany(packet => packet).ToArray();
        var total = (int)(second.Length * Seconds);
        total -= total % _format.BlockAlign;
        _raw = new byte[total];
        for (var offset = 0; offset < total; offset += second.Length)
        {
            second.AsSpan(0, Math.Min(second.Length, total - offset)).CopyTo(_raw.AsSpan(offset));
        }

        var original = OriginalCaptureSignalAnalyzer.Analyze(_raw, _format);
        var shipping = CaptureSignalAnalyzer.Analyze(_raw, _format);
        if (!SameReport(original, shipping))
        {
            throw new InvalidOperationException("The shipping analyzer does not report what the original reports on this input.");
        }
    }

    [Benchmark(Baseline = true)]
    public CaptureSignalReport Original() => OriginalCaptureSignalAnalyzer.Analyze(_raw, _format);

    [Benchmark]
    public CaptureSignalReport Shipping() => CaptureSignalAnalyzer.Analyze(_raw, _format);

    private static bool SameReport(CaptureSignalReport a, CaptureSignalReport b) =>
        a.Channels == b.Channels
        && a.SampleRate == b.SampleRate
        && BitConverter.SingleToInt32Bits(a.Peak) == BitConverter.SingleToInt32Bits(b.Peak)
        && BitConverter.SingleToInt32Bits(a.Rms) == BitConverter.SingleToInt32Bits(b.Rms)
        && BitConverter.SingleToInt32Bits(a.DcOffset) == BitConverter.SingleToInt32Bits(b.DcOffset)
        && BitConverter.DoubleToInt64Bits(a.ClippedFraction) == BitConverter.DoubleToInt64Bits(b.ClippedFraction)
        && BitConverter.DoubleToInt64Bits(a.NearSilentFraction) == BitConverter.DoubleToInt64Bits(b.NearSilentFraction)
        && a.PerChannel.Count == b.PerChannel.Count
        && a.PerChannel.Zip(b.PerChannel).All(pair =>
            BitConverter.SingleToInt32Bits(pair.First.Peak) == BitConverter.SingleToInt32Bits(pair.Second.Peak)
            && BitConverter.SingleToInt32Bits(pair.First.Rms) == BitConverter.SingleToInt32Bits(pair.Second.Rms))
        && string.Equals(a.Describe(), b.Describe(), StringComparison.Ordinal);

    /// <summary>The analyzer as 0.5.0 shipped it, one loop for every format and channel count.</summary>
    private static class OriginalCaptureSignalAnalyzer
    {
        private const float ClipThreshold = 0.999f;
        private const float NearSilenceThreshold = 0.001f;

        public static CaptureSignalReport Analyze(ReadOnlySpan<byte> raw, WaveFormat format)
        {
            ArgumentNullException.ThrowIfNull(format);

            var channels = Math.Max(1, format.Channels);
            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                return Analyze(MemoryMarshal.Cast<byte, float>(raw), channels, format.SampleRate, static f => f);
            }

            if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
            {
                return Analyze(MemoryMarshal.Cast<byte, short>(raw), channels, format.SampleRate, static s => s / 32768f);
            }

            return Empty(channels, format.SampleRate);
        }

        private static CaptureSignalReport Analyze<T>(ReadOnlySpan<T> samples, int channels, int sampleRate, Func<T, float> toFloat)
            where T : struct
        {
            if (samples.Length < channels)
            {
                return Empty(channels, sampleRate);
            }

            var peaks = new float[channels];
            var sumSquares = new double[channels];
            var counts = new long[channels];
            double sum = 0;
            long clipped = 0;
            long nearSilent = 0;

            for (var i = 0; i < samples.Length; i++)
            {
                var value = toFloat(samples[i]);
                var channel = i % channels;
                var magnitude = Math.Abs(value);

                if (magnitude > peaks[channel]) peaks[channel] = magnitude;
                sumSquares[channel] += value * (double)value;
                counts[channel]++;
                sum += value;

                if (magnitude >= ClipThreshold) clipped++;
                if (magnitude < NearSilenceThreshold) nearSilent++;
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

        private static CaptureSignalReport Empty(int channels, int sampleRate) =>
            new(channels, sampleRate, Peak: 0, Rms: 0, ClippedFraction: 0, NearSilentFraction: 0, DcOffset: 0, PerChannel: []);
    }
}
