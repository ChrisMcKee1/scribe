using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scribe.App.Dictation;
using Scribe.App.Infrastructure;
using Scribe.App.Settings;
using Scribe.App.Tray;
using Scribe.Core.Audio;
using Scribe.Core.Hotkeys;
using Scribe.Core.Infrastructure;
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
    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isNew);
        if (!isNew)
        {
            MessageBox.Show("Scribe is already running. Look for the microphone icon in the system tray.",
                "Scribe", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Tray app: never exit just because a window closed; quit happens explicitly from the tray.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var paths = new AppPaths();
        paths.EnsureCreated();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddScribeCore();
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

        _controller = new DictationController(
            services.GetRequiredService<IHotkeyService>(),
            services.GetRequiredService<IAudioCaptureService>(),
            services.GetRequiredService<IVadService>(),
            services.GetRequiredService<ITranscriptionService>(),
            services.GetRequiredService<ITextPostProcessor>(),
            services.GetRequiredService<ITextInjector>(),
            services.GetRequiredService<IHistoryRepository>(),
            services.GetRequiredService<ISettingsRepository>(),
            services.GetRequiredService<ILogger<DictationController>>());

        _controller.StateChanged += state => _tray!.SetState(state);
        _controller.Error += message => _tray!.ShowError(message);

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

        // Reconcile the "launch at logon" registry entry with the saved preference so it self-heals
        // if the app was moved, and clears if the user disabled it elsewhere.
        StartupRegistration.Sync(_controller.CurrentSettings.LaunchOnLogin);

        log.LogInformation("Scribe started. Hold {Key} to dictate.", _controller.CurrentSettings.Hotkey.DisplayName);

        // Allow `Scribe.exe --settings` to jump straight to the settings window on launch.
        if (e.Args.Any(arg => string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase)))
        {
            OpenSettings();
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
            settings => _controller!.ApplySettings(settings));
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    });

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
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
