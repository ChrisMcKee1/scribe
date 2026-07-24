using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Scribe.Core.Audio;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
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
    private readonly IDictionaryLibraryService _libraries;
    private readonly ICleanupFailureLog _failureLog;
    private readonly LastTranscriptStore _lastTranscript;
    private readonly ISettingsRepository _settingsRepository;
    private readonly ILogger<DictationController> _log;

    // Failures shown in Settings are kept to a rolling one-week window; older rows are pruned on each
    // successful cleanup (and at startup by the host) so the diagnostic log never grows unbounded.
    private static readonly TimeSpan FailureRetention = TimeSpan.FromDays(7);

    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private DictationState _state = DictationState.Idle;
    private AppSettings _settings = AppSettings.CreateDefault();
    private AppSettings? _captureSettings;
    private nint _captureTargetWindow;
    private string? _captureTargetApp;
    private long _captureStartedTimestamp;
    private Task? _processingTask;
    private bool _paused;
    private bool _started;
    private bool _disposed;

    // Silence auto-stop (toggle mode, opt-in): tracks live input levels while recording and ends
    // the dictation when the speaker goes quiet, so a forgotten toggle never leaves the mic hot.
    private SilenceAutoStopTracker? _silenceTracker;
    private bool _silenceSubscribed;

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
        IDictionaryLibraryService libraries,
        ICleanupFailureLog failureLog,
        LastTranscriptStore lastTranscript,
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
        _libraries = libraries;
        _failureLog = failureLog;
        _lastTranscript = lastTranscript;
        _settingsRepository = settingsRepository;
        _log = log;
    }

    /// <summary>Raised whenever the dictation state changes (on a background thread).</summary>
    public event Action<DictationState>? StateChanged;

    /// <summary>Raised when a capture or transcription step fails.</summary>
    public event Action<string>? Error;

    /// <summary>Raised for a recoverable capture warning while recording continues.</summary>
    public event Action<string>? Warning;

    /// <summary>Raised after a capture is dictated, with the final injected text.</summary>
    public event Action<string>? Dictated;

    /// <summary>
    /// Raised after the real dictation pipeline completes or fails, so the Playground can show
    /// raw recognition, final text, replacements, and per-stage timings for its focused text box.
    /// </summary>
    public event Action<DictationPipelineReport>? PipelineReported;

    /// <summary>
    /// Raised (on a background thread) when AI cleanup was enabled but failed at runtime, so the
    /// dictation fell back to raw transcription. Carries a short reason for the failure overlay.
    /// </summary>
    public event Action<string>? CleanupFailed;

    /// <summary>
    /// Raised (on a background thread) when text injection failed but the finalized transcript was
    /// preserved in <see cref="LastTranscriptStore"/>, so the shell can point the user at the tray
    /// recovery path instead of leaving the preserved text undiscoverable.
    /// </summary>
    public event Action? InjectionFailed;

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

    /// <summary>
    /// True when the active capture will run AI cleanup. This reflects the per-capture hotkey
    /// override, not just the global setting, so UI state can distinguish the two processing paths.
    /// </summary>
    public bool ActiveCaptureUsesAiCleanup
    {
        get { lock (_gate) { return _captureSettings?.EnableAiCleanup ?? false; } }
    }

    /// <summary>Loads persisted settings, applies the hotkey binding and installs the hook.</summary>
    public void Start()
    {
        if (_started) return;

        _settings = _settingsRepository.Load();
        _postProcessor.Reload();
        _cleanup.Configure(BuildCleanupOptions(_settings));

        _hotkeys.UpdateBindings(_settings.Hotkey, _settings.DictationOnlyHotkey);
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

        if (_hotkeys.Binding != settings.Hotkey ||
            _hotkeys.DictationOnlyBinding != settings.DictationOnlyHotkey)
        {
            _hotkeys.UpdateBindings(settings.Hotkey, settings.DictationOnlyHotkey);
        }
        _postProcessor.Reload();
        _cleanup.Configure(BuildCleanupOptions(settings));
        _log.LogInformation("Applied updated settings; binding = {Binding}.", settings.Hotkey.DisplayName);
    }

    /// <summary>
    /// Routes the settings window's binding-capture state to the hook. While capture is on, the
    /// push-to-talk key types into the capture box instead of starting a dictation, and any
    /// dictation already in flight is deactivated by the hook before capture begins.
    /// </summary>
    public void SetHotkeyCaptureMode(bool enabled) => _hotkeys.SetCaptureMode(enabled);

    /// <summary>Maps persisted AppSettings into the cleanup service's provider-agnostic options.</summary>
    private CleanupOptions BuildCleanupOptions(AppSettings settings) => new(
        settings.EnableAiCleanup,
        settings.AiCleanupProvider,
        settings.AiCleanupModel,
        settings.AiCleanupAzureEndpoint,
        settings.AiCleanupAzureDeployment,
        settings.AiCleanupAzureApiKey,
        AzureSubscriptionSelection.ResolveTenantId(
            settings.AiCleanupAzureSubscriptionId,
            settings.AiCleanupAzureSubscriptionTenantId,
            settings.AiCleanupAzureTenantId),
        settings.AiCleanupWritingStyle,
        BuildGlossary(),
        settings.AiCleanupCustomEndpoint,
        settings.AiCleanupCustomModel,
        settings.AiCleanupCustomApiKey,
        settings.AiCleanupPromptStyle,
        settings.AiCleanupFrontierPrompt,
        settings.AiCleanupLocalPrompt,
        settings.AiCleanupAzureSubscriptionId);

    // Renders the user's enabled dictionary entries into a glossary block appended to the cleanup
    // prompt. Built here (not in the service) so it refreshes whenever settings are (re)applied —
    // i.e. right after the dictionary editor saves — and so the value lives on the value-equality
    // CleanupOptions record, which lets the service detect the change and rebuild its agent. Enabled
    // dictionary libraries are layered on top of the base dictionary, the base winning on conflict.
    private string? BuildGlossary()
    {
        try
        {
            var baseEntries = _dictionary.GetEnabled();
            var libraryEntries = _libraries.GetEnabledLibraryEntries();
            var effective = libraryEntries.Count == 0
                ? (IReadOnlyList<DictionaryEntry>)baseEntries
                : DictionaryLibraryComposer.Merge(baseEntries, libraryEntries);
            var glossary = CleanupPrompt.BuildGlossary(effective);
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
        bool stopRecording;
        bool raiseState;
        lock (_gate)
        {
            if (_paused == paused) return;
            _paused = paused;
            stopRecording = paused && _state == DictationState.Recording;
            raiseState = _state == DictationState.Idle;
        }

        _log.LogInformation("Dictation {State}.", paused ? "paused" : "resumed");
        if (stopRecording)
        {
            _hotkeys.CancelToggle();
            StopAndProcess();
        }
        else if (raiseState)
        {
            Raise(paused ? DictationState.Paused : DictationState.Idle);
        }
    }

    private void OnActivated(object? sender, HotkeyTriggerEventArgs e)
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
            settings = DictationCaptureSettingsResolver.Resolve(_settings, e.Trigger);
            _captureSettings = settings;
            _captureTargetWindow = GetForegroundWindow();
            _captureTargetApp = ProcessNameForWindow(_captureTargetWindow);
            _captureStartedTimestamp = Stopwatch.GetTimestamp();
        }

        try
        {
            _audio.CaptureFaulted += OnCaptureFaulted;
            _audio.Start(settings.InputDeviceId);
            _log.LogInformation("Recording started ({Trigger}).", e.Trigger);
            Raise(DictationState.Recording);

            // Muted endpoints (headset mute, Win11 taskbar mic mute during a meeting) still record,
            // they just record silence. Warn immediately so the user can unmute mid-dictation
            // instead of speaking into a dead capture; recording continues in case they do.
            if (_audio.LastDeviceMuted)
            {
                _log.LogWarning("Recording started on a muted microphone.");
                Warning?.Invoke("microphone is muted, unmute it to dictate");
            }

            if (settings.AutoStopOnSilence && settings.Hotkey.Mode == HotkeyMode.Toggle)
            {
                _silenceTracker = new SilenceAutoStopTracker(Environment.TickCount64);
                if (!_silenceSubscribed)
                {
                    _audio.LevelChanged += OnLevelForSilence;
                    _silenceSubscribed = true;
                }
            }
        }
        catch (Exception ex)
        {
            _audio.CaptureFaulted -= OnCaptureFaulted;
            _log.LogError(ex, "Failed to start audio capture.");
            ResetToIdle();
            Error?.Invoke("microphone unavailable");
        }
    }

    private void OnDeactivated(object? sender, HotkeyTriggerEventArgs e) => StopAndProcess();

    private void OnCaptureFaulted(object? sender, Exception error)
    {
        _log.LogError(error, "The active microphone stopped unexpectedly.");
        Error?.Invoke("microphone disconnected");
        StopAndProcess();
    }

    // Fired on the audio capture thread for every level sample while subscribed.
    private void OnLevelForSilence(object? sender, float level)
    {
        var tracker = _silenceTracker;
        if (tracker is null || !tracker.Update(level, Environment.TickCount64))
        {
            return;
        }

        _log.LogInformation("Silence auto-stop: ending the toggle dictation.");

        // Reset the hook's toggle flag so the next press starts a new dictation rather than being
        // swallowed as the "toggle off" for the dictation we just ended ourselves.
        _hotkeys.CancelToggle();
        StopAndProcess();
    }

    /// <summary>Shared stop path for the hotkey release/toggle-off and the silence auto-stop.</summary>
    private void StopAndProcess()
    {
        UnsubscribeSilence();

        DictationSession session;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_state != DictationState.Recording)
            {
                return;
            }

            _state = DictationState.Processing;
            session = new DictationSession(
                _captureSettings ?? _settings.Clone(),
                _captureTargetWindow,
                _captureTargetApp,
                _captureStartedTimestamp);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _processingTask = completion.Task;
        }

        _audio.CaptureFaulted -= OnCaptureFaulted;
        _audio.RequestStop();
        Raise(DictationState.Processing);
        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessAsync(session, _lifetimeCts.Token).ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
    }

    private void UnsubscribeSilence()
    {
        _silenceTracker = null;
        if (_silenceSubscribed)
        {
            _audio.LevelChanged -= OnLevelForSilence;
            _silenceSubscribed = false;
        }
    }

    private async Task ProcessAsync(DictationSession session, CancellationToken cancellationToken)
    {
        using var activity = ScribeTelemetry.Source.StartActivity(ScribeTelemetry.DictationActivity);
        var settings = session.Settings;
        var report = new DictationPipelineReport(
            session.TargetWindow,
            settings.UseVoiceActivityDetection,
            settings.EnableAiCleanup,
            settings.ApplyPostProcessing,
            session.StartedTimestamp);
        var currentStage = "Audio capture";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captured = _audio.Stop();
            report.CaptureDuration = captured.Duration;
            activity?.SetTag(ScribeTelemetry.TagCaptureSeconds, Math.Round(captured.Duration.TotalSeconds, 2));
            _log.LogInformation("Captured {Seconds:F2}s of audio.", captured.Duration.TotalSeconds);

            if (captured.IsEmpty)
            {
                activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.EmptyCapture);

                // Zero samples from a capture that started without error means the endpoint is not
                // delivering audio at all — classically a Bluetooth headset whose hands-free mic
                // never engaged (seen with AirPods Max: the endpoint opens but streams nothing).
                // This must be loud: a silent return here looks to the user like dictation died.
                var device = _audio.LastDeviceName;
                _log.LogWarning("Capture from '{Device}' produced no audio.", device ?? "default device");
                report.Fail("Audio capture", "The microphone produced no audio.");
                RaisePipelineReport(report);
                Error?.Invoke(device is null
                    ? "no audio captured — check your microphone in Settings"
                    : $"no audio from '{device}' — pick a different microphone in Settings");
                return;
            }

            var audio = captured;
            activity?.SetTag(ScribeTelemetry.TagVadEnabled, settings.UseVoiceActivityDetection);
            if (settings.UseVoiceActivityDetection)
            {
                currentStage = "Voice activity detection";
                var vadTimer = Stopwatch.StartNew();
                var trimmed = _vad.Trim(captured);
                vadTimer.Stop();
                report.VadDuration = vadTimer.Elapsed;
                report.VadAvailable = _vad.IsAvailable;
                if (trimmed.IsEmpty)
                {
                    activity?.SetTag(ScribeTelemetry.TagVadKept, false);
                    activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.VadNoSpeech);
                    if (RaiseSilentCaptureError(report, "Voice activity detection"))
                    {
                        return;
                    }

                    _log.LogInformation("VAD detected no speech; discarding capture.");
                    report.Fail("Voice activity detection", "No speech was detected.");
                    RaisePipelineReport(report);
                    return;
                }

                activity?.SetTag(ScribeTelemetry.TagVadKept, true);
                audio = trimmed;
            }
            report.SpeechDuration = audio.Duration;

            // The recognizer warm-loads at startup, but a very fast first dictation can arrive
            // before that finishes. Rather than throwing the capture away, let Transcribe load the
            // model on demand (it is idempotent) so the user's first utterance is never lost.
            activity?.SetTag(ScribeTelemetry.TagRecognizerReady, _transcription.IsReady);
            if (!_transcription.IsReady)
            {
                _log.LogInformation("Recognizer still warming up; loading on demand for this capture.");
            }

            var targetApp = session.TargetApp;
            var profile = AppProfileMatcher.Match(settings.Profiles, targetApp);
            var newlineMode = profile?.NewlineHandling ?? settings.NewlineHandling;
            var requireSingleLine = InjectionTextFormatter.ShouldFlatten(newlineMode, targetApp);
            var cleanupWritingStyle = CleanupPrompt.ResolveWritingStyleOverride(
                settings.AiCleanupWritingStyle, profile?.WritingStyle, requireSingleLine);
            if (profile is not null)
            {
                _log.LogInformation("Applying profile '{Profile}' for {App}.", profile.Name, targetApp);
            }

            currentStage = "Speech recognition";
            var result = _transcription.Transcribe(audio);
            report.DecodeDuration = result.DecodeDuration;
            report.RealTimeFactor = result.RealTimeFactor;
            report.RawText = result.Text;
            if (result.IsEmpty)
            {
                activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.NoSpeech);
                if (RaiseSilentCaptureError(report, "Speech recognition"))
                {
                    return;
                }

                _log.LogInformation("No speech recognized.");
                report.Fail("Speech recognition", "No speech was recognized.");
                RaisePipelineReport(report);
                return;
            }

            activity?.SetTag(ScribeTelemetry.TagDecodeChars, result.Text.Length);
            activity?.SetTag(ScribeTelemetry.TagRealTimeFactor, Math.Round(result.RealTimeFactor, 2));

            // Optional AI cleanup runs between raw decoding and dictionary canonicalization so the
            // post-processor always has the final say on casing of terms like ".NET" or "ReBAC".
            // CleanAsync never throws for a content failure: it classifies the outcome so we can fall
            // back to raw text and visibly flag a runtime failure without ever disabling cleanup.
            var recognized = result.Text;
            var cleanup = CleanupResult.Skip(recognized);
            activity?.SetTag(ScribeTelemetry.TagAiCleanup, settings.EnableAiCleanup);
            if (settings.EnableAiCleanup)
            {
                currentStage = "AI cleanup";
                var cleanupTimer = Stopwatch.StartNew();
                cleanup = await _cleanup
                    .CleanAsync(recognized, cancellationToken, cleanupWritingStyle)
                    .ConfigureAwait(false);
                cleanupTimer.Stop();
                report.CleanupDuration = cleanupTimer.Elapsed;
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
            report.Cleanup = cleanup;
            report.CleanedText = recognized;

            currentStage = "Dictionary and snippets";
            var postTimer = Stopwatch.StartNew();
            var postProcessing = settings.ApplyPostProcessing
                ? _postProcessor.ProcessDetailed(recognized, result.Text)
                : new TextPostProcessingResult(recognized, []);
            postTimer.Stop();
            report.PostProcessingDuration = postTimer.Elapsed;
            report.PostProcessing = postProcessing;
            var text = postProcessing.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.EmptyAfterPostProcess);
                _log.LogInformation("Post-processing produced empty text; nothing to inject.");
                report.Fail("Dictionary and snippets", "Post-processing produced empty text.");
                RaisePipelineReport(report);
                return;
            }

            activity?.SetTag(ScribeTelemetry.TagFinalChars, text.Length);
            _log.LogInformation(
                "Transcribed {Chars} chars in {Decode:F2}s (RTF {Rtf:F2}).",
                text.Length, result.DecodeDuration.TotalSeconds, result.RealTimeFactor);

            activity?.SetTag(ScribeTelemetry.TagTargetApp, targetApp);

            // Terminals treat an injected newline as Enter, so AI-cleanup paragraph breaks would
            // submit several partial messages; flatten per the configured mode (the profile's
            // override wins when set) before injecting.
            var flattened = InjectionTextFormatter.Apply(text, newlineMode, targetApp);
            if (!ReferenceEquals(flattened, text))
            {
                _log.LogInformation(
                    "Flattened line breaks before injection ({Mode}, target {App}).",
                    newlineMode, targetApp ?? "unknown");
                text = flattened;
            }
            report.FinalText = text;

            _lastTranscript.Set(text);
            cancellationToken.ThrowIfCancellationRequested();
            currentStage = "Text insertion";
            var injectionTimer = Stopwatch.StartNew();
            var injection = _injector.Inject(text, settings.InjectionMethod, session.TargetWindow);
            injectionTimer.Stop();
            report.InjectionDuration = injectionTimer.Elapsed;
            report.Injection = injection;
            if (!injection.Succeeded)
            {
                activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.Error);
                activity?.SetStatus(ActivityStatusCode.Error, injection.Error);
                _log.LogWarning(
                    "Text injection failed for {App}: {Error}", targetApp ?? "the focused app", injection.Error);
                Error?.Invoke(injection.Error == "The focused window changed while processing."
                    ? "focus changed — dictation was not inserted"
                    : "text could not be inserted completely");

                // The transcript was stored just above, so close the loop: without a hint the
                // preserved text looks lost, because the overlay flash is the only other signal.
                RaiseInjectionFailed();
                report.Fail("Text insertion", injection.Error ?? "Text could not be inserted.");
                RaisePipelineReport(report);
                return;
            }

            _log.LogInformation("Text injected into {App} using {Method}.", targetApp ?? "the focused app", injection.Method);

            activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.Injected);
            RecordHistory(settings, audio, result, text, targetApp, cleanup, report.CleanupDuration);
            RaisePipelineReport(report);
            Dictated?.Invoke(text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.Error);
            _log.LogInformation("Dictation processing canceled during shutdown.");
        }
        catch (FileNotFoundException ex)
        {
            activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.Error);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _log.LogInformation(ex, "Dictation skipped because no speech model is installed.");
            report.Fail(currentStage, ex.Message);
            RaisePipelineReport(report);
            Error?.Invoke("choose a speech model in Settings");
        }
        catch (Exception ex)
        {
            activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.Error);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _log.LogError(ex, "Dictation processing failed.");
            report.Fail(currentStage, ex.Message);
            RaisePipelineReport(report);
            Error?.Invoke("transcription failed");
        }
        finally
        {
            ResetToIdle();
        }
    }

    // A "no speech" outcome from a capture that never rose above digital silence is not a quiet
    // room, it is a mic that recorded nothing: muted in a meeting, hardware mute switch, taskbar
    // mic mute. Historically this fell through the silent VAD/no-speech discard paths and looked
    // like Scribe simply did nothing. Returns true when the silent-capture error was raised so the
    // caller can skip its own quiet discard.
    private bool RaiseSilentCaptureError(DictationPipelineReport report, string stage)
    {
        if (!_audio.LastCaptureWasSilent)
        {
            return false;
        }

        var device = _audio.LastDeviceName;
        _log.LogWarning("Capture from '{Device}' contained only silence; the microphone is likely muted.",
            device ?? "default device");
        report.Fail(stage, "The capture contained only silence. The microphone is likely muted.");
        RaisePipelineReport(report);
        Error?.Invoke(device is null
            ? "no sound was captured, your microphone may be muted"
            : $"no sound from '{device}', it may be muted");
        return true;
    }

    private void RecordHistory(
        AppSettings settings,
        CapturedAudio audio,
        TranscriptionResult result,
        string text,
        string? targetApp,
        CleanupResult cleanup,
        TimeSpan cleanupDuration)
    {
        try
        {
            var cleanupMs = settings.EnableAiCleanup && cleanup.Outcome != CleanupOutcome.Skipped
                ? (int?)Math.Max(0, (int)cleanupDuration.TotalMilliseconds)
                : null;
            _history.Add(new HistoryEntry(
                Id: 0,
                TimestampUtc: DateTimeOffset.UtcNow,
                Text: text,
                AudioMilliseconds: (int)result.AudioDuration.TotalMilliseconds,
                DecodeMilliseconds: (int)result.DecodeDuration.TotalMilliseconds,
                CleanupMilliseconds: cleanupMs,
                TargetApp: targetApp,
                TranscriptionModelId: result.ModelId),
                settings.StoreAudioHistory ? audio : null);
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
            _captureSettings = null;
            _captureTargetWindow = 0;
            _captureTargetApp = null;
            _captureStartedTimestamp = 0;
            _processingTask = null;
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

    // Guarded raise: the recovery hint is best-effort UI sugar, so a throwing handler must never
    // propagate into the dictation processing path.
    private void RaiseInjectionFailed()
    {
        try
        {
            InjectionFailed?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "An InjectionFailed handler threw.");
        }
    }

    private void RaisePipelineReport(DictationPipelineReport report)
    {
        report.TotalDuration = Stopwatch.GetElapsedTime(report.StartedTimestamp);
        try
        {
            PipelineReported?.Invoke(report);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A pipeline report handler threw.");
        }
    }

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

    private static string? ProcessNameForWindow(nint handle)
    {
        try
        {
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
        _lifetimeCts.Cancel();
        UnsubscribeSilence();
        _audio.CaptureFaulted -= OnCaptureFaulted;
        if (_audio.IsCapturing)
        {
            _audio.RequestStop();
        }
        if (_started)
        {
            _hotkeys.Activated -= OnActivated;
            _hotkeys.Deactivated -= OnDeactivated;
            _hotkeys.Stop();
        }

        Task? processing;
        lock (_gate)
        {
            processing = _processingTask;
        }

        try
        {
            processing?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
            // Normal shutdown cancellation.
        }

        _lifetimeCts.Dispose();
    }

    private sealed record DictationSession(
        AppSettings Settings,
        nint TargetWindow,
        string? TargetApp,
        long StartedTimestamp);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
}

internal sealed class DictationPipelineReport(
    nint targetWindow,
    bool vadEnabled,
    bool cleanupEnabled,
    bool postProcessingEnabled,
    long startedTimestamp)
{
    public nint TargetWindow { get; } = targetWindow;
    public bool VadEnabled { get; } = vadEnabled;
    public bool VadAvailable { get; set; }
    public bool CleanupEnabled { get; } = cleanupEnabled;
    public bool PostProcessingEnabled { get; } = postProcessingEnabled;
    public TimeSpan CaptureDuration { get; set; }
    public TimeSpan SpeechDuration { get; set; }
    public TimeSpan VadDuration { get; set; }
    public TimeSpan DecodeDuration { get; set; }
    public TimeSpan CleanupDuration { get; set; }
    public TimeSpan PostProcessingDuration { get; set; }
    public TimeSpan InjectionDuration { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public double RealTimeFactor { get; set; }
    public string? RawText { get; set; }
    public string? CleanedText { get; set; }
    public CleanupResult? Cleanup { get; set; }
    public TextPostProcessingResult? PostProcessing { get; set; }
    public string? FinalText { get; set; }
    public InjectionResult? Injection { get; set; }
    public string? FailureStage { get; private set; }
    public string? FailureReason { get; private set; }
    internal long StartedTimestamp { get; } = startedTimestamp;

    public void Fail(string stage, string reason)
    {
        FailureStage = stage;
        FailureReason = reason;
    }
}
