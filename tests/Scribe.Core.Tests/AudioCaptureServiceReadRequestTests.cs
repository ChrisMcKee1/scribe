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
    // Every positive multiple of 16 kHz is limited, so the rule is checked at every ratio a device rate can give it here
    // (1, 2, 3, 4, 6 and 12), for float and integer PCM and up to 6 channels, all through the production chain.
    public static TheoryData<string, int, int> IntegerRatioFormats() => new()
    {
        { "float", 16_000, 1 },
        { "float", 16_000, 2 },
        { "float", 32_000, 1 },
        { "float", 32_000, 2 },
        { "float", 48_000, 1 },
        { "float", 48_000, 2 },
        { "float", 48_000, 4 },
        { "float", 48_000, 6 },
        { "float", 64_000, 1 },
        { "float", 96_000, 1 },
        { "float", 96_000, 2 },
        { "float", 192_000, 1 },
        { "float", 192_000, 2 },
        { "pcm16", 16_000, 1 },
        { "pcm16", 48_000, 2 },
        { "pcm16", 192_000, 1 },
        { "pcm24", 48_000, 2 },
        { "pcm24", 96_000, 6 },
    };

    // Past the 21 s the conversion's other identity tests reach, where its unlimited requests grow to hundreds of
    // thousands of samples and the resampler's position into the millions. Few lengths, so the class stays quick.
    public static TheoryData<string, int, int, int> LongIntegerRatioCaptures() => new()
    {
        { "float", 48_000, 2, 48_000 * 30 },
        { "float", 192_000, 1, 192_000 * 25 },
        { "pcm16", 48_000, 6, (48_000 * 22) + 17 },
        { "pcm24", 96_000, 2, (96_000 * 23) + 5 },
    };

    [Theory]
    [MemberData(nameof(IntegerRatioFormats))]
    public void Bounded_requests_give_the_same_samples_when_the_ratio_is_an_integer(string encoding, int rate, int channels)
    {
        var format = Format(encoding, rate, channels);
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
            AssertBoundedRequestsGiveTheSameSamples(format, frames, [1, 3, 64, AudioCaptureService.ResampleRequestSamples]);
        }
    }

    [Theory]
    [MemberData(nameof(LongIntegerRatioCaptures))]
    public void Bounded_requests_give_the_same_samples_for_long_captures(string encoding, int rate, int channels, int frames)
    {
        AssertBoundedRequestsGiveTheSameSamples(
            Format(encoding, rate, channels), frames, [997, AudioCaptureService.ResampleRequestSamples]);
    }

    [Theory]
    [InlineData(16_000)]
    [InlineData(32_000)]
    [InlineData(48_000)]
    [InlineData(64_000)]
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

    // The unlimited conversion, then the same capture through the same chain with each limit: the samples must be identical.
    private static void AssertBoundedRequestsGiveTheSameSamples(WaveFormat format, int frames, int[] limits)
    {
        var raw = Signal(format, frames, seed: frames + format.SampleRate + format.Channels);
        var expected = AudioCaptureService.ReadAll(AudioCaptureService.CreateConversionChain(raw, raw.Length, format));
        foreach (var limit in limits)
        {
            var bounded = AudioCaptureService.ReadAll(
                AudioCaptureService.CreateConversionChain(raw, raw.Length, format), ArrayPool<float>.Shared, limit);

            Assert.True(
                SameBits(expected, bounded),
                $"{format}, {frames} frames, requests of at most {limit}: " +
                $"{bounded.Length} samples against {expected.Length}, or different values.");
        }
    }

    private static bool SameBits(float[] expected, float[] actual) =>
        expected.Length == actual.Length
        && MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan()));

    private static WaveFormat Format(string encoding, int rate, int channels) => encoding switch
    {
        "float" => WaveFormat.CreateIeeeFloatWaveFormat(rate, channels),
        "pcm16" => new WaveFormat(rate, 16, channels),
        "pcm24" => new WaveFormat(rate, 24, channels),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unknown test encoding."),
    };

    // A tone with noise and a few samples on both rails, in the device format, frame aligned as WASAPI delivers it.
    private static byte[] Signal(WaveFormat format, int frames, int seed)
    {
        var random = new Random(seed);
        var bytes = new byte[frames * format.BlockAlign];
        var samples = frames * format.Channels;
        for (var i = 0; i < samples; i++)
        {
            var t = i / format.Channels / (double)format.SampleRate;
            var value = i % 1_009 == 0
                ? (i % 2 == 0 ? 1.0 : -1.0)
                : (0.3 * Math.Sin(2 * Math.PI * 220 * t)) + (0.2 * (random.NextDouble() - 0.5));
            switch (format.BitsPerSample)
            {
                case 32 when format.Encoding == WaveFormatEncoding.IeeeFloat:
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), (float)value);
                    break;
                case 16:
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), (short)(value * short.MaxValue));
                    break;
                case 24:
                    var code = (int)(value * 8_388_607);
                    bytes[i * 3] = (byte)code;
                    bytes[(i * 3) + 1] = (byte)(code >> 8);
                    bytes[(i * 3) + 2] = (byte)(code >> 16);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format.BitsPerSample, "Unexpected test format.");
            }
        }

        return bytes;
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
