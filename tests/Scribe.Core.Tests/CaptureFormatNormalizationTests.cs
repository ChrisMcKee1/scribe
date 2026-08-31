using NAudio.Wave;
using Scribe.Core.Audio;

namespace Scribe.Core.Tests;

/// <summary>
/// Regression tests for the WAVEFORMATEXTENSIBLE blindness that shipped with the NAudio 3
/// migration: WasapiRecorder reports the shared mix format with its extensible header intact,
/// and every Encoding-switch consumer (peak meter, silence auto-stop, signal analyzer, mute
/// heuristic) stopped recognizing the capture. Support log signature: a mic that recorded
/// "32-bit IeeeFloat ... peak -14.8 dBFS" one session recorded "32-bit Extensible ...
/// peak -99.0 dBFS []" the next.
/// </summary>
public class CaptureFormatNormalizationTests
{
    [Fact]
    public void Extensible_float_normalizes_to_ieee_float()
    {
        var extensible = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1).AsExtensible();

        var normalized = AudioCaptureService.NormalizeFormat(extensible);

        Assert.Equal(WaveFormatEncoding.IeeeFloat, normalized.Encoding);
        Assert.Equal(48_000, normalized.SampleRate);
        Assert.Equal(1, normalized.Channels);
        Assert.Equal(32, normalized.BitsPerSample);
        Assert.True(AudioCaptureService.IsMeterableFormat(normalized));
    }

    [Fact]
    public void Extensible_pcm_normalizes_to_pcm()
    {
        var extensible = new WaveFormat(44_100, 16, 2).AsExtensible();

        var normalized = AudioCaptureService.NormalizeFormat(extensible);

        Assert.Equal(WaveFormatEncoding.Pcm, normalized.Encoding);
        Assert.True(AudioCaptureService.IsMeterableFormat(normalized));
    }

    [Fact]
    public void Standard_formats_pass_through_unchanged()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(16_000, 1);

        Assert.Same(format, AudioCaptureService.NormalizeFormat(format));
    }

    [Fact]
    public void Peak_metering_works_on_normalized_extensible_float()
    {
        var normalized = AudioCaptureService.NormalizeFormat(
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1).AsExtensible());

        // A -6 dBFS sample must register on the meter; before normalization it read 0.
        var samples = new float[] { 0f, 0.5f, -0.25f, 0f };
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        var peak = AudioCaptureService.ComputePeak(bytes, normalized);

        Assert.Equal(0.5f, peak, precision: 5);
    }
}

file static class WaveFormatTestExtensions
{
    /// <summary>
    /// Wraps a standard format in a WAVEFORMATEXTENSIBLE header, the shape WASAPI shared-mode
    /// mix formats actually arrive in.
    /// </summary>
    public static WaveFormatExtensible AsExtensible(this WaveFormat format) =>
        new(format.SampleRate, format.BitsPerSample, format.Channels);
}
