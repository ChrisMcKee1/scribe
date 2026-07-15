using System;
using NAudio.Wave;
using Scribe.Core.Audio;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Proves the capture peak measurement that backs the muted-microphone detection. A muted WASAPI
/// endpoint (Teams meeting mute, headset hardware mute, Win11 taskbar mic mute) still delivers
/// buffers, they just contain digital zeros; the running peak staying under
/// <see cref="AudioCaptureService.SilentCapturePeak"/> is what turns the old silent no-op into a
/// "your microphone may be muted" error.
/// </summary>
public sealed class AudioCapturePeakTests
{
    private static byte[] FloatBuffer(params float[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] Pcm16Buffer(params short[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    [Fact]
    public void Digital_silence_measures_zero_peak()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var buffer = FloatBuffer(new float[480]);

        var peak = AudioCaptureService.ComputePeak(buffer, buffer.Length, format);

        Assert.Equal(0f, peak);
        Assert.True(peak < AudioCaptureService.SilentCapturePeak);
    }

    [Fact]
    public void Quiet_noise_floor_still_counts_as_signal()
    {
        // A real (unmuted) analog mic never sits at exact zero; even its noise floor must clear
        // the silence threshold so a quiet room is not misreported as a muted microphone.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var buffer = FloatBuffer(0.0f, 0.002f, -0.0015f, 0.001f);

        var peak = AudioCaptureService.ComputePeak(buffer, buffer.Length, format);

        Assert.True(peak >= AudioCaptureService.SilentCapturePeak);
    }

    [Fact]
    public void Float_peak_is_the_largest_absolute_sample()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        var buffer = FloatBuffer(0.1f, -0.75f, 0.3f);

        Assert.Equal(0.75f, AudioCaptureService.ComputePeak(buffer, buffer.Length, format), 3);
    }

    [Fact]
    public void Float_peak_is_clamped_to_one()
    {
        // Drivers can deliver float samples slightly outside [-1, 1]; the meter contract is 0..1.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var buffer = FloatBuffer(1.5f, -2.0f);

        Assert.Equal(1f, AudioCaptureService.ComputePeak(buffer, buffer.Length, format));
    }

    [Fact]
    public void Pcm16_peak_is_normalized()
    {
        var format = new WaveFormat(16000, 16, 1);
        var buffer = Pcm16Buffer(0, -16384, 8192);

        Assert.Equal(0.5f, AudioCaptureService.ComputePeak(buffer, buffer.Length, format), 3);
    }

    [Fact]
    public void Pcm16_silence_measures_zero_peak()
    {
        var format = new WaveFormat(16000, 16, 1);
        var buffer = Pcm16Buffer(new short[160]);

        Assert.Equal(0f, AudioCaptureService.ComputePeak(buffer, buffer.Length, format));
    }

    [Fact]
    public void Pcm32_peak_is_normalized()
    {
        var format = new WaveFormat(48000, 32, 1);
        var samples = new[] { 0, int.MinValue / 2, int.MaxValue / 4 };
        var buffer = new byte[samples.Length * sizeof(int)];
        Buffer.BlockCopy(samples, 0, buffer, 0, buffer.Length);

        Assert.Equal(0.5f, AudioCaptureService.ComputePeak(buffer, buffer.Length, format), 3);
    }

    [Fact]
    public void Pcm24_peak_is_normalized()
    {
        var format = new WaveFormat(48000, 24, 1);
        // Two little-endian 24-bit samples: 0x400000 (= +0.5) and a small negative value.
        var buffer = new byte[] { 0x00, 0x00, 0x40, 0xFF, 0xFF, 0xFF };

        Assert.Equal(0.5f, AudioCaptureService.ComputePeak(buffer, buffer.Length, format), 3);
    }

    [Fact]
    public void Unknown_format_reports_zero_instead_of_throwing()
    {
        // 8-bit PCM has no fast path; the meter degrades to zero rather than crashing capture.
        var format = new WaveFormat(22050, 8, 1);
        var buffer = new byte[64];

        Assert.Equal(0f, AudioCaptureService.ComputePeak(buffer, buffer.Length, format));
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(24, true)]
    [InlineData(32, true)]
    [InlineData(8, false)]
    public void Meterable_formats_are_reported(int bits, bool expected)
    {
        // Unmeterable formats must be flagged so the silent-capture heuristic is disabled for
        // them instead of misreporting every capture as a muted microphone.
        Assert.Equal(expected, AudioCaptureService.IsMeterableFormat(new WaveFormat(48000, bits, 1)));
        Assert.True(AudioCaptureService.IsMeterableFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
    }
}
