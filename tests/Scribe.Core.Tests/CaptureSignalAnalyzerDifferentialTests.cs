using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;
using Scribe.Core.Audio;

namespace Scribe.Core.Tests;

/// <summary>
/// <see cref="CaptureSignalAnalyzer"/> has loops of its own for one and two channels. They must report exactly what the
/// original single loop reported, bit for bit, on every input: the original is kept here, verbatim from 0.5.0, as the
/// oracle, and every statistic is compared by its bit pattern (a float comparison would call NaN unequal to itself and
/// -0 equal to +0).
/// </summary>
public sealed class CaptureSignalAnalyzerDifferentialTests
{
    private static readonly int[] ChannelCounts = [0, 1, 2, 3, 5, 8];

    private static readonly int[] SampleCounts = [0, 1, 2, 3, 4, 5, 7, 8, 63, 64, 65, 511, 1000, 4801];

    [Fact]
    public void Every_float_and_pcm16_input_reports_exactly_what_the_original_loop_reports()
    {
        var cases = 0;
        foreach (var encoding in new[] { "float", "pcm16" })
        {
            foreach (var channels in ChannelCounts)
            {
                foreach (var sampleCount in SampleCounts)
                {
                    foreach (var content in new[] { "random", "special" })
                    {
                        var payload = Payload(encoding, sampleCount, content, seed: (channels * 131) + sampleCount);

                        // Unaligned starts and a torn final sample: MemoryMarshal.Cast reads whole samples from wherever the
                        // span begins and drops the leftover bytes, and both implementations must see the same samples.
                        foreach (var offset in new[] { 0, 1, 2, 3 })
                        {
                            foreach (var trailing in new[] { 0, 1, 2, 3 })
                            {
                                var buffer = new byte[offset + payload.Length + trailing];
                                payload.CopyTo(buffer.AsSpan(offset));
                                new Random(sampleCount + trailing).NextBytes(buffer.AsSpan(offset + payload.Length));
                                var span = buffer.AsSpan(offset);
                                var format = Format(encoding, 48_000, channels);

                                AssertIdentical(
                                    OriginalCaptureSignalAnalyzer.Analyze(span, format),
                                    CaptureSignalAnalyzer.Analyze(span, format),
                                    $"{encoding} {channels} ch {sampleCount} samples {content} offset {offset} trailing {trailing}");
                                cases++;
                            }
                        }
                    }
                }
            }
        }

        Assert.Equal(2 * ChannelCounts.Length * SampleCounts.Length * 2 * 4 * 4, cases);
    }

    [Theory]
    [InlineData("float", 1)]
    [InlineData("float", 2)]
    [InlineData("pcm16", 1)]
    [InlineData("pcm16", 2)]
    public void A_long_capture_reports_exactly_what_the_original_loop_reports(string encoding, int channels)
    {
        // 55 s at 48 kHz plus one frame, the length the benchmark measures: long enough for the double sums to carry
        // rounding that any change of order would move.
        var sampleCount = ((48_000 * 55) + 1) * channels;
        var payload = Payload(encoding, sampleCount, "random", seed: 55);
        var format = Format(encoding, 48_000, channels);

        AssertIdentical(
            OriginalCaptureSignalAnalyzer.Analyze(payload, format),
            CaptureSignalAnalyzer.Analyze(payload, format),
            $"{encoding} {channels} ch, 55 s");
    }

    [Theory]
    [InlineData(WaveFormatEncoding.Pcm, 24)]
    [InlineData(WaveFormatEncoding.Pcm, 32)]
    [InlineData(WaveFormatEncoding.Pcm, 8)]
    [InlineData(WaveFormatEncoding.IeeeFloat, 64)]
    public void Unsupported_formats_still_report_empty(WaveFormatEncoding encoding, int bits)
    {
        foreach (var channels in new[] { 1, 2 })
        {
            var format = WaveFormat.CreateCustomFormat(encoding, 48_000, channels, 48_000 * channels * bits / 8, channels * bits / 8, bits);
            var payload = Payload("float", 64, "random", seed: bits);

            AssertIdentical(
                OriginalCaptureSignalAnalyzer.Analyze(payload, format),
                CaptureSignalAnalyzer.Analyze(payload, format),
                $"{encoding} {bits}-bit {channels} ch");
        }
    }

    [Fact]
    public void A_null_format_is_still_refused()
    {
        Assert.Throws<ArgumentNullException>(() => CaptureSignalAnalyzer.Analyze([1, 2, 3, 4], null!));
    }

    private static WaveFormat Format(string encoding, int rate, int channels) =>
        encoding == "float"
            ? WaveFormat.CreateCustomFormat(WaveFormatEncoding.IeeeFloat, rate, channels, rate * Math.Max(1, channels) * 4, Math.Max(1, channels) * 4, 32)
            : WaveFormat.CreateCustomFormat(WaveFormatEncoding.Pcm, rate, channels, rate * Math.Max(1, channels) * 2, Math.Max(1, channels) * 2, 16);

    // Random samples, or a cycle of the values that decide a comparison: NaNs with different payloads, infinities, both
    // zeros, subnormals, the clip and near-silence thresholds and their neighbours, full scale and beyond.
    private static byte[] Payload(string encoding, int sampleCount, string content, int seed)
    {
        var random = new Random(seed);
        if (encoding == "float")
        {
            float[] specials =
            [
                float.NaN, -float.NaN, BitConverter.Int32BitsToSingle(0x7FC0_0001), float.PositiveInfinity, float.NegativeInfinity,
                0f, -0f, float.Epsilon, -float.Epsilon, 1e-40f, -1e-40f,
                CaptureSignalAnalyzer.ClipThreshold, MathF.BitDecrement(CaptureSignalAnalyzer.ClipThreshold),
                MathF.BitIncrement(CaptureSignalAnalyzer.ClipThreshold), -CaptureSignalAnalyzer.ClipThreshold,
                CaptureSignalAnalyzer.NearSilenceThreshold, MathF.BitDecrement(CaptureSignalAnalyzer.NearSilenceThreshold),
                -CaptureSignalAnalyzer.NearSilenceThreshold, 1f, -1f, 0.5f, float.MaxValue, float.MinValue, 3.5f,
            ];
            var floats = new float[sampleCount];
            for (var i = 0; i < floats.Length; i++)
            {
                floats[i] = content == "special" ? specials[(i + seed) % specials.Length] : (float)((random.NextDouble() * 2.4) - 1.2);
            }

            return MemoryMarshal.AsBytes(floats.AsSpan()).ToArray();
        }

        short[] specialShorts = [short.MinValue, short.MaxValue, 0, -1, 1, 32_735, 32_736, -32_735, 32, 33, -33, 16_384];
        var bytes = new byte[sampleCount * 2];
        for (var i = 0; i < sampleCount; i++)
        {
            var value = content == "special" ? specialShorts[(i + seed) % specialShorts.Length] : (short)random.Next(short.MinValue, short.MaxValue + 1);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), value);
        }

        return bytes;
    }

    private static void AssertIdentical(CaptureSignalReport expected, CaptureSignalReport actual, string label)
    {
        Assert.True(expected.Channels == actual.Channels, $"{label}: channels");
        Assert.True(expected.SampleRate == actual.SampleRate, $"{label}: sample rate");
        Assert.True(Bits(expected.Peak) == Bits(actual.Peak), $"{label}: peak");
        Assert.True(Bits(expected.Rms) == Bits(actual.Rms), $"{label}: rms");
        Assert.True(Bits(expected.DcOffset) == Bits(actual.DcOffset), $"{label}: dc offset");
        Assert.True(Bits(expected.ClippedFraction) == Bits(actual.ClippedFraction), $"{label}: clipped fraction");
        Assert.True(Bits(expected.NearSilentFraction) == Bits(actual.NearSilentFraction), $"{label}: near-silent fraction");
        Assert.True(expected.PerChannel.Count == actual.PerChannel.Count, $"{label}: channel count");
        for (var c = 0; c < expected.PerChannel.Count; c++)
        {
            Assert.True(expected.PerChannel[c].Channel == actual.PerChannel[c].Channel, $"{label}: channel {c} index");
            Assert.True(Bits(expected.PerChannel[c].Peak) == Bits(actual.PerChannel[c].Peak), $"{label}: channel {c} peak");
            Assert.True(Bits(expected.PerChannel[c].Rms) == Bits(actual.PerChannel[c].Rms), $"{label}: channel {c} rms");
        }

        Assert.Equal(expected.Describe(), actual.Describe());
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

    private static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);

    /// <summary>The analyzer as 0.5.0 shipped it (one loop for every format and channel count), kept as the oracle.</summary>
    private static class OriginalCaptureSignalAnalyzer
    {
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

        private static CaptureSignalReport Analyze<T>(
            ReadOnlySpan<T> samples,
            int channels,
            int sampleRate,
            Func<T, float> toFloat)
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

        private static CaptureSignalReport Empty(int channels, int sampleRate) => new(
            channels,
            sampleRate,
            Peak: 0,
            Rms: 0,
            ClippedFraction: 0,
            NearSilentFraction: 0,
            DcOffset: 0,
            PerChannel: []);
    }
}
