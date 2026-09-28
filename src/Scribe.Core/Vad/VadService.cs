using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using SherpaOnnx;

namespace Scribe.Core.Vad;

/// <inheritdoc cref="IVadService"/>
public sealed class VadService : IVadService
{
    private const int RequiredSampleRate = 16_000;

    // Silero VAD v5 fixes the window to 512 samples at 16 kHz. Threshold/durations use the model's
    // calibrated defaults; they balance not clipping quiet speech against admitting noise.
    private const int WindowSize = 512;
    private const float Threshold = 0.5f;
    private const float MinSilenceSeconds = 0.5f;
    private const float MinSpeechSeconds = 0.25f;
    private const float MaxSpeechSeconds = 20f;

    // Sizes the detector's internal circular buffer. It bounds only the audio held between drains,
    // NOT the total capture length: Trim drains after every window, so the detector never retains
    // more than the segment currently in flight. Measured across 30 real captures of 57-250 s the
    // high-water mark was 30.9 s (bounded by MaxSpeechSeconds plus the silence lookahead), so 60 s
    // is roughly double the worst case observed. Overrunning it is not fatal either; sherpa-onnx
    // grows the buffer and copies the existing data rather than dropping any.
    //
    // This was previously also used to SKIP trimming for captures longer than 60 s, which meant the
    // captures most likely to hurt (the recogniser degrades on long buffers) were the only ones
    // that kept all of their leading and trailing silence. Segment offsets are absolute and proved
    // identical at 25 s, 60 s and whole-capture buffer sizes, so no such cap is warranted.
    private const float DetectorBufferSeconds = 60f;

    private readonly Func<ISpeechSegmentDetector?> _load;
    private readonly ILogger<VadService> _logger;

    // PerfFlags, read once at construction. VadWindowCancellation: a trim given a token stops between windows when it is
    // canceled (see Trim(CapturedAudio, CancellationToken)); off, the token is ignored exactly as before.
    // CaptureTimingDiagnostics: each trim that runs the detector logs how long it took, numbers only.
    private readonly bool _windowCancellation;
    private readonly bool _timingDiagnostics;

    // Load, trim, unload and dispose all happen under this gate, so an idle Unload can never land between loading
    // the detector and using it, and Dispose can never free it under a trim that is still running.
    private readonly object _gate = new();

    private ISpeechSegmentDetector? _detector;
    private bool _initialized;
    private bool _disposed;
    private double? _lastSpeechSeconds;

    public VadService(ModelLocator locator, ILogger<VadService> logger, PerfFlags? perfFlags = null)
    {
        _logger = logger;
        _load = () => LoadSilero(locator);
        (_windowCancellation, _timingDiagnostics) = ReadFlags(perfFlags);
    }

    /// <summary>
    /// Test seam: supplies the detector instead of loading Silero. A loader returning null stands for a missing model.
    /// </summary>
    internal VadService(Func<ISpeechSegmentDetector?> load, ILogger<VadService> logger, PerfFlags? perfFlags = null)
    {
        _load = load;
        _logger = logger;
        (_windowCancellation, _timingDiagnostics) = ReadFlags(perfFlags);
    }

    public bool IsAvailable
    {
        get { lock (_gate) { return _detector is not null; } }
    }

    public double? LastSpeechSeconds
    {
        get { lock (_gate) { return _lastSpeechSeconds; } }
    }

    public void Initialize()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureInitialized();
        }
    }

    // Caller holds _gate.
    private void EnsureInitialized()
    {
        if (_initialized) return;

        _detector = _load();
        _initialized = true;
    }

    private ISpeechSegmentDetector? LoadSilero(ModelLocator locator)
    {
        var models = locator.Resolve();
        if (!models.VadAvailable)
        {
            _logger.LogWarning(
                "Silero VAD model not found at {Path}; voice activity detection is disabled.",
                models.SileroVadPath);
            return null;
        }

        var config = new VadModelConfig();
        config.SileroVad.Model = models.SileroVadPath;
        config.SileroVad.Threshold = Threshold;
        config.SileroVad.MinSilenceDuration = MinSilenceSeconds;
        config.SileroVad.MinSpeechDuration = MinSpeechSeconds;
        config.SileroVad.MaxSpeechDuration = MaxSpeechSeconds;
        config.SileroVad.WindowSize = WindowSize;
        config.SampleRate = RequiredSampleRate;
        config.NumThreads = 1;
        config.Provider = "cpu";

        var sw = Stopwatch.StartNew();
        var vad = new VoiceActivityDetector(config, DetectorBufferSeconds);
        var windowSize = config.SileroVad.WindowSize;
        sw.Stop();

        _logger.LogInformation(
            "Loaded Silero VAD (window {Window}, threshold {Threshold}) in {ElapsedMs} ms.",
            windowSize, Threshold, sw.ElapsedMilliseconds);
        return new SileroDetector(vad, windowSize);
    }

    public CapturedAudio Trim(CapturedAudio audio) => Trim(audio, CancellationToken.None);

    /// <inheritdoc/>
    public CapturedAudio Trim(CapturedAudio audio, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.IsEmpty) return CapturedAudio.Empty;

        // With PerfFlags.VadWindowCancellation off the token is never looked at, so the trim is the one it always was.
        var token = _windowCancellation ? cancellationToken : CancellationToken.None;
        token.ThrowIfCancellationRequested();

        lock (_gate)
        {
            // Checked under the gate before loading, so a trim racing Dispose can never bring the model back.
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lastSpeechSeconds = null;

            // And the cancellation after the gate, as the recognizer checks it: a dictation abandoned while this waited
            // for another trim does not load a model it no longer wants.
            token.ThrowIfCancellationRequested();

            // Loaded under the same gate as the trim. Loading before taking it let an idle Unload slip in between,
            // which turned this call into a silent pass-through of the untrimmed capture.
            EnsureInitialized();
            var detector = _detector;
            if (detector is null) return audio;                      // model unavailable: pass through
            if (audio.SampleRate != RequiredSampleRate) return audio; // VAD model expects 16 kHz

            var samples = audio.Samples;
            var windowSize = detector.WindowSize;
            detector.Reset();

            var minStart = int.MaxValue;
            var maxEnd = 0;
            var found = false;
            var voicedSamples = 0L;
            var timed = _timingDiagnostics ? Stopwatch.GetTimestamp() : 0;

            var window = new float[windowSize];
            var iterations = samples.Length / windowSize;
            try
            {
                for (var i = 0; i < iterations; i++)
                {
                    // Between windows: a native window cannot be interrupted, so its boundary is the one place to stop.
                    token.ThrowIfCancellationRequested();
                    Array.Copy(samples, i * windowSize, window, 0, windowSize);
                    detector.AcceptWaveform(window);
                    Drain(detector, ref minStart, ref maxEnd, ref found, ref voicedSamples);
                }

                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // A canceled trim throws rather than return what it had found so far, which would pass for the speech of
                // the whole capture, and the detector drops what it was fed; the speech total stays unset.
                ResetAfterCancel(detector);
                throw;
            }

            detector.Flush();
            Drain(detector, ref minStart, ref maxEnd, ref found, ref voicedSamples);

            if (timed != 0)
            {
                TryLogTrimTiming(iterations, (int)audio.Duration.TotalMilliseconds, Stopwatch.GetElapsedTime(timed), found);
            }

            if (!found)
            {
                _logger.LogDebug("VAD found no speech in {Ms} ms capture; rejecting.",
                    (int)audio.Duration.TotalMilliseconds);
                return CapturedAudio.Empty;
            }

            minStart = Math.Clamp(minStart, 0, samples.Length);
            maxEnd = Math.Clamp(maxEnd, minStart, samples.Length);

            var length = maxEnd - minStart;
            if (length <= 0) return CapturedAudio.Empty;

            // Summed voiced audio, which is what "how much did they actually say" means. The
            // returned span is always at least this long and is usually longer, because every
            // pause between the first and last word is inside it.
            _lastSpeechSeconds = voicedSamples / (double)audio.SampleRate;

            if (length == samples.Length) return audio; // nothing to trim

            var trimmed = new float[length];
            Array.Copy(samples, minStart, trimmed, 0, length);

            _logger.LogDebug("VAD trimmed {FromMs} ms to {ToMs} ms of speech.",
                (int)audio.Duration.TotalMilliseconds,
                (int)(length * 1000L / audio.SampleRate));

            return new CapturedAudio(trimmed, audio.SampleRate);
        }
    }

    private void ResetAfterCancel(ISpeechSegmentDetector detector)
    {
        try
        {
            detector.Reset();
        }
        catch (Exception ex)
        {
            // The cancellation is what the caller needs to see; the next trim resets the detector again before it starts.
            TryLogResetFailure(ex);
        }
    }

    // The two diagnostics the performance flags added to a trim, each written so that nothing it does, the failure's shape
    // included, can throw: a line that cannot be written must never cost the dictation its trim, or turn a cancellation
    // into some other failure. Arguments rather than a lambda, so no closure exists on the trim's path.
    private void TryLogTrimTiming(int windows, int audioMs, TimeSpan elapsed, bool found)
    {
        try
        {
            _logger.LogDebug(
                "VAD ran {Windows} windows over {AudioMs} ms of audio in {ElapsedMs:F1} ms (speech found: {Found}).",
                windows,
                audioMs,
                Math.Round(elapsed.TotalMilliseconds, 1),
                found);
        }
        catch
        {
            // Nothing useful is left to do.
        }
    }

    private void TryLogResetFailure(Exception failure)
    {
        try
        {
            _logger.LogDebug("Resetting the VAD after a canceled trim failed ({Failure}).", FailureShape.Describe(failure));
        }
        catch
        {
            // Nothing useful is left to do.
        }
    }

    private static (bool WindowCancellation, bool TimingDiagnostics) ReadFlags(PerfFlags? perfFlags)
    {
        var flags = perfFlags ?? PerfFlags.None;
        return (flags.IsOn(PerfFlags.VadWindowCancellation), flags.IsOn(PerfFlags.CaptureTimingDiagnostics));
    }

    private static void Drain(
        ISpeechSegmentDetector detector, ref int minStart, ref int maxEnd, ref bool found, ref long voicedSamples)
    {
        while (detector.TryPopSegment(out var start, out var length))
        {
            var end = start + length;
            if (start < minStart) minStart = start;
            if (end > maxEnd) maxEnd = end;
            voicedSamples += length;
            found = true;
        }
    }

    public void Unload()
    {
        lock (_gate)
        {
            if (_disposed || !_initialized) return;
            _detector?.Dispose();
            _detector = null;
            _initialized = false; // the next Trim/Initialize reloads the model on demand
            _lastSpeechSeconds = null;
            _logger.LogInformation("Silero VAD unloaded; it will reload on the next dictation.");
        }
    }

    public void Dispose()
    {
        // Same gate as a trim: disposal during shutdown waits for a trim that is still running.
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _detector?.Dispose();
            _detector = null;
            _initialized = false;
        }
    }

    private sealed class SileroDetector(VoiceActivityDetector vad, int windowSize) : ISpeechSegmentDetector
    {
        public int WindowSize { get; } = windowSize;

        public void Reset() => vad.Reset();

        public void AcceptWaveform(float[] window) => vad.AcceptWaveform(window);

        public void Flush() => vad.Flush();

        public bool TryPopSegment(out int start, out int length)
        {
            if (vad.IsEmpty())
            {
                start = 0;
                length = 0;
                return false;
            }

            var segment = vad.Front();
            start = segment.Start;
            length = segment.Samples.Length;
            vad.Pop();
            return true;
        }

        public void Dispose() => vad.Dispose();
    }
}
