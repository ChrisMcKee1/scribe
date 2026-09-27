using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Scribe.Core.Audio;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Pins that the capture conversion reads its source in bounded blocks without changing a single sample. The converted
/// audio is compared bit for bit with the conversion as 0.5.0 built it, where every read of the NAudio converter and of the
/// downmix was as large as the request, and those two buffers grew with it through the large object heap.
/// </summary>
public sealed class MonoDownmixSampleProviderBoundedReadTests
{
    private static readonly (string Encoding, int Rate, int Channels)[] DeviceFormats =
    [
        ("float", 48_000, 2),
        ("float", 48_000, 1),
        ("float", 44_100, 2),
        ("float", 44_100, 1),
        ("float", 16_000, 1),
        ("float", 16_000, 2),
        ("float", 8_000, 1),
        ("float", 22_050, 2),
        ("float", 96_000, 6),
        ("float", 48_000, 3),
        ("pcm16", 48_000, 2),
        ("pcm16", 44_100, 1),
        ("pcm16", 16_000, 1),
        ("pcm24", 48_000, 2),
        ("pcm32", 96_000, 2),
    ];

    public static TheoryData<string, int, int, int> Conversions()
    {
        var data = new TheoryData<string, int, int, int>();
        foreach (var (encoding, rate, channels) in DeviceFormats)
        {
            foreach (var frames in new[] { 0, 1, 997, MonoDownmixSampleProvider.MaxSamplesPerRead + 1, (rate * 23 / 10) + 11 })
            {
                data.Add(encoding, rate, channels, frames);
            }
        }

        // Long enough for the conversion's requests to reach hundreds of thousands of samples.
        data.Add("float", 48_000, 2, (48_000 * 21) + 5);
        data.Add("float", 44_100, 2, (44_100 * 21) + 5);
        data.Add("pcm16", 48_000, 1, (48_000 * 21) + 5);
        return data;
    }

    [Theory]
    [MemberData(nameof(Conversions))]
    public void The_conversion_matches_the_unbounded_conversion_bit_for_bit(string encoding, int rate, int channels, int frames)
    {
        var format = Format(encoding, rate, channels);
        var raw = Signal(format, frames, seed: rate + channels + frames);

        var expected = UnboundedConversion(raw, format);
        var actual = AudioCaptureService.ResampleToTarget(raw, raw.Length, format);

        Assert.Equal(expected.Length, actual.Length);
        Assert.True(
            MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())),
            $"{encoding} {rate} Hz {channels} ch, {frames} frames: the samples differ from the unbounded conversion.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    public void No_read_of_the_source_is_larger_than_the_bound_however_much_is_asked_for(int channels)
    {
        const int frames = 100_000;
        var source = new CountingSource(channels, frames * channels);
        var downmix = new MonoDownmixSampleProvider(source);
        var output = new float[1_000_000];

        var read = downmix.Read(output);

        Assert.Equal(frames, read);
        Assert.All(source.Requests, request => Assert.InRange(request, 1, MonoDownmixSampleProvider.MaxSamplesPerRead));
        Assert.All(source.Requests, request => Assert.Equal(0, request % channels));
        for (var frame = 0; frame < frames; frame++)
        {
            Assert.Equal(ExpectedFrame(frame, channels), output[frame]);
        }
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 5)]
    public void A_short_read_ends_the_read_as_the_single_call_it_replaces_did(int channels, int framesPerShortRead)
    {
        var source = new CountingSource(channels, 1_000 * channels, maxPerRead: 10);
        var downmix = new MonoDownmixSampleProvider(source);
        var output = new float[1_000];

        Assert.Equal(framesPerShortRead, downmix.Read(output));
        Assert.Single(source.Requests);

        Assert.Equal(framesPerShortRead, downmix.Read(output.AsSpan(framesPerShortRead)));
        Assert.Equal(ExpectedFrame(framesPerShortRead, channels), output[framesPerShortRead]);
    }

    [Fact]
    public void A_partial_last_frame_is_dropped_and_the_next_read_returns_nothing()
    {
        var source = new CountingSource(channels: 3, totalSamples: (3 * 100) + 2);
        var downmix = new MonoDownmixSampleProvider(source);
        var output = new float[500];

        Assert.Equal(100, downmix.Read(output));
        Assert.Equal(0, downmix.Read(output));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void An_empty_read_returns_nothing(int channels)
    {
        var downmix = new MonoDownmixSampleProvider(new CountingSource(channels, 100 * channels));

        Assert.Equal(0, downmix.Read(Span<float>.Empty));
    }

    // In the collection that runs alone, so nothing else allocates on the measuring thread.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Fact]
        public void A_long_multichannel_read_allocates_one_block_for_each_buffer()
        {
            // 30 s of 16 kHz stereo float in one read, as large as the conversion's largest requests. Before the bound the
            // downmix buffer and the converter's byte buffer were each as large as the read: 3.84 MB apiece here.
            var format = WaveFormat.CreateIeeeFloatWaveFormat(16_000, 2);
            Warm(format);
            var raw = Signal(format, frames: 16_000 * 30, seed: 1);
            var downmix = new MonoDownmixSampleProvider(new RawSourceWaveStream(raw, 0, raw.Length, format).ToSampleProvider());
            var output = new float[16_000 * 30];

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var read = downmix.Read(output);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            Assert.Equal(16_000 * 30, read);

            // The downmix's float block and the converter's byte block, each at most one block plus an array header.
            var bound = 2 * ((MonoDownmixSampleProvider.MaxSamplesPerRead * sizeof(float)) + 256);
            Assert.True(allocated <= bound, $"The read allocated {allocated} bytes, more than {bound}. During it: {during}.");
        }

        [Fact]
        public void A_long_mono_read_allocates_one_converter_block()
        {
            // 30 s of 48 kHz mono 16-bit PCM in one read: only the converter has a buffer, one block of bytes.
            var format = new WaveFormat(48_000, 16, 1);
            Warm(format);
            var raw = Signal(format, frames: 48_000 * 30, seed: 2);
            var mono = new MonoDownmixSampleProvider(new RawSourceWaveStream(raw, 0, raw.Length, format).ToSampleProvider());
            var output = new float[48_000 * 30];

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var read = mono.Read(output);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            Assert.Equal(48_000 * 30, read);
            var bound = (MonoDownmixSampleProvider.MaxSamplesPerRead * sizeof(float)) + 256;
            Assert.True(allocated <= bound, $"The read allocated {allocated} bytes, more than {bound}. During it: {during}.");
        }

        // The same calls once, on this thread, so the measured read is not also the first call.
        private static void Warm(WaveFormat format)
        {
            var raw = Signal(format, frames: 40_000, seed: 3);
            var provider = new MonoDownmixSampleProvider(new RawSourceWaveStream(raw, 0, raw.Length, format).ToSampleProvider());
            _ = provider.Read(new float[40_000]);
        }
    }

    // The conversion as 0.5.0 built it, where a read of the converter or the downmix was as large as the request.
    private static float[] UnboundedConversion(byte[] raw, WaveFormat format)
    {
        ISampleProvider source = new RawSourceWaveStream(raw, 0, raw.Length, format).ToSampleProvider();
        ISampleProvider mono = format.Channels == 1 ? source : new UnboundedDownmix(source);
        ISampleProvider resampled = mono.WaveFormat.SampleRate == 16_000
            ? mono
            : new WdlResamplingSampleProvider(mono, 16_000);

        return AudioCaptureService.ReadAll(resampled);
    }

    private static float ExpectedFrame(int frame, int channels)
    {
        var sum = 0f;
        for (var channel = 0; channel < channels; channel++)
        {
            sum += CountingSource.Value((frame * channels) + channel);
        }

        return channels == 1 ? CountingSource.Value(frame) : sum / channels;
    }

    private static WaveFormat Format(string encoding, int rate, int channels) => encoding switch
    {
        "float" => WaveFormat.CreateIeeeFloatWaveFormat(rate, channels),
        "pcm16" => new WaveFormat(rate, 16, channels),
        "pcm24" => new WaveFormat(rate, 24, channels),
        "pcm32" => new WaveFormat(rate, 32, channels),
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
            var value = (0.3 * Math.Sin(2 * Math.PI * 220 * t)) + (0.2 * (random.NextDouble() - 0.5));
            if (i % 1_009 == 0)
            {
                value = i % 2 == 0 ? 1.0 : -1.0;
            }

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
                case 32:
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), (int)(value * int.MaxValue));
                    break;
            }
        }

        return bytes;
    }

    /// <summary>0.5.0's multi-channel downmix, verbatim: one read of the source as large as the request.</summary>
    private sealed class UnboundedDownmix : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _buffer = [];

        public UnboundedDownmix(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<float> buffer)
        {
            int needed = buffer.Length * _channels;
            if (_buffer.Length < needed)
            {
                _buffer = new float[needed];
            }

            int read = _source.Read(_buffer.AsSpan(0, needed));
            int frames = read / _channels;
            for (int frame = 0; frame < frames; frame++)
            {
                float sum = 0f;
                int baseIndex = frame * _channels;
                for (int channel = 0; channel < _channels; channel++)
                {
                    sum += _buffer[baseIndex + channel];
                }

                buffer[frame] = sum / _channels;
            }

            return frames;
        }
    }

    /// <summary>A float source of known samples that records what each read asked for, and can answer short.</summary>
    private sealed class CountingSource(int channels, int totalSamples, int maxPerRead = int.MaxValue) : ISampleProvider
    {
        private int _position;

        public List<int> Requests { get; } = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(16_000, channels);

        public static float Value(int index) => (((index * 7_919) % 2_001) - 1_000) / 1_000f;

        public int Read(Span<float> buffer)
        {
            Requests.Add(buffer.Length);
            var count = Math.Min(Math.Min(buffer.Length, maxPerRead), totalSamples - _position);
            for (var i = 0; i < count; i++)
            {
                buffer[i] = Value(_position + i);
            }

            _position += count;
            return count;
        }
    }
}
