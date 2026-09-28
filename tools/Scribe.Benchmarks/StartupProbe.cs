using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Benchmarks;

/// <summary>
/// Cold-start timings for the data rows that change startup (DATA-O-01 DataLayerWarmUp, DATA-O-03 LightTraceBridge,
/// DATA-O-05b OverlappedIntegrityCheck), which BenchmarkDotNet's warm iterations cannot show. Each run is a fresh child
/// process of this tool that builds and starts a host the way <c>App.StartAsync</c> does (the Core services, the
/// telemetry registration, a cleared logger) and then makes the session banner's first settings load, the database's
/// first use. Variants alternate run by run; the output is milliseconds only.
/// <para>
/// Every child gets an explicit data folder under %TEMP% registered after AddScribeCore, as the app registers its own, so
/// the default data folder is never resolved; SCRIBE_PERF_FLAGS, SCRIBE_DATA_DIR and every OTEL_ variable are removed
/// from its environment and it starts in a temporary working folder. No window, no input. Children run at normal
/// priority, unlike BenchmarkDotNet's.
/// </para>
/// </summary>
internal static class StartupProbe
{
    private const string ParentArgument = "--startup-probe";
    private const string ChildArgument = "--startup-child";

    public static bool IsRequested(string[] args) =>
        args.Any(arg => arg is ParentArgument or ChildArgument);

    public static int Run(string[] args, ExecutionEnvironment host)
    {
        if (args.Contains(ChildArgument))
        {
            return Child(args);
        }

        var scenario = Value(args, ParentArgument) ?? "warmup";
        var runs = int.Parse(Value(args, "--runs") ?? "15", CultureInfo.InvariantCulture);
        var affinity = int.Parse(Value(args, "--affinity") ?? "0", CultureInfo.InvariantCulture);
        var megabytes = int.Parse(Value(args, "--mb") ?? "100", CultureInfo.InvariantCulture);
        string[] variants = scenario switch
        {
#if SCRIBE_BASELINE
            "warmup" => ["cold"],
            "trace" => ["none", "sdk"],
            "integrity" => ["inline"],
#else
            "warmup" => ["cold", "warm", "cold+listener", "warm+listener"],
            "trace" => ["none", "sdk", "listener"],
            "integrity" => ["inline", "background", "background+warm"],
#endif
            _ => throw new ArgumentException("Scenarios: warmup, trace, integrity."),
        };

        var root = Path.Combine(Path.GetTempPath(), "scribe-startup-probe-" + Guid.NewGuid().ToString("N"));
        var data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
        var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        try
        {
            Prepare(data, scenario == "integrity" ? megabytes : 0);
            var databaseBytes = new FileInfo(Path.Combine(data, AppPaths.DatabaseFileName)).Length;
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"startup probe: scenario={scenario} runs={runs} affinity={(affinity == 0 ? "all" : affinity.ToString(CultureInfo.InvariantCulture))} database={databaseBytes / (1024.0 * 1024):F1} MB build={BuildKind()} {host.Compact()}"));

            var results = variants.ToDictionary(variant => variant, _ => new Dictionary<string, List<double>>());
            for (var run = 0; run < runs + 1; run++)
            {
                // The first round warms the file cache and the tool's own disk reads; it is not counted.
                var order = run % 2 == 0 ? variants : [.. variants.Reverse()];
                foreach (var variant in order)
                {
                    var metrics = RunChild(scenario, variant, data, work, affinity);
                    if (run == 0)
                    {
                        continue;
                    }

                    foreach (var (metric, value) in metrics)
                    {
                        if (!results[variant].TryGetValue(metric, out var list))
                        {
                            results[variant][metric] = list = [];
                        }

                        list.Add(value);
                    }
                }
            }

            foreach (var (variant, metrics) in results)
            {
                foreach (var (metric, values) in metrics)
                {
                    values.Sort();
                    Console.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{scenario} {variant,-10} {metric,-16} n={values.Count} p50={Percentile(values, 50):F1} p90={Percentile(values, 90):F1} min={values[0]:F1} max={values[^1]:F1} ms"));
                }
            }

            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception)
            {
                // Temp folder; best effort.
            }
        }
    }

    // ---- The child: one cold start ----------------------------------------------------------------------------------

    private static int Child(string[] args)
    {
        var started = Stopwatch.GetTimestamp();
        var index = Array.IndexOf(args, ChildArgument);
        var scenario = args[index + 1];
        var variant = args[index + 2];
        var paths = new AppPaths(args[index + 3]);

        ScribeDatabase? early = null;
#if !SCRIBE_BASELINE
        if (variant.StartsWith("warm", StringComparison.Ordinal) || variant.EndsWith("+warm", StringComparison.Ordinal))
        {
            _ = DataLayerWarmUp.Start();
        }

        if (variant.StartsWith("background", StringComparison.Ordinal))
        {
            early = new ScribeDatabase(paths, NullLogger<ScribeDatabase>.Instance);
            _ = early.InitializeInBackground();
        }
#endif

        var telemetry = scenario == "trace" ? variant : variant.EndsWith("+listener", StringComparison.Ordinal) ? "listener" : "sdk";
        var hostStarted = Stopwatch.GetTimestamp();
        using var host = BuildHost(paths, telemetry, early);
        var hostMs = Stopwatch.GetElapsedTime(hostStarted).TotalMilliseconds;

        var firstUseMs = 0.0;
        if (scenario != "trace")
        {
            var load = Stopwatch.GetTimestamp();
            _ = host.Services.GetRequiredService<ISettingsRepository>().Load();
            firstUseMs = Stopwatch.GetElapsedTime(load).TotalMilliseconds;
        }

        var totalMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"host={hostMs:F3}"));
        if (scenario != "trace")
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"first_use={firstUseMs:F3}"));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"total={totalMs:F3}"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"assemblies={AppDomain.CurrentDomain.GetAssemblies().Length}"));
        host.StopAsync().GetAwaiter().GetResult();
        return 0;
    }

    // The host App.StartAsync builds, less its app-only services: the Core services, the data folder registered after
    // them, the telemetry registration the variant names, and no logging provider.
    private static IHost BuildHost(AppPaths paths, string telemetry, ScribeDatabase? early)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddScribeCore();
        builder.Services.AddSingleton(paths);
        if (early is not null)
        {
            builder.Services.Replace(ServiceDescriptor.Singleton(_ => early));
        }

        switch (telemetry)
        {
            case "sdk":
                // TelemetryRegistration's SDK path without an endpoint: the resource, the source and the log bridge.
                builder.Services.AddOpenTelemetry()
                    .ConfigureResource(resource => resource.AddService("Scribe", serviceVersion: "0.5.0.0"))
                    .WithTracing(tracing =>
                    {
                        tracing.AddSource(ScribeTelemetry.SourceName);
                        tracing.AddProcessor(sp => new LogBridgeProcessor(sp.GetRequiredService<ILoggerFactory>()));
                    });
                break;
#if !SCRIBE_BASELINE
            case "listener":
                builder.Services.AddHostedService<ListenerService>();
                break;
#endif
        }

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        var host = builder.Build();
        host.Start();
        return host;
    }

    // LogTraceProcessor's shape (it lives in the app).
    private sealed class LogBridgeProcessor(ILoggerFactory loggerFactory) : BaseProcessor<Activity>
    {
        private readonly ILogger _log = loggerFactory.CreateLogger("Scribe.Trace");

        public override void OnEnd(Activity activity) =>
            _log.LogInformation("trace {Span}", TraceTagPolicy.FormatSpan(activity.OperationName, activity.TagObjects, activity.Duration));
    }

#if !SCRIBE_BASELINE
    // TraceLogListener's shape (it lives in the app): the listener is registered when the host starts, inside the timing.
    private sealed class ListenerService(ILoggerFactory loggerFactory) : IHostedService, IDisposable
    {
        private IDisposable? _listener;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _listener = TraceLogBridge.Listen(loggerFactory.CreateLogger(TraceLogBridge.Category));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() => _listener?.Dispose();
    }
#endif

    // ---- The parent -------------------------------------------------------------------------------------------------

    // A database like a user's: settings, 300 dictionary rows and 200 dictations, plus recordings to the given size.
    private static void Prepare(string data, int megabytes)
    {
        using (var database = new ScribeDatabase(new AppPaths(data), NullLogger<ScribeDatabase>.Instance))
        {
            database.Initialize();
            new SettingsRepository(database).Save(AppSettings.CreateDefault());
            new DictionaryRepository(database).AddRange(
                [.. Enumerable.Range(0, 300).Select(i => new DictionaryEntry(0, $"spoken form {i:D4}", $"Written {i}", true, true))]);
            var history = new HistoryRepository(database);
            for (var i = 0; i < 200; i++)
            {
                history.Add(new HistoryEntry(
                    0, new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero).AddMinutes(i), $"a sentence someone said {i}", 3400, 120));
            }

            // Ten seconds of 16 kHz audio is 320 KB as 16-bit PCM.
            var recording = new CapturedAudio(Enumerable.Range(0, 160_000).Select(i => (float)Math.Sin(i / 20.0) * 0.3f).ToArray(), 16_000);
            for (var bytes = 0L; bytes < megabytes * 1024L * 1024; bytes += 320_004)
            {
                history.Add(new HistoryEntry(0, new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.Zero), "a recorded dictation", 10_000, 400), recording);
            }
        }

        using var key = new Microsoft.Data.Sqlite.SqliteConnection(ScribeDatabase.BuildFileConnectionString(new AppPaths(data).DatabasePath));
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(key);
    }

    private static List<(string Metric, double Value)> RunChild(string scenario, string variant, string data, string work, int affinity)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = work,
        };
        foreach (var argument in new[] { ChildArgument, scenario, variant, data })
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var name in info.Environment.Keys.ToList())
        {
            if (name.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, PerfFlags.EnvironmentVariable, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "SCRIBE_DATA_DIR", StringComparison.OrdinalIgnoreCase))
            {
                info.Environment.Remove(name);
            }
        }

        using var child = Process.Start(info) ?? throw new InvalidOperationException("The probe child did not start.");
        if (affinity > 0)
        {
            child.ProcessorAffinity = (IntPtr)((1L << affinity) - 1);
        }

        var output = child.StandardOutput.ReadToEnd();
        var errors = child.StandardError.ReadToEnd();
        if (!child.WaitForExit(TimeSpan.FromSeconds(60)) || child.ExitCode != 0)
        {
            throw new InvalidOperationException($"The probe child failed ({scenario} {variant}): {errors}");
        }

        var metrics = new List<(string, double)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = line.IndexOf('=');
            if (equals > 0 && double.TryParse(line[(equals + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                metrics.Add((line[..equals], value));
            }
        }

        return metrics;
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null;
    }

    private static double Percentile(List<double> sorted, int percentile)
    {
        var rank = (percentile / 100.0) * (sorted.Count - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        return sorted[low] + ((sorted[high] - sorted[low]) * (rank - low));
    }

    private static string BuildKind() =>
        typeof(StartupProbe).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .OfType<System.Reflection.AssemblyConfigurationAttribute>().FirstOrDefault()?.Configuration ?? "unknown";
}
