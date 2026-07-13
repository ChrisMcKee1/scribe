using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

    private Mutex? _singleInstanceMutex;
    private IHost? _host;
    private TrayIconHost? _tray;
    private DictationController? _controller;
    private IOverlayController? _overlay;
    private SettingsWindow? _settingsWindow;
    private Onboarding.WelcomeWindow? _welcomeWindow;
    private UpdateService? _updates;
    private int _learningFromHistory;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isNew);
        if (!isNew)
        {
            ShowSingleInstanceNotice();
            Shutdown();
            return;
        }

        // Tray app: never exit just because a window closed; quit happens explicitly from the tray.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var paths = new AppPaths();
        paths.EnsureCreated();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddScribeCore();
        builder.Services.AddScribeTelemetry();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(paths.LogsDir));
        builder.Logging.AddDebug();
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        _host = builder.Build();
        _host.Start();

        var services = _host.Services;
        var log = services.GetRequiredService<ILogger<App>>();
        WireGlobalExceptionLogging(log);

        // Install the seed dictionary on first run so post-processing is useful out of the box.
        services.GetRequiredService<IDictionaryRepository>().SeedIfEmpty(DefaultVocabulary.Entries);

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
        _controller.Error += message =>
        {
            _tray!.ShowError(message);
            // Mirror the failure on the overlay (like cleanup failures): the user is looking at the
            // pill mid-dictation, not the tray, when the microphone produces nothing.
            OnCleanupFailed(message);
        };
        _controller.CleanupFailed += OnCleanupFailed;
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
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to warm-load the transcription engine.");
                _tray!.ShowError("model failed to load — see logs");
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

        _tray.SetAiCleanupChecked(_controller.CurrentSettings.EnableAiCleanup);

        // Trim any stale AI-failure log entries (older than the rolling one-week window) on startup,
        // off the UI thread, so the Settings failure list never accumulates indefinitely.
        _ = Task.Run(() => _controller!.PruneFailureLog());

        // Reconcile the "launch at logon" registry entry with the saved preference so it self-heals
        // if the app was moved, and clears if the user disabled it elsewhere.
        var settingsRepository = services.GetRequiredService<ISettingsRepository>();
        if (!settingsRepository.LastLoadFailed)
        {
            StartupRegistration.Sync(_controller.CurrentSettings.LaunchOnLogin);
        }

        log.LogInformation("Scribe started. Hold {Key} to dictate.", _controller.CurrentSettings.Hotkey.DisplayName);

        // The dictionary seed above forced database initialization, so a corruption repair (if any)
        // already ran — tell the user now rather than let them discover missing history on their own.
        if (services.GetRequiredService<ScribeDatabase>().RepairedAtStartup)
        {
            _tray.ShowInfo("Scribe repaired its database — settings and dictionary were recovered; some history may be missing.");
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
                _tray.ShowError("settings were recovered — review and save them in Settings");
            }
        }
        // --- End onboarding -----------------------------------------------------------------

        // Update checks are user-initiated from Settings so the offline-first startup path performs
        // no network access. Previously staged updates are detected by the same manual check.
        _updates = new UpdateService(services.GetRequiredService<ILogger<UpdateService>>());
        _updates.UpdateReady += message => _tray?.ShowInfo(message);
        _updates.ProbePendingLocal();

        // Allow `Scribe.exe --settings` to jump straight to the settings window on launch.
        if (e.Args.Any(arg => string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase)))
        {
            OpenSettings();
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
        _tray?.SetState(state);

        var overlayEnabled = _controller?.CurrentSettings.ShowOverlay ?? false;
        var polishing = _controller?.CurrentSettings.EnableAiCleanup ?? false;
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
                    _overlay?.ShowProcessing(polishing);
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
            services.GetRequiredService<ICleanupFailureLog>(),
            services.GetRequiredService<ITranscriptionModelInstaller>(),
            position => _overlay?.Preview(position),
            settings =>
            {
                _controller!.ApplySettings(settings);
                _overlay?.SetPosition(settings.OverlayPosition);
                _tray?.SetAiCleanupChecked(settings.EnableAiCleanup);
            },
            capturing => _controller?.SetHotkeyCaptureMode(capturing),
            _updates);
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

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // Stage any downloaded update first so the updater is waiting as the process exits.
            _updates?.ApplyPendingOnExit();

            _overlay?.CloseOverlay();
            _controller?.Dispose();
            _tray?.Dispose();

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
