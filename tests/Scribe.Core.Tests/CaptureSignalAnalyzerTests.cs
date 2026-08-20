using System.Runtime.InteropServices;
using NAudio.Wave;
using Scribe.Core.Audio;

namespace Scribe.Core.Tests;

/// <summary>
/// The analyzer exists to make a failed decode explainable from a log alone. These cases are the
/// microphone shapes that produce an identical-looking capture and completely different causes.
/// </summary>
public class CaptureSignalAnalyzerTests
{
    private static readonly WaveFormat Stereo48Float = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
    private static readonly WaveFormat Mono48Float = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);

    /// <summary>Builds an interleaved float buffer from per-channel sample generators.</summary>
    private static byte[] Interleaved(int frames, params Func<int, float>[] channels)
    {
        var samples = new float[frames * channels.Length];
        for (var frame = 0; frame < frames; frame++)
        {
            for (var c = 0; c < channels.Length; c++)
            {
                samples[(frame * channels.Length) + c] = channels[c](frame);
            }
        }

        return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }

    private static float Tone(int frame, float amplitude) =>
        amplitude * MathF.Sin(frame * 0.05f);

    [Fact]
    public void A_silent_second_channel_is_reported_as_such()
    {
        // The shape that silently halves the speech: Scribe averages every channel, so a dead
        // second channel costs 6 dB before the recognizer ever sees the audio.
        var raw = Interleaved(4_800, f => Tone(f, 0.5f), _ => 0f);

        var report = CaptureSignalAnalyzer.Analyze(raw, Stereo48Float);

        Assert.Equal(2, report.Channels);
        Assert.True(report.HasSilentChannel);
        Assert.True(report.PerChannel[0].Peak > 0.4f);
        Assert.Equal(0f, report.PerChannel[1].Peak);
    }

    [Fact]
    public void Two_channels_carrying_the_same_microphone_are_not_flagged()
    {
        var raw = Interleaved(4_800, f => Tone(f, 0.5f), f => Tone(f, 0.5f));

        var report = CaptureSignalAnalyzer.Analyze(raw, Stereo48Float);

        Assert.False(report.HasSilentChannel);
        Assert.False(report.ChannelsDiverge);
    }

    [Fact]
    public void Channels_at_very_different_levels_are_flagged_as_diverging()
    {
        // What an echo-cancellation reference or a headset's second channel looks like: present,
        // so not "silent", but nothing like the microphone it is being averaged with.
        var raw = Interleaved(4_800, f => Tone(f, 0.5f), f => Tone(f, 0.02f));

        var report = CaptureSignalAnalyzer.Analyze(raw, Stereo48Float);

        Assert.False(report.HasSilentChannel);
        Assert.True(report.ChannelsDiverge);
    }

    [Fact]
    public void Quiet_audio_is_distinguishable_from_digital_silence()
    {
        // The gap that made a real support log unusable. AudioCaptureService.LastCaptureWasSilent
        // only asks "was this above -60 dBFS", so a capture 40 dB too quiet reports exactly the
        // same as a healthy one: "peak audio was present".
        var quiet = CaptureSignalAnalyzer.Analyze(Interleaved(4_800, f => Tone(f, 0.004f)), Mono48Float);
        var healthy = CaptureSignalAnalyzer.Analyze(Interleaved(4_800, f => Tone(f, 0.5f)), Mono48Float);

        Assert.True(quiet.Peak > AudioCaptureService.SilentCapturePeak, "the quiet case is not digital silence");
        Assert.InRange(quiet.PeakDbfs, -60, -45);
        Assert.InRange(healthy.PeakDbfs, -10, 0);
    }

    [Fact]
    public void Clipping_is_measured_as_a_fraction_of_samples()
    {
        var raw = Interleaved(1_000, f => f % 2 == 0 ? 1.0f : 0.1f);

        var report = CaptureSignalAnalyzer.Analyze(raw, Mono48Float);

        Assert.InRange(report.ClippedFraction, 0.49, 0.51);
    }

    [Fact]
    public void A_dc_offset_is_reported()
    {
        // A stuck bias is a real driver fault and it wrecks the filterbank the recognizer runs on,
        // while leaving peak and RMS looking entirely reasonable.
        var raw = Interleaved(4_800, f => 0.3f + Tone(f, 0.1f));

        var report = CaptureSignalAnalyzer.Analyze(raw, Mono48Float);

        Assert.InRange(report.DcOffset, 0.25f, 0.35f);
    }

    [Fact]
    public void Sixteen_bit_pcm_is_supported()
    {
        var samples = new short[2_000];
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)(16_384 * MathF.Sin(i * 0.05f));
        var raw = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();

        var report = CaptureSignalAnalyzer.Analyze(raw, new WaveFormat(48_000, 16, 1));

        Assert.InRange(report.PeakDbfs, -8, -4);
    }

    [Fact]
    public void An_unsupported_format_yields_an_empty_report_rather_than_a_wrong_one()
    {
        var report = CaptureSignalAnalyzer.Analyze(new byte[128], new WaveFormat(48_000, 24, 2));

        Assert.Empty(report.PerChannel);
        Assert.Equal(0f, report.Peak);
        Assert.False(report.HasSilentChannel);
    }

    [Fact]
    public void An_empty_buffer_does_not_throw()
    {
        var report = CaptureSignalAnalyzer.Analyze([], Stereo48Float);

        Assert.Empty(report.PerChannel);
        Assert.Equal(0f, report.Peak);
    }

    [Fact]
    public void Silence_describes_as_a_number_rather_than_negative_infinity()
    {
        var report = CaptureSignalAnalyzer.Analyze(Interleaved(100, _ => 0f), Mono48Float);

        Assert.Equal(-99, report.PeakDbfs);
        Assert.DoesNotContain("Infinity", report.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Describe_carries_the_per_channel_breakdown_and_no_audio()
    {
        var report = CaptureSignalAnalyzer.Analyze(
            Interleaved(4_800, f => Tone(f, 0.5f), _ => 0f), Stereo48Float);

        var text = report.Describe();

        Assert.Contains("48000Hz 2ch", text);
        Assert.Contains("ch0(", text);
        Assert.Contains("ch1(", text);
        Assert.Contains("dBFS", text);
    }
}
