using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.TextInjection;
using Scribe.Core.Transcription;
using Scribe.Core.Vad;
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

    // How long quitting waits for a tray settings change already on its way to the database.
    private static readonly TimeSpan SettingsWriteDrainTimeout = TimeSpan.FromSeconds(2);

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showSettingsSignal;
    private RegisteredWaitHandle? _showSettingsRegistration;
    private IHost? _host;
    private TrayIconHost? _tray;
    private DictationController? _controller;
    private Scribe.Core.Lifecycle.PresentationRelay<DictationStateChange>? _dictationState;
    private IOverlayController? _overlay;
    private SettingsWindow? _settingsWindow;
    private Onboarding.WelcomeWindow? _welcomeWindow;
    private QuickAdd.QuickAddWindow? _quickAddWindow;

    /// <summary>The file log sink, so its health can be reported in Settings.</summary>
    internal static FileLoggerProvider? LogSink { get; private set; }
    private UpdateService? _updates;
    private SessionDiagnostics? _diagnostics;
    private ILogger? _appLog;
    private int _learningFromHistory;

    // The tray's settings writes, saved on a worker in click order so a database wait never freezes the UI thread.
    private Scribe.Core.Settings.SettingsWriteLane? _settingsWrites;

    // A microphone chosen from the tray whose write has not come back yet, so the tray shows the choice at once.
    private (Scribe.Core.Settings.MicrophoneSelection Selection, long Revision)? _pendingTrayMicrophone;

    // Kept so the first stage of OnExit can stop it before anything it uses is torn down.
    private StorageMaintenance? _storageMaintenance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before any window exists, the already-running notice below included.
        TitleBarButtonNames.Apply(Resources);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isNew);
        if (!isNew)
        {
            var wantsSettings = HasSettingsSwitch(e.Args);

            // Hand the request to the running instance rather than telling the user to go find the
            // tray icon themselves. Only fall back to the notice when the signal cannot be raised.
            if (wantsSettings && TrySignalShowSettings())
            {
                Shutdown();
                return;
            }

            ShowSingleInstanceNotice();
            Shutdown();
            return;
        }

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
        if (started && !Dispatcher.HasShutdownStarted && HasSettingsSwitch(e.Args))
        {
            OpenSettings();
        }
    }

    /// <summary>
    /// Everything startup does once this is the only instance. Returns false when it ended the process on purpose,
    /// having told the user why: the data folder cannot be created, or the database is from a newer Scribe.
    /// </summary>
    private async Task<bool> StartAsync()
    {
        StartShowSettingsListener();

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

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddScribeCore();
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<AzureCliInstaller>();
        builder.Services.AddSingleton<StartupRegistration>();
        builder.Services.AddSingleton<SessionDiagnostics>();

        builder.Services.AddScribeTelemetry();
        builder.Logging.ClearProviders();
        // Held in a static so Settings can report whether logging is ACTUALLY working rather
        // than displaying the folder it was asked to use. A packaged build was found writing
        // nothing for an entire session while the About page confidently showed a path.
        var logSink = new FileLoggerProvider(paths.LogsDir);
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
        _host.Start();

        var services = _host.Services;
        var log = services.GetRequiredService<ILogger<App>>();
        _appLog = log;

        // First thing in the file, before anything can fail. A log that opens mid-story is the
        // reason a 0.3.10 report about dictation cutting out short could not be investigated at
        // all: nothing recorded which build, which install channel, which microphone or which
        // settings were in play, and the daily file had rolled over since the process started.
        _diagnostics = services.GetRequiredService<SessionDiagnostics>();
        _diagnostics.WriteBanner(log);

        WireGlobalExceptionLogging(log);
        InitializeApplicationTheme(log);
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

        _settingsWrites = new Scribe.Core.Settings.SettingsWriteLane(
            settingsRepository,
            callback => Dispatcher.BeginInvoke(callback));

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
        _tray.QuitRequested += () => Dispatcher.Invoke(Shutdown);
        _tray.SettingsRequested += OpenSettings;
        _tray.LearnFromHistoryRequested += LearnFromHistory;
        _tray.CopyLastDictationRequested += CopyLastDictation;
        _tray.CopyRecentDictationRequested += CopyRecentDictation;
        _tray.RecentDictationsProvider = () => _host is null
            ? []
            : _host.Services.GetRequiredService<LastTranscriptStore>().GetRecent();
        _tray.WelcomeRequested += ShowWelcome; // reopen the first-run intro on demand
        _tray.OpenStoreRequested += OpenMicrosoftStore;
        _tray.ShareAppRequested += ShareApp;
        _tray.AddToDictionaryRequested += ShowQuickAdd;
        _tray.PauseToggled += paused => _controller?.SetPaused(paused);
        _tray.AiCleanupToggled += ToggleAiCleanup;
        _tray.MicrophoneMenuProvider = BuildTrayMicrophoneMenu;
        _tray.MicrophoneChosen += ChooseMicrophone;
        _tray.SoundSettingsRequested += OpenSoundSettings;

        _controller = new DictationController(
            services.GetRequiredService<IHotkeyService>(),
            services.GetRequiredService<IAudioCaptureService>(),
            services.GetRequiredService<IVadService>(),
            services.GetRequiredService<ITranscriptionService>(),
            services.GetRequiredService<ITextPostProcessor>(),
            services.GetRequiredService<ITextCleanupService>(),
            services.GetRequiredService<ITextInjector>(),
            services.GetRequiredService<IHistoryWriter>(),
            services.GetRequiredService<IDictionaryRepository>(),
            services.GetRequiredService<IDictionaryLibraryService>(),
            services.GetRequiredService<ICleanupFailureLog>(),
            services.GetRequiredService<LastTranscriptStore>(),
            services.GetRequiredService<ISettingsRepository>(),
            services.GetRequiredService<ILogger<DictationController>>());

        _overlay = new OverlayProcessClient(
            services.GetRequiredService<IAudioCaptureService>(),
            services.GetRequiredService<ILogger<OverlayProcessClient>>());

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
            Dispatcher.BeginInvoke(() => _settingsWindow?.ShowPlaygroundPipeline(report));
        _controller.Error += message =>
        {
            _tray!.ShowError(message);
            // Mirror the failure on the overlay (like cleanup failures): the user is looking at the
            // pill mid-dictation, not the tray, when the microphone produces nothing.
            OnCleanupFailed(message);
        };
        _controller.Warning += warning =>
        {
            // The tray notice stands on its own; the pill's warning belongs to one recording and follows its revision.
            _tray!.ShowNotification(warning.Message, isError: true);
            if (warning.PillText is { } pillText)
            {
                OnRecordingWarning(warning.RecordingRevision, pillText);
            }
        };
        _controller.CleanupFailed += OnCleanupFailed;
        _controller.CleanupProviderChanged += message => Dispatcher.BeginInvoke(new Action(() =>
        {
            // Best-effort, exactly like the other tray balloons: a notification failure must never
            // propagate back into a settings save.
            try
            {
                _tray?.ShowNotification(message);
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
            try
            {
                _tray?.ShowNotification(
                    "Dictation could not be inserted. Use the tray menu to copy it.", isError: true);
            }
            catch (Exception ex)
            {
                log.LogWarning("Failed to show the injection recovery notification ({Failure}).", FailureShape.Describe(ex));
            }
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
            }
            catch (FileNotFoundException ex)
            {
                log.LogInformation(
                    "No transcription model is installed; waiting for a Settings selection ({Failure}).",
                    FailureShape.Describe(ex));
                _tray!.ShowInfo("No speech model is installed. Choose one in Settings.");
            }
            catch (Exception ex)
            {
                log.LogError("Failed to warm-load the transcription engine: {Failure}", FailureShape.DescribeWithStack(ex));
                _tray!.ShowError("model failed to load, see logs");
            }
        });

        // Before Start(): the startup reclaim of an earlier session's Foundry Local leftovers runs in the
        // background right after the first Configure, which Start() makes.
        services.GetRequiredService<ITextCleanupService>().FoundryStorageReclaimed += OnFoundryStorageReclaimed;

        _controller.Start();

        // Settings-dependent wiring goes AFTER Start(): CurrentSettings returns compiled defaults
        // until Start() loads the persisted settings, so reading it earlier silently ignored the
        // user's saved overlay position, overlay toggle, and AI-cleanup state on every launch.
        // The helper's idle lifetime follows the speech models' keep-warm setting but is decided inside
        // the client, never on the models' release, which could end a newer recording's pill. It is
        // pushed here before the warmup can arm it, and again with every state change.
        _overlay.SetKeepWarm(_controller.CurrentSettings.ReleaseModelsAfterIdleMinutes);
        _overlay.SetPosition(_controller.CurrentSettings.OverlayPosition);
        // Pre-warm the out-of-process WinUI pill so its transparent surface is ready before first
        // use. Only spawn the helper when the overlay is actually enabled; if the user turns it on
        // later, ShowRecording launches it lazily. A warmed helper left unused is suspended after the
        // keep-warm period like any other, and relaunches on the next show.
        if (_controller.CurrentSettings.ShowOverlay)
        {
            _overlay.Warmup();
        }

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

        log.LogInformation(
            "Scribe started. Dictation hotkey {Key} ({Mode}), dictation-only hotkey {DictationOnlyKey}.",
            HotkeyText.Describe(_controller.CurrentSettings.Hotkey),
            _controller.CurrentSettings.Hotkey.Mode,
            _controller.CurrentSettings.DictationOnlyHotkey is { } dictationOnly ? HotkeyText.Describe(dictationOnly) : "none");

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
            var (notice, isError) = DatabaseRepairNotice.Compose(
                database.SettingsLostInRepair, database.DictionaryLostInRepair);
            if (isError)
            {
                _tray.ShowNotification(notice, isError: true);
            }
            else
            {
                _tray.ShowInfo(notice);
            }
        }

        // --- Onboarding (first-run welcome) -------------------------------------------------
        // Tray-only app has no main window, so a brand-new user sees nothing and may never learn
        // the push-to-talk gesture. Show a one-time welcome once settings are loaded, then persist
        // the flag so it never reappears. Kept as a self-contained block for a clean merge.
        if (!_controller.CurrentSettings.HasCompletedFirstRun)
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
                // them, and the tooltip says so in words true for both.
                _tray.ShowError(SavedSettingsNotice.AtStartup);
            }
        }
        // --- End onboarding -----------------------------------------------------------------

        // Update checks are user-initiated from Settings so the offline-first startup path performs
        // no network access. Previously staged updates are detected by the same manual check.
        _updates = new UpdateService(services.GetRequiredService<ILogger<UpdateService>>());
        _updates.UpdateReady += message => _tray?.ShowInfo(message);
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
            MessageBox.Show(
                Scribe.Core.Lifecycle.StartupFailureNotice.Compose(log is { } status && status.Healthy ? status.Path : null),
                Scribe.Core.Lifecycle.StartupFailureNotice.Title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Exiting is still right if Windows cannot show the dialog.
        }
    }

    private static bool HasSettingsSwitch(IEnumerable<string> args) =>
        args.Any(arg => string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase));

    private static void ShowFatalDataPathNotice(Exception exception)
    {
        try
        {
            MessageBox.Show(
                "Scribe could not create a writable data folder and must close.\n\n" +
                $"{exception.Message}\n\n" +
                "Check disk space, folder permissions, antivirus rules, and whether a file is blocking the ScribeData folder.",
                "Scribe data folder problem",
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
            MessageBox.Show(
                NewerDatabaseSchemaException.UserMessage,
                "Scribe",
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
            MessageBox.Show(
                "Scribe could not use its usual data folder:\n" +
                $"{paths.PreferredRootDir}\n\n" +
                $"{paths.CreationFailureMessage}\n\n" +
                "Scribe is running with temporary data and logs here:\n" +
                $"{paths.RootDir}\n\n" +
                "Open Settings, About to copy the active log and data paths. Check disk space, folder permissions, antivirus rules, and whether a file is blocking the ScribeData folder.",
                "Scribe is using a fallback data folder",
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
    private static bool TrySignalShowSettings()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ShowSettingsEventName, out var signal))
            {
                return false;
            }

            using (signal)
            {
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
            _showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
            _showSettingsRegistration = ThreadPool.RegisterWaitForSingleObject(
                _showSettingsSignal,
                (_, timedOut) =>
                {
                    if (!timedOut)
                    {
                        Dispatcher.BeginInvoke(new Action(OpenSettings));
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
            "Scribe", "Scribe is already running. Look for the microphone icon in the system tray.");

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
    /// recording that started after it. The overlay only shows while recording and only when the user has it enabled.
    /// </summary>
    private void RenderDictationState(DictationStateChange change)
    {
        var state = change.State;

        // The tray and the overlay are independent views of the same state. A failure updating one
        // must never stop the other: when this method threw, the overlay was left showing whatever
        // it had last been told, so the pill sat on "Transcribing" while dictation kept working.
        try
        {
            _tray?.SetState(state);
        }
        catch (Exception ex)
        {
            _appLog?.LogWarning("Could not update the tray icon for state {State} ({Failure}).", state, FailureShape.Describe(ex));
        }

        // Read here, at the render, so a setting saved since the change was raised applies to it.
        var settings = _controller?.CurrentSettings;
        var overlayEnabled = settings?.ShowOverlay ?? false;

        // Pushed with every state change, so a keep-warm saved in Settings reaches the overlay client
        // without its command thread ever reading the controller's settings. Unchanged values cost nothing.
        if (settings is not null)
        {
            _overlay?.SetKeepWarm(settings.ReleaseModelsAfterIdleMinutes);
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
                    _overlay?.HideOverlay();
                    break;
            }
        }

        // Pausing is the user standing Scribe down, so the idle helper (~100 MB) is ended now rather
        // than after the keep-warm period, as pausing already does for the speech models. Only the
        // newest change gets here, so a pause shown late can never release the helper under a newer
        // recording; a newer command or an on-screen state still vetoes it inside the client as well.
        if (state == DictationState.Paused)
        {
            _overlay?.ReleaseWhenIdle();
        }
    }

    /// <summary>
    /// Shows the brief red "intelligence failed" overlay when AI cleanup fell back to raw text.
    /// Raised on a background thread, so the overlay mutation is posted to the UI thread (never
    /// invoked: the raising thread must not wait for it) and only shown when the user has the overlay
    /// enabled and the app is not already shutting down by the time it runs.
    /// </summary>
    private void OnCleanupFailed(string reason)
    {
        var overlayEnabled = _controller?.CurrentSettings.ShowOverlay ?? false;
        if (!overlayEnabled)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_controller?.IsClosing == false)
            {
                _overlay?.ShowFailed(reason);
            }
        });
    }

    /// <summary>
    /// Shows a warning on the recording pill, in the same ordered queue as the state changes and only while the recording
    /// it belongs to is still what the pill shows. Showing a warning puts the pill in its recording state, so one that ran
    /// after a pause or a stop had been shown would bring back a recording pill that nothing would ever hide.
    /// </summary>
    private void OnRecordingWarning(long recordingRevision, string reason)
    {
        if (recordingRevision <= 0)
        {
            return; // the recording was already over when the warning was raised
        }

        _dictationState?.PublishIfCurrent(recordingRevision, () =>
        {
            if (_controller?.CurrentSettings.ShowOverlay == true)
            {
                _overlay?.ShowRecordingWarning(reason);
            }
        });
    }

    /// <summary>
    /// Tells the user once, with a tray notice, that Scribe gave back disk space Foundry Local was using.
    /// Raised on a background thread by the cleanup service, possibly while the host is stopping, so it is
    /// marshalled without blocking that thread and nothing here throws back into it. The log gets numbers
    /// and the reason code only.
    /// </summary>
    private void OnFoundryStorageReclaimed(FoundryStorageReclaim reclaim)
    {
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
                    _tray?.ShowNotification(notice);
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
                _tray?.ShowError(SavedSettingsNotice.FromTray("AI cleanup"));
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
        _controller?.ApplySettings(stored);
        if (!superseded)
        {
            _settingsWindow?.AdoptExternalAiCleanup(stored.EnableAiCleanup, revision);
        }

        _tray?.SetAiCleanupChecked(stored.EnableAiCleanup);
        _tray?.ShowInfo(stored.EnableAiCleanup ? "AI cleanup on" : "AI cleanup off");
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
        _tray?.ShowError("couldn't toggle AI cleanup");
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
                _tray?.ShowError(SavedSettingsNotice.FromTray("the microphone"));
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
        _controller?.ApplySettings(stored);
        var storedChoice = Scribe.Core.Settings.MicrophoneSelection.From(stored);
        if (!superseded)
        {
            _settingsWindow?.AdoptExternalMicrophone(storedChoice, revision);
        }

        _tray?.ShowInfo($"dictating with {Scribe.Core.Settings.MicrophoneChoices.Describe(storedChoice)}");
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

        _tray?.ShowError("couldn't change the microphone");
    }

    // Only the choice this word is about: a newer tray choice keeps showing until its own word arrives.
    private void ClearPendingTrayMicrophone(long revision)
    {
        if (_pendingTrayMicrophone is { } pending && pending.Revision == revision)
        {
            _pendingTrayMicrophone = null;
        }
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

            _tray?.ShowNotification("Couldn't open the Windows sound settings. Open Settings, System, Sound.", isError: true);
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
    private void OpenSettings() => Dispatcher.Invoke(() =>
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var services = _host!.Services;
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
                position => _overlay?.Preview(position),
                settings =>
                {
                    // The window has just saved (or applied) its whole document, so a tray change still on its way reads
                    // the stored settings again before applying anything.
                    _settingsWrites?.NoteExternalApply();
                    _controller!.ApplySettings(settings);
                    _overlay?.SetKeepWarm(settings.ReleaseModelsAfterIdleMinutes);
                    _overlay?.SetPosition(settings.OverlayPosition);
                    _tray?.SetAiCleanupChecked(settings.EnableAiCleanup);
                },
                capturing => _controller?.SetHotkeyCaptureMode(capturing),
                _updates,
                services.GetRequiredService<SessionDiagnostics>());
            _settingsWindow.Closed += (_, _) =>
            {
                audio.InputDevicesChanged -= OnInputDevicesChanged;
                _settingsWindow = null;
                foreground.Dispose();
            };
            _settingsWindow.Show();
            _settingsWindow.Activate();
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

    private void CopyLastDictation() => Dispatcher.Invoke(() =>
    {
        if (_host is null || _tray is null)
        {
            return;
        }

        try
        {
            var services = _host.Services;
            var store = services.GetRequiredService<LastTranscriptStore>();
            var text = store.Get();
            if (string.IsNullOrWhiteSpace(text))
            {
                text = store.Get(services.GetRequiredService<IHistoryRepository>().GetRecent(10));
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                _tray.ShowNotification("No dictation is available to copy.");
                return;
            }

            Clipboard.SetText(text);
            _tray.ShowNotification("Copied the last dictation.");
        }
        catch (Exception ex)
        {
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogWarning("Copying the last dictation failed ({Failure}).", FailureShape.Describe(ex));
            _tray.ShowNotification("Couldn't copy the last dictation.", isError: true);
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
            // Clipboard.SetText can throw under clipboard contention (another app holding the
            // clipboard open), so mirror CopyLastDictation: log and notify, never crash the tray.
            Clipboard.SetText(text);
            _tray.ShowNotification("Copied the dictation.");
        }
        catch (Exception ex)
        {
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogWarning("Copying a recent dictation failed ({Failure}).", FailureShape.Describe(ex));
            _tray.ShowNotification("Couldn't copy the dictation.", isError: true);
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
                _tray.ShowNotification("Couldn't open the Microsoft Store.", isError: true);
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
            Clipboard.SetText(ScribeLinks.StoreWeb);
            _tray.ShowNotification("Copied the Scribe Store link.");
        }
        catch (Exception ex)
        {
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogWarning("Copying the Store link failed ({Failure}).", FailureShape.Describe(ex));
            _tray.ShowNotification("Couldn't copy the Store link.", isError: true);
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
            _tray.ShowNotification("Already learning from recent dictations.");
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
            if (learned.Count > 0)
            {
                await Task.Run(() => services.GetRequiredService<ITextPostProcessor>().Reload());
            }

            _tray.ShowNotification(learned.Count == 0
                ? "No new recurring terms were found."
                : $"Learned {learned.Count} new {(learned.Count == 1 ? "term" : "terms")} from your dictation history.");
        }
        catch (Exception ex)
        {
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogError("Failed to learn dictionary terms from history: {Failure}", FailureShape.DescribeWithStack(ex));
            _tray.ShowNotification("Couldn't learn from history. See the Scribe log for details.", isError: true);
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
        _welcomeWindow = new Onboarding.WelcomeWindow(gesture, OpenSettings);
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
    private void ShowQuickAdd() => Dispatcher.Invoke(() =>
    {
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
            var recent = store.GetRecent();
            if (recent.Count == 0)
            {
                // Same idea as CopyLastDictation: the in-memory ring is empty on a fresh start, but
                // history still holds what was dictated before the last restart.
                recent = services.GetRequiredService<IHistoryRepository>()
                    .GetRecent(LastTranscriptStore.Capacity)
                    .Select(h => h.Text)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();

                // Seed rather than just display. A correction saved against a transcript that only
                // exists in history would otherwise find nothing to repair in the ring, so the fix
                // would look like it worked while "copy last dictation" still returned the mistake.
                store.Seed(recent);
            }

            var window = new QuickAdd.QuickAddWindow(
                recent,
                loadExisting: () =>
                {
                    var baseEntries = _settingsWindow is { } settings
                        ? settings.CurrentDictionaryEntries()
                        : services.GetRequiredService<IDictionaryRepository>().GetAll();

                    // Compose in the enabled libraries. The popup shows finished text, so the term a
                    // user reaches for is often a shipped library's output; without these the
                    // single-pass conflict check misses the very case that is easiest to walk into.
                    try
                    {
                        return DictionaryLibraryComposer.Merge(
                            baseEntries,
                            services.GetRequiredService<IDictionaryLibraryService>().GetEnabledLibraryEntries());
                    }
                    catch
                    {
                        return baseEntries; // libraries are best-effort, never worth blocking a fix
                    }
                },
                persist: entry =>
                {
                    if (_settingsWindow is { } settings)
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
                logger: services.GetService<ILoggerFactory>()?.CreateLogger<QuickAdd.QuickAddWindow>());

            window.Saved += OnQuickAddSaved;
            window.Closed += (_, _) =>
            {
                window.Saved -= OnQuickAddSaved;
                _quickAddWindow = null;
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
            _tray.ShowNotification("Couldn't open the quick add window.", isError: true);
        }
    });

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
        // to reload the post-processor cannot leave the user with a stale transcript as well.
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
            var services = _host.Services;
            await Task.Run(() => services.GetRequiredService<ITextPostProcessor>().Reload());

            _tray.ShowNotification(entry.Replacement.Length == 0
                ? $"\"{entry.Pattern}\" will now be left out of what you dictate."
                    + (repaired ? " Your last dictation was corrected to match." : string.Empty)
                : $"\"{entry.Pattern}\" will now be written as \"{entry.Replacement}\"."
                    + (repaired ? " Your last dictation was corrected to match." : string.Empty));
        }
        catch (Exception ex)
        {
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogError(
                    "Failed to reload the post-processor after a quick dictionary add: {Failure}",
                    FailureShape.DescribeWithStack(ex));
            _tray.ShowNotification(
                "Saved the rule, but it won't apply until Scribe restarts.", isError: true);
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
            ApplicationThemeManager.Apply(theme, updateAccent: true);
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
