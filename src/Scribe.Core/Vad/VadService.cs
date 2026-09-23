using System.Diagnostics;
using Microsoft.Extensions.Logging;
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

    // Load, trim, unload and dispose all happen under this gate, so an idle Unload can never land between loading
    // the detector and using it, and Dispose can never free it under a trim that is still running.
    private readonly object _gate = new();

    private ISpeechSegmentDetector? _detector;
    private bool _initialized;
    private bool _disposed;
    private double? _lastSpeechSeconds;

    public VadService(ModelLocator locator, ILogger<VadService> logger)
    {
        _logger = logger;
        _load = () => LoadSilero(locator);
    }

    /// <summary>
    /// Test seam: supplies the detector instead of loading Silero. A loader returning null stands for a missing model.
    /// </summary>
    internal VadService(Func<ISpeechSegmentDetector?> load, ILogger<VadService> logger)
    {
        _load = load;
        _logger = logger;
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

    public CapturedAudio Trim(CapturedAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.IsEmpty) return CapturedAudio.Empty;

        lock (_gate)
        {
            // Checked under the gate before loading, so a trim racing Dispose can never bring the model back.
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lastSpeechSeconds = null;

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

            var window = new float[windowSize];
            var iterations = samples.Length / windowSize;
            for (var i = 0; i < iterations; i++)
            {
                Array.Copy(samples, i * windowSize, window, 0, windowSize);
                detector.AcceptWaveform(window);
                Drain(detector, ref minStart, ref maxEnd, ref found, ref voicedSamples);
            }

            detector.Flush();
            Drain(detector, ref minStart, ref maxEnd, ref found, ref voicedSamples);

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
