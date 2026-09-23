using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;
using Scribe.Core.Audio;

namespace Scribe.Core.Tests;

/// <summary>
/// Pins how NAudio moves a WASAPI mix format across the native boundary, which is the first thing
/// every capture does: <c>AudioClient.MixFormat</c> decodes GetMixFormat's block with
/// <see cref="WaveFormat.MarshalFromPtr"/> and <c>AudioClient.Initialize</c> hands the same format
/// back through <see cref="WaveFormat.MarshalToPtr"/>. NAudio 3.1 rewrites both by hand instead of
/// StructLayout marshalling, and a regression there would break every dictation before a single
/// sample arrived, so these hold the exact bytes across any NAudio bump. No other test reaches this
/// code without a real microphone.
/// </summary>
public sealed class NAudioInteropContractTests
{
    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT and KSDATAFORMAT_SUBTYPE_PCM (ksmedia.h).
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    [Theory]
    [InlineData(48_000, 2, 32, true)]
    [InlineData(44_100, 1, 32, true)]
    [InlineData(48_000, 2, 16, false)]
    [InlineData(96_000, 4, 24, false)]
    public void Extensible_mix_format_round_trips_through_native_marshalling(
        int sampleRate, int channels, int bits, bool isFloat)
    {
        var native = WaveFormatExtensibleBlock(sampleRate, channels, bits, isFloat ? IeeeFloatSubFormat : PcmSubFormat);
        var block = Marshal.AllocHGlobal(native.Length);
        try
        {
            Marshal.Copy(native, 0, block, native.Length);

            var extensible = Assert.IsType<WaveFormatExtensible>(WaveFormat.MarshalFromPtr(block));
            Assert.Equal(WaveFormatEncoding.Extensible, extensible.Encoding);
            Assert.Equal(sampleRate, extensible.SampleRate);
            Assert.Equal(channels, extensible.Channels);
            Assert.Equal(bits, extensible.BitsPerSample);
            Assert.Equal(22, extensible.ExtraSize);
            Assert.Equal(isFloat ? IeeeFloatSubFormat : PcmSubFormat, extensible.SubFormat);

            // What Scribe then reads the captured bytes as.
            var normalized = AudioCaptureService.NormalizeFormat(extensible);
            Assert.Equal(isFloat ? WaveFormatEncoding.IeeeFloat : WaveFormatEncoding.Pcm, normalized.Encoding);
            Assert.Equal(sampleRate, normalized.SampleRate);
            Assert.Equal(channels, normalized.Channels);
            Assert.Equal(bits, normalized.BitsPerSample);

            var returned = WaveFormat.MarshalToPtr(extensible);
            try
            {
                var roundTrip = new byte[native.Length];
                Marshal.Copy(returned, roundTrip, 0, roundTrip.Length);
                Assert.Equal(native, roundTrip);
            }
            finally
            {
                Marshal.FreeHGlobal(returned);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    [Theory]
    [InlineData(3, 48_000, 2, 32)] // WAVE_FORMAT_IEEE_FLOAT
    [InlineData(1, 44_100, 1, 16)] // WAVE_FORMAT_PCM
    public void Plain_mix_format_round_trips_through_native_marshalling(
        int formatTag, int sampleRate, int channels, int bits)
    {
        var blockAlign = channels * bits / 8;
        var native = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(native, (ushort)formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(native.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(native.AsSpan(4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(native.AsSpan(8), (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(native.AsSpan(12), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(native.AsSpan(14), (ushort)bits);
        var block = Marshal.AllocHGlobal(native.Length);
        try
        {
            Marshal.Copy(native, 0, block, native.Length);

            var format = WaveFormat.MarshalFromPtr(block);
            Assert.IsNotType<WaveFormatExtensible>(format);
            Assert.Equal((WaveFormatEncoding)formatTag, format.Encoding);
            Assert.Equal(sampleRate, format.SampleRate);
            Assert.Equal(channels, format.Channels);
            Assert.Equal(bits, format.BitsPerSample);
            Assert.Same(format, AudioCaptureService.NormalizeFormat(format));

            var returned = WaveFormat.MarshalToPtr(format);
            try
            {
                var roundTrip = new byte[native.Length];
                Marshal.Copy(returned, roundTrip, 0, roundTrip.Length);
                Assert.Equal(native, roundTrip);
            }
            finally
            {
                Marshal.FreeHGlobal(returned);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    // WAVEFORMATEXTENSIBLE exactly as mmreg.h lays it out: an 18-byte WAVEFORMATEX with cbSize 22,
    // then wValidBitsPerSample, dwChannelMask and the SubFormat GUID, 40 bytes in all.
    private static byte[] WaveFormatExtensibleBlock(int sampleRate, int channels, int bits, Guid subFormat)
    {
        var blockAlign = channels * bits / 8;
        var block = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(block, 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(12), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(14), (ushort)bits);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(16), 22);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(18), (ushort)bits);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20), channels switch
        {
            1 => 0x4u,  // SPEAKER_FRONT_CENTER
            2 => 0x3u,  // SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT
            _ => 0x33u, // front pair plus back pair
        });
        subFormat.TryWriteBytes(block.AsSpan(24));
        return block;
    }
}
