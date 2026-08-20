using System.Globalization;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Scribe.Core.Audio;

/// <summary>Peak and RMS for one capture channel, before any downmix.</summary>
public sealed record ChannelLevel(int Channel, float Peak, float Rms)
{
    /// <summary>Peak in dBFS, floored at -99 so a silent channel prints rather than reading -Infinity.</summary>
    public double PeakDbfs => CaptureSignalAnalyzer.ToDbfs(Peak);

    /// <summary>RMS in dBFS, floored at -99.</summary>
    public double RmsDbfs => CaptureSignalAnalyzer.ToDbfs(Rms);
}

/// <summary>
/// The measurable shape of one capture: levels, headroom, clipping, DC offset, and what each
/// channel contributed. Carries no audio and no content, only statistics.
/// </summary>
public sealed record CaptureSignalReport(
    int Channels,
    int SampleRate,
    float Peak,
    float Rms,
    double ClippedFraction,
    double NearSilentFraction,
    float DcOffset,
    IReadOnlyList<ChannelLevel> PerChannel)
{
    /// <summary>Peak in dBFS across all channels.</summary>
    public double PeakDbfs => CaptureSignalAnalyzer.ToDbfs(Peak);

    /// <summary>RMS in dBFS across all channels.</summary>
    public double RmsDbfs => CaptureSignalAnalyzer.ToDbfs(Rms);

    /// <summary>
    /// True when a second channel carries essentially nothing. Scribe averages every channel, so
    /// this halves the speech before it reaches the recognizer.
    /// </summary>
    public bool HasSilentChannel =>
        PerChannel.Count > 1 && PerChannel.Any(c => c.Peak < CaptureSignalAnalyzer.SilentChannelPeak);

    /// <summary>
    /// True when the channels differ enough that they are not the same microphone: a headset
    /// reference channel, an echo-cancellation loopback, or a genuinely stereo pair.
    /// </summary>
    public bool ChannelsDiverge
    {
        get
        {
            if (PerChannel.Count < 2) return false;
            var loudest = PerChannel.Max(c => c.Rms);
            var quietest = PerChannel.Min(c => c.Rms);
            return loudest > 0 && quietest / loudest < 0.5f;
        }
    }

    /// <summary>One line for the log. No content, only shape.</summary>
    public string Describe()
    {
        var channels = string.Join(" ", PerChannel.Select(c => string.Create(
            CultureInfo.InvariantCulture, $"ch{c.Channel}(peak {c.PeakDbfs:F1} rms {c.RmsDbfs:F1})")));

        return string.Create(CultureInfo.InvariantCulture,
            $"{SampleRate}Hz {Channels}ch peak {PeakDbfs:F1}dBFS rms {RmsDbfs:F1}dBFS " +
            $"clipped {ClippedFraction:P2} nearSilent {NearSilentFraction:P0} dc {DcOffset:F4} " +
            $"[{channels}]");
    }
}

/// <summary>
/// Measures a raw capture buffer so a failed dictation can be diagnosed without anyone ever sending
/// their audio.
/// <para>
/// Written after a support log left a real failure unexplainable. A user lost three of six
/// dictations to the recognizer returning an empty string, and the only thing the log could say
/// about the audio was "peak audio was present", which means nothing more than "not digital
/// silence" (the threshold is -60 dBFS). Everything that would have separated the candidates was
/// missing: how loud the capture actually was, whether it was clipping, whether it had a DC offset,
/// and, on their two-channel speakerphone, what each channel contributed before Scribe averaged
/// them together.
/// </para>
/// <para>
/// Deliberately statistics only. PRIVACY.md promises the log never holds what the user said, and
/// levels, clipping and channel balance say nothing about content while answering most of the
/// questions worth asking about a microphone.
/// </para>
/// </summary>
public static class CaptureSignalAnalyzer
{
    /// <summary>A sample this close to full scale is treated as clipped.</summary>
    internal const float ClipThreshold = 0.999f;

    /// <summary>Below this a sample counts toward the near-silent fraction (about -60 dBFS).</summary>
    internal const float NearSilenceThreshold = 0.001f;

    /// <summary>A channel peaking below this is carrying nothing worth averaging in.</summary>
    internal const float SilentChannelPeak = 0.0005f;

    private const double MinimumDbfs = -99;

    /// <summary>Converts a linear amplitude to dBFS, floored so silence prints as a number.</summary>
    public static double ToDbfs(float amplitude) =>
        amplitude <= 0 ? MinimumDbfs : Math.Max(MinimumDbfs, 20 * Math.Log10(amplitude));

    /// <summary>
    /// Analyzes an interleaved raw capture buffer. Supports the two formats WASAPI shared mode
    /// actually hands us: 32-bit float and 16-bit PCM. Any other format yields an empty report
    /// rather than a wrong one, matching how <see cref="AudioCaptureService"/> already treats
    /// unmeterable formats.
    /// </summary>
    public static CaptureSignalReport Analyze(ReadOnlySpan<byte> raw, WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        var channels = Math.Max(1, format.Channels);
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            return Analyze(MemoryMarshal.Cast<byte, float>(raw), channels, format.SampleRate, static f => f);
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            return Analyze(MemoryMarshal.Cast<byte, short>(raw), channels, format.SampleRate, static s => s / 32768f);
        }

        return Empty(channels, format.SampleRate);
    }

    private static CaptureSignalReport Analyze<T>(
        ReadOnlySpan<T> samples,
        int channels,
        int sampleRate,
        Func<T, float> toFloat)
        where T : struct
    {
        if (samples.Length < channels)
        {
            return Empty(channels, sampleRate);
        }

        var peaks = new float[channels];
        var sumSquares = new double[channels];
        var counts = new long[channels];
        double sum = 0;
        long clipped = 0;
        long nearSilent = 0;

        for (var i = 0; i < samples.Length; i++)
        {
            var value = toFloat(samples[i]);
            var channel = i % channels;
            var magnitude = Math.Abs(value);

            if (magnitude > peaks[channel]) peaks[channel] = magnitude;
            sumSquares[channel] += value * (double)value;
            counts[channel]++;
            sum += value;

            if (magnitude >= ClipThreshold) clipped++;
            if (magnitude < NearSilenceThreshold) nearSilent++;
        }

        var perChannel = new List<ChannelLevel>(channels);
        for (var c = 0; c < channels; c++)
        {
            var rms = counts[c] == 0 ? 0f : (float)Math.Sqrt(sumSquares[c] / counts[c]);
            perChannel.Add(new ChannelLevel(c, peaks[c], rms));
        }

        var totalSquares = sumSquares.Sum();
        var overallRms = (float)Math.Sqrt(totalSquares / samples.Length);

        return new CaptureSignalReport(
            channels,
            sampleRate,
            peaks.Max(),
            overallRms,
            clipped / (double)samples.Length,
            nearSilent / (double)samples.Length,
            (float)(sum / samples.Length),
            perChannel);
    }

    private static CaptureSignalReport Empty(int channels, int sampleRate) => new(
        channels,
        sampleRate,
        Peak: 0,
        Rms: 0,
        ClippedFraction: 0,
        NearSilentFraction: 0,
        DcOffset: 0,
        PerChannel: []);
}
