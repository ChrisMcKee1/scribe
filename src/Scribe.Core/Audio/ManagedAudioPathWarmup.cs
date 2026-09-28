using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;
using Scribe.Core.Transcription;

namespace Scribe.Core.Audio;

/// <summary>
/// <see cref="PerfFlags.WarmManagedAudioPath"/>: once per process, right after the speech models are warm-loaded at
/// startup, runs the managed code the first dictation's stop compiles on tiny inputs, so that dictation does not pay for
/// compiling it: the conversion of a tenth of a second of silence in the usual device format (48 kHz float, one channel
/// and two), and the seam planner over a silent capture just over the chunk limit. Measured cold on 0.5.x, the first
/// conversion of a fresh process fell from 18.4 to 8.6 ms and the first long capture's plan from 5.6 to 4.0 ms, for about
/// 15 ms of work here. No decode, and no VAD trim: the first trim of a fresh process measured no slower than a warm one,
/// so there was nothing to take off it.
/// </summary>
/// <remarks>
/// It runs on the warm-load task, never on a dictation's path, and only once: a reload after the idle release finds the
/// code already compiled. It touches no service and keeps nothing (its recording and scratch are its own). A failure is
/// logged by its shape and changes nothing else.
/// </remarks>
public sealed class ManagedAudioPathWarmup
{
    private readonly bool _enabled;
    private readonly ILogger<ManagedAudioPathWarmup> _logger;
    private readonly Action _steps;
    private int _ran;

    public ManagedAudioPathWarmup(PerfFlags perfFlags, ILogger<ManagedAudioPathWarmup> logger)
        : this(perfFlags, logger, WarmUp)
    {
    }

    /// <summary>Test seam: the steps the warm-up runs, in place of the real ones.</summary>
    internal ManagedAudioPathWarmup(PerfFlags perfFlags, ILogger<ManagedAudioPathWarmup> logger, Action steps)
    {
        ArgumentNullException.ThrowIfNull(perfFlags);
        _enabled = perfFlags.IsOn(PerfFlags.WarmManagedAudioPath);
        _logger = logger;
        _steps = steps;
    }

    /// <summary>
    /// Runs the warm-up if the flag is on and it has not run in this process. Never throws. Returns whether it ran to the
    /// end.
    /// </summary>
    public bool RunOnce()
    {
        if (!_enabled || Interlocked.Exchange(ref _ran, 1) != 0)
        {
            return false;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            _steps();
            TryLog(log => log.LogDebug(
                "Managed audio path warmed in {ElapsedMs:F1} ms.",
                Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1)));
            return true;
        }
        catch (Exception ex)
        {
            TryLog(log => log.LogDebug(
                "Warming the managed audio path failed ({Failure}); the first dictation compiles it instead.",
                FailureShape.Describe(ex)));
            return false;
        }
    }

    private static void WarmUp()
    {
        AudioCaptureService.WarmConversionPath();
        TranscriptionChunker.WarmUp();
    }

    // Startup's warm-load task: a log call that throws must not turn a warm-up into a failed startup.
    private void TryLog(Action<ILogger> write)
    {
        try
        {
            write(_logger);
        }
        catch
        {
            // Nothing useful is left to do.
        }
    }
}
