using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using NAudio.Wave;
using Scribe.Core.Audio;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Pins what lets the capture conversion bound its reads: at a device rate that is a multiple of 16 kHz, NAudio's WDL
/// resampler gives the same samples however its output is asked for, so bounded requests change nothing; at every other
/// rate the conversion asks exactly as it always did. A NAudio version whose resampler breaks the first rule fails here,
/// rather than in somebody's transcript.
/// </summary>
public sealed class AudioCaptureServiceReadRequestTests
{
    public static TheoryData<int, int> IntegerRatioFormats() => new()
    {
        { 16_000, 1 },
        { 16_000, 2 },
        { 32_000, 1 },
        { 32_000, 2 },
        { 48_000, 1 },
        { 48_000, 2 },
        { 96_000, 1 },
        { 96_000, 2 },
    };

    [Theory]
    [MemberData(nameof(IntegerRatioFormats))]
    public void Bounded_requests_give_the_same_samples_when_the_ratio_is_an_integer(int rate, int channels)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        var ratio = rate / 16_000;
        int[] frameCounts =
        [
            0, 1, 2, 3, 5, 47, 95, 96, 97, 1_000,
            (AudioCaptureService.ResampleRequestSamples * ratio) - 1,
            (AudioCaptureService.ResampleRequestSamples * ratio) + 1,
            (16_384 * ratio) + 7,
            (rate * 2) + 13,
        ];

        foreach (var frames in frameCounts)
        {
            var raw = Signal(format, frames, seed: frames + rate);
            var expected = AudioCaptureService.ReadAll(AudioCaptureService.CreateConversionChain(raw, raw.Length, format));
            foreach (var limit in new[] { 1, 3, 64, AudioCaptureService.ResampleRequestSamples })
            {
                var bounded = AudioCaptureService.ReadAll(
                    AudioCaptureService.CreateConversionChain(raw, raw.Length, format), ArrayPool<float>.Shared, limit);

                Assert.True(
                    SameBits(expected, bounded),
                    $"{rate} Hz {channels} ch, {frames} frames, requests of at most {limit}: " +
                    $"{bounded.Length} samples against {expected.Length}, or different values.");
            }
        }
    }

    [Theory]
    [InlineData(16_000)]
    [InlineData(32_000)]
    [InlineData(48_000)]
    [InlineData(96_000)]
    [InlineData(192_000)]
    public void Reads_are_bounded_at_rates_that_are_a_multiple_of_16_kHz(int rate)
    {
        Assert.Equal(AudioCaptureService.ResampleRequestSamples, AudioCaptureService.RequestLimit(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)));
        Assert.Equal(AudioCaptureService.ResampleRequestSamples, AudioCaptureService.RequestLimit(new WaveFormat(rate, 16, 1)));
    }

    [Theory]
    [InlineData(8_000)]
    [InlineData(11_025)]
    [InlineData(22_050)]
    [InlineData(24_000)]
    [InlineData(44_100)]
    [InlineData(88_200)]
    public void Reads_keep_their_old_size_at_every_other_rate(int rate)
    {
        Assert.Equal(int.MaxValue, AudioCaptureService.RequestLimit(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)));
        Assert.Equal(int.MaxValue, AudioCaptureService.RequestLimit(new WaveFormat(rate, 16, 1)));
    }

    [Fact]
    public void An_unbounded_read_asks_for_the_rest_of_its_scratch_exactly_as_before()
    {
        // 70,000 samples through the doubling scratch: 16,384, then 32,768, 65,536 and 131,072 samples of room.
        var source = new RecordingSource(70_000);

        var output = AudioCaptureService.ReadAll(source);

        Assert.Equal(70_000, output.Length);
        Assert.Equal(new[] { 16_384, 16_384, 32_768, 65_536, 61_072 }, source.Requests);
    }

    [Fact]
    public void A_bounded_read_never_asks_for_more_than_its_limit_and_returns_everything()
    {
        var source = new RecordingSource(70_000);

        var output = AudioCaptureService.ReadAll(source, ArrayPool<float>.Shared, AudioCaptureService.ResampleRequestSamples);

        Assert.Equal(70_000, output.Length);
        Assert.All(source.Requests, request => Assert.InRange(request, 1, AudioCaptureService.ResampleRequestSamples));
        for (var i = 0; i < output.Length; i++)
        {
            Assert.Equal(RecordingSource.Value(i), output[i]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_limit_below_one_sample_is_refused(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AudioCaptureService.ReadAll(new RecordingSource(10), ArrayPool<float>.Shared, limit));
    }

    // In the collection that runs alone, so nothing else allocates on the measuring thread.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Fact]
        public void A_long_48_kHz_conversion_allocates_little_beyond_its_result()
        {
            // 30 s of 48 kHz stereo float, the default device format. Before its reads were bounded the resampler resized
            // its input buffer to about three times each request and zero padded twice the last one: about 11 MB here on
            // top of the result. The pool keeps its arrays, so the scratch is warm and exactly as long as the shared pool's.
            var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
            var raw = Signal(format, frames: 48_000 * 30, seed: 4);
            var pool = new KeepingPool();
            _ = Convert(raw, format, pool);

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = Convert(raw, format, pool);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            Assert.Equal(16_000 * 30, result.Length);

            // The owned result, then about 400 KB measured: one block each for the converter, the downmix and the
            // resampler's input, the resampler's end-of-stream padding for its last small request, and the chain itself.
            var bound = (result.Length * (long)sizeof(float)) + (1024 * 1024);
            Assert.True(allocated <= bound, $"The conversion allocated {allocated} bytes, more than {bound}. During it: {during}.");
        }

        private static float[] Convert(byte[] raw, WaveFormat format, ArrayPool<float> pool) =>
            AudioCaptureService.ReadAll(
                AudioCaptureService.CreateConversionChain(raw, raw.Length, format), pool, AudioCaptureService.RequestLimit(format));
    }

    private static bool SameBits(float[] expected, float[] actual) =>
        expected.Length == actual.Length
        && MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan()));

    // A tone with noise and a few samples on both rails, as 32-bit float frames.
    private static byte[] Signal(WaveFormat format, int frames, int seed)
    {
        var random = new Random(seed);
        var samples = new float[frames * format.Channels];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / format.Channels / (double)format.SampleRate;
            samples[i] = i % 1_009 == 0
                ? (i % 2 == 0 ? 1f : -1f)
                : (float)((0.3 * Math.Sin(2 * Math.PI * 220 * t)) + (0.2 * (random.NextDouble() - 0.5)));
        }

        return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }

    /// <summary>A 16 kHz mono source of known samples that records how much each read asked for.</summary>
    private sealed class RecordingSource(int totalSamples) : ISampleProvider
    {
        private int _position;

        public List<int> Requests { get; } = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(16_000, 1);

        public static float Value(int index) => ((index % 997) - 498) / 1_000f;

        public int Read(Span<float> buffer)
        {
            Requests.Add(buffer.Length);
            var count = Math.Min(buffer.Length, totalSamples - _position);
            for (var i = 0; i < count; i++)
            {
                buffer[i] = Value(_position + i);
            }

            _position += count;
            return count;
        }
    }

    /// <summary>
    /// Keeps every array it is given back, by length, and hands out the lengths the shared pool does (the next power of
    /// two, at least 16), so a second conversion rents only what the first returned.
    /// </summary>
    private sealed class KeepingPool : ArrayPool<float>
    {
        private readonly Dictionary<int, Stack<float[]>> _kept = [];

        public override float[] Rent(int minimumLength)
        {
            var length = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(minimumLength, 16));
            return _kept.TryGetValue(length, out var stack) && stack.TryPop(out var array) ? array : new float[length];
        }

        public override void Return(float[] array, bool clearArray = false)
        {
            if (!_kept.TryGetValue(array.Length, out var stack))
            {
                stack = new Stack<float[]>();
                _kept[array.Length] = stack;
            }

            stack.Push(array);
        }
    }
}
