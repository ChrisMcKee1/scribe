using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>
/// Pins the stored audio formats. New audio is 16-bit PCM behind a 4-byte header, about half the
/// size of the float32 samples earlier builds wrote, and both must keep decoding: a v7 database is
/// full of float32 blobs. The header lets a PCM16 blob identify itself when its column is lost.
/// </summary>
public class AudioBlobCodecTests
{
    // Half of one quantization step of the symmetric 32767 scale, plus float rounding slack.
    private const float MaxPcm16Error = (0.5f / 32767f) + 1e-7f;

    [Fact]
    public void Pcm16_round_trip_stays_within_half_a_quantization_step()
    {
        var random = new Random(20260922);
        var samples = new float[48_000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)((random.NextDouble() * 2) - 1);
        }

        var decoded = AudioBlobCodec.DecodePcm16(AudioBlobCodec.EncodePcm16(samples));

        Assert.Equal(samples.Length, decoded.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            Assert.InRange(MathF.Abs(decoded[i] - samples[i]), 0f, MaxPcm16Error);
        }
    }

    [Fact]
    public void Pcm16_is_exact_at_the_rails_and_at_silence_and_symmetric_between_polarities()
    {
        Assert.Equal((short)32767, AudioBlobCodec.ToPcm16(1f));
        Assert.Equal((short)-32767, AudioBlobCodec.ToPcm16(-1f));
        Assert.Equal((short)0, AudioBlobCodec.ToPcm16(0f));

        foreach (var sample in new[] { 0.25f, 0.5f, 0.123456f, 0.999f, 1e-6f, 0.5f / 32767f })
        {
            Assert.Equal(-AudioBlobCodec.ToPcm16(sample), AudioBlobCodec.ToPcm16(-sample));
        }

        var decoded = AudioBlobCodec.DecodePcm16(AudioBlobCodec.EncodePcm16([1f, -1f, 0f]));
        Assert.Equal(new[] { 1f, -1f, 0f }, decoded);
    }

    [Fact]
    public void Pcm16_clamps_out_of_range_samples_and_stores_nan_as_silence()
    {
        var decoded = AudioBlobCodec.DecodePcm16(AudioBlobCodec.EncodePcm16(
            [1.5f, -2f, float.PositiveInfinity, float.NegativeInfinity, float.NaN]));

        Assert.Equal(new[] { 1f, -1f, 1f, -1f, 0f }, decoded);
    }

    [Fact]
    public void Pcm16_opens_with_its_header_then_little_endian_samples_at_about_half_the_float32_size()
    {
        var samples = new[] { 1f, -1f, 0.5f };

        var pcm = AudioBlobCodec.EncodePcm16(samples);
        var float32 = AudioBlobCodec.EncodeFloat32(samples);

        Assert.Equal(AudioBlobCodec.Pcm16HeaderLength + (samples.Length * 2), pcm.Length);
        Assert.Equal(AudioBlobCodec.EncodedLength(samples.Length, AudioBlobEncoding.Pcm16), pcm.Length);
        Assert.Equal(AudioBlobCodec.EncodedLength(samples.Length, AudioBlobEncoding.Float32), float32.Length);
        Assert.Equal(AudioBlobCodec.Pcm16Magic.ToArray(), pcm[..4]);
        Assert.Equal(new byte[] { 0xFF, 0x7F }, pcm[4..6]);   // +32767
        Assert.Equal(new byte[] { 0x01, 0x80 }, pcm[6..8]);   // -32767
    }

    [Fact]
    public void The_header_read_as_float32_is_a_signaling_nan_so_no_float32_blob_starts_with_it()
    {
        var bits = BinaryPrimitives.ReadUInt32LittleEndian(AudioBlobCodec.Pcm16Magic);

        Assert.Equal(0xFFu, (bits >> 23) & 0xFF);   // exponent all ones
        Assert.NotEqual(0u, bits & 0x7FFFFF);       // non-zero payload: a NaN, not an infinity
        Assert.Equal(0u, (bits >> 22) & 1);         // quiet bit clear: signaling
        Assert.True(float.IsNaN(BitConverter.Int32BitsToSingle((int)bits)));

        // Captures are finite, and a NaN that arithmetic produces is quiet, so neither can match.
        var zero = 0f;
        foreach (var first in new[] { 0f, 1f, -1f, float.Epsilon, zero / zero, float.NaN, float.PositiveInfinity })
        {
            Assert.False(AudioBlobCodec.HasPcm16Magic(AudioBlobCodec.EncodeFloat32([first, 0.5f])));
        }
    }

    [Theory]
    [InlineData(0)] // float32, as after an older build's repair dropped the column
    [InlineData(1)] // pcm16
    [InlineData(7)] // unknown
    public void A_pcm16_blob_decodes_from_its_header_whatever_the_column_says(int column)
    {
        var samples = new[] { 0.5f, -0.25f, 0.125f, 1f, -1f };

        var decoded = AudioBlobCodec.Decode(AudioBlobCodec.EncodePcm16(samples), (AudioBlobEncoding)column);

        Assert.NotNull(decoded);
        Assert.Equal(samples.Length, decoded.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            Assert.InRange(MathF.Abs(decoded[i] - samples[i]), 0f, MaxPcm16Error);
        }
    }

    [Fact]
    public void Headerless_pcm16_from_the_first_builds_of_the_format_still_decodes_by_its_column()
    {
        Assert.Equal(new[] { 1f, -1f }, AudioBlobCodec.Decode([0xFF, 0x7F, 0x01, 0x80], AudioBlobEncoding.Pcm16));
    }

    [Fact]
    public void Float32_blobs_written_by_earlier_builds_decode_bit_for_bit()
    {
        var samples = new[] { -1f, -0.333333f, 0f, 1e-9f, 0.75f, 1f };
        var legacy = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();

        var decoded = AudioBlobCodec.Decode(legacy, AudioBlobEncoding.Float32);

        Assert.Equal(samples, decoded);
    }

    [Fact]
    public void Decoding_rejects_an_encoding_this_build_does_not_know()
    {
        Assert.Null(AudioBlobCodec.Decode([0, 0, 0, 0], (AudioBlobEncoding)7));
    }

    [Fact]
    public void Decoding_ignores_a_trailing_partial_sample()
    {
        Assert.Single(AudioBlobCodec.DecodePcm16([0xFF, 0x7F, 0x12]));
        Assert.Single(AudioBlobCodec.DecodeFloat32([0, 0, 128, 63, 1, 2]));
    }

    [Fact]
    public void Decoding_keeps_a_foreign_minus_32768_inside_the_capture_range()
    {
        Assert.Equal(new[] { -1f }, AudioBlobCodec.DecodePcm16([0x00, 0x80]));
    }
}
