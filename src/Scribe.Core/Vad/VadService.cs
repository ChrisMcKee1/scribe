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

    // The detector's internal circular buffer; captures longer than this skip trimming.
    private const float BufferSeconds = 60f;

    private readonly ModelLocator _locator;
    private readonly ILogger<VadService> _logger;
    private readonly object _gate = new();

    private VoiceActivityDetector? _vad;
    private int _windowSize = WindowSize;
    private bool _available;
    private bool _initialized;
    private bool _disposed;

    public VadService(ModelLocator locator, ILogger<VadService> logger)
    {
        _locator = locator;
        _logger = logger;
    }

    public bool IsAvailable
    {
        get { lock (_gate) { return _available; } }
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
            _vad = new VoiceActivityDetector(config, BufferSeconds);
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
            if (!_available || _vad is null) return audio;          // model unavailable: pass through
            if (audio.SampleRate != RequiredSampleRate) return audio; // VAD model expects 16 kHz
            if (audio.Duration.TotalSeconds > BufferSeconds) return audio; // too long to buffer safely

            var samples = audio.Samples;
            _vad.Reset();

            var minStart = int.MaxValue;
            var maxEnd = 0;
            var found = false;

            var window = new float[_windowSize];
            var iterations = samples.Length / _windowSize;
            for (var i = 0; i < iterations; i++)
            {
                Array.Copy(samples, i * _windowSize, window, 0, _windowSize);
                _vad.AcceptWaveform(window);
                Drain(ref minStart, ref maxEnd, ref found);
            }

            _vad.Flush();
            Drain(ref minStart, ref maxEnd, ref found);

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
            if (length == samples.Length) return audio; // nothing to trim

            var trimmed = new float[length];
            Array.Copy(samples, minStart, trimmed, 0, length);

            _logger.LogDebug("VAD trimmed {FromMs} ms to {ToMs} ms of speech.",
                (int)audio.Duration.TotalMilliseconds,
                (int)(length * 1000L / audio.SampleRate));

            return new CapturedAudio(trimmed, audio.SampleRate);
        }
    }

    private void Drain(ref int minStart, ref int maxEnd, ref bool found)
    {
        while (!_vad!.IsEmpty())
        {
            var segment = _vad.Front();
            var start = segment.Start;
            var end = segment.Start + segment.Samples.Length;
            if (start < minStart) minStart = start;
            if (end > maxEnd) maxEnd = end;
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
