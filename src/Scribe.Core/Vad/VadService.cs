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
    // is roughly double the worst case observed. Overrunning it is not fatal either — sherpa-onnx
    // grows the buffer and copies the existing data rather than dropping any.
    //
    // This was previously also used to SKIP trimming for captures longer than 60 s, which meant the
    // captures most likely to hurt (the recogniser degrades on long buffers) were the only ones
    // that kept all of their leading and trailing silence. Segment offsets are absolute and proved
    // identical at 25 s, 60 s and whole-capture buffer sizes, so no such cap is warranted.
    private const float DetectorBufferSeconds = 60f;

    private readonly ModelLocator _locator;
    private readonly ILogger<VadService> _logger;
    private readonly object _gate = new();

    private VoiceActivityDetector? _vad;
    private int _windowSize = WindowSize;
    private bool _available;
    private bool _initialized;
    private bool _disposed;
    private double? _lastSpeechSeconds;

    public VadService(ModelLocator locator, ILogger<VadService> logger)
    {
        _locator = locator;
        _logger = logger;
    }

    public bool IsAvailable
    {
        get { lock (_gate) { return _available; } }
    }

    public double? LastSpeechSeconds
    {
        get { lock (_gate) { return _lastSpeechSeconds; } }
    }

    public void Initialize() => EnsureInitialized();

    private void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized)) return;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized) return;

            var models = _locator.Resolve();
            if (!models.VadAvailable)
            {
                _logger.LogWarning(
                    "Silero VAD model not found at {Path}; voice activity detection is disabled.",
                    models.SileroVadPath);
                _available = false;
                _initialized = true;
                return;
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
            _vad = new VoiceActivityDetector(config, DetectorBufferSeconds);
            _windowSize = config.SileroVad.WindowSize;
            sw.Stop();

            _available = true;
            _initialized = true;
            _logger.LogInformation(
                "Loaded Silero VAD (window {Window}, threshold {Threshold}) in {ElapsedMs} ms.",
                _windowSize, Threshold, sw.ElapsedMilliseconds);
        }
    }

    public CapturedAudio Trim(CapturedAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.IsEmpty) return CapturedAudio.Empty;

        EnsureInitialized();

        lock (_gate)
        {
            _lastSpeechSeconds = null;
            if (!_available || _vad is null) return audio;          // model unavailable: pass through
            if (audio.SampleRate != RequiredSampleRate) return audio; // VAD model expects 16 kHz

            var samples = audio.Samples;
            _vad.Reset();

            var minStart = int.MaxValue;
            var maxEnd = 0;
            var found = false;
            var voicedSamples = 0L;

            var window = new float[_windowSize];
            var iterations = samples.Length / _windowSize;
            for (var i = 0; i < iterations; i++)
            {
                Array.Copy(samples, i * _windowSize, window, 0, _windowSize);
                _vad.AcceptWaveform(window);
                Drain(ref minStart, ref maxEnd, ref found, ref voicedSamples);
            }

            _vad.Flush();
            Drain(ref minStart, ref maxEnd, ref found, ref voicedSamples);

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

    private void Drain(ref int minStart, ref int maxEnd, ref bool found, ref long voicedSamples)
    {
        while (!_vad!.IsEmpty())
        {
            var segment = _vad.Front();
            var start = segment.Start;
            var end = segment.Start + segment.Samples.Length;
            if (start < minStart) minStart = start;
            if (end > maxEnd) maxEnd = end;
            voicedSamples += segment.Samples.Length;
            found = true;
            _vad.Pop();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _vad?.Dispose();
            _vad = null;
        }
    }
}
