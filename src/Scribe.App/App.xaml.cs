using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using Scribe.App.Dictation;
using Scribe.App.Infrastructure;
using Scribe.App.Overlay;
using Scribe.App.Settings;
using Scribe.App.Tray;
using Scribe.Core.Audio;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Hotkeys;
using Scribe.Core.Infrastructure;
using Scribe.Core.Lifecycle;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Overlay;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.TextInjection;
using Scribe.Core.Transcription;
using Scribe.Core.Tray;
using Scribe.Core.Vad;
using Scribe.Core.Vocabulary;
using Wpf.Ui.Appearance;

namespace Scribe.App;

/// <summary>
/// Application entry point. Scribe is a tray-only app: there is no main window, so the host is
/// started in <see cref="OnStartup"/>, the tray icon and dictation loop are wired up, and the
/// process stays alive until the user quits from the tray. A named mutex enforces a single
/// instance so two keyboard hooks never fight over the same hotkey.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = "Scribe.SingleInstance.9E5C1A2F";

    // A second launch carrying --settings signals this instead of dying behind an "already running"
    // dialog. Shortcuts and the installer both use that switch, so the old behaviour turned a
    // deliberate "open settings" into a dead end.
    private const string ShowSettingsEventName = "Scribe.ShowSettings.9E5C1A2F";
    private const string ShowSettingsPayloadName = "Scribe.ShowSettings.Payload.9E5C1A2F";
    private const int ShowSettingsPayloadBytes = 256;

    // How long quitting waits for a tray settings change already on its way to the database.
    private static readonly TimeSpan SettingsWriteDrainTimeout = TimeSpan.FromSeconds(2);

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showSettingsSignal;
    private MemoryMappedFile? _showSettingsPayload;
    private RegisteredWaitHandle? _showSettingsRegistration;
    private IHost? _host;
    private TrayIconHost? _tray;
    private DictationController? _controller;
    private Scribe.Core.Lifecycle.PresentationRelay<DictationStateChange>? _dictationState;
    private IOverlayController? _overlay;

    // UI thread only: the dictation state rendered last, so a render can tell a resume (Paused, then anything else) and warm
    // the helper the pause released, and a settings save can tell it is paused.
    private DictationState _lastRenderedState = DictationState.Idle;
    private SettingsWindow? _settingsWindow;
    private Onboarding.WelcomeWindow? _welcomeWindow;
    private QuickAdd.QuickAddWindow? _quickAddWindow;
    private TextScaleService? _textScale;

    /// <summary>The file log sink, so its health can be reported in Settings.</summary>
    internal static FileLoggerProvider? LogSink { get; private set; }
    private UpdateService? _updates;
    private SessionDiagnostics? _diagnostics;
    private ILogger? _appLog;
    private int _learningFromHistory;
    private readonly TrayFeedbackPolicy _trayFeedback = new();
    private readonly QuickAddOpenGate _quickAddOpenGate = new();
    private TrayCondition _trayCondition;
    private bool _foundryDownloadedModel;
    private readonly FoundryModelCacheRefresh _foundryRefresh = new();
    private Guid? _noticeCopyEntryId;
    private Task? _appExitOperation;

    // The tray's settings writes, saved on a worker in click order so a database wait never freezes the UI thread.
    private Scribe.Core.Settings.SettingsWriteLane? _settingsWrites;

    // A microphone chosen from the tray whose write has not come back yet, so the tray shows the choice at once.
    private (Scribe.Core.Settings.MicrophoneSelection Selection, long Revision)? _pendingTrayMicrophone;

    // Kept so the first stage of OnExit can stop it before anything it uses is torn down.
    private StorageMaintenance? _storageMaintenance;

    // StartupStageTiming: the start's stage timeline, anchored first thing in Main and dropped once the start is described;
    // and how many Settings windows this process has opened, so each open's stage line says which one it was.
    internal StageTimeline? StartupStages { get; set; }

    // The performance changes this process runs, from the host (every one off until the host has started).
    private PerfFlags _perfFlags = PerfFlags.None;

    private int _settingsOpens;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before any window exists, the already-running notice below included.
        TitleBarButtonNames.Apply(Resources);
        ButtonLabelContrast.Apply(Resources);
        _textScale = new TextScaleService(Dispatcher);
        _textScale.Start();
        StartupStages?.Mark("resources");

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isNew);
        var requestedPage = RequestedSettingsPage(e.Args, out var hasSettingsSwitch);
        if (!isNew)
        {
            // Hand the request to the running instance rather than telling the user to go find the
            // tray icon themselves. Only fall back to the notice when the signal cannot be raised.
            if (TrySignalShowSettings(requestedPage))
            {
                Shutdown();
                return;
            }

            ShowSingleInstanceNotice();
            Shutdown();
            return;
        }

        StartupStages?.Mark("mutex");
        // Everything from here on holds the single-instance mutex, so nothing may fail without ending the process.
        // This method is async void and the dispatcher handler marks what reaches it as handled, so a failure that
        // escaped used to leave a hidden process that every relaunch reported as already running, or a tray icon
        // over a dictation loop that never started.
        bool started;
        try
        {
            started = await StartAsync();
        }
        catch (Exception ex)
        {
            AbandonStartup(ex);
            return;
        }

        // Allow `Scribe.exe --settings` to jump straight to the settings window on launch. After the guard on purpose:
        // Scribe is running by now, so a window that fails to open is a window fault, reported as any other is.
        if (started && !Dispatcher.HasShutdownStarted && hasSettingsSwitch)
        {
            OpenSettings(requestedPage);
        }
    }

    /// <summary>
    /// Everything startup does once this is the only instance. Returns false when it ended the process on purpose,
    /// having told the user why: the data folder cannot be created, or the database is from a newer Scribe; and when
    /// shutdown began while it waited for the first vocabulary generation, when nothing is left to start.
    /// </summary>
    private async Task<bool> StartAsync()
    {
        StartShowSettingsListener();

        // Read once for the whole process, here: some of what the flags change runs before the container exists, and the
        // container is handed this same instance below, so every consumer sees one answer (PerfFlags).
        var perfFlags = PerfFlags.FromEnvironment();

        // Tray app: never exit just because a window closed; quit happens explicitly from the tray.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        AppPaths paths;
        try
        {
            paths = AppPaths.CreateForStartup();
        }
        catch (Exception ex)
        {
            ShowFatalDataPathNotice(ex);
            Shutdown();
            return false;
        }

        StartupStages?.Mark("paths");
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddScribeCore();
        builder.Services.Replace(ServiceDescriptor.Singleton(perfFlags));
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<AzureCliInstaller>();
        builder.Services.AddSingleton<StartupRegistration>();
        builder.Services.AddSingleton<SessionDiagnostics>();

        // Every dictation's vocabulary is built by the publisher from the library vocabulary's source, which AddScribeCore
        // registers: the library service (DictionaryLibraryService) is the one ILibraryVocabularySource, and AI cleanup
        // takes it through its constructor as the admission point for every outbound request.
        builder.Services.AddSingleton<VocabularyPublisher>();

        builder.Services.AddScribeTelemetry();
        builder.Logging.ClearProviders();
        // Held in a static so Settings can report whether logging is ACTUALLY working rather
        // than displaying the folder it was asked to use. A packaged build was found writing
        // nothing for an entire session while the About page confidently showed a path.
        // The append mode is shared with the overlay client, which decides it for each helper it launches (DATA-O-02).
        var logAppendMode = new AppendOnlyLogMode(
            perfFlags.IsOn(PerfFlags.AppendOnlyLog), AppendOnlyLogMode.ReadVersion(typeof(App).Assembly.Location));
        var logSink = new FileLoggerProvider(paths.LogsDir, appendMode: logAppendMode);
        LogSink = logSink;

        // A factory registration rather than AddProvider(instance): the container disposes what a
        // factory hands it but never an instance it was given, and disposing the provider is what
        // drains its background writer when the host shuts down.
        builder.Services.AddSingleton<ILoggerProvider>(_ => logSink);
        builder.Logging.AddDebug();

        // Debug, not Information. The log is the only diagnostic channel this app has: it is a tray
        // app with no console, users report problems days later, and the failures that matter are
        // intermittent and hardware-specific. Retention (7 days, budgeted) is what keeps the extra
        // detail from costing anyone disk space, so there is no reason left to log less.
        builder.Logging.SetMinimumLevel(LogLevel.Debug);

        // Except for the framework's own chatter, which at Debug is thousands of lines of hosting
        // and HTTP internals per session and would bury the pipeline events entirely.
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);
        builder.Logging.AddFilter("Azure", LogLevel.Warning);

        _host = builder.Build();
        StartupStages?.Mark("build");
        _host.Start();
        StartupStages?.Mark("hoststart");

        var services = _host.Services;
        var log = services.GetRequiredService<ILogger<App>>();
        _appLog = log;
        _perfFlags = services.GetRequiredService<PerfFlags>();

        // First thing in the file, before anything can fail. A log that opens mid-story is the
        // reason a 0.3.10 report about dictation cutting out short could not be investigated at
        // all: nothing recorded which build, which install channel, which microphone or which
        // settings were in play, and the daily file had rolled over since the process started.
        _diagnostics = services.GetRequiredService<SessionDiagnostics>();
        _diagnostics.WriteBanner(log);
        StartupStages?.Mark("banner");

        WireGlobalExceptionLogging(log);

        // Before the first theme is applied, so the foregrounds on accent fills are chosen for it and for every theme
        // change after it, in time for the first window.
        AccentContrastResources.Attach(
            this, services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AccentContrastResources).FullName!));
        InitializeApplicationTheme(log);
        StartupStages?.Mark("theme");
        if (paths.IsFallbackRoot)
        {
            log.LogWarning(
                "Scribe could not create the preferred data folder {PreferredRootDir}; using fallback data folder {RootDir}. {Failure}",
                paths.PreferredRootDir,
                paths.RootDir,
                paths.CreationFailureMessage);
            ShowDataPathFallbackNotice(paths);
        }
        else if (paths.OrphanedFallbackRootDir is { } orphaned)
        {
            // An earlier session fell back, wrote data there, and this one recovered. Saying so is
            // the difference between a user recovering that history and silently running with two
            // divergent copies. Log only: the data in use is correct, so a modal on every launch
            // would be noise, and Settings > About shows both paths.
            log.LogWarning(
                "An earlier session stored data in the fallback folder {OrphanedRootDir} because {RootDir} was unavailable. " +
                "That data is not in use now. Copy scribe.db across if you need its history.",
                orphaned,
                paths.RootDir);
        }

        // Azure CLI may have been installed or updated after this tray process inherited its PATH.
        // Prepare it before DictationController.Start configures cloud cleanup in the background.
        var azureCliInstaller = services.GetRequiredService<AzureCliInstaller>();
        if (azureCliInstaller.PrepareEnvironment())
        {
            log.LogInformation("Azure CLI environment prepared for Microsoft Foundry authentication.");
        }

        // Open the database here, before anything else relies on it. A file written by a newer Scribe
        // (a later schema) cannot be used by this build, and gets its own message rather than the generic
        // one OnStartup shows for any other failure here.
        try
        {
            services.GetRequiredService<ScribeDatabase>().Initialize();
            StartupStages?.Mark("database");
        }
        catch (NewerDatabaseSchemaException ex)
        {
            log.LogError(
                "The database was written by a newer Scribe (schema v{DatabaseVersion}; this build supports v{SupportedVersion}). Exiting.",
                ex.DatabaseVersion,
                ex.SupportedVersion);
            ShowNewerDataNotice();
            Shutdown();
            return false;
        }

        // Install the seed dictionary on first run so post-processing is useful out of the box, then
        // retire the entries older versions seeded that replaced ordinary words.
        var dictionary = services.GetRequiredService<IDictionaryRepository>();
        var settingsRepository = services.GetRequiredService<ISettingsRepository>();

        // Decided here, before anything below can write the settings document (the seed retirement and Foundry reset
        // flags, the welcome flag): once defaults are saved over a lost or unreadable document, nothing can tell them
        // from the user's choice. A session on defaults must never delete history text by a retention period the
        // user did not pick.
        var withoutSavedSettings = SettingsRepository.StartsWithoutSavedSettings(
            settingsRepository, services.GetRequiredService<ScribeDatabase>());
        if (withoutSavedSettings)
        {
            services.GetRequiredService<StorageMaintenance>().KeepAllTextThisSession();
        }

        dictionary.SeedIfEmpty(DefaultVocabulary.Entries);

        // Both save the whole settings document, so they wait for a start that could read it. A read that failed
        // outright (a transient lock) answers true above without recording anything in the repository, so neither may
        // go by the repository alone; each also checks the read it makes itself.
        if (!withoutSavedSettings)
        {
            SeedVocabularyRetirement.Apply(
                settingsRepository,
                dictionary,
                DefaultVocabulary.RetiredEntries,
                log);

            // Older builds demoted a model to its CPU build on any load failure, including a variant
            // needing an execution provider this PC never had. Scribe now avoids that up front, so the
            // saved markers only pin cleanup to the CPU for no reason.
            FoundryDemotionReset.Apply(settingsRepository, services.GetRequiredService<AppPaths>(), log);
        }

        StartupStages?.Mark("seed");
        _settingsWrites = new Scribe.Core.Settings.SettingsWriteLane(
            settingsRepository,
            callback => Dispatcher.BeginInvoke(callback));
        services.GetRequiredService<HistoryDeletionNotifier>().Deleted += OnHistoryDeleted;

        _controller = new DictationController(
            services.GetRequiredService<IHotkeyService>(),
            services.GetRequiredService<IAudioCaptureService>(),
            services.GetRequiredService<IVadService>(),
            services.GetRequiredService<ITranscriptionService>(),
            services.GetRequiredService<ITextPostProcessor>(),
            services.GetRequiredService<ITextCleanupService>(),
            services.GetRequiredService<ITextInjector>(),
            services.GetRequiredService<IHistoryWriter>(),
            services.GetRequiredService<VocabularyPublisher>(),
            services.GetRequiredService<ICleanupFailureLog>(),
            services.GetRequiredService<LastTranscriptStore>(),
            services.GetRequiredService<ISettingsRepository>(),
            services.GetRequiredService<ILogger<DictationController>>(),
            services.GetRequiredService<PerfFlags>());
        StartupStages?.Mark("controller");

        // Before anything that takes input exists (the tray, and at Start the hotkey): the persisted settings load, and the
        // first vocabulary generation is built on a worker and awaited here, never waited on, so the library source's first
        // read, which can load a cold catalog, stays off this thread and the first dictation never runs without its
        // vocabulary. Whatever PrepareAsync throws ends startup through AbandonStartup, with the startup failure notice: an
        // exception from loading the settings or the snippets, or a TimeoutException when the first generation is not
        // built within VocabularyPublisher.StartupDeadline (a read that never returns). A first build that cannot read its
        // inputs throws nothing: unreadable libraries count as none, so dictation starts on the personal dictionary alone,
        // and an unreadable dictionary leaves dictation without vocabulary until a later build reads it. The publisher logs
        // both.
        await _controller.PrepareAsync();
        if (Dispatcher.HasShutdownStarted || _controller.IsClosing)
        {
            return false;
        }

        StartupStages?.Mark("vocabulary");
        _tray = new TrayIconHost(ex =>
        {
            try
            {
                _appLog?.LogWarning("A tray update failed ({Failure}).", FailureShape.Describe(ex));
            }
            catch
            {
                // Tray updates are best effort, and so is saying one failed.
            }
        });
        _tray.QuitRequested += TrayQuit;
        _tray.SettingsRequested += OpenSettings;
        _tray.CopyLastDictationRequested += CopyLastDictation;
        _tray.CopyRecentDictationRequested += CopyRecentDictation;
        _tray.CopyRecentDictationByIdRequested += CopyRecentDictation;
        _tray.RecentDictationsProvider = () => _host is null
            ? []
            : _host.Services.GetRequiredService<LastTranscriptStore>().GetRecent();
        _tray.RecentTranscriptProvider = () => _host is null
            ? []
            : _host.Services.GetRequiredService<LastTranscriptStore>().GetRecentEntries();
        _tray.CopyLastAvailableProvider = HasRecentDictationForTray;
        _tray.AddToDictionaryRequested += ShowQuickAdd;
        _tray.PauseToggled += paused => _controller?.SetPaused(paused);
        _tray.AiCleanupToggled += ToggleAiCleanup;
        _tray.AiCleanupItemProvider = DescribeTrayAiCleanup;
        _tray.UpdateReadyProvider = () => _updates?.PendingVersion is not null;
        _tray.UpdateVersionProvider = () => _updates?.PendingVersion;
        _tray.ConditionProvider = CurrentTrayCondition;
        _tray.MicrophoneMenuProvider = BuildTrayMicrophoneMenu;
        _tray.MicrophoneChosen += ChooseMicrophone;
        _tray.SoundSettingsRequested += OpenSoundSettings;
        _tray.RestartToUpdateRequested += RestartToUpdate;
        _tray.OpenHistoryRequested += () => OpenSettings(Scribe.Core.Settings.SettingsPage.History);
        _tray.SetUpAiCleanupRequested += () => OpenSettings(Scribe.Core.Settings.SettingsPage.AiCleanup);
        _tray.NoticeActionRequested += OnTrayNoticeAction;
        StartupStages?.Mark("tray");

        _overlay = new OverlayProcessClient(
            services.GetRequiredService<IAudioCaptureService>(),
            services.GetRequiredService<ILogger<OverlayProcessClient>>(),
            services.GetRequiredService<Scribe.Core.Diagnostics.PerfFlags>(),
            logAppendMode);

        // State changes are raised on whichever thread made them and can arrive out of order; the relay posts each to this
        // thread without making the raising thread wait, and shows only the newest (see PresentationRelay).
        var controllerForState = _controller;
        _dictationState = new Scribe.Core.Lifecycle.PresentationRelay<DictationStateChange>(
            post: work => Dispatcher.BeginInvoke(work),
            render: RenderDictationState,
            isClosed: () => controllerForState.IsClosing,
            onFailure: ex =>
            {
                try
                {
                    _appLog?.LogWarning("Could not show a dictation state change ({Failure}).", FailureShape.Describe(ex));
                }
                catch
                {
                    // Best effort, like the views it describes.
                }
            });
        _controller.StateChanged += OnStateChanged;
        _controller.PipelineReported += report =>
            Dispatcher.BeginInvoke(() =>
            {
                _settingsWindow?.ShowPlaygroundPipeline(report);
                if (report.CleanupEnabled &&
                    report.Cleanup?.Outcome is CleanupOutcome.Cleaned or CleanupOutcome.Unchanged)
                {
                    _trayFeedback.Decide(TrayFeedbackEvent.AiCleanupSuccess);
                }

                // Once per failing episode, as the controller's CleanupFailed event did (only for a failed attempt; a skip
                // because cleanup wasn't ready never raised it). The pill's outcome says "Typed without AI cleanup" either way.
                if (report.Cleanup?.Outcome is CleanupOutcome.Failed &&
                    _trayFeedback.Decide(TrayFeedbackEvent.AiCleanupFailure).Channel == TrayFeedbackChannel.Notice)
                {
                    ShowTrayNotice(TrayNotices.AiCleanupEpisodeFailed());
                }
            });
        _controller.Error += report =>
        {
            Dispatcher.BeginInvoke(() => ShowDictationProblem(report, controllerError: true));
        };
        _controller.Warning += report =>
        {
            // The tray notice stands on its own; the pill's warning belongs to one recording and follows its revision.
            Dispatcher.BeginInvoke(() => ShowDictationProblem(report, controllerError: false));
        };
        _controller.CleanupProviderChanged += message => Dispatcher.BeginInvoke(new Action(() =>
        {
            // Best-effort, exactly like the other tray balloons: a notification failure must never
            // propagate back into a settings save.
            try
            {
                _trayFeedback.Decide(TrayFeedbackEvent.AiCleanupSetupChanged);
                ShowTrayNotice(TrayNotices.AiCleanupActivation(message));
            }
            catch (Exception ex)
            {
                _appLog?.LogDebug("Could not show the AI cleanup provider notification ({Failure}).", FailureShape.Describe(ex));
            }
        }));

        _controller.InjectionFailed += () =>
        {
            // The failed dictation survives in LastTranscriptStore; a balloon closes the loop so
            // the user knows the tray menu can recover it. Best-effort: a notification failure
            // must never throw back into the dictation processing path.
            log.LogDebug("Injection failure event received; controller error owns user feedback.");
        };

        // Warm-load the ~600 MB recognizer and the VAD model off the UI thread so the first
        // dictation is fast and does not stall on model initialization.
        var transcription = services.GetRequiredService<ITranscriptionService>();
        var vad = services.GetRequiredService<IVadService>();
        _ = Task.Run(() =>
        {
            try
            {
                vad.Initialize();
                transcription.Initialize();
                log.LogInformation("Transcription engine warm-loaded.");

                // PerfFlags.WarmManagedAudioPath (off by default, so this does nothing): once per process, the managed
                // code the first dictation's stop compiles, after the models so it never competes with their load.
                // Never throws.
                services.GetRequiredService<ManagedAudioPathWarmup>().RunOnce();
            }
            catch (FileNotFoundException ex)
            {
                log.LogInformation(
                    "No transcription model is installed; waiting for a Settings selection ({Failure}).",
                    FailureShape.Describe(ex));
                SetTrayCondition(TrayCondition.NoSpeechModel);
                ShowTrayNotice(TrayNotices.NoSpeechModel());
            }
            catch (Exception ex)
            {
                log.LogError("Failed to warm-load the transcription engine: {Failure}", FailureShape.DescribeWithStack(ex));
                SetTrayCondition(TrayCondition.SpeechModelFailed);
                ShowTrayNotice(TrayNotices.SpeechModelFailed());
            }
        });

        // Before Start(): the startup reclaim of an earlier session's Foundry Local leftovers runs in the
        // background right after the first Configure, which Start() makes.
        var cleanupService = services.GetRequiredService<ITextCleanupService>();
        cleanupService.FoundryStorageReclaimed += OnFoundryStorageReclaimed;
        cleanupService.StatusChanged += RefreshFoundryDownloadedModel;

        _controller.Start();
        StartupStages?.Mark("hook");
        AccentContrastResources.UseSource(_controller.CurrentSettings.AccentSource);
        RefreshFoundryDownloadedModel();

        // Settings-dependent wiring goes AFTER Start(): CurrentSettings returns compiled defaults
        // until Start() loads the persisted settings, so reading it earlier silently ignored the
        // user's saved overlay position, overlay toggle, and AI-cleanup state on every launch.
        // The helper's idle lifetime follows the speech models' keep-warm setting but is decided inside
        // the client, never on the models' release, which could end a newer recording's pill. The period and
        // whether the helper is kept resident (the idle deadline then trims it rather than ending it) are pushed
        // together, here before the warmup can arm the deadline, and again with every state change. Resident means
        // the pill is on and dictation is not paused (OverlayWarmup decides); dictation always starts unpaused.
        _overlay.SetKeepWarm(
            _controller.CurrentSettings.ReleaseModelsAfterIdleMinutes,
            OverlayWarmup.KeepResident(_controller.CurrentSettings.ShowOverlay, paused: false));
        _overlay.SetPosition(_controller.CurrentSettings.OverlayPosition);
        _ = SeedRecentDictationsAsync(services, services.GetRequiredService<HistoryDeletionNotifier>());
        UpdateTrayShortcut(_controller.CurrentSettings);
        // Pre-warm the out-of-process WinUI pill so its transparent surface is ready before first
        // use. Only spawn the helper when the overlay is actually enabled; if the user turns it on
        // later, the settings save warms it. A warmed helper left unused is trimmed after the keep-warm
        // period and kept running, so the next pill shows at once.
        if (_controller.CurrentSettings.ShowOverlay)
        {
            _overlay.Warmup();
        }

        StartupStages?.Mark("warmup");
        _tray.SetAiCleanupChecked(_controller.CurrentSettings.EnableAiCleanup);

        // History text, stored audio, the AI cleanup failure log and damaged-database copies are
        // kept bounded by Core for the whole session, off the UI thread: shortly after startup,
        // hourly, and soon after audio is stored or history is deleted. The retention setting is
        // read live, so a change in Settings applies without a restart. The one-time VACUUM that
        // shrinks an old database runs only while no dictation is in flight and no window that
        // writes on the UI thread (Settings, quick add) is open; Core asks again right before it
        // starts. A dictation starting makes maintenance yield at once: a running VACUUM is
        // interrupted and rolls back, so the history write at its end never queues behind one.
        // OnExit stops it first, right after the controller begins shutting down, so a VACUUM in
        // flight is interrupted before anything else is torn down; the host's disposal repeats that.
        var controller = _controller;
        var historyMaintenance = services.GetRequiredService<IHistoryMaintenance>();
        var storageMaintenance = services.GetRequiredService<StorageMaintenance>();
        _storageMaintenance = storageMaintenance;

        // Whether a dictation is in flight comes from the controller's lifecycle; this handler is only the
        // push that makes maintenance let go the moment one starts.
        controller.StateChanged += state =>
        {
            if (state.State is DictationState.Recording or DictationState.Processing)
            {
                historyMaintenance.RequestYield();
            }
        };
        storageMaintenance.Start(
            () => controller.CurrentSettings,
            () => !controller.IsDictationInFlight && _settingsWindow is null && _quickAddWindow is null);
        StartupStages?.Mark("maintenance");

        log.LogInformation(
            "Scribe started. Dictation hotkey {Key} ({Mode}), dictation-only hotkey {DictationOnlyKey}.",
            HotkeyText.Describe(_controller.CurrentSettings.Hotkey),
            _controller.CurrentSettings.Hotkey.Mode,
            _controller.CurrentSettings.DictationOnlyHotkey is { } dictationOnly ? HotkeyText.Describe(dictationOnly) : "none");
        LogStartupStages(log, services.GetRequiredService<PerfFlags>());

        // The accelerator inventory itself is on the session banner above ("compute: ..."); only
        // the advice is repeated here, because a recommendation deserves its own Warning line
        // rather than being buried in a banner a reader skims past.
        try
        {
            if (Scribe.Core.Diagnostics.ComputeCapabilityReport.Detect().Recommendation is { } advice)
            {
                log.LogWarning("{Advice}", advice);
            }
        }
        catch (Exception ex)
        {
            log.LogDebug("Compute capability detection failed ({Failure}).", FailureShape.Describe(ex));
        }

        // The dictionary seed above forced database initialization, so a corruption repair (if any)
        // already ran; tell the user now rather than let them discover missing history on their own.
        // What it says follows what the repair brought back: an old single message claimed settings
        // were recovered even when the salvage got none of them, which is exactly the silent reset a
        // user cannot diagnose.
        var database = services.GetRequiredService<ScribeDatabase>();
        if (database.RepairedAtStartup)
        {
            var (notice, _) = DatabaseRepairNotice.Compose(
                database.SettingsLostInRepair, database.DictionaryLostInRepair);
            ShowTrayNotice(TrayNotices.DatabaseRepaired(notice));
        }

        // --- Onboarding (first-run welcome) -------------------------------------------------
        // Tray-only app has no main window, so a brand-new user sees nothing and may never learn
        // the push-to-talk gesture. Show a one-time welcome once settings are loaded, then persist
        // the flag so it never reappears. Kept as a self-contained block for a clean merge.
        var settingsLoadFailed = settingsRepository.LastLoadFailed;
        if (FirstRunWelcome.ShouldShow(_controller.CurrentSettings.HasCompletedFirstRun, settingsLoadFailed))
        {
            ShowWelcome();
            var repo = services.GetRequiredService<ISettingsRepository>();
            if (!repo.LastLoadFailed)
            {
                // One field, atomically, and deliberately still on this thread, before any window can take input: the
                // Settings window saves its whole document, so a copy it loaded before this write landed would put the
                // welcome back on the next launch. It runs once per install.
                try
                {
                    repo.Update(stored => stored.HasCompletedFirstRun = true);
                }
                catch (Exception ex)
                {
                    log.LogWarning(
                        "Could not record that the welcome was shown; it will show again at the next launch ({Failure}).",
                        FailureShape.Describe(ex));
                }
            }
            else
            {
                // The stored settings were unreadable, or a repair lost them: the welcome flag is not written over
                // them, and the tray says so in words true for both.
                SetTrayCondition(TrayCondition.DefaultSettings);
                ShowTrayNotice(TrayNotices.SavedSettingsStartup());
            }
        }
        else if (settingsLoadFailed)
        {
            SetTrayCondition(TrayCondition.DefaultSettings);
            ShowTrayNotice(TrayNotices.SavedSettingsStartup());
        }
        // --- End onboarding -----------------------------------------------------------------

        // Update checks are user-initiated from Settings so the offline-first startup path performs
        // no network access. Previously staged updates are detected by the same manual check.
        _updates = new UpdateService(services.GetRequiredService<ILogger<UpdateService>>());
        _updates.UpdateReady += message =>
        {
            var version = _updates.PendingVersion;
            _tray?.SetUpdateReady(true, version);
            ShowTrayNotice(TrayNotices.UpdateReady(version ?? UpdateService.RunningVersion));
        };
        _updates.ProbePendingLocal();

        // Finish synchronous initialization before yielding to WinRT. Store installs need a
        // manifest startup task, not a virtualized Run key; existing opt-ins migrate here.
        // An isolated data folder (SCRIBE_DATA_DIR) never reconciles: its preference belongs to a
        // scratch profile, while the Run entry or task belongs to the installed app, which a
        // direct-download build would otherwise delete or repoint at this build.
        if (paths.IsIsolatedRoot)
        {
            log.LogInformation("Startup registration not reconciled: this instance uses an isolated data folder.");
        }
        else if (!settingsRepository.LastLoadFailed)
        {
            var startup = await services.GetRequiredService<StartupRegistration>()
                .SyncAsync(_controller.CurrentSettings.LaunchOnLogin);
            if (!startup.IsKnown)
            {
                log.LogWarning("Startup registration could not be reconciled: Windows did not report its startup state.");
            }
        }

        return true;
    }

    /// <summary>
    /// Ends a start that failed partway: records why, tells the user, and shuts down, which releases the
    /// single-instance mutex so the next launch starts cleanly. Never throws.
    /// </summary>
    private void AbandonStartup(Exception failure)
    {
        try
        {
            // If the host never started, the sink it would have used writes the line itself.
            var log = _appLog ?? LogSink?.CreateLogger(typeof(App).FullName!);
            log?.LogCritical("Scribe could not start: {Failure}", FailureShape.DescribeWithStack(failure));
            LogSink?.Flush(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Nothing is left to write to; the notice still tells the user.
        }

        // The newer-schema message wherever that failure surfaces, since it tells the user exactly what to do.
        if (failure is NewerDatabaseSchemaException)
        {
            ShowNewerDataNotice();
        }
        else
        {
            ShowStartupFailureNotice();
        }

        try
        {
            Shutdown();
        }
        catch
        {
            // A process that cannot shut down must still not keep the mutex.
            Environment.Exit(1);
        }
    }

    private static void ShowStartupFailureNotice()
    {
        try
        {
            var log = LogSink?.CurrentStatus();
            var notice = StartupNotices.StartupFailure(log is { } status && status.Healthy ? status.Path : null);
            MessageBox.Show(
                notice.Body,
                notice.Title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Exiting is still right if Windows cannot show the dialog.
        }
    }

    private static Scribe.Core.Settings.SettingsPage RequestedSettingsPage(IEnumerable<string> args, out bool hasSettingsSwitch)
    {
        hasSettingsSwitch = Scribe.Core.Settings.SettingsNavigation.TryParseSettingsArgument(args, out var page);
        return hasSettingsSwitch ? page : Scribe.Core.Settings.SettingsPage.Dictation;
    }

    private static void ShowFatalDataPathNotice(Exception exception)
    {
        try
        {
            var folder = exception switch
            {
                AppPathsCreationException creation => creation.PreferredRootDir,
                IsolatedDataFolderException isolated => isolated.RootDir,
                _ => null,
            };
            var notice = StartupNotices.DataFolderProblem(folder);
            MessageBox.Show(
                notice.Body,
                notice.Title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // If Windows cannot show the dialog, there is no safe startup path left.
        }
    }

    private static void ShowNewerDataNotice()
    {
        try
        {
            var notice = StartupNotices.NewerDatabase();
            MessageBox.Show(
                notice.Body,
                notice.Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // Exiting is still right if Windows cannot show the dialog.
        }
    }

    private static void ShowDataPathFallbackNotice(AppPaths paths)
    {
        try
        {
            var notice = StartupNotices.TemporaryDataFolder(paths.PreferredRootDir, paths.RootDir);
            MessageBox.Show(
                notice.Body,
                notice.Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // A failure showing the warning must not undo the fallback startup.
        }
    }

    /// <summary>
    /// Raises the cross-process signal that asks the running instance to show Settings. Returns
    /// false when no instance is listening, so the caller can fall back to the notice.
    /// </summary>
    private static bool TrySignalShowSettings(Scribe.Core.Settings.SettingsPage page)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ShowSettingsEventName, out var signal))
            {
                return false;
            }

            using (signal)
            {
                TryWriteSettingsRequest(page);
                return signal.Set();
            }
        }
        catch
        {
            // A second launch must never crash on its way out; the notice is the fallback.
            return false;
        }
    }

    /// <summary>
    /// Listens for <see cref="ShowSettingsEventName"/> for the life of the process. The wait is
    /// registered on a pool thread, so opening Settings hops back to the dispatcher.
    /// </summary>
    private void StartShowSettingsListener()
    {
        try
        {
            _showSettingsPayload = MemoryMappedFile.CreateOrOpen(ShowSettingsPayloadName, ShowSettingsPayloadBytes);
            _showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
            _showSettingsRegistration = ThreadPool.RegisterWaitForSingleObject(
                _showSettingsSignal,
                (_, timedOut) =>
                {
                    if (!timedOut)
                    {
                        var page = ReadSettingsRequestPage();
                        Dispatcher.BeginInvoke(new Action(() => OpenSettings(page)));
                    }
                },
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch
        {
            // Losing the listener only costs the shortcut; the tray menu still opens Settings.
            _showSettingsSignal = null;
            _showSettingsRegistration = null;
            _showSettingsPayload?.Dispose();
            _showSettingsPayload = null;
        }
    }

    private static void TryWriteSettingsRequest(Scribe.Core.Settings.SettingsPage page)
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(ShowSettingsPayloadName);
            using var accessor = map.CreateViewAccessor(0, ShowSettingsPayloadBytes);
            var bytes = Encoding.UTF8.GetBytes(page.ToString());
            var length = Math.Min(bytes.Length, ShowSettingsPayloadBytes - sizeof(int));
            accessor.Write(0, length);
            accessor.WriteArray(sizeof(int), bytes, 0, length);
        }
        catch
        {
            // The event still opens Dictation in the running instance.
        }
    }

    private Scribe.Core.Settings.SettingsPage ReadSettingsRequestPage()
    {
        try
        {
            if (_showSettingsPayload is null)
            {
                return Scribe.Core.Settings.SettingsPage.Dictation;
            }

            using var accessor = _showSettingsPayload.CreateViewAccessor(0, ShowSettingsPayloadBytes);
            var length = accessor.ReadInt32(0);
            if (length <= 0 || length > ShowSettingsPayloadBytes - sizeof(int))
            {
                return Scribe.Core.Settings.SettingsPage.Dictation;
            }

            var bytes = new byte[length];
            accessor.ReadArray(sizeof(int), bytes, 0, length);
            return Scribe.Core.Settings.SettingsNavigation.TryParsePage(Encoding.UTF8.GetString(bytes), out var page)
                ? page
                : Scribe.Core.Settings.SettingsPage.Dictation;
        }
        catch
        {
            return Scribe.Core.Settings.SettingsPage.Dictation;
        }
    }

    /// <summary>
    /// Shows the Fluent-themed "already running" notice modally during startup. The dispatcher loop
    /// has not begun pumping yet at this point, so a nested <see cref="DispatcherFrame"/> keeps the
    /// dialog responsive until the user dismisses it, then unwinds so the second instance can exit.
    /// </summary>
    private void ShowSingleInstanceNotice()
    {
        var dialog = ThemedNotice.Create(
            StartupNotices.AlreadyRunning().Title,
            StartupNotices.AlreadyRunning().Body);

        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = dialog.ShowDialogAsync().ContinueWith(
            _ => frame.Continue = false,
            System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// Hands a dictation state change to the relay, which shows it on the UI thread unless a newer one has been shown
    /// already. Raised on background threads (and on the UI thread for a pause), so nothing here may wait.
    /// </summary>
    private void OnStateChanged(DictationStateChange change) => _dictationState?.Publish(change.Revision, change);

    /// <summary>
    /// Reflects the newest dictation state in the tray icon and the recording overlay. Runs on the UI thread, only for a
    /// change newer than the last one shown, so a late notice from the previous dictation can never hide the pill of the
    /// recording that started after it. The overlay only shows while recording and only when the user has it enabled; the
    /// return to idle that ends a dictation shows its outcome in place of the hide, under the same rule, so a late outcome
    /// never covers a newer recording.
    /// </summary>
    private void RenderDictationState(DictationStateChange change)
    {
        var state = change.State;
        var previous = _lastRenderedState;
        _lastRenderedState = state;

        // The tray and the overlay are independent views of the same state. A failure updating one
        // must never stop the other: when this method threw, the overlay was left showing whatever
        // it had last been told, so the pill sat on "Transcribing" while dictation kept working.
        // DictationStateViews gives each view the change once, in order, and still gives it to the second view when the
        // first throws. PillBeforeTray picks the order: the pill's commands first and the tray icon (a new icon and the
        // shell's own notify calls) after them, so the pill's thread starts on them sooner; off, the tray first, as before.
        // Either way both come before the warmup and the release below, which a throw from the pill's calls skips, as before.
        var overlayEnabled = false;
        DictationStateViews.Show(
            pillFirst: _perfFlags.IsOn(PerfFlags.PillBeforeTray),
            tray: () => UpdateTrayState(state),
            pill: () =>
            {
                // Read here, at the render, so a setting saved since the change was raised applies to it.
                var settings = _controller?.CurrentSettings;
                overlayEnabled = settings?.ShowOverlay ?? false;

                // Pushed with every state change, so a keep-warm saved in Settings reaches the overlay client
                // without its command thread ever reading the controller's settings. Unchanged values cost nothing.
                // The resident flag is for the state rendered here, so a pause pushes false before its release below (the
                // push is unstamped and vetoes nothing): while paused the idle deadline ends whatever brought the helper
                // back, a preview or a release that a stamped command vetoed, instead of trimming it until the resume. The
                // resume pushes true.
                if (settings is not null)
                {
                    _overlay?.SetKeepWarm(
                        settings.ReleaseModelsAfterIdleMinutes,
                        OverlayWarmup.KeepResident(settings.ShowOverlay, state == DictationState.Paused));
                }

                if (!overlayEnabled)
                {
                    _overlay?.HideOverlay();
                }
                else
                {
                    switch (state)
                    {
                        case DictationState.Recording:
                            _overlay?.ShowRecording();
                            break;
                        case DictationState.Processing:
                            // The dictation-only hotkey overrides AI cleanup for its capture without changing the global
                            // setting, so this comes from the capture that was admitted, carried with the change.
                            _overlay?.ShowProcessing(change.AiPolishing);
                            break;
                        default:
                            // What the finished dictation did ("Typed", or a notice), held by the overlay and then hidden;
                            // a quietly discarded one, a pause while idle and every other idle change just hide.
                            if (change.Outcome is { } outcome)
                            {
                                _overlay?.ShowOutcome(outcome);
                            }
                            else
                            {
                                _overlay?.HideOverlay();
                            }

                            break;
                    }
                }
            });

        // A pause released the helper; the first dictation after the resume would otherwise wait for a launch (0.5 s idle,
        // 2 to 11 s while the speech models reload), so the resume warms it again (OverlayWarmup decides).
        if (OverlayWarmup.AfterRender(previous == DictationState.Paused, state == DictationState.Paused, overlayEnabled))
        {
            _overlay?.Warmup();
        }

        // Pausing is the user standing Scribe down, so the helper is ended now, handing back its whole ~100 MB
        // of private memory (an idle trim only empties its working set), as pausing already does for the speech
        // models. Only the newest change gets here, so a pause shown late can never release the helper under a newer
        // recording; a newer command or an on-screen state still vetoes it inside the client as well,
        // and an outcome the pause's own dictation just showed holds the release until it has hidden.
        if (state == DictationState.Paused)
        {
            _overlay?.ReleaseWhenIdle();
        }
    }

    // The tray's view of a dictation state, guarded on its own so a failure never stops the pill's (see RenderDictationState).
    private void UpdateTrayState(DictationState state)
    {
        try
        {
            _tray?.SetState(state);
        }
        catch (Exception ex)
        {
            _appLog?.LogWarning("Could not update the tray icon for state {State} ({Failure}).", state, FailureShape.Describe(ex));
        }
    }

    /// <summary>
    /// Shows a warning on the recording pill, in the same ordered queue as the state changes and only while the recording
    /// it belongs to is still what the pill shows. Showing a warning puts the pill in its recording state, so one that ran
    /// after a pause or a stop had been shown would bring back a recording pill that nothing would ever hide.
    /// </summary>
    private void ShowRecordingWarningOrNotice(DictationProblemReport report, string reason, DictationProblemNotice notice)
    {
        if (_dictationState is not { } relay)
        {
            ShowTrayNotice(new TrayNotice(notice.Title, notice.Body, notice.Kind, notice.Action));
            return;
        }

        // The same revision twice on purpose: here only a warning for a recording that was already over (revision 0) is
        // decided. Whether its recording is still the one shown is decided by PublishIfCurrent below, in queue order after
        // that recording's own change has rendered; reading the relay's last revision here could run ahead of it.
        if (DictationProblemRouting.DecideRecordingWarning(
                report.Problem,
                recordingIndicatorOn: _controller?.CurrentSettings.ShowOverlay == true,
                report.RecordingRevision,
                report.RecordingRevision) == DictationProblemSurface.Notice)
        {
            ShowTrayNotice(new TrayNotice(notice.Title, notice.Body, notice.Kind, notice.Action));
            return;
        }

        // The indicator is judged again when the warning's turn comes: a Save can turn it off between here and there, and
        // then the notice is the one place the warning shows.
        relay.PublishIfCurrent(
            report.RecordingRevision,
            () =>
            {
                if (_controller?.CurrentSettings.ShowOverlay == true)
                {
                    _overlay?.ShowRecordingWarning(reason);
                }
                else
                {
                    ShowTrayNotice(new TrayNotice(notice.Title, notice.Body, notice.Kind, notice.Action));
                }
            },
            () => ShowTrayNotice(new TrayNotice(notice.Title, notice.Body, notice.Kind, notice.Action)));
    }

    /// <summary>
    /// Tells the user once, with a tray notice, that Scribe gave back disk space Foundry Local was using.
    /// Raised on a background thread by the cleanup service, possibly while the host is stopping, so it is
    /// marshalled without blocking that thread and nothing here throws back into it. The log gets numbers
    /// and the reason code only.
    /// </summary>
    private void OnFoundryStorageReclaimed(FoundryStorageReclaim reclaim)
    {
        RefreshFoundryDownloadedModel();
        try
        {
            var notice = FoundryStorageReclaimNotice.Format(reclaim);
            _appLog?.LogDebug(
                "Foundry Local storage reclaim notice: reason={Reason} bytes={Bytes} models={Models} files={Files} nextStart={NextStart} shown={Shown}.",
                reclaim.Reason,
                reclaim.BytesFreed,
                reclaim.ModelsRemoved,
                reclaim.FilesDeleted,
                reclaim.RuntimeDeletedAtNextStart,
                notice is not null);
            if (notice is null || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    ShowTrayNotice(TrayNotices.FoundryStorageReclaimed(notice));
                }
                catch (Exception ex)
                {
                    _appLog?.LogDebug("Could not show the Foundry Local storage notice ({Error}).", ex.GetType().Name);
                }
            });
        }
        catch (Exception ex)
        {
            try
            {
                _appLog?.LogDebug("Could not queue the Foundry Local storage notice ({Error}).", ex.GetType().Name);
            }
            catch (Exception)
            {
                // A notice is a courtesy; its failure must never reach the cleanup service's thread.
            }
        }
    }

    /// <summary>
    /// Quick tray toggle for AI cleanup: persist the flipped flag and apply it live, without
    /// opening settings. Lets the user hop between raw Parakeet output and AI-polished text in
    /// two clicks. The flag is saved on a worker, in click order, as a one-field change, and
    /// applied once it is stored; an open settings window adopts it at once so its next save keeps it.
    /// </summary>
    private void ToggleAiCleanup(bool enabled)
    {
        // Taken first, so an open settings window can tell this change, and word of how it ended, from anything newer.
        var revision = Scribe.Core.Settings.ExternalSwitchSync.NextRevision();
        try
        {
            var repo = _host!.Services.GetRequiredService<ISettingsRepository>();
            if (repo.LastLoadFailed)
            {
                _tray?.SetAiCleanupChecked(_controller?.CurrentSettings.EnableAiCleanup ?? false);
                ShowTrayNotice(TrayNotices.SavedSettingsTray("AI cleanup"));
                return;
            }

            // Refused only once quitting has begun, when nothing is left to apply the change to.
            if (_settingsWrites?.Submit(
                    stored => stored.EnableAiCleanup = enabled,
                    revision,
                    (stored, superseded) => OnAiCleanupToggleSaved(stored, superseded, revision),
                    error => OnAiCleanupToggleFailed(error, revision)) == true)
            {
                // At once, like the tray item itself, and even while the window is recording a hotkey (its switch then
                // catches up when the recording ends): a Settings save made before the write lands carries the switch
                // instead of putting back the window's older value.
                _settingsWindow?.AdoptExternalAiCleanup(enabled, revision);
            }
        }
        catch (Exception ex)
        {
            OnAiCleanupToggleFailed(ex, revision);
        }
    }

    // Runs on the UI thread with the settings as stored, which are the truth even when a Settings save landed meanwhile,
    // so dictation and the tray always take them, never the value asked for. A superseded change was never written: a
    // Settings save that accounted for it committed first, so the window already holds a newer choice and is not told
    // again. Otherwise an open window takes it unless something newer, such as a later click in it, has set its switch.
    private void OnAiCleanupToggleSaved(AppSettings stored, bool superseded, long revision)
    {
        // The switch changes no vocabulary, so nothing here waits for the generation ApplySettings asks for.
        _ = _controller?.ApplySettings(stored);
        RefreshFoundryDownloadedModel();
        if (!superseded)
        {
            _settingsWindow?.AdoptExternalAiCleanup(stored.EnableAiCleanup, revision);
        }

        _settingsWindow?.AdoptStoredSettings(stored);
        _tray?.SetAiCleanupChecked(stored.EnableAiCleanup);
        ShowTrayNotice(TrayNotices.AiCleanupActivation(stored.EnableAiCleanup
            ? "AI cleanup is on. Scribe fixes punctuation and grammar before it types. If AI cleanup isn't ready, Scribe types what it hears."
            : "AI cleanup is off. Scribe types what it hears, with your dictionary and snippets."));
    }

    private void OnAiCleanupToggleFailed(Exception ex, long revision)
    {
        try
        {
            _appLog?.LogWarning("Toggling AI cleanup from the tray failed ({Failure}).", FailureShape.Describe(ex));
        }
        catch
        {
            // The log must never stop the tray from showing the real state.
        }

        // Back to what dictation is actually using.
        var current = _controller?.CurrentSettings.EnableAiCleanup ?? false;
        _settingsWindow?.AdoptExternalAiCleanup(current, revision);
        _tray?.SetAiCleanupChecked(current);
        ShowTrayNotice(TrayNotices.AiCleanupChangeFailed());
    }

    /// <summary>
    /// The tray's microphone picker, built each time the menu opens from the devices Windows offers now and the current
    /// choice: a tray choice still being saved, otherwise what dictation uses. Throws when the devices cannot be read,
    /// which the tray shows as "Microphones unavailable".
    /// </summary>
    private Scribe.Core.Settings.MicrophoneMenu BuildTrayMicrophoneMenu()
    {
        var devices = _host!.Services.GetRequiredService<IAudioCaptureService>().GetInputDevices();
        return Scribe.Core.Settings.MicrophoneChoices.Build(devices, CurrentTrayMicrophone());
    }

    private Scribe.Core.Settings.MicrophoneSelection CurrentTrayMicrophone() =>
        _pendingTrayMicrophone?.Selection
        ?? (_controller is { } controller
            ? Scribe.Core.Settings.MicrophoneSelection.From(controller.CurrentSettings)
            : Scribe.Core.Settings.MicrophoneSelection.WindowsDefault);

    /// <summary>
    /// The tray's microphone choice, saved exactly like the AI cleanup toggle: a revision taken first, a one-field change
    /// written on the settings lane (superseded only by a Settings save whose own microphone intent accounts for it), and
    /// an open settings window told at once so its next save keeps the choice. Dictation uses it from the next press,
    /// once it is stored.
    /// </summary>
    private void ChooseMicrophone(Scribe.Core.Settings.MicrophoneSelection chosen)
    {
        // Taken first, so an open settings window can tell this change, and word of how it ended, from anything newer.
        var revision = Scribe.Core.Settings.ExternalSwitchSync.NextRevision();
        var selection = Scribe.Core.Settings.MicrophoneSelection.Normalize(chosen.DeviceId, chosen.DeviceName);
        try
        {
            var repo = _host!.Services.GetRequiredService<ISettingsRepository>();
            if (repo.LastLoadFailed)
            {
                ShowTrayNotice(TrayNotices.SavedSettingsTray("the microphone"));
                return;
            }

            // Choosing what is already chosen writes nothing.
            if (selection == CurrentTrayMicrophone())
            {
                return;
            }

            // Refused only once quitting has begun, when nothing is left to apply the change to.
            if (_settingsWrites?.Submit(
                    stored => selection.ApplyTo(stored),
                    ExternalSetting.Microphone,
                    revision,
                    (stored, superseded) => OnMicrophoneChoiceSaved(stored, superseded, revision),
                    error => OnMicrophoneChoiceFailed(error, revision)) == true)
            {
                _pendingTrayMicrophone = (selection, revision);
                _settingsWindow?.AdoptExternalMicrophone(selection, revision);
            }
        }
        catch (Exception ex)
        {
            OnMicrophoneChoiceFailed(ex, revision);
        }
    }

    // Runs on the UI thread with the settings as stored, which are the truth even when a Settings save landed meanwhile,
    // so dictation and the tray always take them, never the choice asked for. A superseded change was never written: a
    // Settings save that accounted for it committed first, so the window already holds a newer choice and is not told
    // again. Otherwise an open window takes it unless something newer, such as a later choice in it, has set its picker.
    private void OnMicrophoneChoiceSaved(AppSettings stored, bool superseded, long revision)
    {
        ClearPendingTrayMicrophone(revision);

        // A microphone changes no vocabulary, so nothing here waits for the generation ApplySettings asks for.
        _ = _controller?.ApplySettings(stored);
        var storedChoice = Scribe.Core.Settings.MicrophoneSelection.From(stored);
        if (!superseded)
        {
            _settingsWindow?.AdoptExternalMicrophone(storedChoice, revision);
        }

        _settingsWindow?.AdoptStoredSettings(stored);

        // The check mark is the feedback for a successful microphone change.
    }

    private void OnMicrophoneChoiceFailed(Exception ex, long revision)
    {
        try
        {
            _appLog?.LogWarning("Choosing a microphone from the tray failed ({Failure}).", FailureShape.Describe(ex));
        }
        catch
        {
            // The log must never stop the tray from showing the real state.
        }

        // Back to what dictation is actually using.
        ClearPendingTrayMicrophone(revision);
        if (_controller is { } controller)
        {
            _settingsWindow?.AdoptExternalMicrophone(
                Scribe.Core.Settings.MicrophoneSelection.From(controller.CurrentSettings), revision);
        }

        ShowTrayNotice(TrayNotices.MicrophoneChangeFailed());
    }

    // Only the choice this word is about: a newer tray choice keeps showing until its own word arrives.
    private void ClearPendingTrayMicrophone(long revision)
    {
        if (_pendingTrayMicrophone is { } pending && pending.Revision == revision)
        {
            _pendingTrayMicrophone = null;
        }
    }

    private void ShowTrayNotice(TrayNotice notice) => _tray?.ShowNotice(notice);

    private void SetTrayCondition(TrayCondition condition)
    {
        _trayCondition = condition;
        _tray?.SetCondition(condition, _updates?.PendingVersion);
    }

    private TrayCondition CurrentTrayCondition() => _trayCondition;

    private bool HasRecentDictationForTray()
    {
        if (_host is null)
        {
            return false;
        }

        return _host.Services.GetRequiredService<LastTranscriptStore>().GetRecent().Count > 0;
    }

    private TrayAiCleanupItem DescribeTrayAiCleanup()
    {
        var settings = _controller?.CurrentSettings ?? AppSettings.CreateDefault();
        var status = _host?.Services.GetRequiredService<ITextCleanupService>().Status ?? CleanupStatus.Disabled;
        var recovered = _host?.Services.GetRequiredService<ISettingsRepository>().LastLoadFailed ?? false;
        var setupComplete = settings.AiCleanupProvider != CleanupProvider.FoundryLocal || _foundryDownloadedModel;
        return TrayAiCleanup.Describe(settings, setupComplete, status, recovered);
    }

    private void RefreshFoundryDownloadedModel()
    {
        if (_host is null)
        {
            return;
        }

        var services = _host.Services;
        var revision = _foundryRefresh.Next();
        _ = Task.Run(() =>
        {
            try
            {
                var hasModel = FoundryLocalStorage.HoldsDownloadedModel(services.GetRequiredService<AppPaths>());
                if (Dispatcher.HasShutdownStarted)
                {
                    return;
                }

                Dispatcher.BeginInvoke(() =>
                {
                    if (!_foundryRefresh.IsLatest(revision))
                    {
                        return;
                    }

                    _foundryDownloadedModel = hasModel;
                    if (_tray is not null)
                    {
                        _tray.SetAiCleanupChecked(_controller?.CurrentSettings.EnableAiCleanup ?? false);
                    }
                });
            }
            catch (Exception ex)
            {
                _appLog?.LogDebug("Could not inspect the Foundry Local model cache ({Failure}).", FailureShape.Describe(ex));
            }
        });
    }

    private void UpdateTrayShortcut(AppSettings settings)
    {
        _tray?.SetShortcutSentence(HotkeyText.SentenceName(settings.Hotkey), settings.Hotkey.Mode);
    }

    private async Task SeedRecentDictationsAsync(IServiceProvider services, HistoryDeletionNotifier deletions)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var revision = deletions.Revision;
            try
            {
                var recent = await Task.Run(() => services.GetRequiredService<IHistoryRepository>()
                    .GetRecent(LastTranscriptStore.Capacity)
                    .Where(h => !string.IsNullOrWhiteSpace(h.Text))
                    .ToList());

                if (Dispatcher.HasShutdownStarted || _controller?.IsClosing != false)
                {
                    return;
                }

                var seeded = await Dispatcher.InvokeAsync(() =>
                    services.GetRequiredService<LastTranscriptStore>().SeedHistory(recent, revision, () => deletions.Revision));
                if (seeded)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _appLog?.LogDebug("Could not seed recent dictations from history ({Failure}).", FailureShape.Describe(ex));
                return;
            }
        }
    }

    private void ShowDictationProblem(DictationProblemReport report, bool controllerError)
    {
        // Posted from the dictation path, so it can run after shutdown began; nobody is left to read a notice then.
        if (Dispatcher.HasShutdownStarted || _controller?.IsClosing != false)
        {
            return;
        }

        var settings = _controller?.CurrentSettings;
        var mode = report.ShortcutMode;

        // The shortcut that started the dictation names the key: the one without AI cleanup has its own key and mode.
        var notice = DictationProblemText.Describe(
            report,
            mode,
            (report.Shortcut ?? settings?.Hotkey) is { } hotkey ? HotkeyText.SentenceName(hotkey) : null);
        var routing = DictationProblemRouting.Decide(report.Problem, settings?.ShowOverlay == true);
        if (routing == DictationProblemSurface.PillAndNotice)
        {
            // Only a notice that offers Copy last dictation binds the entry it copies: a no-model notice (Open Settings)
            // must not rebind a Copy notice still on screen to whatever is newest now.
            if (notice.Action == TrayNoticeAction.CopyLastDictation)
            {
                _noticeCopyEntryId = _host?.Services.GetRequiredService<LastTranscriptStore>().CurrentId();
            }

            ShowTrayNotice(new TrayNotice(notice.Title, notice.Body, notice.Kind, notice.Action));
            return;
        }

        if (controllerError && routing == DictationProblemSurface.PillOutcome)
        {
            return;
        }

        if (!controllerError && routing == DictationProblemSurface.RecordingPill)
        {
            ShowRecordingWarningOrNotice(report, DictationProblemText.PillLine(report, mode) ?? notice.Title, notice);
            return;
        }

        if (routing == DictationProblemSurface.Notice || routing == DictationProblemSurface.PillOutcome)
        {
            if (notice.Action == TrayNoticeAction.CopyLastDictation)
            {
                _noticeCopyEntryId = _host?.Services.GetRequiredService<LastTranscriptStore>().CurrentId();
            }

            ShowTrayNotice(new TrayNotice(notice.Title, notice.Body, notice.Kind, notice.Action));
        }
    }

    private QuickAddVocabulary BuildQuickAddVocabulary(IServiceProvider services)
    {
        var vocabulary = services.GetRequiredService<ILibraryVocabularySource>().Current;
        var libraries = new[]
        {
            new DictionaryLibrary("current-word-packs", "Word packs", string.Empty, null, BuiltIn: true, vocabulary.Entries),
        };
        var draft = _settingsWindow is { IsDraftDiscardedForAppExit: false } window ? window : null;
        var personal = draft is not null
            ? draft.CurrentDictionaryEntries()
            : services.GetRequiredService<IDictionaryRepository>().GetAll();
        var pending = draft?.PendingQuickAddSpokenForms() ?? [];
        return QuickAddVocabulary.Compose(personal, pending, libraries, ["current-word-packs"]);
    }

    private void OpenSettingsForDictionary(string spoken)
    {
        OpenSettings(Scribe.Core.Settings.SettingsPage.Dictionary, "DictionaryGrid");
        _settingsWindow?.ShowDictionaryEntry(spoken);
    }

    private void AddQuickAddTermInSettings(string spoken)
    {
        OpenSettings(Scribe.Core.Settings.SettingsPage.Dictionary, "DictionaryGrid");
        _settingsWindow?.AddDictionaryDraft(spoken);
    }

    private void FixQuickAddTermInstead(string spoken)
    {
        if (_quickAddWindow is null || string.IsNullOrWhiteSpace(spoken))
        {
            return;
        }

        _quickAddWindow.UseHeardText(spoken);
    }

    private async void TrayQuit()
    {
        await RunAppExitOperationAsync(() => RunCloseGuardsThenAsync(CloseTrigger.TrayQuit, () =>
        {
            Shutdown();
            return Task.CompletedTask;
        }));
    }

    private async void RestartToUpdate()
    {
        await RunAppExitOperationAsync(() => RunCloseGuardsThenAsync(CloseTrigger.UpdateRestart, () =>
        {
            if (_updates?.ApplyNowAndRestart() != true)
            {
                ShowTrayNotice(TrayNotices.RestartFailed());
            }

            return Task.CompletedTask;
        }));
    }

    private Task RunAppExitOperationAsync(Func<Task> operation)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.InvokeAsync(() => RunAppExitOperationAsync(operation)).Task.Unwrap();
        }

        if (_appExitOperation is { IsCompleted: false } existing)
        {
            return existing;
        }

        // Reserved before the operation starts: its prompts are modal and pump messages, and a Quit or Restart clicked
        // meanwhile must join this exit, not start a second one that runs the restart again.
        var reservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _appExitOperation = reservation.Task;

        async Task RunReservedAsync()
        {
            try
            {
                await operation();
                reservation.TrySetResult();
            }
            catch (Exception ex)
            {
                reservation.TrySetException(ex);
            }
            finally
            {
                if (ReferenceEquals(_appExitOperation, reservation.Task))
                {
                    _appExitOperation = null;
                }
            }
        }

        _ = RunReservedAsync();
        return reservation.Task;
    }

    private async Task RunCloseGuardsThenAsync(CloseTrigger trigger, Func<Task> action)
    {
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() => RunCloseGuardsThenAsync(trigger, action)).Task.Unwrap();
            return;
        }

        var state = CurrentAppExitCloseState(trigger, includeSettingsSave: true);
        var step = AppExitCloseGuard.First(state);
        while (step is AppExitCloseStep.Settings or AppExitCloseStep.QuickAdd)
        {
            var accepted = step switch
            {
                AppExitCloseStep.Settings => _settingsWindow is null || await _settingsWindow.RequestAppCloseAsync(trigger),
                AppExitCloseStep.QuickAdd => _quickAddWindow is null || await _quickAddWindow.RequestAppCloseAsync(),
                _ => true,
            };
            var result = accepted ? AppExitCloseStepResult.Saved : AppExitCloseStepResult.KeepEditing;
            state = step == AppExitCloseStep.Settings
                ? CurrentAppExitCloseState(trigger, includeSettingsSave: false, refreshQuickAdd: true)
                : CurrentAppExitCloseState(trigger, includeSettingsSave: false);
            step = AppExitCloseGuard.Next(step, result, state);
        }

        if (step == AppExitCloseStep.Proceed)
        {
            await action();
        }
    }

    private async Task RunAboutUpdateGuardThenAsync(Func<Task> action)
    {
        await RunAppExitOperationAsync(async () =>
        {
            var closeSettingsAfterAction = false;
            if (_settingsWindow is { } settings)
            {
                var result = await settings.RequestUpdateRestartWithoutClosingAsync();
                if (result == SettingsUpdateRestartGuardResult.Canceled)
                {
                    return;
                }

                closeSettingsAfterAction = result == SettingsUpdateRestartGuardResult.ProceedCloseAfterAction;
            }

            // With Discard chosen, Settings stays open to own the update's UI, but its draft no longer counts, so Add to
            // dictionary is judged against what is stored (IsDraftDiscardedForAppExit) and asks about a correction the
            // discarded draft was blocking.
            var proceeded = false;
            try
            {
                if (_quickAddWindow?.RefreshAppCloseVocabularyAndHasCorrection() == true &&
                    !await _quickAddWindow.RequestAppCloseAsync())
                {
                    return;
                }

                proceeded = true;
                await action();
            }
            finally
            {
                if (_settingsWindow is { } liveSettings)
                {
                    if (proceeded && closeSettingsAfterAction)
                    {
                        liveSettings.CloseAfterAppUpdateDiscard();
                    }
                    else
                    {
                        // The update didn't go ahead, so nothing was discarded: the draft counts again.
                        liveSettings.IsDraftDiscardedForAppExit = false;
                    }
                }
            }
        });
    }

    private AppExitCloseState CurrentAppExitCloseState(CloseTrigger trigger, bool includeSettingsSave, bool refreshQuickAdd = false)
    {
        var settingsUnsaved = _settingsWindow?.HasAppCloseChanges(trigger) == true
            || includeSettingsSave && _settingsWindow?.HasAppCloseSaveInProgress == true;
        var quickAddUnsaved = refreshQuickAdd
            ? _quickAddWindow?.RefreshAppCloseVocabularyAndHasCorrection() == true
            : _quickAddWindow?.HasAppCloseCorrection() == true;
        return new AppExitCloseState(settingsUnsaved, quickAddUnsaved);
    }

    private void OnTrayNoticeAction(TrayNoticeAction action)
    {
        switch (action)
        {
            case TrayNoticeAction.OpenSettings:
                OpenSettings(Scribe.Core.Settings.SettingsPage.Dictation);
                break;
            case TrayNoticeAction.OpenSettingsAiCleanup:
                OpenSettings(Scribe.Core.Settings.SettingsPage.AiCleanup);
                break;
            case TrayNoticeAction.OpenSettingsDictionary:
                OpenSettings(Scribe.Core.Settings.SettingsPage.Dictionary);
                break;
            case TrayNoticeAction.OpenSettingsDiagnostics:
                OpenSettings(Scribe.Core.Settings.SettingsPage.Diagnostics);
                break;
            case TrayNoticeAction.OpenSettingsHistory:
                OpenSettings(Scribe.Core.Settings.SettingsPage.History);
                break;
            case TrayNoticeAction.CopyLastDictation:
            case TrayNoticeAction.RetryCopy:
                CopyNoticeDictation();
                break;
            case TrayNoticeAction.OpenSoundSettings:
                OpenSoundSettings();
                break;
        }
    }

    private void CopyNoticeDictation()
    {
        if (_host is null)
        {
            return;
        }

        var id = _noticeCopyEntryId;
        if (id is null)
        {
            CopyLastDictation();
            return;
        }

        var text = _host.Services.GetRequiredService<LastTranscriptStore>().Get(id.Value);
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowTrayNotice(TrayNotices.NothingToCopy());
            return;
        }

        ShowTrayNotice(ScribeClipboard.SetText(text) ? TrayNotices.CopiedLastDictation() : TrayNotices.ClipboardBusy());
    }

    private void SettingsWindow_ShowWelcomeRequested(object? sender, EventArgs e) => ShowWelcome();

    private void OnHistoryDeleted(HistoryDeletion deletion)
    {
        if (_host is null)
        {
            return;
        }

        var store = _host.Services.GetRequiredService<LastTranscriptStore>();
        store.ApplyDeletion(deletion);
        var quickAdd = _quickAddWindow;
        quickAdd?.Dispatcher.BeginInvoke(() => quickAdd.ApplyHistoryDeletion(deletion));
    }

    /// <summary>
    /// Opens Windows Settings at System, Sound, where the default input device is chosen
    /// (https://learn.microsoft.com/windows/apps/develop/launch/launch-settings-app lists ms-settings:sound).
    /// </summary>
    private void OpenSoundSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            try
            {
                _appLog?.LogWarning("Opening the Windows sound settings failed ({Failure}).", FailureShape.Describe(ex));
            }
            catch
            {
                // Best effort, like the notice below.
            }

            ShowTrayNotice(TrayNotices.SoundSettingsFailed());
        }
    }

    /// <summary>
    /// Raised by the capture service when a reading of the devices finds a change: on the watcher's thread pool thread
    /// after a burst of Windows notifications, or on this thread when the tray menu's own reading noticed first. Posted,
    /// never invoked, so neither thread waits for the window, and a failure here must never reach them.
    /// </summary>
    private void OnInputDevicesChanged(IReadOnlyList<AudioDevice> devices)
    {
        try
        {
            if (Dispatcher.HasShutdownStarted)
            {
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (_controller?.IsClosing != true)
                {
                    _settingsWindow?.ShowInputDevices(devices);
                }
            });
        }
        catch
        {
            // Keeping an open window's list live is a courtesy.
        }
    }

    /// <summary>
    /// Opens the settings window (or focuses it if already open). Built per-open from the host so
    /// it always reflects the latest persisted state; on save it calls back into the controller to
    /// apply the new binding and dictionary live.
    /// </summary>
    private void OpenSettings() => OpenSettings(Scribe.Core.Settings.SettingsPage.Dictation);

    private void OpenSettings(Scribe.Core.Settings.SettingsPage page, string? focusName = null) => Dispatcher.Invoke(() =>
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.ShowPage(page, focusName);
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            _settingsWindow.Activate();
            return;
        }

        var services = _host!.Services;
        var openStages = services.GetRequiredService<PerfFlags>().IsOn(PerfFlags.StartupStageTiming) ? StageTimeline.Start() : null;
        var audio = services.GetRequiredService<IAudioCaptureService>();

        // While the window is open its saves run on this thread: the one-time VACUUM stays off until it
        // closes, and one already running stops now.
        var foreground = services.GetRequiredService<StorageMaintenance>().EnterForegroundWork();
        try
        {
            // Only while the window is open: its microphone list follows device changes, and the watcher behind the
            // capture service keeps checking that it can still hear Windows (the tray reads the devices whenever its
            // menu opens, so it needs neither). Before the window reads its list, so a change any later reading finds
            // reaches it; the handler posts, so it runs once the window is in place.
            audio.InputDevicesChanged += OnInputDevicesChanged;
            _settingsWindow = new SettingsWindow(
                services.GetRequiredService<ISettingsRepository>(),
                audio,
                services.GetRequiredService<IDictionaryRepository>(),
                services.GetRequiredService<IDictionaryLibraryService>(),
                services.GetRequiredService<ISnippetRepository>(),
                services.GetRequiredService<IHistoryRepository>(),
                services.GetRequiredService<ITextCleanupService>(),
                services.GetRequiredService<IAzureFoundryDiscovery>(),
                services.GetRequiredService<AzureCliInstaller>(),
                services.GetRequiredService<ILogger<SettingsWindow>>(),
                services.GetRequiredService<ICleanupFailureLog>(),
                services.GetRequiredService<ITranscriptionModelInstaller>(),
                services.GetRequiredService<AppPaths>(),
                services.GetRequiredService<StartupRegistration>(),
                services.GetRequiredService<IOptions<TranscriptionOptions>>(),
                position => _overlay?.Preview(position),
                settings =>
                {
                    // The window has just saved its whole document, or applied the stored settings after storing a
                    // dictionary entry itself, so a tray change still on its way reads the stored settings again before
                    // applying anything. The window awaits what this returns, the answer of the vocabulary generation the
                    // application asked for, before it says the change is in effect.
                    _settingsWrites?.NoteExternalApply();
                    var applying = _controller!.ApplySettings(settings);
                    var paused = _lastRenderedState == DictationState.Paused;
                    _overlay?.SetKeepWarm(
                        settings.ReleaseModelsAfterIdleMinutes,
                        OverlayWarmup.KeepResident(settings.ShowOverlay, paused));
                    _overlay?.SetPosition(settings.OverlayPosition);
                    if (OverlayWarmup.AfterSettingsApplied(settings.ShowOverlay, paused))
                    {
                        _overlay?.Warmup(); // turning the pill on must not leave the next dictation to launch it
                    }

                    _tray?.SetAiCleanupChecked(settings.EnableAiCleanup);
                    AccentContrastResources.UseSource(settings.AccentSource);
                    UpdateTrayShortcut(settings);
                    RefreshFoundryDownloadedModel();
                    return applying;
                },
                () => _controller!.ReloadVocabulary(),
                services.GetRequiredService<ILibraryVocabularySource>(),
                _textScale,
                capturing => _controller?.SetHotkeyCaptureMode(capturing),
                _updates,
                RunAboutUpdateGuardThenAsync,
                () => ShowTrayNotice(TrayNotices.RestartFailed()),
                services.GetRequiredService<SessionDiagnostics>(),
                services.GetRequiredService<HistoryDeletionNotifier>(),
                perfFlags: services.GetRequiredService<PerfFlags>(),
                openStages: openStages);
            openStages?.Mark("ctor");
            _settingsWindow.Closed += (_, _) =>
            {
                audio.InputDevicesChanged -= OnInputDevicesChanged;
                _settingsWindow.ShowWelcomeRequested -= SettingsWindow_ShowWelcomeRequested;
                _settingsWindow = null;
                foreground.Dispose();
            };
            _settingsWindow.ShowWelcomeRequested += SettingsWindow_ShowWelcomeRequested;
            if (openStages is not null)
            {
                LogSettingsOpenStagesWhenRendered(_settingsWindow, openStages);
            }

            _settingsWindow.Show();
            openStages?.Mark("show");
            _settingsWindow.ShowPage(page, focusName);
            openStages?.Mark("page");
            _settingsWindow.Activate();
            openStages?.Mark("activate");
        }
        catch
        {
            // A window that never opened never closes, and its scope would hold the compaction off for
            // the rest of the session. Disposing twice is harmless if it did close, and so is removing the
            // device listener twice.
            audio.InputDevicesChanged -= OnInputDevicesChanged;
            foreground.Dispose();
            throw;
        }
    });

    // StartupStageTiming: where the start went, as stage codes and whole milliseconds since Windows created the process (or
    // since Main when that time cannot be read). Codes and numbers only; the timeline is dropped either way.
    private void LogStartupStages(ILogger log, PerfFlags perfFlags)
    {
        var stages = StartupStages;
        StartupStages = null;
        if (stages is null || !perfFlags.IsOn(PerfFlags.StartupStageTiming))
        {
            return;
        }

        try
        {
            stages.Mark("started");
            long? createdToMain = null;
            try
            {
                using var self = Process.GetCurrentProcess();
                createdToMain = stages.AnchorAfter(self.StartTime.ToUniversalTime(), DateTime.UtcNow);
            }
            catch (Exception)
            {
                // Without the creation time the stages are still given, from Main.
            }

            if (createdToMain is { } offset)
            {
                log.LogInformation("Startup stages in ms since the process was created: main={Main} {Stages}.", offset, stages.Describe(offset));
            }
            else
            {
                log.LogInformation("Startup stages in ms since Main: {Stages}.", stages.Describe());
            }
        }
        catch (Exception ex)
        {
            log.LogDebug("Could not describe the startup stages ({Failure}).", FailureShape.Describe(ex));
        }
    }

    // StartupStageTiming: one line per Settings window, once its content has first rendered: which open of this process it
    // was, and its stages in whole milliseconds since it was asked for. A window closed before it rendered logs nothing.
    private void LogSettingsOpenStagesWhenRendered(SettingsWindow window, StageTimeline stages)
    {
        var open = ++_settingsOpens;
        EventHandler? rendered = null;
        rendered = (_, _) =>
        {
            window.ContentRendered -= rendered;
            stages.Mark("rendered");
            _appLog?.LogInformation("Settings window open {Open} stages in ms since it was asked for: {Stages}.", open, stages.Describe());
        };
        window.ContentRendered += rendered;
    }

    private void CopyLastDictation() => Dispatcher.Invoke(() =>
    {
        if (_host is null || _tray is null)
        {
            return;
        }

        LastTranscriptStore? store = null;
        try
        {
            var services = _host.Services;
            store = services.GetRequiredService<LastTranscriptStore>();
            var copyId = store.CurrentId();
            var text = store.Get();
            if (string.IsNullOrWhiteSpace(text))
            {
                text = store.Get(services.GetRequiredService<IHistoryRepository>().GetRecent(10));
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                ShowTrayNotice(TrayNotices.NothingToCopy());
                return;
            }

            var copied = ScribeClipboard.SetText(text);
            if (!copied)
            {
                _noticeCopyEntryId = copyId;
            }

            ShowTrayNotice(copied ? TrayNotices.CopiedLastDictation() : TrayNotices.ClipboardBusy());
        }
        catch (Exception ex)
        {
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogWarning("Copying the last dictation failed ({Failure}).", FailureShape.Describe(ex));
            _noticeCopyEntryId = store?.CurrentId();
            ShowTrayNotice(TrayNotices.ClipboardBusy());
        }
    });

    /// <summary>
    /// Copies one specific transcript picked from the "Copy recent dictation" submenu. The text
    /// arrives with the event (a ring snapshot taken when the menu opened), so no store lookup is
    /// needed and the copy matches exactly what the user clicked.
    /// </summary>
    private void CopyRecentDictation(string text) => Dispatcher.Invoke(() =>
    {
        if (_host is null || _tray is null)
        {
            return;
        }

        try
        {
            ShowTrayNotice(ScribeClipboard.SetText(text) ? TrayNotices.CopiedRecentDictation() : TrayNotices.ClipboardBusy());
        }
        catch (Exception ex)
        {
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogWarning("Copying a recent dictation failed ({Failure}).", FailureShape.Describe(ex));
            ShowTrayNotice(TrayNotices.ClipboardBusy());
        }
    });

    private void CopyRecentDictation(Guid id, string text) => Dispatcher.Invoke(() =>
    {
        if (_host is null || _tray is null)
        {
            return;
        }

        try
        {
            var copied = ScribeClipboard.SetText(text);
            if (!copied)
            {
                _noticeCopyEntryId = id;
            }

            ShowTrayNotice(copied ? TrayNotices.CopiedRecentDictation() : TrayNotices.ClipboardBusy());
        }
        catch (Exception ex)
        {
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogWarning("Copying a recent dictation failed ({Failure}).", FailureShape.Describe(ex));
            _noticeCopyEntryId = id;
            ShowTrayNotice(TrayNotices.ClipboardBusy());
        }
    });

    /// <summary>
    /// Opens the Store listing through the ms-windows-store protocol so it lands in the Store app
    /// rather than a browser tab.
    /// </summary>
    private void OpenMicrosoftStore() => Dispatcher.Invoke(() =>
    {
        if (_host is null || _tray is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(ScribeLinks.StoreProtocol) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Falls back to the web listing: the protocol handler is missing on a machine where
            // the Store app has been removed, which is common on managed devices.
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogWarning(
                    "Opening the Microsoft Store listing failed; falling back to the web listing ({Failure}).",
                    FailureShape.Describe(ex));
            try
            {
                Process.Start(new ProcessStartInfo(ScribeLinks.StoreWeb) { UseShellExecute = true });
            }
            catch (Exception fallbackError)
            {
                _host.Services.GetRequiredService<ILogger<App>>()
                    .LogWarning("Opening the Store web listing failed ({Failure}).", FailureShape.Describe(fallbackError));
                ShowTrayNotice(new TrayNotice("Couldn't open Microsoft Store", "Open Scribe from the Microsoft Store app, then search for Scribe AI.", TrayNoticeKind.Error));
            }
        }
    });

    /// <summary>
    /// Copies the shareable Store link. The web form is used rather than the protocol form because
    /// whoever receives it may not be on a Windows device.
    /// </summary>
    private void ShareApp() => Dispatcher.Invoke(() =>
    {
        if (_host is null || _tray is null)
        {
            return;
        }

        try
        {
            ShowTrayNotice(ScribeClipboard.SetText(ScribeLinks.StoreWeb)
                ? new TrayNotice("Copied", "The Scribe link is on the clipboard.", TrayNoticeKind.Info)
                : TrayNotices.ClipboardBusy());
        }
        catch (Exception ex)
        {
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogWarning("Copying the Store link failed ({Failure}).", FailureShape.Describe(ex));
            ShowTrayNotice(TrayNotices.ClipboardBusy());
        }
    });

    private async void LearnFromHistory()
    {
        if (_host is null || _tray is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _learningFromHistory, 1) != 0)
        {
            ShowTrayNotice(new TrayNotice("Learning already started", "Scribe is already checking your recent dictations.", TrayNoticeKind.Info));
            return;
        }

        try
        {
            var services = _host.Services;

            // Its dictionary write lands on this thread, so the one-time VACUUM stays off (and stops
            // at once if it is running) until the learning is done.
            using var foreground = services.GetRequiredService<StorageMaintenance>().EnterForegroundWork();
            var candidates = await Task.Run(() =>
            {
                var history = services.GetRequiredService<IHistoryRepository>();
                var dictionary = services.GetRequiredService<IDictionaryRepository>();
                return DictionaryHistoryLearner.BuildEntries(
                    history.GetRecent(1000),
                    dictionary.GetAll());
            });

            // Persistence stays on the dispatcher so an open Settings window cannot reconcile a
            // stale dictionary snapshot between the insert and its in-memory row merge.
            var learned = _settingsWindow is { } settings
                ? settings.PersistLearnedDictionaryEntries(candidates)
                : services.GetRequiredService<IDictionaryRepository>().AddRange(candidates);

            // The terms reach dictation in the next vocabulary generation, built off this thread from the dictionary as
            // stored now; they are said to be learned only once that generation is what a dictation is given.
            var applied = true;
            if (learned.Count > 0)
            {
                var controller = _controller!;
                applied = (await Task.Run(() => controller.ReloadVocabulary())).Applied;
            }

            var learnedNotice = $"Learned {learned.Count} new {(learned.Count == 1 ? "term" : "terms")} from your dictation history";
            ShowTrayNotice(new TrayNotice(
                learned.Count == 0 ? "No words found" : applied ? "Words learned" : "Saved, but not in use yet",
                learned.Count == 0
                    ? "No new recurring words were found."
                    : applied
                        ? "Scribe added words from your dictation history."
                        : "Scribe saved the words but couldn't start using them yet. Quit and reopen Scribe to use them.",
                applied ? TrayNoticeKind.Info : TrayNoticeKind.Warning));
        }
        catch (Exception ex)
        {
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogError("Failed to learn dictionary terms from history: {Failure}", FailureShape.DescribeWithStack(ex));
            ShowTrayNotice(new TrayNotice("Couldn't learn from history", "Try again, or add words in Settings, Dictionary.", TrayNoticeKind.Error, TrayNoticeAction.OpenSettingsDictionary));
        }
        finally
        {
            Interlocked.Exchange(ref _learningFromHistory, 0);
        }
    }

    /// <summary>
    /// Shows the first-run welcome (or focuses it if already open). Non-modal so the tray and
    /// dictation loop keep running behind it. The gesture text uses the user's actual push-to-talk
    /// key, and "Open settings" routes to the existing settings window.
    /// </summary>
    private void ShowWelcome() => Dispatcher.Invoke(() =>
    {
        if (_welcomeWindow is not null)
        {
            _welcomeWindow.Activate();
            return;
        }

        var gesture = HotkeyCapture.Gesture(_controller?.CurrentSettings);
        _welcomeWindow = new Onboarding.WelcomeWindow(
            gesture,
            _textScale,
            () => OpenSettings(Scribe.Core.Settings.SettingsPage.Dictation),
            () => OpenSettings(Scribe.Core.Settings.SettingsPage.TryDictation));
        _welcomeWindow.Closed += (_, _) => _welcomeWindow = null;
        _welcomeWindow.Show();
        _welcomeWindow.Activate();
    });

    /// <summary>
    /// Shows the tray's quick "Add to dictionary" popup (or focuses it if already open).
    ///
    /// Both the duplicate check and the write are passed in as delegates that re-resolve the
    /// settings window every time they run. That is deliberate: the settings window can open or
    /// close while this popup sits on screen, and <see cref="IDictionaryRepository.SaveAll"/>
    /// deletes stored rows the open grid does not know about, so a write that ignored the grid
    /// would be silently undone by the user's next Save in that window.
    /// </summary>
    private void ShowQuickAdd()
    {
        _ = _quickAddOpenGate.RunAsync(ShowQuickAddAsync, FocusQuickAddWhenOpenAsync);
    }

    private Task FocusQuickAddWhenOpenAsync()
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.InvokeAsync(FocusQuickAddWhenOpenAsync).Task.Unwrap();
        }

        _quickAddWindow?.Activate();
        return Task.CompletedTask;
    }

    private async Task ShowQuickAddAsync()
    {
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(ShowQuickAdd);
            return;
        }
        if (_host is null || _tray is null)
        {
            return;
        }

        if (_quickAddWindow is not null)
        {
            _quickAddWindow.Activate();
            return;
        }

        IDisposable? foreground = null;
        try
        {
            var services = _host.Services;

            // The popup writes the dictionary on this thread: the one-time VACUUM stays off (and stops
            // at once if it is running) until the popup closes.
            var scope = services.GetRequiredService<StorageMaintenance>().EnterForegroundWork();
            foreground = scope;
            var store = services.GetRequiredService<LastTranscriptStore>();
            var recent = store.GetRecentEntries();
            if (recent.Count == 0)
            {
                // Same idea as CopyLastDictation: the in-memory ring is empty on a fresh start, but
                // history still holds what was dictated before the last restart.
                // Seed rather than just display. A correction saved against a transcript that only
                // exists in history would otherwise find nothing to repair in the ring, so the fix
                // would look like it worked while "copy last dictation" still returned the mistake.
                await SeedRecentDictationsAsync(services, services.GetRequiredService<HistoryDeletionNotifier>());
                recent = store.GetRecentEntries();
                if (_quickAddWindow is not null)
                {
                    _quickAddWindow.Activate();
                    return;
                }
            }

            var window = new QuickAdd.QuickAddWindow(
                recent.Select(source => new QuickAdd.QuickAddWindow.QuickAddSource(source.Id, source.HistoryText, source.Text, source.TimestampUtc, source.AddedAtRevision)).ToList(),
                loadExisting: () =>
                {
                    var baseEntries = _settingsWindow is { IsDraftDiscardedForAppExit: false } settings
                        ? settings.CurrentDictionaryEntries()
                        : services.GetRequiredService<IDictionaryRepository>().GetAll();

                    // Compose in the committed library vocabulary. The popup shows finished text, so the term a user
                    // reaches for is often a shipped library's output; without these the single-pass conflict check
                    // misses the very case that is easiest to walk into. The vocabulary dictation applies, never a
                    // draft or a fresh read of the stored document.
                    try
                    {
                        return DictionaryLibraryComposer.Merge(
                            baseEntries, services.GetRequiredService<ILibraryVocabularySource>().Current.Entries);
                    }
                    catch
                    {
                        return baseEntries; // libraries are best-effort, never worth blocking a fix
                    }
                },
                persist: entry =>
                {
                    // A draft discarded for an update never receives the correction: it would update a row the user
                    // threw away. Storage takes it, and that Settings window closes once the update runs.
                    if (_settingsWindow is { IsDraftDiscardedForAppExit: false } settings)
                    {
                        return settings.ApplyQuickDictionaryEntry(entry);
                    }

                    var dictionary = services.GetRequiredService<IDictionaryRepository>();
                    if (entry.Id == 0)
                    {
                        return dictionary.Add(entry);
                    }

                    dictionary.Update(entry);
                    return entry;
                },
                _textScale,
                logger: services.GetService<ILoggerFactory>()?.CreateLogger<QuickAdd.QuickAddWindow>(),
                options: new QuickAdd.QuickAddWindow.QuickAddWindowOptions(
                    AiCleanupEnabled: _controller?.CurrentSettings.EnableAiCleanup ?? false,
                    DictionaryEnabled: _controller?.CurrentSettings.ApplyPostProcessing ?? true,
                    SettingsOpen: _settingsWindow is not null,
                    LoadVocabulary: () => BuildQuickAddVocabulary(services),
                    PendingSettingsSpokenForms: () => _settingsWindow?.PendingQuickAddSpokenForms() ?? [],
                    OpenAdvancedSettings: () => OpenSettings(Scribe.Core.Settings.SettingsPage.Advanced, "PostCheck"),
                    ShowInSettings: spoken => OpenSettingsForDictionary(spoken),
                    AddItInSettings: spoken => AddQuickAddTermInSettings(spoken),
                    FixInstead: spoken => FixQuickAddTermInstead(spoken)));

            window.Saved += OnQuickAddSaved;
            window.Closed += (_, _) =>
            {
                window.Saved -= OnQuickAddSaved;
                if (ReferenceEquals(_quickAddWindow, window))
                {
                    _quickAddWindow = null;
                }

                scope.Dispose();
            };

            _quickAddWindow = window;
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            _quickAddWindow = null;
            foreground?.Dispose();
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogError("Failed to open the quick dictionary add window: {Failure}", FailureShape.DescribeWithStack(ex));
            ShowTrayNotice(TrayNotices.QuickAddOpenFailed());
        }
    }

    /// <summary>
    /// Activates a freshly quick-added rule. Without the reload the entry sits in the database and
    /// changes nothing until the next settings save, which reads as the feature being broken.
    ///
    /// Also repairs the retained copy of the dictation the correction came from, so the tray's
    /// "copy last dictation" hands back the fixed wording instead of the mistake the user just
    /// taught Scribe to stop making.
    /// </summary>
    private async void OnQuickAddSaved(QuickAdd.QuickAddWindow.QuickAddResult result)
    {
        if (_host is null || _tray is null)
        {
            return;
        }

        var entry = result.Entry;

        // Before the await: the repair is a plain in-memory swap, and doing it first means a failure
        // to reload the vocabulary cannot leave the user with a stale transcript as well.
        var repaired = false;
        if (result.CorrectedTranscript is not null)
        {
            try
            {
                repaired = _host.Services.GetRequiredService<LastTranscriptStore>()
                    .Update(result.SourceTranscript, result.CorrectedTranscript);
            }
            catch (Exception ex)
            {
                // Never surfaced: the rule is saved and working, and a stale recovery copy is a far
                // smaller problem than an error toast implying the save failed.
                _host.Services.GetRequiredService<ILogger<App>>()
                    .LogWarning(
                        "Failed to repair the retained transcript after a quick dictionary add: {Failure}",
                        FailureShape.DescribeWithStack(ex));
            }
        }

        try
        {
            // The rule reaches dictation in the next vocabulary generation, built off this thread from the dictionary as
            // stored now; it is said to be written only once that generation is what a dictation is given.
            var controller = _controller!;
            var refresh = await Task.Run(() => controller.ReloadVocabulary());
            var corrected = repaired ? " Your last dictation was corrected to match." : string.Empty;
            if (!refresh.Applied)
            {
                ShowTrayNotice(TrayNotices.QuickAddSavedButNotReloaded());
                return;
            }

            if (result.CloseAfterSaving)
            {
                ShowTrayNotice(TrayNotices.QuickAddSavedAndClosed());
            }
        }
        catch (Exception ex)
        {
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogError(
                    "Failed to reload the vocabulary after a quick dictionary add: {Failure}",
                    FailureShape.DescribeWithStack(ex));
            ShowTrayNotice(TrayNotices.QuickAddSavedButNotReloaded());
        }
    }


    private void InitializeApplicationTheme(ILogger log)
    {
        ApplyCurrentWindowsTheme(log, "startup");

        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch (Exception ex)
        {
            log.LogWarning("Unable to subscribe to Windows theme changes ({Failure}).", FailureShape.Describe(ex));
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(() => ApplyCurrentWindowsTheme(_appLog, "user preference changed"));
        }
        catch (Exception ex)
        {
            _appLog?.LogWarning("Unable to queue Windows theme refresh ({Failure}).", FailureShape.Describe(ex));
        }
    }

    private static void ApplyCurrentWindowsTheme(ILogger? log, string reason)
    {
        var (theme, registryValue, readRegistry) = ReadWindowsAppTheme();

        try
        {
            ApplicationThemeManager.Apply(theme, updateAccent: false);
            var applied = ApplicationThemeManager.GetAppTheme();
            log?.LogInformation(
                "Applied Windows theme: {Theme} (AppsUseLightTheme={RegistryValue}, source={Source}, registryRead={RegistryRead}).",
                applied,
                (object?)registryValue ?? "unavailable",
                reason,
                readRegistry);
        }
        catch (Exception ex)
        {
            log?.LogWarning(
                "Unable to apply the Windows theme; keeping the current app resources ({Failure}).",
                FailureShape.Describe(ex));
        }
    }

    private static (ApplicationTheme Theme, int? RegistryValue, bool ReadRegistry) ReadWindowsAppTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int intValue)
            {
                return (intValue == 0 ? ApplicationTheme.Dark : ApplicationTheme.Light, intValue, true);
            }
        }
        catch
        {
        }

        try
        {
            var systemTheme = ApplicationThemeManager.GetSystemTheme();
            if (systemTheme == SystemTheme.Light)
            {
                return (ApplicationTheme.Light, null, false);
            }

            if (systemTheme == SystemTheme.Dark)
            {
                return (ApplicationTheme.Dark, null, false);
            }
        }
        catch
        {
        }

        return (ApplicationTheme.Dark, null, false);
    }

    private void DisposeThemeWatcher()
    {
        try { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Each step is independent and guarded on its own, so one failure no longer skips the rest:
        // a single try block used to abandon tray, theme, host shutdown and host disposal together on
        // the first throw. The order still matters and is kept: storage maintenance stops, the
        // controller stops using the core services (and drains its history), and the tray's settings
        // writes finish, before the host disposes them, and the settings-signal registration is
        // unregistered before the handle it waits on is disposed. There is no exit deadline beyond the
        // bounded waits inside the steps themselves.
        Scribe.Core.Lifecycle.StagedTeardown.Run(
        [
            // First, and waiting for nothing: from here the controller starts nothing new and stops raising the events
            // whose handlers marshal synchronously onto this thread, so nothing raised during the steps below can park
            // behind OnExit and turn the controller's bounded wait into a stall.
            new("begin dictation shutdown", () => _controller?.BeginShutdown()),

            // Next, before anything is disposed: a VACUUM in flight is interrupted and rolls back, and a pass stops at
            // its next step, so the history drain in the controller's disposal and the host's disposal of the database
            // below never wait behind storage maintenance. Bounded; past the bound SQLite still commits or rolls back
            // atomically on its own.
            new("stop storage maintenance", () => _storageMaintenance?.Stop(TimeSpan.FromSeconds(3))),

            // Nothing new is queued and nothing is applied from here on; a tray change already on its way still reaches
            // the database, and the drain before host shutdown below waits for it.
            new("stop settings writes", () => _settingsWrites?.Close()),
            new("session end", () =>
            {
                // Closes the story the banner opened. A log covering several restarts is otherwise a
                // run of session banners with no way to tell an orderly quit from a crash: the absence
                // of this line before the next banner is the signal that the process died.
                if (_diagnostics is { } diagnostics)
                {
                    _appLog?.LogInformation(
                        "===== Scribe session end ===== session={Session} uptime={Uptime:hh\\:mm\\:ss}",
                        diagnostics.Session.Id,
                        DateTimeOffset.Now - diagnostics.Session.StartedLocal);
                }
            }),

            // Stage any downloaded update early, before the slower steps, so the updater is waiting as the process exits.
            new("pending update", () => _updates?.ApplyPendingOnExit()),
            new("overlay", () => _overlay?.CloseOverlay()),
            new("dictation controller", () => _controller?.Dispose()),
            new("tray icon", () => _tray?.Dispose()),
            new("text scale", () => _textScale?.Dispose()),
            new("theme watcher", DisposeThemeWatcher),
            new("drain settings writes", () =>
            {
                if (_settingsWrites is { } writes && !writes.WaitForIdle(SettingsWriteDrainTimeout))
                {
                    _appLog?.LogWarning(
                        "A tray settings change was still being saved after {Seconds} s at exit; it may not be stored.",
                        SettingsWriteDrainTimeout.TotalSeconds);
                }
            }),
            new("host stop", () => _host?.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult()),
            new("host dispose", () => _host?.Dispose()),
            new("settings signal registration", () => _showSettingsRegistration?.Unregister(null)),
            new("settings signal", () => _showSettingsSignal?.Dispose()),
            new("settings request payload", () => _showSettingsPayload?.Dispose()),
            new("single-instance mutex", () => _singleInstanceMutex?.Dispose()),
        ],
        (step, ex) => _appLog?.LogWarning(
            "Shutdown step '{Step}' failed; the remaining steps still ran: {Failure}",
            step,
            FailureShape.DescribeWithStack(ex)));

        base.OnExit(e);
    }

    /// <summary>
    /// Routes unhandled exceptions from the UI thread, background threads and faulted tasks to
    /// the log file. UI-thread faults are marked handled so a single bad dictation never tears
    /// down the whole tray app.
    /// </summary>
    private void WireGlobalExceptionLogging(ILogger log)
    {
        // The session banner was queued just before this, and startup goes on to load the native
        // speech and cleanup runtimes, where a hard crash would take unwritten lines with it. Wait
        // (briefly) until the banner is on disk. Warnings and errors, including the ones below, wait a
        // short bounded time to be written before their logging call returns, and the provider drains
        // its queue on its own at process exit and on an unhandled exception.
        LogSink?.Flush(FileLoggerProvider.PromptFlushTimeout);

        // Only now, so the log opens with the banner: removes sensitive values that earlier versions
        // wrote into past days' log files, on a background thread.
        LogSink?.StartHistoricalRedaction();

        // By shape and stack (FailureShape): the frames are the diagnosis of a crash, and the messages can be a
        // provider's or the user's text.
        DispatcherUnhandledException += (_, args) =>
        {
            log.LogError("Unhandled dispatcher exception: {Failure}", FailureShape.DescribeWithStack(args.Exception));
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            log.LogCritical(
                "Unhandled domain exception (terminating={Terminating}): {Failure}",
                args.IsTerminating,
                FailureShape.DescribeWithStack(args.ExceptionObject as Exception));

            // The process is about to end: give the crash line itself a bounded chance to reach the disk.
            LogSink?.Flush(TimeSpan.FromSeconds(1));
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            log.LogError("Unobserved task exception: {Failure}", FailureShape.DescribeWithStack(args.Exception));
            args.SetObserved();
        };
    }
}
