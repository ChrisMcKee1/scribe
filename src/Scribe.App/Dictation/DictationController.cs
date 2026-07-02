using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Scribe.Core.Audio;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
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
    private readonly IDictionaryRepository _dictionary;
    private readonly ICleanupFailureLog _failureLog;
    private readonly ISettingsRepository _settingsRepository;
    private readonly ILogger<DictationController> _log;

    // Failures shown in Settings are kept to a rolling one-week window; older rows are pruned on each
    // successful cleanup (and at startup by the host) so the diagnostic log never grows unbounded.
    private static readonly TimeSpan FailureRetention = TimeSpan.FromDays(7);

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
        IDictionaryRepository dictionary,
        ICleanupFailureLog failureLog,
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
        _dictionary = dictionary;
        _failureLog = failureLog;
        _settingsRepository = settingsRepository;
        _log = log;
    }

    /// <summary>Raised whenever the dictation state changes (on a background thread).</summary>
    public event Action<DictationState>? StateChanged;

    /// <summary>Raised when a capture or transcription step fails.</summary>
    public event Action<string>? Error;

    /// <summary>Raised after a capture is dictated, with the final injected text.</summary>
    public event Action<string>? Dictated;

    /// <summary>
    /// Raised (on a background thread) when AI cleanup was enabled but failed at runtime, so the
    /// dictation fell back to raw transcription. Carries a short reason for the failure overlay.
    /// </summary>
    public event Action<string>? CleanupFailed;

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
    private CleanupOptions BuildCleanupOptions(AppSettings settings) => new(
        settings.EnableAiCleanup,
        settings.AiCleanupProvider,
        settings.AiCleanupModel,
        settings.AiCleanupAzureEndpoint,
        settings.AiCleanupAzureDeployment,
        settings.AiCleanupAzureApiKey,
        settings.AiCleanupAzureTenantId,
        settings.AiCleanupWritingStyle,
        BuildGlossary(),
        settings.AiCleanupCustomEndpoint,
        settings.AiCleanupCustomModel,
        settings.AiCleanupCustomApiKey);

    // Renders the user's enabled dictionary entries into a glossary block appended to the cleanup
    // prompt. Built here (not in the service) so it refreshes whenever settings are (re)applied —
    // i.e. right after the dictionary editor saves — and so the value lives on the value-equality
    // CleanupOptions record, which lets the service detect the change and rebuild its agent.
    private string? BuildGlossary()
    {
        try
        {
            var glossary = CleanupPrompt.BuildGlossary(_dictionary.GetEnabled());
            return string.IsNullOrEmpty(glossary) ? null : glossary;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to build the AI glossary from the dictionary; continuing without it.");
            return null;
        }
    }

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
        using var activity = ScribeTelemetry.Source.StartActivity(ScribeTelemetry.DictationActivity);
        var settings = CurrentSettings;
        try
        {
            var captured = _audio.Stop();
            activity?.SetTag(ScribeTelemetry.TagCaptureSeconds, Math.Round(captured.Duration.TotalSeconds, 2));
            _log.LogInformation("Captured {Seconds:F2}s of audio.", captured.Duration.TotalSeconds);

            if (captured.IsEmpty)
            {
                activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.EmptyCapture);
                _log.LogInformation("Capture was empty; nothing to transcribe.");
                return;
            }

            var audio = captured;
            activity?.SetTag(ScribeTelemetry.TagVadEnabled, settings.UseVoiceActivityDetection);
            if (settings.UseVoiceActivityDetection)
            {
                var trimmed = _vad.Trim(captured);
                if (trimmed.IsEmpty)
                {
                    activity?.SetTag(ScribeTelemetry.TagVadKept, false);
                    activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.VadNoSpeech);
                    _log.LogInformation("VAD detected no speech; discarding capture.");
                    return;
                }

                activity?.SetTag(ScribeTelemetry.TagVadKept, true);
                audio = trimmed;
            }

            // The recognizer warm-loads at startup, but a very fast first dictation can arrive
            // before that finishes. Rather than throwing the capture away, let Transcribe load the
            // model on demand (it is idempotent) so the user's first utterance is never lost.
            activity?.SetTag(ScribeTelemetry.TagRecognizerReady, _transcription.IsReady);
            if (!_transcription.IsReady)
            {
                _log.LogInformation("Recognizer still warming up; loading on demand for this capture.");
            }

            var result = _transcription.Transcribe(audio);
            if (result.IsEmpty)
            {
                activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.NoSpeech);
                _log.LogInformation("No speech recognized.");
                return;
            }

            activity?.SetTag(ScribeTelemetry.TagDecodeChars, result.Text.Length);
            activity?.SetTag(ScribeTelemetry.TagRealTimeFactor, Math.Round(result.RealTimeFactor, 2));

            // Optional AI cleanup runs between raw decoding and dictionary canonicalization so the
            // post-processor always has the final say on casing of terms like ".NET" or "ReBAC".
            // CleanAsync never throws for a content failure: it classifies the outcome so we can fall
            // back to raw text and visibly flag a runtime failure without ever disabling cleanup.
            var recognized = result.Text;
            activity?.SetTag(ScribeTelemetry.TagAiCleanup, settings.EnableAiCleanup);
            if (settings.EnableAiCleanup)
            {
                var cleanup = await _cleanup.CleanAsync(recognized).ConfigureAwait(false);
                activity?.SetTag(ScribeTelemetry.TagAiOutcome, cleanup.Outcome.ToString());
                activity?.SetTag(ScribeTelemetry.TagAiChanged, cleanup.Changed);

                if (cleanup.Outcome == CleanupOutcome.Failed)
                {
                    // Intelligence failed at runtime: keep the raw transcription, signal the UI FIRST
                    // so the overlay flashes red immediately, then persist the failure on a background
                    // thread. The failure log opens its own SQLite connection per call, so a busy
                    // timeout there must never sit in front of raising the flash or injecting the raw
                    // text. Cleanup stays enabled — the very next dictation tries again.
                    var reason = cleanup.FailureReason ?? "Intelligence failed.";
                    _log.LogWarning("AI cleanup failed ({Reason}); using raw transcription.", reason);
                    RaiseCleanupFailed(reason);
                    var rawForLog = result.Text;
                    _ = Task.Run(() => RecordCleanupFailure(settings, reason, rawForLog));
                }
                else
                {
                    if (cleanup.Changed)
                    {
                        _log.LogInformation("AI cleanup refined the transcription.");
                    }

                    recognized = cleanup.Text;

                    // A partial degradation (some segments failed, or a very long tail was left raw)
                    // is recorded for the Settings log but does not flash red — the user still got
                    // usable cleaned text back. Persist off the dictation path so the DB write never
                    // sits in front of text injection.
                    if (cleanup.FailureReason is not null)
                    {
                        var partialReason = cleanup.FailureReason;
                        var rawForLog = result.Text;
                        _ = Task.Run(() => RecordCleanupFailure(settings, partialReason, rawForLog));
                    }

                    // A genuine cleanup run is a good moment to prune the one-week failure window.
                    if (cleanup.Outcome is CleanupOutcome.Cleaned or CleanupOutcome.Unchanged)
                    {
                        _ = Task.Run(PruneOldFailures);
                    }
                }
            }

            var text = settings.ApplyPostProcessing ? _postProcessor.Process(recognized) : recognized;
            if (string.IsNullOrWhiteSpace(text))
            {
                activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.EmptyAfterPostProcess);
                _log.LogInformation("Post-processing produced empty text; nothing to inject.");
                return;
            }

            activity?.SetTag(ScribeTelemetry.TagFinalChars, text.Length);
            _log.LogInformation(
                "Transcribed {Chars} chars in {Decode:F2}s (RTF {Rtf:F2}).",
                text.Length, result.DecodeDuration.TotalSeconds, result.RealTimeFactor);

            var targetApp = ForegroundProcessName();
            activity?.SetTag(ScribeTelemetry.TagTargetApp, targetApp);

            // Terminals treat an injected newline as Enter, so AI-cleanup paragraph breaks would
            // submit several partial messages; flatten per the configured mode before injecting.
            var flattened = InjectionTextFormatter.Apply(text, settings.NewlineHandling, targetApp);
            if (!ReferenceEquals(flattened, text))
            {
                _log.LogInformation(
                    "Flattened line breaks before injection ({Mode}, target {App}).",
                    settings.NewlineHandling, targetApp ?? "unknown");
                text = flattened;
            }

            _injector.Inject(text, settings.InjectionMethod);
            _log.LogInformation("Text injected into {App}.", targetApp ?? "the focused app");

            activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.Injected);
            RecordHistory(settings, audio, result, text, targetApp);
            Dictated?.Invoke(text);
        }
        catch (Exception ex)
        {
            activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.Error);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
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

    // Persists a cleanup failure (hard or partial) for the Settings log. Best-effort: a logging
    // hiccup must never break the dictation that is already falling back to raw text.
    private void RecordCleanupFailure(AppSettings settings, string reason, string rawText)
    {
        try
        {
            var model = settings.AiCleanupProvider == CleanupProvider.AzureFoundry
                ? settings.AiCleanupAzureDeployment
                : settings.AiCleanupModel;
            _failureLog.Add(CleanupFailure.New(reason, settings.AiCleanupProvider.ToString(), model, rawText));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to record an AI cleanup failure.");
        }
    }

    /// <summary>Prunes cleanup failures older than the rolling retention window. Safe to call anytime.</summary>
    public void PruneFailureLog()
    {
        try
        {
            var removed = _failureLog.PruneOlderThan(DateTimeOffset.UtcNow - FailureRetention);
            if (removed > 0)
            {
                _log.LogDebug("Pruned {Count} AI cleanup failures older than a week.", removed);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to prune AI cleanup failures.");
        }
    }

    private void PruneOldFailures() => PruneFailureLog();

    private void RaiseCleanupFailed(string reason)
    {
        try
        {
            CleanupFailed?.Invoke(reason);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A CleanupFailed handler threw.");
        }
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
