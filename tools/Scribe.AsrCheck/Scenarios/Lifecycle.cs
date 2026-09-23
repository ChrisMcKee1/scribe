using System.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.Transcription;
using Scribe.Core.Vad;

namespace Scribe.AsrCheck.Scenarios;

internal sealed class UnloadRaceResult
{
    public double WindowSeconds { get; set; }

    public int Attempts { get; set; }

    public int Succeeded { get; set; }

    public int RecognizerNotInitialized { get; set; }

    public int ObjectDisposed { get; set; }

    public int OtherFailures { get; set; }

    public List<string> OtherFailureTypes { get; set; } = [];

    public int UntrimmedVad { get; set; }

    public int EmptyDecodes { get; set; }

    public long UnloadCalls { get; set; }

    public double MeanAttemptMs { get; set; }
}

internal sealed class ColdReloadResult
{
    public double WarmWallMs { get; set; }

    public double WarmDecodeMs { get; set; }

    public List<double> ColdWallMs { get; set; } = [];

    public List<double> ColdReportedDecodeMs { get; set; } = [];

    public double MedianColdOverheadMs { get; set; }

    public bool ReportedDecodeExcludesLoad { get; set; }

    public double VadWarmTrimMs { get; set; }

    public double VadColdTrimMs { get; set; }
}

internal sealed class WarmSteadyResult
{
    public int Iterations { get; set; }

    public double ClipSeconds { get; set; }

    public double MinMs { get; set; }

    public double MedianMs { get; set; }

    public double P95Ms { get; set; }

    public double MaxMs { get; set; }

    public double MeanDecodeMs { get; set; }

    public double MeanRealTimeFactor { get; set; }

    public double PrivateMbBefore { get; set; }

    public double PrivateMbAfter { get; set; }
}

internal sealed class CancellationResult
{
    public bool Supported { get; set; }

    public double AudioSeconds { get; set; }

    public double? CancelRequestedAfterMs { get; set; }

    public double? ReturnedAfterCancelMs { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public int? Characters { get; set; }

    public double? UncancellableDecodeMs { get; set; }
}

internal sealed class LifecycleReport
{
    public bool FixExpected { get; set; }

    public string FixExpectedReason { get; set; } = string.Empty;

    public UnloadRaceResult? UnloadRace { get; set; }

    public ColdReloadResult? ColdReload { get; set; }

    public WarmSteadyResult? WarmSteady { get; set; }

    public CancellationResult? Cancellation { get; set; }

    public List<CheckResult> Checks { get; } = [];
}

/// <summary>
/// Lifecycle and concurrency scenarios for the speech engine. They use only APIs the 0.4.2 baseline
/// already has, so the same source measures both builds; the cancellable decode is reached through
/// <see cref="ProductionHooks"/> and reported as not supported where it does not exist.
/// </summary>
internal sealed class LifecycleScenarios(
    TranscriptionService transcription,
    VadService vad,
    ProductionHooks hooks,
    Func<string, float[]> pad)
{
    /// <summary>
    /// Decodes continuously on this thread while another thread unloads both engines as fast as it
    /// can. It amplifies the idle-release race (R18) so it can be counted: in a build that loads and
    /// decodes as one step under the engine's own lock, every attempt succeeds and every trim trims.
    /// The unloader never sleeps, because the window being hunted is the gap between "ensure the
    /// model is loaded" and "use it", which a timed pause would mostly miss.
    /// </summary>
    public UnloadRaceResult UnloadRace(string clipName, TimeSpan window, int minimumAttempts)
    {
        var audio = new CapturedAudio(pad(clipName));
        var result = new UnloadRaceResult { WindowSeconds = window.TotalSeconds };

        using var stop = new ManualResetEventSlim(false);
        long unloads = 0;
        var unloader = new Thread(() =>
        {
            while (!stop.IsSet)
            {
                transcription.Unload();
                vad.Unload();
                Interlocked.Increment(ref unloads);
            }
        })
        {
            IsBackground = true,
            Name = "scenario-unloader",
        };

        var total = Stopwatch.StartNew();
        unloader.Start();
        try
        {
            // A floor on attempts so a slow runner still reports more than a single sample.
            while (total.Elapsed < window || result.Attempts < minimumAttempts)
            {
                result.Attempts++;
                try
                {
                    var trimmed = vad.Trim(audio);
                    if (trimmed.Samples.Length == audio.Samples.Length)
                    {
                        // The padded clip always has room tone to trim, so an unchanged length means
                        // Trim ran with the detector unloaded underneath it and passed the audio through.
                        result.UntrimmedVad++;
                    }

                    var decoded = transcription.Transcribe(trimmed.IsEmpty ? audio : trimmed);
                    if (decoded.IsEmpty)
                    {
                        result.EmptyDecodes++;
                    }
                    else
                    {
                        result.Succeeded++;
                    }
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("not initialized", StringComparison.OrdinalIgnoreCase))
                {
                    result.RecognizerNotInitialized++;
                }
                catch (ObjectDisposedException)
                {
                    result.ObjectDisposed++;
                }
                catch (Exception ex)
                {
                    result.OtherFailures++;
                    if (!result.OtherFailureTypes.Contains(ex.GetType().Name))
                    {
                        result.OtherFailureTypes.Add(ex.GetType().Name);
                    }
                }
            }
        }
        finally
        {
            stop.Set();
            unloader.Join();
            total.Stop();
        }

        result.UnloadCalls = Interlocked.Read(ref unloads);
        result.MeanAttemptMs = Math.Round(total.Elapsed.TotalMilliseconds / Math.Max(1, result.Attempts), 1);
        return result;
    }

    /// <summary>Unload, then decode: what the first dictation after an idle release pays.</summary>
    public ColdReloadResult ColdReload(string clipName, int repetitions)
    {
        var audio = vad.Trim(new CapturedAudio(pad(clipName)));
        var result = new ColdReloadResult();

        transcription.Transcribe(audio);
        var warmTimer = Stopwatch.StartNew();
        var warm = transcription.Transcribe(audio);
        warmTimer.Stop();
        result.WarmWallMs = Math.Round(warmTimer.Elapsed.TotalMilliseconds, 1);
        result.WarmDecodeMs = Math.Round(warm.DecodeDuration.TotalMilliseconds, 1);

        for (var i = 0; i < repetitions; i++)
        {
            transcription.Unload();
            var timer = Stopwatch.StartNew();
            var cold = transcription.Transcribe(audio);
            timer.Stop();
            result.ColdWallMs.Add(Math.Round(timer.Elapsed.TotalMilliseconds, 1));
            result.ColdReportedDecodeMs.Add(Math.Round(cold.DecodeDuration.TotalMilliseconds, 1));
        }

        result.MedianColdOverheadMs = Math.Round(Median(result.ColdWallMs) - result.WarmWallMs, 1);

        // The load happens inside Transcribe, so a reported decode near the warm one (rather than near
        // the cold wall time) means the result keeps model loading out of DecodeDuration.
        result.ReportedDecodeExcludesLoad = Median(result.ColdReportedDecodeMs) < (result.WarmWallMs + Median(result.ColdWallMs)) / 2;

        var raw = new CapturedAudio(pad(clipName));
        vad.Trim(raw);
        var vadWarm = Stopwatch.StartNew();
        vad.Trim(raw);
        vadWarm.Stop();
        vad.Unload();
        var vadCold = Stopwatch.StartNew();
        vad.Trim(raw);
        vadCold.Stop();
        result.VadWarmTrimMs = Math.Round(vadWarm.Elapsed.TotalMilliseconds, 2);
        result.VadColdTrimMs = Math.Round(vadCold.Elapsed.TotalMilliseconds, 2);
        return result;
    }

    /// <summary>Rapid short dictations on a warm engine: the steady state a user feels.</summary>
    public WarmSteadyResult WarmSteady(string clipName, int iterations, Func<CapturedAudio, TranscriptionResult> pipeline)
    {
        var audio = new CapturedAudio(pad(clipName));
        pipeline(audio);
        var before = ResourceMeter.PrivateBytes();
        var wall = new List<double>(iterations);
        var decode = new List<double>(iterations);
        var rtf = new List<double>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var timer = Stopwatch.StartNew();
            var result = pipeline(audio);
            timer.Stop();
            wall.Add(timer.Elapsed.TotalMilliseconds);
            decode.Add(result.DecodeDuration.TotalMilliseconds);
            rtf.Add(result.RealTimeFactor);
        }

        var after = ResourceMeter.PrivateBytes();
        wall.Sort();
        return new WarmSteadyResult
        {
            Iterations = iterations,
            ClipSeconds = Math.Round(audio.Duration.TotalSeconds, 2),
            MinMs = Math.Round(wall[0], 1),
            MedianMs = Math.Round(Median(wall), 1),
            P95Ms = Math.Round(wall[(int)Math.Ceiling(0.95 * wall.Count) - 1], 1),
            MaxMs = Math.Round(wall[^1], 1),
            MeanDecodeMs = Math.Round(decode.Average(), 1),
            MeanRealTimeFactor = Math.Round(rtf.Average(), 3),
            PrivateMbBefore = Math.Round(before / (1024.0 * 1024.0), 1),
            PrivateMbAfter = Math.Round(after / (1024.0 * 1024.0), 1),
        };
    }

    /// <summary>
    /// Starts a long decode and cancels it after <paramref name="cancelAfter"/>, through the
    /// cancellable overload when the build has one. Where it does not, the decode cannot be stopped,
    /// and the report says how long a shutdown during it would have had to wait.
    /// </summary>
    public CancellationResult Cancel(CapturedAudio longAudio, TimeSpan cancelAfter, double? uncancellableDecodeMs)
    {
        var result = new CancellationResult { AudioSeconds = Math.Round(longAudio.Duration.TotalSeconds, 1) };
        var cancellable = hooks.BindCancellableTranscribe(transcription);
        if (cancellable is null)
        {
            result.Supported = false;
            result.Outcome = "not supported: this build has no Transcribe(CapturedAudio, CancellationToken)";
            result.UncancellableDecodeMs = uncancellableDecodeMs;
            return result;
        }

        result.Supported = true;
        transcription.Initialize();
        using var cts = new CancellationTokenSource();
        long requestedAt = 0;
        using var registration = cts.Token.Register(() => Interlocked.Exchange(ref requestedAt, Stopwatch.GetTimestamp()));

        var started = Stopwatch.GetTimestamp();
        var decode = Task.Factory.StartNew(
            () => cancellable(longAudio, cts.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        cts.CancelAfter(cancelAfter);

        try
        {
            var decoded = decode.GetAwaiter().GetResult();
            result.Outcome = "completed";
            result.Characters = decoded.Text.Length;
        }
        catch (OperationCanceledException)
        {
            result.Outcome = "canceled";
        }
        catch (Exception ex)
        {
            result.Outcome = $"faulted: {ex.GetType().Name}";
        }

        var ended = Stopwatch.GetTimestamp();
        var requested = Interlocked.Read(ref requestedAt);
        if (requested != 0)
        {
            result.CancelRequestedAfterMs = Math.Round(Stopwatch.GetElapsedTime(started, requested).TotalMilliseconds, 1);
            result.ReturnedAfterCancelMs = Math.Round(Stopwatch.GetElapsedTime(requested, ended).TotalMilliseconds, 1);
        }

        return result;
    }

    internal static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
