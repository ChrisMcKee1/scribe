using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Scribe.Core.Audio;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Hotkeys;
using Scribe.Core.Infrastructure;
using Scribe.Core.Lifecycle;
using Scribe.Core.Models;
using Scribe.Core.Overlay;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.TextInjection;
using Scribe.Core.Transcription;
using Scribe.Core.Vad;
using Scribe.Core.Vocabulary;

namespace Scribe.App.Dictation;

/// <summary>
/// Wires the dictation loop together: hotkey down starts microphone capture; hotkey up stops
/// capture and, on a background thread, runs VAD trimming, transcription, dictionary
/// post-processing and text injection, then queues the result for history, which commits in the
/// background so the next dictation never waits on SQLite. A single state gate prevents
/// overlapping sessions, and the heavy stop→decode→inject work is offloaded so the hotkey
/// consumer thread is never blocked. Live settings are honored per capture and can be swapped at
/// runtime via <see cref="ApplySettings"/>.
/// </summary>
internal sealed class DictationController : IDisposable
{
    private readonly IHotkeyService _hotkeys;
    private readonly IAudioCaptureService _audio;
    private readonly IVadService _vad;
    private readonly ITranscriptionService _transcription;

    // The dictionary pass over the vocabulary generation of the dictation being processed (DictationPostProcessor).
    private readonly DictationPostProcessor _postProcessor;
    private readonly ITextCleanupService _cleanup;
    private readonly ITextInjector _injector;
    private readonly IHistoryWriter _historyWriter;

    // Every dictation takes the publisher's current generation when its recording is admitted and uses that one for
    // its cleanup and its post-processing; saves, library changes and dictionary changes publish newer ones off the
    // dispatcher (plan 3.8, R6).
    private readonly VocabularyPublisher _vocabulary;
    private readonly ICleanupFailureLog _failureLog;
    private readonly LastTranscriptStore _lastTranscript;
    private readonly ISettingsRepository _settingsRepository;
    private readonly ILogger<DictationController> _log;

    // Shutdown waits, bounded so quitting never hangs: long enough for the last dictation to reach a cancellation
    // point (or finish the native call it is in), and for its history write to commit.
    private static readonly TimeSpan ProcessingDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HistoryDrainTimeout = TimeSpan.FromSeconds(5);

    // Every decision about whether something may start (a recording, processing, a timer schedule, an idle release, a
    // duration-ceiling stop) and the shutdown order live here, in Core, where they are tested. It also issues the
    // per-dictation ordinal stamped on every line of one capture's story: a busy log interleaves the controller, the
    // audio service and the out-of-process overlay, and without an id the only way to tell two adjacent dictations apart
    // is by reading timestamps and guessing.
    //
    // It owns the two timers as well. Idle model release: after ReleaseModelsAfterIdleMinutes without a dictation the
    // recognizer and VAD are unloaded, returning their memory (model weights plus whatever high-water the ONNX Runtime
    // arena accumulated) to the OS; re-armed on every return to idle, disarmed the moment a recording starts. Duration
    // ceiling (MaxDictationMinutes): one-shot, armed per recording; a forgotten toggle or stuck key otherwise grows the
    // raw capture ~23 MB/min without bound, and hitting the ceiling ends the dictation through the normal stop path so
    // everything captured still transcribes.
    private readonly DictationLifecycle<CaptureContext> _lifecycle;

    // Replaced wholesale by ApplySettings and only ever read as a snapshot, so a volatile reference is enough.
    private AppSettings _settings = AppSettings.CreateDefault();
    private bool _prepared;
    private bool _started;

    // Tells the user once per episode, not on every press, that the chosen microphone is unavailable and the default is
    // recording; fed every committed microphone choice and every capture that opened.
    private readonly UnavailableMicrophoneNotice _unavailableMicrophone = new();

    // The cleanup configuration most recently handed to the service, and the announcement waiting
    // for it to become Ready. Both are touched from the UI thread and from the service's status
    // callback, so they are guarded rather than relying on the caller's thread.
    private readonly object _announceGate = new();
    private CleanupOptions? _announcedCleanupOptions;
    private string? _pendingAnnouncement;

    public DictationController(
        IHotkeyService hotkeys,
        IAudioCaptureService audio,
        IVadService vad,
        ITranscriptionService transcription,
        ITextPostProcessor postProcessor,
        ITextCleanupService cleanup,
        ITextInjector injector,
        IHistoryWriter historyWriter,
        VocabularyPublisher vocabulary,
        ICleanupFailureLog failureLog,
        LastTranscriptStore lastTranscript,
        ISettingsRepository settingsRepository,
        ILogger<DictationController> log)
    {
        _hotkeys = hotkeys;
        _audio = audio;
        _vad = vad;
        _transcription = transcription;
        _postProcessor = new DictationPostProcessor(postProcessor);
        _cleanup = cleanup;
        _injector = injector;
        _historyWriter = historyWriter;
        _vocabulary = vocabulary;
        _failureLog = failureLog;
        _lastTranscript = lastTranscript;
        _settingsRepository = settingsRepository;
        _log = log;
        _lifecycle = new DictationLifecycle<CaptureContext>(ReleaseIdleModels, OnDurationLimitReached);
    }

    /// <summary>
    /// Raised whenever what the shell shows for the dictation loop changes, on whichever background thread made the change
    /// (the keyboard hook's dispatch thread, the audio capture thread, a timer, processing) or on the UI thread for a pause.
    /// Handlers must never block: the UI thread waits for several of those threads at shutdown. Two changes can arrive in
    /// the opposite order to the one they were made in, so a handler that shows them keeps only the one with the highest
    /// <see cref="DictationStateChange.Revision"/> (see <see cref="PresentationRelay{T}"/>).
    /// </summary>
    public event Action<DictationStateChange>? StateChanged;

    /// <summary>
    /// Raised (on a background thread) after the idle timeout unloaded the speech models and compacted the heap.
    /// Purely informational; the overlay helper owns its own idle lifetime and must not be suspended from here.
    /// The models reload on demand at the next dictation. The event is skipped when a dictation began while the
    /// release was running, but it can still race the very start of one, so no consumer may treat it as proof that
    /// the controller is idle.
    /// </summary>
    public event Action? ModelsReleased;

    /// <summary>Raised when a capture or transcription step fails.</summary>
    public event Action<string>? Error;

    /// <summary>
    /// Raised for a recoverable capture warning while recording continues. Carries the revision of the Recording change it
    /// belongs to, taken under the lifecycle's gate, so the pill shows it only while that recording is still what it shows;
    /// 0 when that recording was already over, when only the tray notice still applies.
    /// </summary>
    public event Action<DictationWarning>? Warning;

    /// <summary>
    /// Raised after a capture is dictated and inserted, with the text as history keeps it: without the space the target
    /// may have been given after it (<see cref="AppSettings.AddSpaceAfterDictation"/>).
    /// </summary>
    public event Action<string>? Dictated;

    /// <summary>
    /// Raised after the real dictation pipeline completes or fails, so the Playground can show
    /// raw recognition, final text, replacements, and per-stage timings for its focused text box.
    /// </summary>
    public event Action<DictationPipelineReport>? PipelineReported;

    /// <summary>
    /// Raised (on a background thread) when text injection failed but the finalized transcript was
    /// preserved in <see cref="LastTranscriptStore"/>, so the shell can point the user at the tray
    /// recovery path instead of leaving the preserved text undiscoverable.
    /// </summary>
    public event Action? InjectionFailed;

    /// <summary>
    /// Raised after a settings save changes the AI cleanup configuration and the new provider is
    /// genuinely serving requests. Deliberately not raised at startup: the swap is what users
    /// cannot otherwise observe, whereas a toast on every launch would be noise.
    /// </summary>
    public event Action<string>? CleanupProviderChanged;

    /// <summary>
    /// True while dictation is suspended (the push-to-talk bindings pass through to other apps and never start a
    /// dictation).
    /// </summary>
    public bool IsPaused => _lifecycle.IsPaused;

    /// <summary>
    /// True while a dictation is recording or processing. Read from the lifecycle itself rather than tracked from
    /// <see cref="StateChanged"/>, whose handlers can run out of order when two threads raise it back to back.
    /// </summary>
    public bool IsDictationInFlight => _lifecycle.Phase != DictationPhase.Idle;

    /// <summary>True once shutdown has begun (<see cref="BeginShutdown"/>); nothing new starts and no state is raised.</summary>
    public bool IsClosing => _lifecycle.IsClosing;

    /// <summary>The settings currently driving the loop.</summary>
    public AppSettings CurrentSettings => Volatile.Read(ref _settings);

    /// <summary>
    /// Loads the persisted settings and builds the first vocabulary generation off the dispatcher (the library source's
    /// first read can load a cold catalog), completing once it is published. The app awaits this before <see cref="Start"/>
    /// installs the hotkey, so the first dictation after startup never runs without its vocabulary, and nothing waits on
    /// the build synchronously. Throws <see cref="TimeoutException"/> when the first generation is not built within
    /// <see cref="VocabularyPublisher.StartupDeadline"/>, which ends startup with its failure notice. A first build that
    /// cannot read the dictionary throws nothing and leaves dictation without vocabulary until the next change builds one,
    /// as before; the publisher logs it.
    /// </summary>
    public async Task PrepareAsync()
    {
        if (_prepared)
        {
            return;
        }

        var settings = _settingsRepository.Load();
        Volatile.Write(ref _settings, settings);
        _unavailableMicrophone.SelectionCommitted(settings.InputDeviceId);
        _postProcessor.ReloadSnippets();
        await _vocabulary.StartAsync();
        _prepared = true;
    }

    /// <summary>
    /// Applies the hotkey binding to the settings <see cref="PrepareAsync"/> loaded, or any applied since, configures AI
    /// cleanup and installs the hook. Refuses to run before <see cref="PrepareAsync"/> has completed.
    /// </summary>
    public void Start()
    {
        if (_started) return;
        if (!_prepared)
        {
            throw new InvalidOperationException("The first vocabulary generation must be prepared before dictation starts.");
        }

        var settings = CurrentSettings;
        var startupOptions = BuildCleanupOptions(settings);
        lock (_announceGate)
        {
            // Seeded so the first save is measured against the startup configuration. Startup
            // itself is deliberately silent: a toast on every launch would be noise, and the swap
            // is the only part a user cannot otherwise observe.
            _announcedCleanupOptions = startupOptions;
        }

        _cleanup.StatusChanged += OnCleanupStatusChangedForAnnouncement;
        _cleanup.Configure(startupOptions);

        // Subscribed for the controller's lifetime rather than per recording: silence auto-stop is decided per recording
        // by the lifecycle, which feeds a tracker only while the recording it belongs to is live.
        _audio.LevelChanged += OnLevelForSilence;

        _hotkeys.UpdateBindings(settings.Hotkey, settings.DictationOnlyHotkey);
        _hotkeys.Activated += OnActivated;
        _hotkeys.Deactivated += OnDeactivated;
        _hotkeys.Start();
        _started = true;
        _lifecycle.Start(IdleReleaseDelay(settings));

        _log.LogInformation("Dictation controller started; binding = {Binding}.", HotkeyText.Describe(settings.Hotkey));
    }

    /// <summary>
    /// (Re)arms the idle-release countdown from the live settings when the loop is idle. A zero or
    /// negative setting disarms it entirely, keeping the models resident forever.
    /// </summary>
    private void RescheduleIdleRelease() => _lifecycle.RescheduleIdleRelease(IdleReleaseDelay(CurrentSettings));

    private static TimeSpan IdleReleaseDelay(AppSettings settings) =>
        settings.ReleaseModelsAfterIdleMinutes > 0
            ? TimeSpan.FromMinutes(settings.ReleaseModelsAfterIdleMinutes)
            : Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Unloads the recognizer and VAD if the controller is genuinely idle, drops the capture service's
    /// retained working buffer, then compacts the LOH with the one blocking gen-2 collection that
    /// actually returns large buffers to the OS (a background gen-2 never honors CompactOnce). Skips
    /// silently when a dictation is recording or still processing; the timer re-arms when that work
    /// returns to idle.
    /// </summary>
    /// <remarks>
    /// The release is claimed by the lifecycle and every step runs outside its gate (see
    /// <see cref="IdleModelRelease"/>). Unloading stays safe if a dictation starts meanwhile, because the
    /// speech services load and use their models under their own gates, and so does dropping the capture
    /// buffer, because the capture service never retains a buffer a capture is using. The buffer goes on
    /// every claimed release, with or without resident models, and before the compaction so that collection
    /// returns it too. The compaction and the announcement are skipped when a recording began after the
    /// claim; what remains is activity that begins while the collection itself is running, which that
    /// collection briefly pauses like every other managed thread.
    /// </remarks>
    private void ReleaseIdleModels()
    {
        try
        {
            var outcome = _lifecycle.RunIdleRelease(
                anythingResident: () => _transcription.IsReady || _vad.IsAvailable,
                unload: () =>
                {
                    _transcription.Unload();
                    _vad.Unload();
                },
                compact: () =>
                {
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                },
                announce: () =>
                {
                    try
                    {
                        ModelsReleased?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning("A ModelsReleased handler failed: {Failure}", FailureShape.DescribeWithStack(ex));
                    }
                },
                releaseRetained: () =>
                {
                    var bytes = _audio.ReleaseRetainedBuffers();
                    if (bytes > 0)
                    {
                        TryLog(log => log.LogDebug("Released a {Bytes}-byte idle capture buffer.", bytes));
                    }
                });

            switch (outcome)
            {
                case IdleReleaseOutcome.Released:
                    _log.LogInformation(
                        "Speech models released after {Minutes} idle minutes; they reload on the next dictation.",
                        CurrentSettings.ReleaseModelsAfterIdleMinutes);
                    break;

                // Closing also invalidates a claim; nothing is worth reporting or reloading then.
                case IdleReleaseOutcome.UnloadedThenActivityResumed when !_lifecycle.IsClosing:
                    // The dictation that interrupted the release may have found the models still resident when it
                    // started, in which case nothing began reloading them. Reload now, overlapping its recording as
                    // an activation would have; this is a no-op if the activation's own reload got there first.
                    _log.LogInformation(
                        "Speech models were unloaded as a dictation started; the heap compaction was skipped and the " +
                        "models are reloading for it.");
                    StartBackgroundModelReload();
                    break;

                case IdleReleaseOutcome.CompactedThenActivityResumed when !_lifecycle.IsClosing:
                    _log.LogInformation(
                        "Speech models released as a dictation started; the release was not announced.");
                    break;
            }
        }
        catch (Exception ex)
        {
            // Releasing memory is an optimization; it must never destabilize dictation.
            TryLog(log => log.LogWarning("Idle model release failed: {Failure}", FailureShape.DescribeWithStack(ex)));
        }
    }

    // Best-effort model reload on a pool thread. Transcribe reports the real failure with full context and loads on
    // demand anyway, so a failure here must never surface its own error path.
    private void StartBackgroundModelReload()
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (_lifecycle.IsClosing)
                {
                    return;
                }

                _vad.Initialize();
                _transcription.Initialize();
            }
            catch (Exception ex)
            {
                TryLog(log => log.LogDebug(
                    "Background model reload failed; Transcribe will retry: {Failure}", FailureShape.DescribeWithStack(ex)));
            }
        });
    }

    /// <summary>
    /// Replaces the live settings with settings as stored: the settings window's document right after its save stored
    /// it, the stored settings after a dictionary change the window stored on its own (<see cref="StoredSettingsReapply"/>),
    /// and the tray's change once it is stored. Re-binds the hotkey, reloads the snippets and asks for a new vocabulary
    /// generation, built off the dispatcher, so changes take effect on the next capture without a restart. Returns that
    /// request's answer: it completes once a generation built from the dictionary and libraries as stored now is what the
    /// next dictation is admitted with, or says it could not be built. A caller that reports a stored change as in effect
    /// awaits it first, and never waits on it synchronously. Decode-thread changes still require a restart (the recognizer
    /// is warm-loaded).
    /// </summary>
    public Task<VocabularyRefresh> ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Volatile.Write(ref _settings, settings.Clone());

        // A Settings save and a tray choice both land here; only a different microphone ends an unavailable episode.
        _unavailableMicrophone.SelectionCommitted(settings.InputDeviceId);

        if (_hotkeys.Binding != settings.Hotkey ||
            _hotkeys.DictationOnlyBinding != settings.DictationOnlyHotkey)
        {
            _hotkeys.UpdateBindings(settings.Hotkey, settings.DictationOnlyHotkey);
        }
        _postProcessor.ReloadSnippets();
        var vocabulary = _vocabulary.RefreshAsync();
        ReconfigureCleanup(settings);
        RescheduleIdleRelease(); // pick up a changed ReleaseModelsAfterIdleMinutes immediately
        _log.LogInformation("Applied updated settings; binding = {Binding}.", HotkeyText.Describe(settings.Hotkey));
        return vocabulary;
    }

    /// <summary>
    /// Puts a dictionary change stored outside a settings save into effect: quick add and learning from history, and the
    /// settings window's own dictionary change when the stored settings cannot be applied (<see cref="StoredSettingsReapply"/>).
    /// The snippets reload, and a new vocabulary generation is built from the dictionary as stored and the library
    /// vocabulary in use, which a dictionary-only reload never changes. No settings are applied, so the provider, the
    /// hotkeys, the libraries and everything else stay as they are: the stored document is not read again, since the
    /// defaults standing in for it are not the user's. Returns the generation request's answer, as
    /// <see cref="ApplySettings"/> does. Safe off the dispatcher.
    /// </summary>
    public Task<VocabularyRefresh> ReloadVocabulary()
    {
        _postProcessor.ReloadSnippets();
        var vocabulary = _vocabulary.RefreshAsync();
        _log.LogInformation("Reloaded the dictionary on the settings in use; no settings were applied.");
        return vocabulary;
    }

    // Hands cleanup the configuration these settings describe, with the glossary built from the dictionary as it is now.
    private void ReconfigureCleanup(AppSettings settings)
    {
        var previous = _announcedCleanupOptions;
        var next = BuildCleanupOptions(settings);
        _cleanup.Configure(next);
        AnnounceCleanupChange(previous, next);
    }

    /// <summary>
    /// Records the reconfigured provider and announces it once it is genuinely serving.
    /// <para>
    /// Waiting for Ready matters: the provider swap is asynchronous, so announcing at save time
    /// would claim success before the model had loaded and would still say "running" when it went
    /// on to fail. A failure is left to the existing status UI rather than being reported here as
    /// though it had worked.
    /// </para>
    /// </summary>
    private void AnnounceCleanupChange(CleanupOptions? previous, CleanupOptions next)
    {
        // Value equality, not reference: every save builds a fresh record, so an unrelated save
        // (a hotkey change) while a swap is still initializing must not be read as a new
        // configuration and cancel the announcement that is still pending. A change confined to
        // the prompt (a dictionary term, a library, the writing style) is not a new configuration
        // either: the service rebuilds the agent in place, so there is no swap to announce.
        var changed = previous is not null && !previous.MatchesIgnoringPrompt(next);

        lock (_announceGate)
        {
            _announcedCleanupOptions = next;
            if (changed)
            {
                // A new configuration replaces whatever was pending, with nothing when it has
                // nothing to announce on Ready, so a superseded configuration cannot fire later.
                _pendingAnnouncement = CleanupActivationMessage.ForReady(next);
            }
        }

        if (!changed)
        {
            return;
        }

        if (!next.Enabled)
        {
            if (CleanupActivationMessage.ForDisabled(next) is { } offMessage)
            {
                CleanupProviderChanged?.Invoke(offMessage);
            }

            return;
        }

        // Ready can arrive before this method finishes (a prompt-only edit, or a provider that was
        // already serving), so the pending announcement is flushed here as well as from the event.
        FlushCleanupAnnouncement();
    }

    /// <summary>
    /// Single, permanent status subscription. A per-save closure was tried and leaked: if the
    /// service never reached a terminal status the handler stayed attached forever, and a Ready
    /// raised between the status check and the subscription was missed entirely.
    /// </summary>
    private void OnCleanupStatusChangedForAnnouncement()
    {
        switch (_cleanup.Status)
        {
            case CleanupStatus.Ready:
                FlushCleanupAnnouncement();
                break;

            // The reconfigured provider failed. The status UI already explains why, and announcing
            // "is running" for something that just failed would be worse than staying quiet.
            case CleanupStatus.Unavailable:
            case CleanupStatus.Disabled:
                lock (_announceGate)
                {
                    _pendingAnnouncement = null;
                }

                break;
        }
    }

    private void FlushCleanupAnnouncement()
    {
        if (_cleanup.Status != CleanupStatus.Ready)
        {
            return;
        }

        string message;
        lock (_announceGate)
        {
            if (_pendingAnnouncement is null)
            {
                return;
            }

            message = _pendingAnnouncement;
            _pendingAnnouncement = null;
        }

        CleanupProviderChanged?.Invoke(message);
    }

    /// <summary>
    /// Routes the settings window's binding-capture state to the hook. While capture is on, the
    /// push-to-talk key types into the capture box instead of starting a dictation, and any
    /// dictation already in flight is deactivated by the hook before capture begins.
    /// </summary>
    public void SetHotkeyCaptureMode(bool enabled) => _hotkeys.SetCaptureMode(enabled);

    /// <summary>
    /// Maps persisted AppSettings into the cleanup service's provider-agnostic options. They carry no glossary: each
    /// dictation's requests carry the glossary of the vocabulary generation it was admitted with
    /// (<see cref="ITextCleanupService.Admit"/>), judged by that generation's library scope, so a vocabulary change is
    /// never a configuration change and a request never carries vocabulary no admission stands behind.
    /// </summary>
    private CleanupOptions BuildCleanupOptions(AppSettings settings) => new(
        settings.EnableAiCleanup,
        settings.AiCleanupProvider,
        settings.AiCleanupModel,
        settings.AiCleanupAzureEndpoint,
        settings.AiCleanupAzureDeployment,
        settings.AiCleanupAzureApiKey,
        // A service principal names its own tenant, so it wins outright. Deriving the tenant from the
        // selected subscription is a CLI-only convenience, and applying it here would authenticate
        // the app registration against the wrong directory.
        settings.AiCleanupAzureAuthMode == AzureAuthMode.ServicePrincipal
            ? settings.AiCleanupAzureTenantId
            : AzureSubscriptionSelection.ResolveTenantId(
                settings.AiCleanupAzureSubscriptionId,
                settings.AiCleanupAzureSubscriptionTenantId,
                settings.AiCleanupAzureTenantId),
        settings.AiCleanupWritingStyle,
        Glossary: null,
        settings.AiCleanupCustomEndpoint,
        settings.AiCleanupCustomModel,
        settings.AiCleanupCustomApiKey,
        settings.AiCleanupPromptStyle,
        settings.AiCleanupFrontierPrompt,
        settings.AiCleanupLocalPrompt,
        settings.AiCleanupAzureSubscriptionId,
        settings.AiCleanupAzureAuthMode,
        settings.AiCleanupAzureClientId,
        settings.AiCleanupAzureClientSecret,
        settings.AiCleanupCopilotModel);

    /// <summary>Suspends or resumes dictation without removing the keyboard hook.</summary>
    public void SetPaused(bool paused)
    {
        var change = _lifecycle.SetPaused(paused);
        if (!change.Changed) return;

        // Outside the lifecycle's gate, so two pause changes can reach the hook in the opposite order; the number the
        // gate gave this change lets the hook keep whichever was made last.
        _hotkeys.SetPaused(paused, change.Sequence);
        var stopRecording = paused && change.WasRecording;

        _log.LogInformation("Dictation {State}.", paused ? "paused" : "resumed");

        // Pausing is the user saying "Scribe should stand down": release the models right away
        // rather than waiting out the idle window. Resume just re-arms the countdown; the next
        // dictation reloads on demand.
        if (paused && !stopRecording)
        {
            _ = Task.Run(ReleaseIdleModels);
        }
        else if (!paused)
        {
            RescheduleIdleRelease();
        }

        if (stopRecording)
        {
            StopAndProcess(DictationStopReason.Paused);
        }
        else if (change.Presentation is { } shown)
        {
            // Only an idle loop shows the change now; one landing mid-dictation shows when that dictation returns to idle.
            Raise(shown);
        }
    }

    private void OnActivated(object? sender, HotkeyTriggerEventArgs e)
    {
        // Resolved before the lifecycle's gate is taken; the factory below only reads cheap platform state and the current
        // vocabulary generation under it, so the target window it records is the one focused at the exact moment the
        // recording began. A press turned away because the previous dictation is still processing releases its own latch
        // (see DictationStartPolicy), so a toggle's next tap starts a dictation instead of ending one that never began.
        var current = CurrentSettings;
        var activation = DictationStartPolicy.BeginRecording(
            _lifecycle,
            () =>
            {
                var targetWindow = GetForegroundWindow();

                // The vocabulary generation is taken here, at the recording's admission, with its settings: a lock-free
                // read of the newest complete generation, so a Save while this dictation runs changes nothing it uses.
                return new CaptureContext(
                    DictationCaptureSettingsResolver.Resolve(current, e.Trigger),
                    targetWindow,
                    ProcessNameForWindow(targetWindow),
                    Stopwatch.GetTimestamp(),
                    e.Activation,
                    _vocabulary.Current);
            },
            () => _hotkeys.CancelToggle(e.Activation));

        if (activation.Capture is not { } capture)
        {
            // Logged outside the gate, so a slow log write never holds up the next hook decision.
            switch (activation.Decision)
            {
                case ActivationDecision.Paused:
                    _log.LogDebug("Hotkey activated while paused; ignoring.");
                    break;

                case ActivationDecision.StillProcessing:
                    _log.LogDebug(
                        "#{Id} hotkey activated while processing; ignoring ({Count} rejected so far).",
                        activation.DictationId, activation.RejectedWhileProcessing);
                    break;

                case ActivationDecision.AlreadyRecording:
                    _log.LogDebug("Hotkey activated while {State}; ignoring.", DictationState.Recording);
                    break;
            }

            return; // Closing needs no line: shutting down, nothing new starts
        }

        var id = activation.DictationId;
        var settings = capture.Settings;

        // Which microphone this capture asks for, under which committed choice, for the unavailable-microphone notice.
        var microphone = _unavailableMicrophone.Begin(settings.InputDeviceId);

        // If the idle release ran, start reloading the models NOW so the warm-up overlaps with the
        // recording instead of stacking onto decode latency after the key is released. Transcribe
        // falls back to a synchronous load if the user finishes speaking first.
        if (!_transcription.IsReady)
        {
            StartBackgroundModelReload();
        }

        try
        {
            _audio.CaptureFaulted += OnCaptureFaulted;

            // Hands the opened capture to its one owner: this recording while it is live; its processing when a stop (a
            // pause, a fault) admitted it meanwhile; or, when shutdown began first, nobody, and the open stops it again. An
            // open that comes after this recording's stop reached the capture service opens nothing at all (see
            // RecordingCapture). The capture service also guards itself against its own disposal: that waits, with a bound,
            // for an open in progress, and refuses a Start that reaches it once disposal began (handled below).
            var open = RecordingCapture.Open(_lifecycle, _audio, id, settings.InputDeviceId);

            // Opening the endpoint blocks this path, and nothing is recorded until it returns. A
            // support log caught a USB speakerphone taking 5.19 s on the first press after launch:
            // the user held the key, saw no overlay, gave up, and the dictation came back as "no
            // audio from your microphone". The cold-open cost is invisible everywhere else.
            if (open.OpenDuration.TotalMilliseconds > 400)
            {
                _log.LogWarning(
                    "#{Id} the microphone '{Device}' took {Ms} ms to start. Nothing spoken before it " +
                    "opened was recorded.",
                    id, _audio.LastDeviceName ?? "unknown", (long)open.OpenDuration.TotalMilliseconds);
            }

            // Every open that reached a microphone says how the choice fared, whoever ended up owning the capture: a chosen
            // microphone that opened ends an episode even when shutdown reclaimed it, and a fallback a pause or a fault took
            // to processing is still heard about (see UnavailableMicrophoneNotice).
            var unavailable = _unavailableMicrophone.ReportOpen(microphone, open);

            if (open.Presentation is not { } shown)
            {
                _audio.CaptureFaulted -= OnCaptureFaulted;
                LogOpenWithoutRecording(id, open);
                AnnounceUnavailableMicrophone(unavailable, id, settings, open);
                return;
            }

            // Everything a "my dictation cut out" report needs to be answerable: which press, which
            // mode (a hold that ends early and a toggle that auto-stops look identical to the user
            // but have completely different causes), and which app was focused. The mode and key are
            // the binding that fired: the dictation-only binding has a mode of its own. Whether that app is a Remote
            // Desktop or virtual machine client, whose typing is paced for the remote session (TypingPace).
            var binding = CaptureTriggerBinding.For(settings, e.Trigger);
            _log.LogInformation(
                "#{Id} recording started: trigger={Trigger} mode={Mode} key='{Key}' device='{Device}' " +
                "target={App} remote={Remote} autoStopOnSilence={AutoStop} vad={Vad} cleanup={Cleanup}",
                id,
                e.Trigger,
                (object?)binding?.Mode ?? "unknown",
                binding is null ? "custom" : HotkeyText.Describe(binding),
                _audio.LastDeviceName ?? "unknown",
                capture.TargetApp ?? "unknown",
                RemoteClientProcesses.IsRemoteClient(capture.TargetApp),
                settings.AutoStopOnSilence,
                settings.UseVoiceActivityDetection,
                settings.EnableAiCleanup);

            Raise(shown);

            // A chosen microphone that is unplugged, disabled or gone records from the Windows default instead (the
            // capture service falls back by itself). Announced after the Recording change, so the pill can show it.
            AnnounceUnavailableMicrophone(unavailable, id, settings, open);

            // Muted endpoints (headset mute, Win11 taskbar mic mute during a meeting) still record,
            // they just record silence. Warn immediately so the user can unmute mid-dictation
            // instead of speaking into a dead capture; recording continues in case they do.
            if (_audio.LastDeviceMuted)
            {
                _log.LogWarning("Recording started on a muted microphone.");
                RaiseWarning("microphone is muted, unmute it to dictate", id, "Microphone muted");
            }

            // Only for a toggle, judged by the binding that fired, and attached only while this recording is still the
            // live one: a fault or a pause can end it between its microphone opening and here, and a tracker attached
            // after that would be fed the next recording's levels. Whatever ends this recording drops its tracker.
            if (CaptureTriggerBinding.StopsOnSilence(settings, e.Trigger))
            {
                _lifecycle.TryAttachSilenceTracker(id, new SilenceAutoStopTracker(Environment.TickCount64));
            }

            // Armed only if this recording is still the live one: a fault or an auto-stop can end it while
            // this path is still opening the device.
            if (settings.MaxDictationMinutes > 0)
            {
                _lifecycle.ArmDurationLimit(id, TimeSpan.FromMinutes(settings.MaxDictationMinutes));
            }
        }
        catch (ObjectDisposedException) when (_lifecycle.IsClosing)
        {
            // Shutdown had already disposed the capture service when this activation reached it, so nothing was opened.
            // Nothing is left to report to anybody.
            _audio.CaptureFaulted -= OnCaptureFaulted;
            TryLog(log => log.LogInformation("#{Id} the microphone was not started: shutdown had already begun.", id));
            AbandonRecording(id);
        }
        catch (Exception ex)
        {
            _audio.CaptureFaulted -= OnCaptureFaulted;

            // Guarded: a log failure here would skip the reset and leave the controller Recording for good.
            TryLog(log => log.LogError("Failed to start audio capture: {Failure}", FailureShape.DescribeWithStack(ex)));
            const string failure = "microphone unavailable";
            AbandonRecording(id, PillOutcome.Of(insertion: null, cleanupRequested: false, cleanup: null, failure));
            RaiseError(failure);
        }
    }

    // A recording that never got going goes back to idle, unless a pause already admitted it to processing while its
    // device was opening: that processing owns the way back to idle, and returning here as well could end the next one.
    // One whose microphone would not open tells the pill that nothing was typed, with that change.
    private void AbandonRecording(long id, PillOutcome? outcome = null)
    {
        if (_lifecycle.TryAbandonRecording(id, IdleReleaseDelay(CurrentSettings)) is { } idle)
        {
            Raise(idle.Presentation, outcome: outcome);
            LogOutcome(id, outcome);
        }
    }

    // Shape only: which of the owners took the capture, and how much audio a reclaim threw away.
    private void LogOpenWithoutRecording(long id, RecordingOpen open)
    {
        switch (open.Outcome)
        {
            case RecordingOpenOutcome.LeftToProcessing:
                TryLog(log => log.LogInformation(
                    "#{Id} the recording was stopped as its microphone opened; its processing takes the capture.", id));
                break;

            case RecordingOpenOutcome.NotOpened:
                TryLog(log => log.LogInformation(
                    "#{Id} the recording was stopped before its microphone opened, so none was opened.", id));
                break;

            case RecordingOpenOutcome.Reclaimed when open.ReclaimFailure is { } failure:
                TryLog(log => log.LogWarning(
                    "#{Id} shutdown began as the microphone opened, and stopping it again failed ({Failure}).",
                    id,
                    FailureShape.Describe(failure)));
                break;

            case RecordingOpenOutcome.Reclaimed:
                TryLog(log => log.LogInformation(
                    "#{Id} shutdown began as the microphone opened; it was stopped again and {Ms} ms of audio discarded.",
                    id, (long)open.Discarded.TotalMilliseconds));
                break;
        }
    }

    // A desktop switch, or a mouse hook Windows had removed, ends a recording the way its binding would have (see
    // HotkeyEngine.OnDesktopSwitch and OnMouseHookLost); it goes the same way as a release from here on, and only its
    // logged reason differs.
    private void OnDeactivated(object? sender, HotkeyTriggerEventArgs e) =>
        StopAndProcess(e.Deactivation switch
        {
            HotkeyDeactivation.DesktopSwitch => DictationStopReason.DesktopSwitch,
            HotkeyDeactivation.MouseHookLost => DictationStopReason.MouseHookLost,
            _ => DictationStopReason.HotkeyReleased,
        });

    private void OnCaptureFaulted(object? sender, Exception error)
    {
        _log.LogError(
            "#{Id} the active microphone stopped unexpectedly: {Failure}",
            _lifecycle.CurrentDictationId,
            FailureShape.DescribeWithStack(error));
        RaiseError("microphone disconnected");
        StopAndProcess(DictationStopReason.MicrophoneFault);
    }

    // Fired on the audio capture thread for every level sample of every capture. Only the live recording's own tracker is
    // ever fed (see DictationLifecycle.UpdateSilence), and the stop it asks for names that recording, so it can never end
    // a later one.
    private void OnLevelForSilence(object? sender, float level)
    {
        if (_lifecycle.UpdateSilence(level, Environment.TickCount64) is not { } stop)
        {
            return;
        }

        var tracker = stop.Tracker;

        // Which of the tracker's two rules fired changes the advice completely: "you paused and it
        // ended" is working as designed, while "it never heard you at all" is a device or gain
        // problem that reads to the user as an unexplained cut-off a few seconds in. The noise floor
        // and the voice threshold it implied say which: a floor that rose with the room, or a signal
        // that never came near the threshold.
        if (tracker.HeardSpeech)
        {
            _log.LogInformation(
                "#{Id} silence auto-stop: the speaker went quiet (peak level {Peak:F4}; at the stop, noise floor " +
                "{NoiseFloor:F4}, voice threshold {VoiceThreshold:F4}).",
                stop.DictationId, tracker.PeakLevel, tracker.NoiseFloor, tracker.VoiceThreshold);
        }
        else
        {
            _log.LogWarning(
                "#{Id} silence auto-stop: no speech was ever detected on '{Device}' (peak level {Peak:F4}; at the " +
                "stop, noise floor {NoiseFloor:F4}, voice threshold {VoiceThreshold:F4}). Recording ended on the " +
                "lead-in limit, not on anything the user did.",
                stop.DictationId, _audio.LastDeviceName ?? "unknown", tracker.PeakLevel,
                tracker.NoiseFloor, tracker.VoiceThreshold);
        }

        // The shared stop path releases the hook's toggle for this stop (see DictationStopPolicy), so the next press starts
        // a new dictation rather than being swallowed as the toggle-off of this one.
        StopAndProcess(DictationStopReason.SilenceAutoStop, stop.DictationId);
    }

    // Fired on a timer thread when a recording has run for the full MaxDictationMinutes. Ends the
    // dictation through the same path as the silence auto-stop, so the capture is transcribed and
    // injected normally; only the trigger differs.
    private void OnDurationLimitReached()
    {
        try
        {
            // A tick can arrive after shutdown began (a timer queues callbacks to the pool and disposal does not wait for
            // them), after the recording it was armed for has ended, or queued for an earlier recording just as the next
            // one armed the same ceiling. The lifecycle refuses all three, the last because the live recording has not
            // run for its ceiling yet.
            if (_lifecycle.TryAcceptDurationLimit() is not { } id)
            {
                return;
            }

            var minutes = _lifecycle.CurrentCapture?.Settings.MaxDictationMinutes ?? CurrentSettings.MaxDictationMinutes;
            _log.LogWarning(
                "#{Id} recording reached the {Minutes}-minute ceiling; stopping and transcribing. " +
                "A forgotten toggle looks exactly like this.",
                id, minutes);

            RaiseWarning($"dictation hit the {minutes} minute limit and was transcribed", id, pillText: null);

            StopAndProcess(DictationStopReason.DurationLimit, id);
        }
        catch (Exception ex)
        {
            // An exception on a timer thread terminates the process.
            TryLog(log => log.LogWarning(
                "The dictation duration limit could not stop the recording ({Failure}).", FailureShape.Describe(ex)));
        }
    }

    /// <summary>
    /// Shared stop path for the hotkey release/toggle-off, the silence auto-stop, a capture fault,
    /// pause, the duration ceiling and a desktop switch. The reason is logged with the hold
    /// duration, because "it stopped after about ten seconds" is the single most common way a
    /// dictation problem gets reported and the causes are indistinguishable from the outside.
    /// Once a stop Scribe makes itself is admitted, it releases the hotkey latch of the press that
    /// started this recording, and no other (see <see cref="DictationStopPolicy.BeginStop"/>), so the
    /// next press starts a new dictation instead of being swallowed as the toggle-off of this one.
    /// </summary>
    /// <param name="expectedId">When set, only this dictation may be stopped; a stop meant for an earlier one is ignored.</param>
    private void StopAndProcess(DictationStopReason reason, long expectedId = 0)
    {
        // Only the stop that actually ends the live recording is admitted, and only that one disarms its duration
        // ceiling, so a late or redundant stop can never cancel the ceiling of a recording that started after it. Nor can
        // it touch the hotkey: only an admitted stop releases, and only the press this recording kept.
        var stop = DictationStopPolicy.BeginStop(
            _lifecycle,
            reason,
            expectedId,
            admitted => _hotkeys.CancelToggle(admitted.HotkeyActivation),
            out var releaseFailure);
        if (stop.Admission is not { } admission)
        {
            // A redundant stop (both the hook release and a fault racing to end the same
            // capture) is normal and harmless. Logged at Debug so the reason is still visible
            // when chasing a double-stop, without a Recording line every user sees.
            _log.LogDebug(
                "#{Id} stop ignored while {State}: reason={Reason}", stop.CurrentDictationId, stop.ObservedPhase, reason);
            return;
        }

        var capture = admission.Capture;
        var session = new DictationSession(
            admission.DictationId,
            capture.Settings,
            capture.TargetWindow,
            capture.TargetApp,
            capture.StartedTimestamp,
            reason,
            capture.Vocabulary);

        // Everything from here to the hand-off is guarded: the admission is ended only by the processing task, so a
        // throw before that task starts would leave the controller Processing for good.
        TryLog(log => log.LogInformation(
            "#{Id} recording stopped after {Held:F2}s of hold: reason={Reason}",
            session.Id,
            Stopwatch.GetElapsedTime(session.StartedTimestamp).TotalSeconds,
            reason));

        if (releaseFailure is { } failure)
        {
            TryLog(log => log.LogWarning(
                "#{Id} releasing the hotkey for this stop failed ({Failure}).", session.Id, FailureShape.Describe(failure)));
        }

        try
        {
            // The lifecycle dropped this recording's silence tracking with the admission, as it disarmed the ceiling:
            // only the stop that ends a recording does either. Tagged with the recording, so it only ever stops that
            // recording's capture, and an open for it that has not reached the capture service yet opens nothing.
            _audio.CaptureFaulted -= OnCaptureFaulted;
            _audio.RequestStop(session.Id);
        }
        catch (Exception ex)
        {
            // Processing still runs (its Stop call finishes the capture), so the admission is always ended.
            TryLog(log => log.LogWarning(
                "#{Id} the microphone did not accept the stop request ({Failure}).", session.Id, FailureShape.Describe(ex)));
        }

        // Announced before the processing starts, which can queue its failure flash at once: the shell's one queue then
        // keeps Processing ahead of that flash, and showing Processing after it would clear the flash's hold (see
        // ProcessingHandOff). Raising never waits for the UI thread, so the processing is never held up by it.
        ProcessingHandOff.Run(
            announce: () => Raise(stop.Presentation, capture.Settings.EnableAiCleanup),
            start: () => _ = Task.Run(() => RunProcessingAsync(session, admission)));
    }

    private async Task RunProcessingAsync(DictationSession session, ProcessingAdmission<CaptureContext> admission)
    {
        var faulted = false;
        try
        {
            await ProcessAsync(session, admission.Lifetime).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ProcessAsync handles every failure it expects, so anything arriving here is a defect. It is logged and
            // counted, never rethrown: a faulted processing task once turned an ordinary quit into an exception inside
            // the host's teardown.
            faulted = true;
            TryLog(log => log.LogError(
                "#{Id} dictation processing ended with an unexpected fault: {Failure}",
                session.Id,
                FailureShape.DescribeWithStack(ex)));
        }
        finally
        {
            // The very last step, after ResetToIdle has published Idle and re-armed the idle timer, so shutdown keeps
            // observing this dictation until its cleanup has genuinely finished.
            _lifecycle.EndProcessing(admission, faulted);
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
        long insertedTimestamp = 0;

        // What the pill says about this dictation when it returns to idle (PillOutcome.Of decides): how the insertion went,
        // what AI cleanup returned, and the failure processing reported, each set where the pipeline learns it.
        InjectionResult? pillInsertion = null;
        CleanupResult? pillCleanup = null;
        string? pillFailure = null;
        try
        {
            // This dictation owns the capture from its admission until the capture is stopped and released, so that
            // happens first, even when shutdown has already canceled it. Left to the host's disposal instead, the stop
            // and its unbounded join of the capture thread would run on the UI thread in the middle of the app's exit.
            // Tagged with the dictation, so this receives its own capture, all of it, and nothing else.
            var captured = _audio.Stop(session.Id);
            cancellationToken.ThrowIfCancellationRequested();
            report.CaptureDuration = captured.Duration;
            activity?.SetTag(ScribeTelemetry.TagCaptureSeconds, Math.Round(captured.Duration.TotalSeconds, 2));

            var heldSeconds = Stopwatch.GetElapsedTime(session.StartedTimestamp).TotalSeconds;
            _log.LogInformation(
                "#{Id} captured {Seconds:F2}s of audio from a {Held:F2}s hold (device='{Device}' " +
                "silent={Silent} muted={Muted} stop={Reason}).",
                session.Id,
                captured.Duration.TotalSeconds,
                heldSeconds,
                _audio.LastDeviceName ?? "unknown",
                _audio.LastCaptureWasSilent,
                _audio.LastDeviceMuted,
                session.StopReason);

            // The shape of "I couldn't talk for more than a few seconds". If the capture is much
            // shorter than the hold, the microphone stream ended on its own part-way through: a
            // device the OS reconfigured mid-capture (Studio Effects engaging, a meeting app taking
            // exclusive mode, a Bluetooth profile switch) rather than anything the user did. WASAPI
            // reports that as a clean stop with no exception, so nothing else in the pipeline
            // notices, and the user simply loses everything they said after it happened.
            //
            // The allowance covers the honest gap: the stop handshake and the final buffer flush.
            var shortfall = heldSeconds - captured.Duration.TotalSeconds;
            if (session.StopReason == DictationStopReason.HotkeyReleased
                && heldSeconds > 2
                && shortfall > 1.0)
            {
                _log.LogWarning(
                    "#{Id} the microphone stream ended {Shortfall:F2}s before the key was released " +
                    "({Captured:F2}s captured of a {Held:F2}s hold on '{Device}'). Everything spoken " +
                    "after the stream stopped was lost.",
                    session.Id,
                    shortfall,
                    captured.Duration.TotalSeconds,
                    heldSeconds,
                    _audio.LastDeviceName ?? "unknown");
            }

            if (captured.IsEmpty)
            {
                activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.EmptyCapture);

                // Zero samples from a capture that started without error means the endpoint is not
                // delivering audio at all: classically a Bluetooth headset whose hands-free mic
                // never engaged (seen with AirPods Max: the endpoint opens but streams nothing).
                // This must be loud: a silent return here looks to the user like dictation died.
                var device = _audio.LastDeviceName;
                _log.LogWarning(
                    "#{Id} capture from '{Device}' produced no audio after a {Held:F2}s hold.",
                    session.Id, device ?? "default device", heldSeconds);
                report.Fail("Audio capture", "The microphone produced no audio.");
                RaisePipelineReport(report);

                // A press too brief to record anything is not a broken microphone, and telling
                // somebody to go and change devices over it sends them to fix the wrong thing. A
                // support log showed this exact misdirection: the device took five seconds to open,
                // the user let go, and Scribe blamed their hardware.
                pillFailure = heldSeconds < 1.0
                    ? "that was too quick, hold the key while you speak"
                    : device is null
                        ? "no audio captured. Check your microphone in Settings"
                        : $"no audio from '{device}'. Pick a different microphone in Settings";
                RaiseError(pillFailure);
                return;
            }

            var audio = captured;
            activity?.SetTag(ScribeTelemetry.TagVadEnabled, settings.UseVoiceActivityDetection);
            if (settings.UseVoiceActivityDetection)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                    if (RaiseSilentCaptureError(report, "Voice activity detection") is { } silent)
                    {
                        pillFailure = silent;
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
            var recognizerResident = _transcription.IsReady;
            activity?.SetTag(ScribeTelemetry.TagRecognizerReady, recognizerResident);
            if (!recognizerResident)
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
            var recognitionStarted = Stopwatch.GetTimestamp();
            var result = _transcription.Transcribe(audio, cancellationToken);
            LogRecognitionTiming(session.Id, recognizerResident, Stopwatch.GetElapsedTime(recognitionStarted), result);
            report.DecodeDuration = result.DecodeDuration;
            report.RealTimeFactor = result.RealTimeFactor;
            report.RawText = result.Text;
            if (result.IsEmpty)
            {
                activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.NoSpeech);
                if (RaiseSilentCaptureError(report, "Speech recognition") is { } silent)
                {
                    pillFailure = silent;
                    return;
                }

                // The capture carried real audio (the meter moved, VAD found speech) but the
                // recogniser returned nothing. This was the one failure in the whole pipeline that
                // told the user absolutely nothing: it logged at Information, and the only other
                // signal, the pipeline report, goes solely to the Playground page in Settings,
                // which is normally closed. The overlay simply vanished and the dictation was gone.
                // Production logs show 34 of these across 22 days, none of them reported.
                // "Peak audio was present" on its own means only "not digital silence", a -60 dBFS
                // bar that a support log proved useless: a user lost three of six dictations here
                // and there was no way to tell a quiet microphone from a bad one from a decoder
                // that simply gave up. The measured signal shape is what makes those separable.
                _log.LogWarning(
                    "#{Id} speech recognition returned no text for a {Seconds:F2}s capture that was " +
                    "not silent. The dictation was lost. Device='{Device}' signal: {Signal}",
                    session.Id,
                    audio.Duration.TotalSeconds,
                    _audio.LastDeviceName ?? "unknown",
                    _audio.LastSignalReport?.Describe() ?? "unavailable");
                report.Fail("Speech recognition", "No speech was recognized.");
                RaisePipelineReport(report);
                pillFailure = "nothing was recognised, try again";
                RaiseError(pillFailure);
                return;
            }

            activity?.SetTag(ScribeTelemetry.TagDecodeChars, result.Text.Length);
            activity?.SetTag(ScribeTelemetry.TagRealTimeFactor, Math.Round(result.RealTimeFactor, 2));

            // The recogniser's other degenerate mode: instead of returning nothing it returns a
            // single filler token ("Yeah.") for several seconds of speech, which reaches the user's
            // document as an ordinary success. Measured against summed VOICED audio, not the
            // trimmed span, because the span still contains every thinking pause and a genuine
            // "Yeah." followed by a long pause would otherwise look like a collapse.
            //
            // Log only, deliberately. The threshold was derived from unlabelled history rows using
            // the same ratio it now tests, so it cannot yet distinguish a collapsed decode from a
            // terse speaker, and the text is injected either way. Showing a failure indicator on
            // that reasoning would cry wolf on correct dictations. This makes the occurrences
            // findable so they can be labelled from retained audio before anything is surfaced.
            var voicedSeconds = _vad.LastSpeechSeconds ?? audio.Duration.TotalSeconds;
            if (TerseDecodeDetector.IsSuspiciouslyTerse(result.Text, voicedSeconds))
            {
                // Character count and ratio only: PRIVACY.md promises transcripts are never
                // written to the diagnostic log.
                _log.LogWarning(
                    "Speech recognition returned only {Chars} characters for {Seconds:F2}s of " +
                    "voiced audio ({Ratio:F1} chars/s); the decode may have collapsed.",
                    result.Text.Length, voicedSeconds, result.Text.Length / Math.Max(voicedSeconds, 0.001));
            }

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

                // With the vocabulary this dictation was admitted with: its glossary, and every request handed over only
                // while that vocabulary's library scope is still permitted, so local rules finish a revoked one.
                cleanup = await _cleanup.Admit(session.Vocabulary.Cleanup)
                    .CleanAsync(recognized, cancellationToken, cleanupWritingStyle)
                    .ConfigureAwait(false);
                cleanupTimer.Stop();
                report.CleanupDuration = cleanupTimer.Elapsed;
                activity?.SetTag(ScribeTelemetry.TagAiOutcome, cleanup.Outcome.ToString());
                activity?.SetTag(ScribeTelemetry.TagAiChanged, cleanup.Changed);

                // Cleanup is switched on but the engine was not ready, so this dictation went out
                // raw. Without this the only clue was a single startup warning, and every later
                // dictation logged an unexplained "Skipped" while the user assumed cleanup ran.
                // The log and the trace carry codes only (the provider and status names), which the
                // live log redaction and the trace tag policy keep visible: the skip reason is a
                // sentence, and its display detail can name the endpoint host.
                if (cleanup.SkippedUnexpectedly)
                {
                    var status = _cleanup.Status;
                    _log.LogWarning(
                        "#{Id} AI cleanup was skipped for this dictation because {Provider} was {Status}. " +
                        "The raw transcription was used.",
                        session.Id, settings.AiCleanupProvider, status);
                    activity?.SetTag(ScribeTelemetry.TagAiSkipReason, AiSkipReason.NotReady(status));
                }

                if (cleanup.Outcome == CleanupOutcome.Failed)
                {
                    // Cleanup is unavailable or failed: keep the raw transcription and persist the failure on a
                    // background thread. The failure log opens its own SQLite connection per call, so a busy
                    // timeout there must never sit in front of injecting the raw text. The pill says so once the
                    // text is in ("Typed without AI cleanup" and the reason; PillOutcome.Of). Cleanup stays enabled;
                    // runtime failures can retry on the next dictation.
                    var reason = cleanup.FailureReason ?? "Intelligence failed.";

                    // The reason is diagnostics-safe and is what the pill shows. The display detail, which can name
                    // the endpoint's host or quote its own error, goes only to the Settings failure log in the local
                    // database. The log line keeps to codes, as above.
                    var localDetail = cleanup.DisplayDetail ?? reason;
                    _log.LogWarning(
                        "#{Id} AI cleanup failed ({Provider}, status {Status}); using raw transcription.",
                        session.Id, settings.AiCleanupProvider, _cleanup.Status);
                    var rawForLog = result.Text;
                    _ = Task.Run(() => RecordCleanupFailure(settings, localDetail, rawForLog));
                }
                else
                {
                    if (cleanup.Changed)
                    {
                        _log.LogInformation("AI cleanup refined the transcription.");
                    }

                    recognized = cleanup.Text;

                    // A partial degradation (some segments failed, or a very long tail was left raw)
                    // is recorded for the Settings log but does not flash red; the user still got
                    // usable cleaned text back. Persist off the dictation path so the DB write never
                    // sits in front of text injection.
                    if (cleanup.FailureReason is not null)
                    {
                        var partialReason = cleanup.DisplayDetail ?? cleanup.FailureReason;
                        var rawForLog = result.Text;
                        _ = Task.Run(() => RecordCleanupFailure(settings, partialReason, rawForLog));
                    }
                }
            }
            report.Cleanup = cleanup;
            pillCleanup = cleanup;
            report.CleanedText = recognized;

            currentStage = "Dictionary and snippets";
            var postTimer = Stopwatch.StartNew();

            // The same generation the cleanup above ran with, never a newer one a Save published meanwhile.
            _postProcessor.Use(session.Vocabulary);
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

            // Only the target is given the space after the dictation (AddSpaceAfterDictation), and only here, after the
            // line breaks were handled, which trims the text for a single-line target. DictationInsertion keeps the text
            // for recovery before it types anything, and hands back the text as dictated for history: only the injector
            // callback below ever sees the typed form with the space, so nothing that keeps text can be given it.
            currentStage = "Text insertion";
            var injectionTimer = Stopwatch.StartNew();
            var insertion = DictationInsertion.Insert(
                text,
                settings.AddSpaceAfterDictation,
                _lastTranscript,
                typed => _injector.Inject(
                    typed, settings.InjectionMethod, session.TargetWindow, settings.ShiftEnterLineBreaks, targetApp),
                cancellationToken);
            injectionTimer.Stop();
            var injection = insertion.Injection;
            pillInsertion = injection;
            report.InjectionDuration = injectionTimer.Elapsed;
            report.Injection = injection;
            report.SpaceAddedAfterText = insertion.SpaceAdded;
            if (!injection.Succeeded)
            {
                activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.Error);
                activity?.SetStatus(ActivityStatusCode.Error, injection.Error);
                _log.LogWarning(
                    "Text injection failed for {App}: {Error}", targetApp ?? "the focused app", injection.Error);
                pillFailure = injection.Error == InjectionResult.FocusChangedError
                    ? "focus changed, so the dictation was not inserted"
                    : "text could not be inserted completely";
                RaiseError(pillFailure);

                // The transcript was stored before typing began, so close the loop: without a hint the
                // preserved text looks lost, because the overlay flash is the only other signal.
                RaiseInjectionFailed();
                report.Fail("Text insertion", injection.Error ?? "Text could not be inserted.");
                RaisePipelineReport(report);
                return;
            }

            insertedTimestamp = Stopwatch.GetTimestamp();
            _log.LogInformation(
                "Text injected into {App} using {Method}; space added after it: {SpaceAdded}.",
                targetApp ?? "the focused app", injection.Method, insertion.SpaceAdded);

            activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.Injected);
            EnqueueHistory(session.Id, settings, audio, result, insertion.Recorded, targetApp, cleanup, report.CleanupDuration);
            RaisePipelineReport(report);
            Dictated?.Invoke(insertion.Recorded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.Error);
            TryLog(log => log.LogInformation("#{Id} dictation processing canceled during shutdown.", session.Id));
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown outlived its wait for this dictation and the host disposed a service it was still using. The
            // speech services refuse use after disposal rather than touching freed native memory, so this is the
            // expected end of a dictation that was already canceled, not a processing failure.
            activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.Error);
            TryLog(log => log.LogInformation(
                "#{Id} dictation processing stopped during shutdown: a service it used was already disposed.",
                session.Id));
        }
        catch (FileNotFoundException ex)
        {
            activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.Error);

            // By shape here too: the trace bridge writes an error span's description into the shared log.
            activity?.SetStatus(ActivityStatusCode.Error, FailureShape.Describe(ex));
            _log.LogInformation("Dictation skipped because no speech model is installed ({Failure}).", FailureShape.Describe(ex));
            report.Fail(currentStage, ex.Message);
            RaisePipelineReport(report);
            pillFailure = "choose a speech model in Settings";
            RaiseError(pillFailure);
        }
        catch (Exception ex)
        {
            activity?.SetTag(ScribeTelemetry.TagOutcome, DictationOutcome.Error);
            activity?.SetStatus(ActivityStatusCode.Error, FailureShape.Describe(ex));
            _log.LogError("Dictation processing failed: {Failure}", FailureShape.DescribeWithStack(ex));
            report.Fail(currentStage, ex.Message);
            RaisePipelineReport(report);
            pillFailure = "transcription failed";
            RaiseError(pillFailure);
        }
        finally
        {
            // After the pipeline, never in it: the outcome is decided here from what it set, and shown with the return to
            // idle, which already accepts the next press.
            ResetToIdle(session.Id, insertedTimestamp, PillOutcome.Of(pillInsertion, settings.EnableAiCleanup, pillCleanup, pillFailure));
        }
    }

    // Cold load and decode are reported apart: a slow dictation after an idle release is the model
    // loading, not the recognizer decoding slowly, and the two call for different fixes. Anything in
    // the call that was not decoding is model load or waiting for the engine's gate. Guarded, because
    // it runs mid-pipeline and a diagnostic must never cost the user their dictation.
    private void LogRecognitionTiming(long id, bool recognizerResident, TimeSpan elapsed, TranscriptionResult result)
    {
        var loadOrWait = elapsed - result.DecodeDuration;
        if (loadOrWait < TimeSpan.Zero)
        {
            loadOrWait = TimeSpan.Zero;
        }

        TryLog(log => log.Log(
            recognizerResident ? LogLevel.Debug : LogLevel.Information,
            "#{Id} speech recognition took {TotalMs} ms: decode {DecodeMs} ms, model load or wait {LoadMs} ms " +
            "(recognizer resident at start: {Resident}).",
            id,
            (long)elapsed.TotalMilliseconds,
            (long)result.DecodeDuration.TotalMilliseconds,
            (long)loadOrWait.TotalMilliseconds,
            recognizerResident));
    }

    // A "no speech" outcome from a capture that never rose above digital silence is not a quiet
    // room, it is a mic that recorded nothing: muted in a meeting, hardware mute switch, taskbar
    // mic mute. Historically this fell through the silent VAD/no-speech discard paths and looked
    // like Scribe simply did nothing. Returns the message it raised, which the pill shows too, so the
    // caller can skip its own quiet discard; null when the capture was not silent.
    private string? RaiseSilentCaptureError(DictationPipelineReport report, string stage)
    {
        if (!_audio.LastCaptureWasSilent)
        {
            return null;
        }

        var device = _audio.LastDeviceName;
        _log.LogWarning("Capture from '{Device}' contained only silence; the microphone is likely muted.",
            device ?? "default device");
        report.Fail(stage, "The capture contained only silence. The microphone is likely muted.");
        RaisePipelineReport(report);
        var message = device is null
            ? "no sound was captured, your microphone may be muted"
            : $"no sound from '{device}', it may be muted";
        RaiseError(message);
        return message;
    }

    private void EnqueueHistory(
        long id,
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

            // Every field is fixed here, the timestamp included, because the commit happens later on
            // the writer. The writer owns the capture from this point: nothing below touches it again.
            var entry = new HistoryEntry(
                Id: 0,
                TimestampUtc: DateTimeOffset.UtcNow,
                Text: text,
                AudioMilliseconds: (int)result.AudioDuration.TotalMilliseconds,
                DecodeMilliseconds: (int)result.DecodeDuration.TotalMilliseconds,
                CleanupMilliseconds: cleanupMs,
                TargetApp: targetApp,
                TranscriptionModelId: result.ModelId);
            _historyWriter.Enqueue(entry, settings.StoreAudioHistory ? audio : null, id);
        }
        catch (Exception ex)
        {
            // History is best-effort; never fail a dictation because persistence hiccupped.
            _log.LogWarning("#{Id} failed to queue dictation history ({Failure}).", id, FailureShape.Describe(ex));
        }
    }

    private void ResetToIdle(long id, long insertedTimestamp, PillOutcome? outcome)
    {
        // A pause requested mid-capture takes effect here, once processing finishes. This only publishes idle: the
        // dictation calling it is still finishing, and only its own EndProcessing tells shutdown it is done.
        var idle = _lifecycle.ReturnToIdle(IdleReleaseDelay(CurrentSettings));

        // Stamped at the commit point itself: from here the next press is accepted, whatever the StateChanged handlers
        // below still take to repaint the tray.
        var idleTimestamp = Stopwatch.GetTimestamp();

        // The outcome rides this change, under its revision: the shell shows it in place of the hide, and a newer
        // recording's change is numbered after it, so it always wins.
        Raise(idle.Presentation, outcome: outcome);
        LogOutcome(id, outcome);

        // Numbers only: how long a successful dictation kept the next one waiting after its text was
        // in place, and how many presses were turned away while it processed.
        var rejected = idle.RejectedWhileProcessing;
        if (insertedTimestamp != 0)
        {
            TryLog(log => log.LogInformation(
                "#{Id} accepting the next dictation {Ms} ms after insertion; {Rejected} activation(s) were " +
                "rejected while it processed.",
                id, (long)Stopwatch.GetElapsedTime(insertedTimestamp, idleTimestamp).TotalMilliseconds, rejected));
        }
        else if (rejected > 0)
        {
            TryLog(log => log.LogInformation(
                "#{Id} {Rejected} activation(s) were rejected while this dictation processed.", id, rejected));
        }
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
            _log.LogWarning("Failed to record an AI cleanup failure ({Failure}).", FailureShape.Describe(ex));
        }
    }

    // Diagnostics near teardown and timer paths go through here: a log call that throws must never
    // turn into the failure it was describing.
    private void TryLog(Action<ILogger> write)
    {
        try
        {
            write(_log);
        }
        catch
        {
            // Nothing useful is left to do.
        }
    }

    // Guarded raise: the recovery hint is best-effort UI sugar, so a throwing handler must never
    // propagate into the dictation processing path.
    private void RaiseInjectionFailed()
    {
        if (_lifecycle.IsClosing)
        {
            return;
        }

        try
        {
            InjectionFailed?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogWarning("An InjectionFailed handler threw: {Failure}", FailureShape.DescribeWithStack(ex));
        }
    }

    // Once shutdown has begun nobody is left to read an error or a warning. The shell never waits for the UI thread to
    // show one (it posts; see UiThreadDispatch), so standing down here is about not queuing notices for a closing app.
    // Guarded as well, so a throwing handler is reported instead of re-entering the pipeline's own failure path.
    private void RaiseError(string message)
    {
        if (_lifecycle.IsClosing)
        {
            return;
        }

        try
        {
            Error?.Invoke(message);
        }
        catch (Exception ex)
        {
            _log.LogWarning("An Error handler threw: {Failure}", FailureShape.DescribeWithStack(ex));
        }
    }

    // The revision is taken under the lifecycle's gate: a warning raised late for a recording that a pause or a stop has
    // already ended carries 0, and one raised just before is dropped by the shell once a newer change has been shown.
    // The pill text is what the recording pill shows while that recording is live; null shows nothing there.
    private void RaiseWarning(string message, long dictationId, string? pillText)
    {
        if (_lifecycle.IsClosing)
        {
            return;
        }

        try
        {
            var revision = _lifecycle.TryGetRecordingPresentation(dictationId)?.Revision ?? 0;
            Warning?.Invoke(new DictationWarning(message, revision, pillText));
        }
        catch (Exception ex)
        {
            _log.LogWarning("A Warning handler threw: {Failure}", FailureShape.DescribeWithStack(ex));
        }
    }

    // Names both microphones: the one the user chose and the one this dictation is really using.
    private static string UnavailableMicrophoneMessage(string? chosenName, string? usedName)
    {
        var chosen = string.IsNullOrWhiteSpace(chosenName) ? "your chosen microphone" : $"'{chosenName}'";
        var used = string.IsNullOrWhiteSpace(usedName) ? string.Empty : $", '{usedName}'";
        return $"{chosen} isn't available, so Scribe is using the Windows default microphone{used}. " +
            "Choose a microphone from the tray menu or in Settings";
    }

    // Through the notice a muted microphone uses: a tray notification always, and the pill only while the recording is
    // live. For a recording already in processing the warning carries no pill text, and its revision is 0, so the shell
    // shows only the notification. The log gets the device names.
    private void AnnounceUnavailableMicrophone(
        UnavailableMicrophoneAnnouncement announcement, long id, AppSettings settings, RecordingOpen open)
    {
        if (announcement == UnavailableMicrophoneAnnouncement.None)
        {
            return;
        }

        TryLog(log => log.LogWarning(
            "#{Id} the chosen microphone '{Chosen}' is not available; recording from the Windows default '{Device}' ({When}).",
            id, settings.InputDeviceName ?? "unnamed", open.DeviceName ?? "unknown", announcement));
        RaiseWarning(
            UnavailableMicrophoneMessage(settings.InputDeviceName, open.DeviceName),
            id,
            announcement == UnavailableMicrophoneAnnouncement.WhileRecording ? "Using default mic" : null);
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
            _log.LogWarning("A pipeline report handler threw: {Failure}", FailureShape.DescribeWithStack(ex));
        }
    }

    // Raised on whichever thread made the change, after the lifecycle's gate is released, so it carries the revision the
    // gate gave the change: the shell keeps the newest, whatever order two raises arrive in. The handlers never wait for the
    // UI thread, which matters most on the audio capture thread: at shutdown the UI thread joins it. Once shutdown has begun
    // nobody needs the state any more, so nothing is raised.
    private void Raise(DictationPresentation shown, bool aiPolishing = false, PillOutcome? outcome = null)
    {
        if (shown.Revision <= 0 || _lifecycle.IsClosing)
        {
            return;
        }

        var state = shown.Phase switch
        {
            DictationPhase.Recording => DictationState.Recording,
            DictationPhase.Processing => DictationState.Processing,
            _ => shown.Paused ? DictationState.Paused : DictationState.Idle,
        };

        ResilientEvent.InvokeAll(
            StateChanged,
            new DictationStateChange(state, shown.Revision, aiPolishing, outcome),
            ex => TryLog(log => log.LogWarning("A StateChanged handler threw: {Failure}", FailureShape.DescribeWithStack(ex))));
    }

    // The outcome's kind only: its detail is shown on the pill, and it can name a microphone.
    private void LogOutcome(long id, PillOutcome? outcome) =>
        TryLog(log => log.LogInformation("#{Id} finished with outcome {Outcome}.", id, (object?)outcome?.Kind ?? "None"));

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

    /// <summary>
    /// Begins shutdown without waiting for anything: from here no recording, processing, timer schedule or idle release
    /// starts, and the controller stops raising state, error and warning events. The app calls this as the very first
    /// step of its exit, so nothing raised while the UI thread works through the later teardown steps is queued for an
    /// app that is closing. Idempotent; <see cref="Dispose"/> calls it too.
    /// </summary>
    public void BeginShutdown() => _lifecycle.BeginShutdown();

    /// <summary>
    /// Stops the dictation loop for good. Nothing new starts once this begins; a dictation still
    /// processing is canceled and waited for with a bound, and its queued history is drained, so
    /// the host can then dispose the core services. Never throws: every step is guarded and a
    /// failure is logged, because this runs inside the app's own teardown.
    /// </summary>
    public void Dispose()
    {
        // The order (close admission, close the timers, cancel, stop the inputs below, wait for processing, then complete
        // history, then release the token source) is the lifecycle's, where it is tested.
        var stopInputs = new List<TeardownStep>
        {
            new("stop capture", () =>
            {
                _audio.LevelChanged -= OnLevelForSilence;
                _audio.CaptureFaulted -= OnCaptureFaulted;

                // Read without the capture service's lock on purpose: a device open in progress holds it for seconds, and
                // this runs on the UI thread. That open is covered twice over: RecordingCapture.Open stops the microphone
                // it opened when shutdown began before any stop admitted the recording (an admitted one is its processing's
                // to stop), and the service's disposal waits, with a bound, for the open.
                if (_audio.IsCapturing)
                {
                    _audio.RequestStop();
                }
            }),
        };

        if (_started)
        {
            stopInputs.Add(new("stop the hotkey hook", () =>
            {
                _hotkeys.Activated -= OnActivated;
                _hotkeys.Deactivated -= OnDeactivated;
                _hotkeys.Stop();
            }));
            stopInputs.Add(new(
                "cleanup status subscription",
                () => _cleanup.StatusChanged -= OnCleanupStatusChangedForAnnouncement));
        }

        var shutdown = _lifecycle.Shutdown(
            stopInputs,
            _historyWriter,
            ProcessingDrainTimeout,
            HistoryDrainTimeout,
            (step, ex) => TryLog(log => log.LogWarning(
                "Dictation controller shutdown step '{Step}' failed; continuing: {Failure}",
                step,
                FailureShape.DescribeWithStack(ex))));
        if (shutdown.AlreadyShutDown)
        {
            return;
        }

        if (!shutdown.Processing.Drained)
        {
            // The host disposes the core services next, and that stays safe for this dictation. The
            // speech services take the same gate as a decode or a trim, so disposal waits out the
            // native call in progress instead of freeing memory under it, and any later use is
            // refused with ObjectDisposedException, which processing treats as the end of a canceled
            // dictation. The canceled token stops the pipeline at its next checkpoint; the last one
            // sits just before insertion, so nothing is typed after shutdown began unless insertion
            // was already under way. History it queues after the writer completed is refused and
            // logged, never written to a disposed database.
            TryLog(log => log.LogWarning(
                "#{Id} dictation processing was still running {Seconds:F0} s into shutdown; it was canceled " +
                "and stops at its next cancellation point.",
                shutdown.StillRunningDictationId ?? 0,
                ProcessingDrainTimeout.TotalSeconds));
        }

        if (shutdown.Processing.Faulted > 0)
        {
            TryLog(log => log.LogWarning(
                "{Count} dictation(s) ended with an unexpected fault this session; each was logged when it happened.",
                shutdown.Processing.Faulted));
        }

        if (shutdown.History is { } history && (!history.Drained || history.Abandoned > 0))
        {
            TryLog(log => log.LogWarning(
                "History writer stopped with {StillWriting} write still committing and {Abandoned} " +
                "dictation(s) not recorded.",
                history.StillWriting, history.Abandoned));
        }
    }

    private sealed record DictationSession(
        long Id,
        AppSettings Settings,
        nint TargetWindow,
        string? TargetApp,
        long StartedTimestamp,
        DictationStopReason StopReason,
        VocabularyGeneration Vocabulary);

    // Everything a recording needs to remember from the moment it started, captured under the lifecycle's gate
    // atomically with the phase change. Settings already carry the per-trigger overrides. HotkeyActivation is the press
    // that started it, the only one a stop Scribe makes itself may release (see DictationStopPolicy.BeginStop).
    // Vocabulary is the generation the dictation was admitted with, the one its cleanup and its post-processing use.
    private sealed record CaptureContext(
        AppSettings Settings,
        nint TargetWindow,
        string? TargetApp,
        long StartedTimestamp,
        long HotkeyActivation,
        VocabularyGeneration Vocabulary);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
}

/// <summary>One change to what the shell shows for the dictation loop.</summary>
/// <param name="State">What to show.</param>
/// <param name="Revision">
/// The number the lifecycle gave the change (see <see cref="DictationPresentation.Revision"/>). Show a change only when this
/// is higher than the last one shown.
/// </param>
/// <param name="AiPolishing">
/// For Processing: the capture runs AI cleanup, from that capture's own settings (the dictation-only hotkey overrides the
/// global setting), taken when it was admitted rather than read later, when a newer capture may already be live.
/// </param>
/// <param name="Outcome">
/// For the return to idle that ends a dictation: what the pill says about it (<see cref="PillOutcome.Of"/>), to be shown in
/// place of the hide. It carries this change's revision, so a late outcome never covers a newer recording. Null for every
/// other change, and for a dictation discarded quietly.
/// </param>
internal readonly record struct DictationStateChange(DictationState State, long Revision, bool AiPolishing, PillOutcome? Outcome = null);

/// <summary>A recoverable capture warning.</summary>
/// <param name="Message">The notice for the tray.</param>
/// <param name="RecordingRevision">
/// The revision of the Recording change the warning belongs to (see <see cref="PresentationRelay{T}.PublishIfCurrent"/>),
/// or 0 when that recording was already over when the warning was raised.
/// </param>
/// <param name="PillText">What the recording pill shows for it while that recording is live, or null for nothing.</param>
internal readonly record struct DictationWarning(string Message, long RecordingRevision, string? PillText);

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

    // The text as dictated, which is also what history keeps: without the space the target may have been given after it.
    public string? FinalText { get; set; }
    public InjectionResult? Injection { get; set; }

    // Whether the target was given a space after FinalText (AddSpaceAfterDictation).
    public bool SpaceAddedAfterText { get; set; }
    public string? FailureStage { get; private set; }
    public string? FailureReason { get; private set; }
    internal long StartedTimestamp { get; } = startedTimestamp;

    public void Fail(string stage, string reason)
    {
        FailureStage = stage;
        FailureReason = reason;
    }
}
