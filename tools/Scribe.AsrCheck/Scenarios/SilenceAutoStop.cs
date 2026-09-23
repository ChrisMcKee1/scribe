using NAudio.Wave;
using Scribe.Core.Audio;

namespace Scribe.AsrCheck.Scenarios;

internal sealed class AutoStopMeasurement
{
    public int BufferMs { get; set; }

    public double ClipMs { get; set; }

    public bool Stopped { get; set; }

    public double? StopAtMs { get; set; }

    public bool HeardSpeech { get; set; }

    public double PeakLevel { get; set; }

    public double? LastVoicedMs { get; set; }

    public double? SpeechEndMs { get; set; }

    public double? LatencyAfterLastVoicedMs { get; set; }

    public double? LatencyAfterSpeechEndMs { get; set; }

    public string? CapturedText { get; set; }

    public double? CapturedOverlap { get; set; }

    /// <summary>
    /// The tracker's voice threshold when the measurement ended, on builds whose threshold adapts to the noise floor:
    /// read against <see cref="PeakLevel"/> it says whether steady noise had been learned by then. Null on builds with a
    /// fixed threshold.
    /// </summary>
    public double? FinalVoiceThreshold { get; set; }
}

/// <summary>
/// Feeds a scenario to the production <see cref="SilenceAutoStopTracker"/> the way a toggle-mode
/// dictation does: one level per capture buffer, computed by the capture service's own peak meter
/// on the device-format bytes, with buffer-end timestamps. Event-driven shared-mode WASAPI delivers
/// a buffer per device period, 10 ms by default, so that is the cadence used.
/// </summary>
internal static class SilenceAutoStop
{
    public const int BufferMs = 10;

    public static AutoStopMeasurement Run(
        byte[] data,
        WaveFormat format,
        IReadOnlyList<Placement> placements,
        int placementRate,
        ProductionHooks hooks,
        out int stopByte)
    {
        var meter = hooks.ComputePeak ?? throw new InvalidOperationException("AudioCaptureService.ComputePeak is not available.");
        var bufferBytes = Math.Max(1, format.SampleRate * BufferMs / 1000) * format.BlockAlign;
        var threshold = hooks.SilenceDefaults?.SilenceThreshold;
        var liveThreshold = hooks.VoiceThreshold;

        // Constructed exactly as DictationController constructs it: start time only, so every
        // threshold is the production default.
        var tracker = new SilenceAutoStopTracker(0);
        var measurement = new AutoStopMeasurement
        {
            BufferMs = BufferMs,
            ClipMs = Math.Round(data.Length * 1000.0 / format.AverageBytesPerSecond, 1),
            SpeechEndMs = placements.Count == 0 ? null : Math.Round(placements.Max(p => p.End) * 1000.0 / placementRate, 1),
        };

        stopByte = data.Length;
        var buffers = (data.Length + bufferBytes - 1) / bufferBytes;
        for (var i = 0; i < buffers; i++)
        {
            var offset = i * bufferBytes;
            var level = meter(data.AsSpan(offset, Math.Min(bufferBytes, data.Length - offset)), format);
            long timestamp = (i + 1) * BufferMs;

            // "Voiced" by the tracker's own rule, so the hold window is measured from the buffer the tracker counted: an
            // adaptive tracker judges each buffer against the threshold as it stood before that buffer, strictly above;
            // a fixed-threshold build counts any buffer at or above its one threshold.
            var voiced = liveThreshold is not null
                ? level > liveThreshold(tracker)
                : threshold is { } fixedThreshold && level >= fixedThreshold;
            if (voiced)
            {
                measurement.LastVoicedMs = timestamp;
            }

            if (tracker.Update(level, timestamp))
            {
                measurement.Stopped = true;
                measurement.StopAtMs = timestamp;
                stopByte = Math.Min(data.Length, offset + bufferBytes);
                break;
            }
        }

        measurement.HeardSpeech = tracker.HeardSpeech;
        measurement.PeakLevel = Math.Round(tracker.PeakLevel, 5);
        measurement.FinalVoiceThreshold = liveThreshold is null ? null : Math.Round(liveThreshold(tracker), 5);
        if (measurement.StopAtMs is { } stop)
        {
            measurement.LatencyAfterLastVoicedMs = measurement.LastVoicedMs is { } last ? stop - last : null;
            measurement.LatencyAfterSpeechEndMs = measurement.SpeechEndMs is { } end ? Math.Round(stop - end, 1) : null;
        }

        return measurement;
    }

    public static IEnumerable<CheckResult> Check(AutoStopExpectation expectation, AutoStopMeasurement m, SilenceTrackerDefaults? defaults)
    {
        if (defaults is null)
        {
            yield return new CheckResult("auto-stop", CheckStatus.NotSupported,
                "the tracker's constructor defaults could not be read, so the expected window is unknown");
            yield break;
        }

        var hold = defaults.SilenceHoldMs;
        switch (expectation)
        {
            case AutoStopExpectation.StopAfterSpeech:
            case AutoStopExpectation.NoStopDuringPause:
            {
                var onTime = m is { Stopped: true, HeardSpeech: true, LatencyAfterLastVoicedMs: { } latency }
                    && latency >= hold && latency <= hold + BufferMs;
                var afterSpeech = m.StopAtMs is { } stop && m.SpeechEndMs is { } end && stop >= end;
                var name = expectation == AutoStopExpectation.StopAfterSpeech
                    ? "auto-stop: stops one hold window after speech"
                    : "auto-stop: a 2 s pause does not stop the recording";
                yield return new CheckResult(name, onTime && afterSpeech ? CheckStatus.Pass : CheckStatus.Fail,
                    $"stopped={m.Stopped} at {m.StopAtMs?.ToString() ?? "-"} ms, last voiced buffer {m.LastVoicedMs?.ToString() ?? "-"} ms, " +
                    $"speech ends {m.SpeechEndMs?.ToString() ?? "-"} ms, hold {hold} ms");
                break;
            }

            case AutoStopExpectation.LeadInStop:
            {
                var ok = m is { Stopped: true, HeardSpeech: false, StopAtMs: { } stop }
                    && stop >= defaults.LeadInLimitMs && stop <= defaults.LeadInLimitMs + BufferMs;
                yield return new CheckResult("auto-stop: lead-in limit with no speech", ok ? CheckStatus.Pass : CheckStatus.Fail,
                    $"stopped={m.Stopped} at {m.StopAtMs?.ToString() ?? "-"} ms, heardSpeech={m.HeardSpeech}, lead-in {defaults.LeadInLimitMs} ms");
                break;
            }

            default:
                string detail;
                if (!m.Stopped)
                {
                    detail = m.FinalVoiceThreshold is { } final
                        ? $"never stopped within the {m.ClipMs} ms clip (peak level {m.PeakLevel}; the adaptive voice threshold was {final} at its end, above an absolute floor of {defaults.SilenceThreshold})"
                        : $"never stopped within the {m.ClipMs} ms clip (peak level {m.PeakLevel} against a {defaults.SilenceThreshold} threshold)";
                }
                else if (m.StopAtMs < m.SpeechEndMs)
                {
                    detail = $"stopped at {m.StopAtMs} ms, {m.SpeechEndMs - m.StopAtMs:0} ms before the speech ended: speech after the stop is lost";
                }
                else
                {
                    detail = $"stopped at {m.StopAtMs} ms ({m.LatencyAfterSpeechEndMs?.ToString() ?? "-"} ms after the speech ended), heardSpeech={m.HeardSpeech}, peak {m.PeakLevel}";
                }

                yield return new CheckResult("auto-stop", CheckStatus.Report, detail);
                break;
        }
    }
}
