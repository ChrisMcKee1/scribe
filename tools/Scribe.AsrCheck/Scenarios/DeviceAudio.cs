using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NAudio.Wave;

namespace Scribe.AsrCheck.Scenarios;

internal enum SampleEncoding
{
    Pcm16,
    Float32,
}

/// <summary>
/// A capture format as a Windows audio endpoint presents it. <see cref="Extensible"/> formats carry
/// the WAVE_FORMAT_EXTENSIBLE header WASAPI reports for its shared-mode mix format, so the production
/// <c>NormalizeFormat</c> unwrapping is exercised exactly as it is on a live capture.
/// </summary>
internal sealed record DeviceFormat(string Label, int SampleRate, int Channels, SampleEncoding Encoding, bool Extensible)
{
    /// <summary>What the dictation pipeline itself produces: 16 kHz, mono, 16-bit, plain PCM header.</summary>
    public static DeviceFormat Canonical { get; } = new("16 kHz mono 16-bit PCM", 16_000, 1, SampleEncoding.Pcm16, false);

    public int BitsPerSample => Encoding == SampleEncoding.Pcm16 ? 16 : 32;

    public WaveFormat ToWaveFormat()
    {
        if (!Extensible)
        {
            return Encoding == SampleEncoding.Float32
                ? WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels)
                : new WaveFormat(SampleRate, 16, Channels);
        }

        return new WaveFormatExtensible(
            SampleRate,
            BitsPerSample,
            Channels,
            useIeeeFloat: Encoding == SampleEncoding.Float32,
            validBitsPerSample: BitsPerSample,
            channelMask: ChannelMask(Channels));
    }

    // KSAUDIO_SPEAKER_MONO, _STEREO and _5POINT1: the masks Windows reports for those endpoint shapes.
    private static int ChannelMask(int channels) => channels switch
    {
        1 => 0x4,
        2 => 0x3,
        6 => 0x3F,
        _ => 0,
    };
}

/// <summary>Encodes synthesized channels into device bytes and moves them through WAV files.</summary>
internal static class DeviceAudio
{
    /// <summary>Interleaves per-channel samples into the byte layout the device format describes.</summary>
    public static byte[] Interleave(IReadOnlyList<float[]> channels, SampleEncoding encoding)
    {
        ArgumentOutOfRangeException.ThrowIfZero(channels.Count);
        var frames = channels.Max(c => c.Length);
        var bytesPerSample = encoding == SampleEncoding.Pcm16 ? 2 : 4;
        var data = new byte[checked(frames * channels.Count * bytesPerSample)];
        var span = data.AsSpan();
        var offset = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            foreach (var channel in channels)
            {
                var sample = frame < channel.Length ? channel[frame] : 0f;
                if (encoding == SampleEncoding.Pcm16)
                {
                    // Scaled by 32768 to mirror NAudio's decode (sample / 32768f), then saturated the
                    // way an analogue-to-digital converter saturates.
                    var value = (short)Math.Clamp((int)Math.Round(sample * 32768.0), short.MinValue, short.MaxValue);
                    BinaryPrimitives.WriteInt16LittleEndian(span[offset..], value);
                }
                else
                {
                    BinaryPrimitives.WriteSingleLittleEndian(span[offset..], sample);
                }

                offset += bytesPerSample;
            }
        }

        return data;
    }

    public static void WriteWav(string path, ReadOnlySpan<byte> data, WaveFormat format)
    {
        using var writer = new WaveFileWriter(path, format);
        writer.Write(data);
    }

    /// <summary>
    /// Reads a WAV back as a capture would see it: the raw data chunk, and its declared format
    /// presented the way a Windows endpoint presents it.
    /// </summary>
    public static (byte[] Data, WaveFormat Format) ReadWav(string path)
    {
        using var reader = new WaveFileReader(path);
        var data = new byte[checked((int)reader.Length)];
        var total = 0;
        while (total < data.Length)
        {
            var read = reader.Read(data.AsSpan(total));
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return (total == data.Length ? data : data[..total], AsEndpointFormat(reader.WaveFormat));
    }

    /// <summary>
    /// WaveFileReader parses an extensible fmt chunk into a <see cref="WaveFormatExtraData"/>, but
    /// WASAPI's mix format reaches the capture service through <see cref="WaveFormat.MarshalFromPtr"/>
    /// (NAudio's <c>AudioClient.MixFormat</c>), which yields a <see cref="WaveFormatExtensible"/>.
    /// The capture path unwraps only the latter, and an unwrapped header blinds the peak meter, the
    /// signal analyzer and silence auto-stop. So the header is laid out as the native WAVEFORMATEX it
    /// came from and marshalled exactly the way the endpoint's format is.
    /// </summary>
    internal static WaveFormat AsEndpointFormat(WaveFormat format)
    {
        var extra = format is WaveFormatExtraData withExtra
            ? withExtra.ExtraData.AsSpan(0, Math.Min(format.ExtraSize, withExtra.ExtraData.Length))
            : [];

        // Zero-padded well past the largest structure MarshalFromPtr can read (WAVEFORMATEX plus
        // NAudio's 100-byte extra-data block), so no branch of it reads beyond the allocation.
        var native = new byte[256];
        var span = native.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)format.Encoding);
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)format.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], format.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], format.AverageBytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)format.BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)format.BitsPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], (ushort)extra.Length);
        extra.CopyTo(span[18..]);

        var pointer = Marshal.AllocHGlobal(native.Length);
        try
        {
            Marshal.Copy(native, 0, pointer, native.Length);
            return WaveFormat.MarshalFromPtr(pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
