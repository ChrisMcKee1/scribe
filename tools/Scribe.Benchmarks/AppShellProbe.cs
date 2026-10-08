using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scribe.App.Infrastructure;
using Scribe.App.Settings;
using Scribe.Core.Audio;
using Scribe.Core.Cleanup;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.Transcription;
using Scribe.Core.Vad;
using Scribe.Core.Vocabulary;

namespace Scribe.Benchmarks;

internal static class AppShellProbe
{
    private const string Switch = "--app-shell";
    private const string DefaultRoot = "C:\\Users\\chrismckee\\sw\\perf\\scratch\\pa10\\app-shell-probe";
    private const string OwnershipMarker = ".scribe-app-shell-probe";

    public static bool IsRequested(string[] args) => args.Contains(Switch, StringComparer.OrdinalIgnoreCase);

    public static int Run(string[] args, ExecutionEnvironment environment)
    {
        var requestedIterations = ReadInt(args, "--iterations", 1);
        var iterations = Math.Min(requestedIterations, 1);
        var pumpMilliseconds = ReadInt(args, "--pump-ms", 2000);
        var dataRootBase = ReadValue(args, "--data-root") ?? DefaultRoot;
        var seedRows = ReadInt(args, "--seed-history", 1000);
        var seedFailures = ReadInt(args, "--seed-failures", 10000);
        var loadSpeechModel = args.Contains("--load-speech-model", StringComparer.OrdinalIgnoreCase);
        var dataRoot = CreateOwnedDataRoot(dataRootBase);

        Console.WriteLine($"App shell probe host: {environment.Describe()}");
        Console.WriteLine($"dataRootBase={dataRootBase}");
        Console.WriteLine($"dataRoot={dataRoot}");
        Console.WriteLine($"iterations={iterations} pumpMs={pumpMilliseconds} seedHistory={seedRows} seedFailures={seedFailures} loadSpeechModel={loadSpeechModel}");
        if (requestedIterations > iterations)
        {
            Console.WriteLine("The Settings window probe runs one window per process because WPF-UI theme dictionaries are process-global.");
        }

        Environment.SetEnvironmentVariable(AppPaths.DataDirVariable, dataRoot);
        EnsureApplication();

        using (var seeded = CreateHost(dataRoot, out _))
        {
            var database = seeded.Services.GetRequiredService<ScribeDatabase>();
            database.Initialize();
            Seed(seedRows, seedFailures, seeded.Services);
        }

        Console.WriteLine(
            "iteration,host_build_ms,db_initialize_ms,settings_construct_ms,pump_ms,private_mb,working_set_mb,gc_heap_mb,loh_mb");
        for (var i = 0; i < iterations; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            var hostTimer = Stopwatch.StartNew();
            using var host = CreateHost(dataRoot, out var paths);
            hostTimer.Stop();

            var dbTimer = Stopwatch.StartNew();
            host.Services.GetRequiredService<ScribeDatabase>().Initialize();
            dbTimer.Stop();
            if (loadSpeechModel)
            {
                host.Services.GetRequiredService<IVadService>().Initialize();
                host.Services.GetRequiredService<ITranscriptionService>().Initialize();
            }

            SettingsWindow? window = null;
            var constructTimer = Stopwatch.StartNew();
            try
            {
                window = CreateSettingsWindow(host.Services, paths);
            }
            finally
            {
                constructTimer.Stop();
            }

            var pumpTimer = Stopwatch.StartNew();
            Pump(TimeSpan.FromMilliseconds(pumpMilliseconds));
            pumpTimer.Stop();

            var process = Process.GetCurrentProcess();
            process.Refresh();
            var gc = GC.GetGCMemoryInfo();
            Console.WriteLine(string.Join(
                ',',
                i + 1,
                Milliseconds(hostTimer),
                Milliseconds(dbTimer),
                Milliseconds(constructTimer),
                Milliseconds(pumpTimer),
                Megabytes(process.PrivateMemorySize64),
                Megabytes(process.WorkingSet64),
                Megabytes(gc.HeapSizeBytes),
                Megabytes(gc.GenerationInfo.Length > 3 ? gc.GenerationInfo[3].SizeAfterBytes : 0)));

            window?.Close();
            Pump(TimeSpan.FromMilliseconds(100));
        }

        return 0;
    }

    private static string CreateOwnedDataRoot(string requestedBase)
    {
        var rootBase = Path.GetFullPath(requestedBase);
        RefuseSensitiveDataRoot(rootBase);
        Directory.CreateDirectory(rootBase);

        var root = Path.Combine(
            rootBase,
            "run-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root))
        {
            var marker = Path.Combine(root, OwnershipMarker);
            if (!File.Exists(marker))
            {
                throw new InvalidOperationException("Refusing to reuse an app shell probe data folder without the probe ownership marker.");
            }

            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, OwnershipMarker), "Scribe app shell probe owns this directory.");
        return root;
    }

    private static void RefuseSensitiveDataRoot(string root)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (IsSameOrUnder(root, Path.Combine(localAppData, "ScribeData")) ||
            IsSameOrUnder(root, Path.Combine(userProfile, ".Scribe")) ||
            IsPackageLocalCache(root, localAppData))
        {
            throw new InvalidOperationException("Refusing to run the app shell probe in a Scribe user data folder.");
        }
    }

    private static bool IsPackageLocalCache(string root, string localAppData)
    {
        var packages = Path.Combine(localAppData, "Packages");
        if (!IsSameOrUnder(root, packages))
        {
            return false;
        }

        var localCache = Path.DirectorySeparatorChar + "LocalCache";
        var normalized = NormalizeForComparison(root);
        return normalized.EndsWith(localCache, StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(localCache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrUnder(string candidate, string parent)
    {
        var normalizedCandidate = NormalizeForComparison(candidate);
        var normalizedParent = NormalizeForComparison(parent);
        return string.Equals(normalizedCandidate, normalizedParent, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeForComparison(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static IHost CreateHost(string dataRoot, out AppPaths paths)
    {
        paths = AppPaths.CreateForStartup(dataRoot);
        paths.EnsureCreated();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddScribeCore();
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<AzureCliInstaller>();
        builder.Services.AddSingleton<StartupRegistration>();
        builder.Services.AddSingleton<SessionDiagnostics>();
        builder.Services.AddSingleton<VocabularyPublisher>();
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        return builder.Build();
    }

    private static SettingsWindow CreateSettingsWindow(IServiceProvider services, AppPaths paths)
    {
        return new SettingsWindow(
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
            paths,
            services.GetRequiredService<StartupRegistration>(),
            services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TranscriptionOptions>>(),
            _ => { },
            _ => Task.FromResult(new VocabularyRefresh(VocabularyRefreshOutcome.Applied, VocabularyGeneration.Empty)),
            () => Task.FromResult(new VocabularyRefresh(VocabularyRefreshOutcome.Applied, VocabularyGeneration.Empty)),
            services.GetRequiredService<ILibraryVocabularySource>(),
            textScale: null,
            setHotkeyCaptureMode: _ => { },
            updates: null,
            runUpdateRestartGuard: null,
            showRestartFailedNotice: null,
            diagnostics: services.GetRequiredService<SessionDiagnostics>(),
            historyDeletionNotifier: services.GetRequiredService<HistoryDeletionNotifier>())
        {
            OllamaService = services.GetRequiredService<OllamaServiceController>(),
        };
    }

    private static void Seed(int historyRows, int failureRows, IServiceProvider services)
    {
        var database = services.GetRequiredService<ScribeDatabase>();
        var now = DateTimeOffset.UtcNow;
        using var scope = database.EnterWriteScope();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var history = connection.CreateCommand();
        history.Transaction = transaction;
        history.CommandText =
            """
            INSERT INTO history (timestamp_utc, text, audio_ms, decode_ms, cleanup_ms, target_app, transcription_model_id)
            VALUES ($ts, $text, $audio_ms, $decode_ms, $cleanup_ms, $target_app, $model_id);
            """;
        var historyTimestamp = history.Parameters.Add("$ts", SqliteType.Text);
        history.Parameters.AddWithValue("$text", "synthetic entry");
        history.Parameters.AddWithValue("$audio_ms", 12000);
        history.Parameters.AddWithValue("$decode_ms", 380);
        history.Parameters.AddWithValue("$cleanup_ms", 45);
        history.Parameters.AddWithValue("$target_app", "probe");
        history.Parameters.AddWithValue("$model_id", "parakeet-tdt-0.6b-v3-int8");

        for (var i = 0; i < historyRows; i++)
        {
            historyTimestamp.Value = now.AddMinutes(-i).ToString("O", CultureInfo.InvariantCulture);
            history.ExecuteNonQuery();
        }

        using var failure = connection.CreateCommand();
        failure.Transaction = transaction;
        failure.CommandText =
            """
            INSERT INTO cleanup_failures (timestamp_utc, provider, model, reason, sample)
            VALUES ($ts, $provider, $model, $reason, $sample);
            """;
        var failureTimestamp = failure.Parameters.Add("$ts", SqliteType.Text);
        failure.Parameters.AddWithValue("$provider", "Probe");
        failure.Parameters.AddWithValue("$model", "ProbeModel");
        failure.Parameters.AddWithValue("$reason", "Synthetic probe failure.");
        failure.Parameters.AddWithValue("$sample", DBNull.Value);

        for (var i = 0; i < failureRows; i++)
        {
            failureTimestamp.Value = now.AddMinutes(-i).ToString("O", CultureInfo.InvariantCulture);
            failure.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void EnsureApplication()
    {
        if (Application.Current is not null)
        {
            return;
        }

        var app = new Scribe.App.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = duration,
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        return int.TryParse(ReadValue(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private static string? ReadValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static string Milliseconds(Stopwatch stopwatch) =>
        stopwatch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture);

    private static string Megabytes(long bytes) =>
        (bytes / 1024d / 1024d).ToString("F1", CultureInfo.InvariantCulture);
}
