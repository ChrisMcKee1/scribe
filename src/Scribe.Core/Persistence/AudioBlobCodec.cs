using System.Buffers.Binary;

namespace Scribe.Core.Persistence;

/// <summary>
/// How the samples of one stored audio blob are laid out, persisted as <c>audio_blobs.encoding</c>.
/// </summary>
internal enum AudioBlobEncoding
{
    /// <summary>
    /// Raw 32-bit float samples in machine byte order. Every blob written by builds up to 0.4.2, and
    /// the value SQLite supplies for those rows through the column default.
    /// </summary>
    Float32 = 0,

    /// <summary>
    /// <see cref="AudioBlobCodec.Pcm16Magic"/>, then little-endian signed 16-bit PCM scaled by 32767:
    /// about half the size of <see cref="Float32"/>.
    /// </summary>
    Pcm16 = 1,
}

/// <summary>Converts capture samples to and from the formats stored in <c>audio_blobs</c>.</summary>
internal static class AudioBlobCodec
{
    /// <summary>Length of <see cref="Pcm16Magic"/>, which opens every PCM16 blob this build writes.</summary>
    internal const int Pcm16HeaderLength = 4;

    // Symmetric full scale: +1 and -1 map to +32767 and -32767, so a sample on either rail decodes
    // back to exactly the value that was stored and the two polarities quantize identically.
    // -32768 is never written.
    private const float Pcm16FullScale = 32767f;

    /// <summary>
    /// The bytes that open every PCM16 blob, so a blob identifies its own format even where the
    /// encoding column is gone: a repair by an older build rebuilds the table without it, and the
    /// column then comes back with the float32 default. Read as a little-endian float32 they are a
    /// signaling NaN (exponent all ones, quiet bit clear, non-zero payload), which no float32 blob
    /// can start with: capture samples are finite, and arithmetic only ever produces quiet NaNs.
    /// </summary>
    internal static ReadOnlySpan<byte> Pcm16Magic => [0x53, 0x31, 0xB6, 0x7F];

    /// <summary>Stored size of <paramref name="sampleCount"/> samples in <paramref name="encoding"/>, header included.</summary>
    internal static long EncodedLength(long sampleCount, AudioBlobEncoding encoding) =>
        encoding == AudioBlobEncoding.Pcm16
            ? Pcm16HeaderLength + (sampleCount * sizeof(short))
            : sampleCount * sizeof(float);

    internal static byte[] Encode(ReadOnlySpan<float> samples, AudioBlobEncoding encoding) =>
        encoding == AudioBlobEncoding.Pcm16 ? EncodePcm16(samples) : EncodeFloat32(samples);

    /// <summary>The PCM16 format this build writes: <see cref="Pcm16Magic"/>, then the samples.</summary>
    internal static byte[] EncodePcm16(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[checked(Pcm16HeaderLength + (samples.Length * sizeof(short)))];
        Pcm16Magic.CopyTo(bytes);
        var destination = bytes.AsSpan(Pcm16HeaderLength);
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(i * sizeof(short)), ToPcm16(samples[i]));
        }

        return bytes;
    }

    internal static short ToPcm16(float sample)
    {
        // NaN carries no level at all, and silence is the only code that cannot be heard as one.
        if (float.IsNaN(sample))
        {
            return 0;
        }

        var clamped = Math.Clamp(sample, -1f, 1f);
        return (short)MathF.Round(clamped * Pcm16FullScale, MidpointRounding.AwayFromZero);
    }

    internal static byte[] EncodeFloat32(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[checked(samples.Length * sizeof(float))];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples).CopyTo(bytes);
        return bytes;
    }

    internal static bool HasPcm16Magic(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Pcm16Magic);

    /// <summary>
    /// Decodes a stored blob. Its own header wins over <paramref name="columnEncoding"/>, so a PCM16
    /// blob whose column was lost still reads as PCM16. Without the header the column decides:
    /// float32 for every blob written up to 0.4.2, and PCM16 as the first builds of that format wrote
    /// it. Returns <see langword="null"/> for an encoding this build does not know (a damaged row, or
    /// a format a later build added to the shared database), because decoding it as either known
    /// format would hand back noise, so it reads as missing audio instead.
    /// </summary>
    internal static float[]? Decode(byte[] bytes, AudioBlobEncoding columnEncoding)
    {
        if (HasPcm16Magic(bytes))
        {
            return DecodePcm16Samples(bytes.AsSpan(Pcm16HeaderLength));
        }

        return columnEncoding switch
        {
            AudioBlobEncoding.Float32 => DecodeFloat32(bytes),
            AudioBlobEncoding.Pcm16 => DecodePcm16Samples(bytes),
            _ => null,
        };
    }

    /// <summary>Decodes PCM16 as <see cref="EncodePcm16"/> writes it, header or not.</summary>
    internal static float[] DecodePcm16(ReadOnlySpan<byte> bytes) =>
        DecodePcm16Samples(HasPcm16Magic(bytes) ? bytes[Pcm16HeaderLength..] : bytes);

    internal static float[] DecodeFloat32(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, floats.Length * sizeof(float));
        return floats;
    }

    private static float[] DecodePcm16Samples(ReadOnlySpan<byte> source)
    {
        var samples = new float[source.Length / sizeof(short)];
        for (var i = 0; i < samples.Length; i++)
        {
            var code = BinaryPrimitives.ReadInt16LittleEndian(source.Slice(i * sizeof(short)));

            // Clamped so a -32768 from some other writer still honors CapturedAudio's [-1, 1] range.
            samples[i] = Math.Max(-1f, code / Pcm16FullScale);
        }

        return samples;
    }
}