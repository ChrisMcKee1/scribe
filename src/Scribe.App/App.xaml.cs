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

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showSettingsSignal;
    private RegisteredWaitHandle? _showSettingsRegistration;
    private IHost? _host;
    private TrayIconHost? _tray;
    private DictationController? _controller;
    private IOverlayController? _overlay;
    private SettingsWindow? _settingsWindow;
    private Onboarding.WelcomeWindow? _welcomeWindow;
    private QuickAdd.QuickAddWindow? _quickAddWindow;
    private TextActions.TextActionController? _textActions;
    private Infrastructure.GlobalHotkey? _textActionsHotkey;

    /// <summary>The file log sink, so its health can be reported in Settings.</summary>
    internal static FileLoggerProvider? LogSink { get; private set; }
    private Infrastructure.ForegroundTracker? _foreground;
    private TextActions.TextActionDockWindow? _textActionDock;
    private UpdateService? _updates;
    private SessionDiagnostics? _diagnostics;
    private ILogger? _appLog;
    private int _learningFromHistory;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
            return;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddScribeCore();
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<AzureCliInstaller>();
        builder.Services.AddSingleton<SessionDiagnostics>();

        // The non-destructive selection reader lives in the shell because System.Windows.Automation
        // ships with the Windows Desktop framework and is only referenced where UseWPF is set.
        // Scribe.Core owns the ISelectionProbe contract and the fallback ordering.
        builder.Services.AddSingleton<UiaSelectionProbe>();
        builder.Services.AddSingleton<Scribe.Core.TextInjection.ISelectionProbe>(
            sp => sp.GetRequiredService<UiaSelectionProbe>());
        builder.Services.AddScribeTelemetry();
        builder.Logging.ClearProviders();
        // Held in a static so Settings can report whether logging is ACTUALLY working rather
        // than displaying the folder it was asked to use. A packaged build was found writing
        // nothing for an entire session while the About page confidently showed a path.
        LogSink = new FileLoggerProvider(paths.LogsDir);
        builder.Logging.AddProvider(LogSink);
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

        // Install the seed dictionary on first run so post-processing is useful out of the box, then
        // retire the entries older versions seeded that replaced ordinary words.
        var dictionary = services.GetRequiredService<IDictionaryRepository>();
        var settingsRepository = services.GetRequiredService<ISettingsRepository>();
        dictionary.SeedIfEmpty(DefaultVocabulary.Entries);
        SeedVocabularyRetirement.Apply(
            settingsRepository,
            dictionary,
            DefaultVocabulary.RetiredEntries,
            log);

        // Older builds demoted a model to its CPU build on any load failure, including a variant
        // needing an execution provider this PC never had. Scribe now avoids that up front, so the
        // saved markers only pin cleanup to the CPU for no reason.
        FoundryDemotionReset.Apply(settingsRepository, services.GetRequiredService<AppPaths>(), log);

        _tray = new TrayIconHost();
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

        _controller = new DictationController(
            services.GetRequiredService<IHotkeyService>(),
            services.GetRequiredService<IAudioCaptureService>(),
            services.GetRequiredService<IVadService>(),
            services.GetRequiredService<ITranscriptionService>(),
            services.GetRequiredService<ITextPostProcessor>(),
            services.GetRequiredService<ITextCleanupService>(),
            services.GetRequiredService<ITextInjector>(),
            services.GetRequiredService<IHistoryRepository>(),
            services.GetRequiredService<IDictionaryRepository>(),
            services.GetRequiredService<IDictionaryLibraryService>(),
            services.GetRequiredService<ICleanupFailureLog>(),
            services.GetRequiredService<LastTranscriptStore>(),
            services.GetRequiredService<ISettingsRepository>(),
            services.GetRequiredService<ILogger<DictationController>>());

        _overlay = new OverlayProcessClient(
            services.GetRequiredService<IAudioCaptureService>(),
            services.GetRequiredService<ILogger<OverlayProcessClient>>());

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
        _controller.Warning += message =>
        {
            _tray!.ShowNotification(message, isError: true);
            OnRecordingWarning("Microphone muted");
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
                _appLog?.LogDebug(ex, "Could not show the AI cleanup provider notification.");            }
        }));
        _controller.ModelsReleased += () =>
        {
            // The overlay helper is the other idle-only resident (~100 MB of WinUI runtime).
            // ShowRecording relaunches it lazily, exactly like the overlay-disabled path, so
            // closing it here costs one warm-up on the first post-idle dictation and nothing else.
            try
            {
                _overlay?.CloseOverlay();
            }
            catch (Exception ex)
            {
                _appLog?.LogDebug(ex, "Could not close the overlay during idle release.");
            }
        };

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
                log.LogWarning(ex, "Failed to show the injection recovery notification.");
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
                log.LogInformation(ex, "No transcription model is installed; waiting for a Settings selection.");
                _tray!.ShowInfo("No speech model is installed. Choose one in Settings.");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to warm-load the transcription engine.");
                _tray!.ShowError("model failed to load, see logs");
            }
        });

        _controller.Start();

        // Settings-dependent wiring goes AFTER Start(): CurrentSettings returns compiled defaults
        // until Start() loads the persisted settings, so reading it earlier silently ignored the
        // user's saved overlay position, overlay toggle, and AI-cleanup state on every launch.
        _overlay.SetPosition(_controller.CurrentSettings.OverlayPosition);
        // Pre-warm the out-of-process WinUI pill so its transparent surface is ready before first
        // use. Only spawn the helper when the overlay is actually enabled; if the user turns it on
        // later, ShowRecording launches it lazily.
        if (_controller.CurrentSettings.ShowOverlay)
        {
            _overlay.Warmup();
        }

        // Text actions: a separate controller and a separate RegisterHotKey trigger, deliberately
        // sharing nothing with the push-to-talk hook. If any of this fails, dictation is unaffected.
        _textActions = new TextActions.TextActionController(
            services.GetRequiredService<SelectionReader>(),
            services.GetRequiredService<ITextCleanupService>(),
            services.GetRequiredService<ITextPostProcessor>(),
            services.GetRequiredService<IDictionaryRepository>(),
            services.GetRequiredService<ITextInjector>(),
            services.GetRequiredService<ILogger<TextActions.TextActionController>>(),
            services.GetRequiredService<IDictionaryLibraryService>());
        _textActions.Notice += message =>
        {
            try
            {
                _tray?.ShowNotification(message);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Could not show the text action notice.");
            }
        };

        // Remembers the last window that was not Scribe's, so the tray route can still find the
        // selection after its own menu has taken the foreground.
        _foreground = new Infrastructure.ForegroundTracker(log);
        _foreground.Start();
        _textActions.ForegroundTargetProvider = () => _foreground?.LastForeignWindow ?? 0;
        _textActions.StateChanged += state =>
            Dispatcher.BeginInvoke(() => _textActionDock?.SetState(state));

        _textActionsHotkey = new Infrastructure.GlobalHotkey(log);

        // The hotkey and the dock do not disturb activation, so they read the live foreground
        // window. The tray menu does, so it asks for the remembered one.
        _textActionsHotkey.Pressed += () => _textActions?.Invoke();
        _tray.TextActionsRequested += () => _textActions?.Invoke(useRememberedTarget: true);

        ApplyTextActionSettings(_controller.CurrentSettings);

        _tray.SetAiCleanupChecked(_controller.CurrentSettings.EnableAiCleanup);

        // Trim any stale AI-failure log entries (older than the rolling one-week window) on startup,
        // off the UI thread, so the Settings failure list never accumulates indefinitely.
        _ = Task.Run(() => _controller!.PruneFailureLog());

        // Apply the history retention window the same way. Stored audio is the cost that makes
        // this matter (~1.9 MB per dictated minute); before this, both tables grew for the life
        // of the install with no pruning path but a manual Clear.
        var retentionDays = _controller.CurrentSettings.HistoryRetentionDays;
        if (retentionDays > 0)
        {
            var history = services.GetRequiredService<IHistoryRepository>();
            _ = Task.Run(() =>
            {
                var removed = history.PruneOlderThan(DateTimeOffset.UtcNow.AddDays(-retentionDays));
                if (removed > 0)
                {
                    log.LogInformation(
                        "Pruned {Count} dictation history entries older than {Days} days.",
                        removed, retentionDays);
                }
            });
        }

        // Reconcile the "launch at logon" registry entry with the saved preference so it self-heals
        // if the app was moved, and clears if the user disabled it elsewhere.
        if (!settingsRepository.LastLoadFailed)
        {
            StartupRegistration.Sync(_controller.CurrentSettings.LaunchOnLogin);
        }

        log.LogInformation("Scribe started. Hold {Key} to dictate.", _controller.CurrentSettings.Hotkey.DisplayName);

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
            log.LogDebug(ex, "Compute capability detection failed.");
        }

        // The dictionary seed above forced database initialization, so a corruption repair (if any)
        // already ran; tell the user now rather than let them discover missing history on their own.
        if (services.GetRequiredService<ScribeDatabase>().RepairedAtStartup)
        {
            _tray.ShowInfo("Scribe repaired its database. Settings and dictionary were recovered; some history may be missing.");
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
                var settings = repo.Load();
                settings.HasCompletedFirstRun = true;
                repo.Save(settings);
            }
            else
            {
                _tray.ShowError("settings were recovered, review and save them in Settings");
            }
        }
        // --- End onboarding -----------------------------------------------------------------

        // Update checks are user-initiated from Settings so the offline-first startup path performs
        // no network access. Previously staged updates are detected by the same manual check.
        _updates = new UpdateService(services.GetRequiredService<ILogger<UpdateService>>());
        _updates.UpdateReady += message => _tray?.ShowInfo(message);
        _updates.ProbePendingLocal();

        // Allow `Scribe.exe --settings` to jump straight to the settings window on launch.
        if (HasSettingsSwitch(e.Args))
        {
            OpenSettings();
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
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Scribe",
            Content = "Scribe is already running. Look for the microphone icon in the system tray.",
            PrimaryButtonText = "OK",
            IsSecondaryButtonEnabled = false,
            IsCloseButtonEnabled = false,
        };

        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = dialog.ShowDialogAsync().ContinueWith(
            _ => frame.Continue = false,
            System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// Reflects a dictation state change in the tray icon and the recording overlay. Runs on a
    /// background thread, so overlay mutations are marshalled to the UI thread. The overlay only
    /// shows while recording and only when the user has it enabled.
    /// </summary>
    private void OnStateChanged(DictationState state)
    {
        // The tray and the overlay are independent views of the same state. A failure updating one
        // must never stop the other: when this method threw, the overlay was left showing whatever
        // it had last been told, so the pill sat on "Transcribing" while dictation kept working.
        try
        {
            _tray?.SetState(state);
        }
        catch (Exception ex)
        {
            _host?.Services.GetRequiredService<ILogger<App>>()
                .LogWarning(ex, "Could not update the tray icon for state {State}.", state);
        }

        var overlayEnabled = _controller?.CurrentSettings.ShowOverlay ?? false;
        // The dictation-only hotkey overrides AI cleanup for its capture without changing the
        // global setting, so the overlay must read the active capture snapshot.
        var aiPolishing = _controller?.ActiveCaptureUsesAiCleanup ?? false;
        Dispatcher.BeginInvoke(() =>
        {
            if (!overlayEnabled)
            {
                _overlay?.HideOverlay();
                return;
            }

            switch (state)
            {
                case DictationState.Recording:
                    _overlay?.ShowRecording();
                    break;
                case DictationState.Processing:
                    _overlay?.ShowProcessing(aiPolishing);
                    break;
                default:
                    _overlay?.HideOverlay();
                    break;
            }
        });
    }

    /// <summary>
    /// Shows the brief red "intelligence failed" overlay when AI cleanup fell back to raw text.
    /// Raised on a background thread, so the overlay mutation is marshalled to the UI thread and
    /// only shown when the user has the overlay enabled.
    /// </summary>
    private void OnCleanupFailed(string reason)
    {
        var overlayEnabled = _controller?.CurrentSettings.ShowOverlay ?? false;
        if (!overlayEnabled)
        {
            return;
        }

        Dispatcher.BeginInvoke(() => _overlay?.ShowFailed(reason));
    }

    private void OnRecordingWarning(string reason)
    {
        var overlayEnabled = _controller?.CurrentSettings.ShowOverlay ?? false;
        if (!overlayEnabled)
        {
            return;
        }

        Dispatcher.BeginInvoke(() => _overlay?.ShowRecordingWarning(reason));
    }

    /// <summary>
    /// Quick tray toggle for AI cleanup: persist the flipped flag and apply it live, without
    /// opening settings. Lets the user hop between raw Parakeet output and AI-polished text in
    /// two clicks. Note an already-open settings window keeps its own snapshot; saving it wins.
    /// </summary>
    private void ToggleAiCleanup(bool enabled)
    {
        try
        {
            var repo = _host!.Services.GetRequiredService<ISettingsRepository>();
            if (repo.LastLoadFailed)
            {
                _tray?.SetAiCleanupChecked(_controller?.CurrentSettings.EnableAiCleanup ?? false);
                _tray?.ShowError("review recovered settings before changing AI cleanup");
                return;
            }

            var settings = repo.Load();
            settings.EnableAiCleanup = enabled;
            repo.Save(settings);
            _controller?.ApplySettings(settings);
            _settingsWindow?.ReloadExternalSettings();
            _tray?.ShowInfo(enabled ? "AI cleanup on" : "AI cleanup off");
        }
        catch (Exception ex)
        {
            _host?.Services.GetRequiredService<ILogger<App>>()
                .LogWarning(ex, "Toggling AI cleanup from the tray failed.");
            _tray?.SetAiCleanupChecked(_controller?.CurrentSettings.EnableAiCleanup ?? false);
            _tray?.ShowError("couldn't toggle AI cleanup");
        }
    }

    /// <summary>
    /// Applies the text action settings: hands the controller its snapshot, shows or hides the tray
    /// entry, and registers or clears the global hotkey.
    /// </summary>
    /// <remarks>
    /// Every step is best effort. A hotkey Windows refuses (because another app already owns the
    /// combination) leaves the feature reachable from the tray rather than failing the save, and no
    /// failure here is allowed to propagate into the settings write or the dictation loop.
    /// </remarks>
    private void ApplyTextActionSettings(AppSettings settings)
    {
        try
        {
            _textActions?.ApplySettings(settings);
            _tray?.SetTextActionsVisible(settings.EnableTextActions);

            var binding = settings.EnableTextActions ? settings.TextActionsHotkey : null;
            if (_textActionsHotkey?.Update(binding) == false && binding is not null)
            {
                _tray?.ShowError($"another app already uses {binding.DisplayName}");
            }

            ApplyTextActionDock(settings);
        }
        catch (Exception ex)
        {
            _appLog?.LogWarning(ex, "Applying the text action settings failed.");
        }
    }

    /// <summary>Shows, hides, or repositions the floating dock to match the saved settings.</summary>
    private void ApplyTextActionDock(AppSettings settings)
    {
        var wanted = settings.EnableTextActions && settings.ShowTextActionDock;

        if (!wanted)
        {
            _textActionDock?.Close();
            _textActionDock = null;
            return;
        }

        if (_textActionDock is null)
        {
            var dock = new TextActions.TextActionDockWindow();

            // The dock never takes focus, so unlike the tray route it reads the live foreground
            // window and the user's selection is still sitting there intact.
            dock.Clicked += () => _textActions?.Invoke();
            dock.Moved += (left, top) => SaveDockPosition(left, top);
            dock.Closed += (_, _) => { if (ReferenceEquals(_textActionDock, dock)) _textActionDock = null; };

            _textActionDock = dock;
            dock.Show();
        }

        if (settings.TextActionDockLeft is { } savedLeft && settings.TextActionDockTop is { } savedTop)
        {
            _textActionDock.PlaceAt(savedLeft, savedTop);
        }
        else
        {
            _textActionDock.PlaceAtDefault();
        }
    }

    private void SaveDockPosition(double left, double top)
    {
        try
        {
            var repo = _host!.Services.GetRequiredService<ISettingsRepository>();
            if (repo.LastLoadFailed)
            {
                return;
            }

            var settings = repo.Load();
            settings.TextActionDockLeft = left;
            settings.TextActionDockTop = top;
            repo.Save(settings);
            _controller?.ApplySettings(settings);
            _textActions?.ApplySettings(settings);
        }
        catch (Exception ex)
        {
            // Losing the dock position is a cosmetic failure; it must never surface as an error.
            _appLog?.LogDebug(ex, "Could not save the dock position.");
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
        _settingsWindow = new SettingsWindow(
            services.GetRequiredService<ISettingsRepository>(),
            services.GetRequiredService<IAudioCaptureService>(),
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
            position => _overlay?.Preview(position),
            settings =>
            {
                _controller!.ApplySettings(settings);
                _overlay?.SetPosition(settings.OverlayPosition);
                _tray?.SetAiCleanupChecked(settings.EnableAiCleanup);
                ApplyTextActionSettings(settings);
            },
            capturing => _controller?.SetHotkeyCaptureMode(capturing),
            _updates,
            services.GetRequiredService<SessionDiagnostics>());
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
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
                .LogWarning(ex, "Copying the last dictation failed.");
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
                .LogWarning(ex, "Copying a recent dictation failed.");
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
                .LogWarning(ex, "Opening the Microsoft Store listing failed; falling back to the web listing.");
            try
            {
                Process.Start(new ProcessStartInfo(ScribeLinks.StoreWeb) { UseShellExecute = true });
            }
            catch (Exception fallbackError)
            {
                _host.Services.GetRequiredService<ILogger<App>>()
                    .LogWarning(fallbackError, "Opening the Store web listing failed.");
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
                .LogWarning(ex, "Copying the Store link failed.");
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
                .LogError(ex, "Failed to learn dictionary terms from history.");
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

        var hotkey = _controller?.CurrentSettings.Hotkey.DisplayName ?? "Right Ctrl";
        _welcomeWindow = new Onboarding.WelcomeWindow(hotkey, OpenSettings);
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

        try
        {
            var services = _host.Services;
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
            };

            _quickAddWindow = window;
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            _quickAddWindow = null;
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogError(ex, "Failed to open the quick dictionary add window.");
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
                    .LogWarning(ex, "Failed to repair the retained transcript after a quick dictionary add.");
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
                .LogError(ex, "Failed to reload the post-processor after a quick dictionary add.");
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
            log.LogWarning(ex, "Unable to subscribe to Windows theme changes.");
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
            _appLog?.LogWarning(ex, "Unable to queue Windows theme refresh.");
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
                registryValue?.ToString() ?? "unavailable",
                reason,
                readRegistry);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Unable to apply the Windows theme; keeping the current app resources.");
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
        try
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

            // Stage any downloaded update first so the updater is waiting as the process exits.
            _updates?.ApplyPendingOnExit();

            _overlay?.CloseOverlay();
            _controller?.Dispose();
            _textActionsHotkey?.Dispose();
            _textActionDock?.Close();
            _foreground?.Dispose();
            _textActions?.Dispose();
            _tray?.Dispose();
            DisposeThemeWatcher();

            if (_host is not null)
            {
                _host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                _host.Dispose();
            }
        }
        catch
        {
            // Best-effort shutdown; never let teardown errors block process exit.
        }
        finally
        {
            // Unregister before disposing the handle it waits on, or the pool can touch a freed one.
            try { _showSettingsRegistration?.Unregister(null); } catch { }
            try { _showSettingsSignal?.Dispose(); } catch { }
            _singleInstanceMutex?.Dispose();
        }

        base.OnExit(e);
    }

    /// <summary>
    /// Routes unhandled exceptions from the UI thread, background threads and faulted tasks to
    /// the log file. UI-thread faults are marked handled so a single bad dictation never tears
    /// down the whole tray app.
    /// </summary>
    private void WireGlobalExceptionLogging(ILogger log)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            log.LogError(args.Exception, "Unhandled dispatcher exception.");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            log.LogCritical(args.ExceptionObject as Exception, "Unhandled domain exception (terminating={Terminating}).", args.IsTerminating);

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            log.LogError(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };
    }
}
