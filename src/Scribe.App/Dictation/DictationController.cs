using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Scribe.Core.Audio;
using Scribe.Core.Cleanup;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.TextInjection;
using Scribe.Core.Transcription;
using Scribe.Core.Vad;

namespace Scribe.App.Dictation;

/// <summary>
/// Wires the dictation loop together: hotkey down starts microphone capture; hotkey up stops
/// capture and, on a background thread, runs VAD trimming, transcription, dictionary
/// post-processing and text injection, then records the result to history. A single state gate
/// prevents overlapping sessions, and the heavy stop→decode→inject work is offloaded so the
/// hotkey consumer thread is never blocked. Live settings are honored per capture and can be
/// swapped at runtime via <see cref="ApplySettings"/>.
/// </summary>
internal sealed class DictationController : IDisposable
{
    private readonly IHotkeyService _hotkeys;
    private readonly IAudioCaptureService _audio;
    private readonly IVadService _vad;
    private readonly ITranscriptionService _transcription;
    private readonly ITextPostProcessor _postProcessor;
    private readonly ITextCleanupService _cleanup;
    private readonly ITextInjector _injector;
    private readonly IHistoryRepository _history;
    private readonly ISettingsRepository _settingsRepository;
    private readonly ILogger<DictationController> _log;

    private readonly object _gate = new();
    private DictationState _state = DictationState.Idle;
    private AppSettings _settings = AppSettings.CreateDefault();
    private bool _paused;
    private bool _started;
    private bool _disposed;

    public DictationController(
        IHotkeyService hotkeys,
        IAudioCaptureService audio,
        IVadService vad,
        ITranscriptionService transcription,
        ITextPostProcessor postProcessor,
        ITextCleanupService cleanup,
        ITextInjector injector,
        IHistoryRepository history,
        ISettingsRepository settingsRepository,
        ILogger<DictationController> log)
    {
        _hotkeys = hotkeys;
        _audio = audio;
        _vad = vad;
        _transcription = transcription;
        _postProcessor = postProcessor;
        _cleanup = cleanup;
        _injector = injector;
        _history = history;
        _settingsRepository = settingsRepository;
        _log = log;
    }

    /// <summary>Raised whenever the dictation state changes (on a background thread).</summary>
    public event Action<DictationState>? StateChanged;

    /// <summary>Raised when a capture or transcription step fails.</summary>
    public event Action<string>? Error;

    /// <summary>Raised after a capture is dictated, with the final injected text.</summary>
    public event Action<string>? Dictated;

    /// <summary>True while dictation is suspended (the hotkey is ignored).</summary>
    public bool IsPaused
    {
        get { lock (_gate) { return _paused; } }
    }

    /// <summary>The settings currently driving the loop.</summary>
    public AppSettings CurrentSettings
    {
        get { lock (_gate) { return _settings; } }
    }

    /// <summary>Loads persisted settings, applies the hotkey binding and installs the hook.</summary>
    public void Start()
    {
        if (_started) return;

        _settings = _settingsRepository.Load();
        _postProcessor.Reload();
        _cleanup.Configure(BuildCleanupOptions(_settings));

        _hotkeys.UpdateBinding(_settings.Hotkey);
        _hotkeys.Activated += OnActivated;
        _hotkeys.Deactivated += OnDeactivated;
        _hotkeys.Start();
        _started = true;
        _log.LogInformation("Dictation controller started; binding = {Binding}.", _settings.Hotkey.DisplayName);
    }

    /// <summary>
    /// Replaces the live settings (called by the settings window after a save). Re-binds the
    /// hotkey and reloads the post-processor so changes take effect on the next capture without
    /// a restart. Decode-thread changes still require a restart (the recognizer is warm-loaded).
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            _settings = settings.Clone();
        }

        _hotkeys.UpdateBinding(settings.Hotkey);
        _postProcessor.Reload();
        _cleanup.Configure(BuildCleanupOptions(settings));
        _log.LogInformation("Applied updated settings; binding = {Binding}.", settings.Hotkey.DisplayName);
    }

    /// <summary>Maps persisted AppSettings into the cleanup service's provider-agnostic options.</summary>
    private static CleanupOptions BuildCleanupOptions(AppSettings settings) => new(
        settings.EnableAiCleanup,
        settings.AiCleanupProvider,
        settings.AiCleanupModel,
        settings.AiCleanupAzureEndpoint,
        settings.AiCleanupAzureDeployment,
        settings.AiCleanupAzureApiKey);

    /// <summary>Suspends or resumes dictation without removing the keyboard hook.</summary>
    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_paused == paused) return;
            _paused = paused;
        }

        _log.LogInformation("Dictation {State}.", paused ? "paused" : "resumed");
        Raise(paused ? DictationState.Paused : DictationState.Idle);
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        AppSettings settings;
        lock (_gate)
        {
            if (_paused)
            {
                _log.LogDebug("Hotkey activated while paused; ignoring.");
                return;
            }

            if (_state != DictationState.Idle)
            {
                _log.LogDebug("Hotkey activated while {State}; ignoring.", _state);
                return;
            }

            _state = DictationState.Recording;
            settings = _settings;
        }

        try
        {
            _audio.Start(settings.InputDeviceId);
            _log.LogInformation("Recording started.");
            Raise(DictationState.Recording);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to start audio capture.");
            ResetToIdle();
            Error?.Invoke("microphone unavailable");
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_state != DictationState.Recording)
            {
                return;
            }

            _state = DictationState.Processing;
        }

        Raise(DictationState.Processing);
        _ = Task.Run(ProcessAsync);
    }

    private async Task ProcessAsync()
    {
        var settings = CurrentSettings;
        try
        {
            var captured = _audio.Stop();
            _log.LogInformation("Captured {Seconds:F2}s of audio.", captured.Duration.TotalSeconds);

            if (captured.IsEmpty)
            {
                _log.LogInformation("Capture was empty; nothing to transcribe.");
                return;
            }

            var audio = captured;
            if (settings.UseVoiceActivityDetection)
            {
                var trimmed = _vad.Trim(captured);
                if (trimmed.IsEmpty)
                {
                    _log.LogInformation("VAD detected no speech; discarding capture.");
                    return;
                }

                audio = trimmed;
            }

            // The recognizer warm-loads at startup, but a very fast first dictation can arrive
            // before that finishes. Rather than throwing the capture away, let Transcribe load the
            // model on demand (it is idempotent) so the user's first utterance is never lost.
            if (!_transcription.IsReady)
            {
                _log.LogInformation("Recognizer still warming up; loading on demand for this capture.");
            }

            var result = _transcription.Transcribe(audio);
            if (result.IsEmpty)
            {
                _log.LogInformation("No speech recognized.");
                return;
            }

            // Optional AI cleanup runs between raw decoding and dictionary canonicalization so the
            // post-processor always has the final say on casing of terms like ".NET" or "ReBAC".
            // CleanAsync never throws and returns the input unchanged when cleanup is off or not
            // ready, so a dictation is never blocked or lost by this stage.
            var recognized = result.Text;
            if (settings.EnableAiCleanup)
            {
                var cleaned = await _cleanup.CleanAsync(recognized).ConfigureAwait(false);
                if (!string.Equals(cleaned, recognized, StringComparison.Ordinal))
                {
                    _log.LogInformation("AI cleanup refined the transcription.");
                    recognized = cleaned;
                }
            }

            var text = settings.ApplyPostProcessing ? _postProcessor.Process(recognized) : recognized;
            if (string.IsNullOrWhiteSpace(text))
            {
                _log.LogInformation("Post-processing produced empty text; nothing to inject.");
                return;
            }

            _log.LogInformation(
                "Transcribed {Chars} chars in {Decode:F2}s (RTF {Rtf:F2}).",
                text.Length, result.DecodeDuration.TotalSeconds, result.RealTimeFactor);

            var targetApp = ForegroundProcessName();
            _injector.Inject(text, settings.InjectionMethod);
            _log.LogInformation("Text injected into {App}.", targetApp ?? "the focused app");

            RecordHistory(settings, audio, result, text, targetApp);
            Dictated?.Invoke(text);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Dictation processing failed.");
            Error?.Invoke("transcription failed");
        }
        finally
        {
            ResetToIdle();
        }
    }

    private void RecordHistory(
        AppSettings settings, CapturedAudio audio, TranscriptionResult result, string text, string? targetApp)
    {
        try
        {
            long? blobId = settings.StoreAudioHistory ? _history.AddAudioBlob(audio) : null;
            _history.Add(new HistoryEntry(
                Id: 0,
                TimestampUtc: DateTimeOffset.UtcNow,
                Text: text,
                AudioMilliseconds: (int)result.AudioDuration.TotalMilliseconds,
                DecodeMilliseconds: (int)result.DecodeDuration.TotalMilliseconds,
                TargetApp: targetApp,
                AudioBlobId: blobId));
        }
        catch (Exception ex)
        {
            // History is best-effort; never fail a dictation because persistence hiccupped.
            _log.LogWarning(ex, "Failed to record dictation history.");
        }
    }

    private void ResetToIdle()
    {
        bool paused;
        lock (_gate)
        {
            _state = DictationState.Idle;
            paused = _paused; // a pause requested mid-capture takes effect once processing finishes
        }

        Raise(paused ? DictationState.Paused : DictationState.Idle);
    }

    private void Raise(DictationState state)
    {
        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A StateChanged handler threw.");
        }
    }

    private static string? ForegroundProcessName()
    {
        try
        {
            var handle = GetForegroundWindow();
            if (handle == 0) return null;

            _ = GetWindowThreadProcessId(handle, out var pid);
            if (pid == 0) return null;

            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        if (_started)
        {
            _hotkeys.Activated -= OnActivated;
            _hotkeys.Deactivated -= OnDeactivated;
            _hotkeys.Stop();
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
}
