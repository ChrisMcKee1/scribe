using System.Diagnostics;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

/// <summary>
/// DATA-O-03 (<see cref="PerfFlags.LightTraceBridge"/>): without anything configuring OpenTelemetry, a plain listener
/// bridges the spans to the log instead of the SDK. These tests hold it to the real SDK (OpenTelemetry 1.18.0, its default
/// sampler) scenario by scenario: whether an activity exists, <see cref="Activity.Current"/> before, during and after, its
/// ids, trace state, requested data and recorded flag, how many lines are written and what they say, with a second
/// listener present, and after disposal. Each run uses a source of its own, so no other test's listener is involved.
/// </summary>
public sealed class TraceLogBridgeTests
{
    private static readonly ActivityTraceId ParentTrace = ActivityTraceId.CreateFromString("0af7651916cd43dd8448eb211c80319c");
    private static readonly ActivitySpanId ParentSpan = ActivitySpanId.CreateFromString("b7ad6b7169203331");
    private static readonly DateTime Start = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string> Scenarios() =>
    [
        "root", "ambient chain", "recorded local parent", "unrecorded local parent", "recorded remote parent",
        "unrecorded remote parent", "recorded W3C parent id", "unrecorded W3C parent id", "hierarchical parent id",
        "trace state", "error", "unrecorded ambient parent", "recorded ambient parent", "tags",
    ];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void The_listener_decides_and_writes_exactly_as_the_SDK_does(string scenario)
    {
        if (EnvironmentConfiguresOpenTelemetry())
        {
            return; // The SDK would not be running its default sampler here, so there is nothing to compare with.
        }

        var sdk = Run(Arm.Sdk, source => Play(scenario, source));
        var listener = Run(Arm.Listener, source => Play(scenario, source));

        AssertSame(sdk, listener);
    }

    [Theory]
    [InlineData("unrecorded local parent")]
    [InlineData("unrecorded remote parent")]
    [InlineData("root")]
    public void A_second_listener_that_asks_for_all_data_changes_both_the_same_way(string scenario)
    {
        if (EnvironmentConfiguresOpenTelemetry())
        {
            return;
        }

        var sdk = Run(Arm.Sdk, source => Play(scenario, source), secondListener: true);
        var listener = Run(Arm.Listener, source => Play(scenario, source), secondListener: true);

        AssertSame(sdk, listener);
        Assert.Contains("trace work", listener.Log, StringComparison.Ordinal);
    }

    [Fact]
    public void After_disposal_neither_creates_an_activity_or_writes_a_line()
    {
        if (EnvironmentConfiguresOpenTelemetry())
        {
            return;
        }

        var sdk = Run(Arm.Sdk, _ => [], afterDisposal: source => Play("root", source));
        var listener = Run(Arm.Listener, _ => [], afterDisposal: source => Play("root", source));

        Assert.Equal(sdk.Observations, listener.Observations);
        Assert.Contains("created=False", Assert.Single(listener.Observations), StringComparison.Ordinal);
        Assert.Equal(string.Empty, listener.Log);
    }

    [Fact]
    public void The_listener_samples_as_parent_based_always_on()
    {
        Assert.Equal(ActivitySamplingResult.AllDataAndRecorded, Sample(default));
        Assert.Equal(ActivitySamplingResult.AllDataAndRecorded, Sample(new(ParentTrace, ParentSpan, ActivityTraceFlags.Recorded)));
        Assert.Equal(
            ActivitySamplingResult.AllDataAndRecorded,
            Sample(new(ParentTrace, ParentSpan, ActivityTraceFlags.Recorded, isRemote: true)));
        Assert.Equal(ActivitySamplingResult.None, Sample(new(ParentTrace, ParentSpan, ActivityTraceFlags.None)));
        Assert.Equal(
            ActivitySamplingResult.PropagationData,
            Sample(new(ParentTrace, ParentSpan, ActivityTraceFlags.None, isRemote: true)));

        static ActivitySamplingResult Sample(ActivityContext parent)
        {
            using var source = new ActivitySource("Scribe.Parity.Sample");
            var options = CreationOptions(source, parent);
            return TraceLogBridge.Sample(ref options);
        }
    }

    [Theory]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT")]
    [InlineData("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT")]
    [InlineData("OTEL_TRACES_SAMPLER")]
    [InlineData("OTEL_TRACES_SAMPLER_ARG")]
    [InlineData("OTEL_SDK_DISABLED")]
    [InlineData("otel_service_name")]
    public void Any_OpenTelemetry_setting_keeps_the_SDK(string name) =>
        Assert.True(TraceLogBridge.OpenTelemetryConfigured(["PATH", name], [], _ => false));

    [Fact]
    public void A_self_diagnostics_file_in_either_folder_keeps_the_SDK_and_nothing_else_does()
    {
        var working = Path.Combine("C:", "work");
        var app = Path.Combine("C:", "app");

        Assert.False(TraceLogBridge.OpenTelemetryConfigured(["PATH", "TEMP", "DOTNET_ROOT", "OTEL"], [working, app], _ => false));
        Assert.True(TraceLogBridge.OpenTelemetryConfigured(
            [], [working, app], path => path == Path.Combine(working, "OTEL_DIAGNOSTICS.json")));
        Assert.True(TraceLogBridge.OpenTelemetryConfigured(
            [], [working, app], path => path == Path.Combine(app, "OTEL_DIAGNOSTICS.json")));
    }

    [Fact]
    public void A_bridge_that_cannot_write_never_throws_into_the_span()
    {
        using var source = new ActivitySource("Scribe.Parity." + Guid.NewGuid().ToString("N"));
        using var listener = TraceLogBridge.Listen(new ThrowingLogger(), source.Name);

        var activity = source.StartActivity("work");
        Assert.NotNull(activity);
        activity.Dispose();
    }

    [Fact]
    public void The_listener_path_loads_no_OpenTelemetry_assembly_and_the_app_keeps_the_SDK_apart()
    {
        Assert.DoesNotContain(
            typeof(TraceLogBridge).Assembly.GetReferencedAssemblies(),
            reference => reference.Name!.StartsWith("OpenTelemetry", StringComparison.OrdinalIgnoreCase));

        var listener = Source("src", "Scribe.App", "Infrastructure", "TraceLogListener.cs");
        var listenerCode = string.Join(
            '\n', listener.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Assert.DoesNotContain("OpenTelemetry", listenerCode, StringComparison.Ordinal);
        Assert.DoesNotContain("TracerProvider", listenerCode, StringComparison.Ordinal);
        Assert.Contains("_listener ??= TraceLogBridge.Listen(_log);", listener, StringComparison.Ordinal);
        Assert.Contains("loggerFactory.CreateLogger(TraceLogBridge.Category)", listener, StringComparison.Ordinal);
        Assert.Contains("public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;", listener, StringComparison.Ordinal);

        var registration = Source("src", "Scribe.App", "Infrastructure", "TelemetryRegistration.cs");
        var entry = Between(registration, "public static IServiceCollection AddScribeTelemetry(", "[MethodImpl(MethodImplOptions.NoInlining)]");
        Assert.Contains(
            "if (flags?.IsOn(PerfFlags.LightTraceBridge) == true && !OpenTelemetryConfigured(configuration))", entry, StringComparison.Ordinal);
        Assert.Contains("services.AddHostedService<TraceLogListener>();", entry, StringComparison.Ordinal);
        Assert.Contains("return AddOpenTelemetrySdk(services);", entry, StringComparison.Ordinal);
        foreach (var sdkOnly in new[] { "AddOpenTelemetry(", "WithTracing", "AddOtlpExporter", "LogTraceProcessor(", "ConfigureResource" })
        {
            Assert.DoesNotContain(sdkOnly, entry, StringComparison.Ordinal);
        }

        Assert.Contains(
            "[MethodImpl(MethodImplOptions.NoInlining)]\n    private static IServiceCollection AddOpenTelemetrySdk(IServiceCollection services)",
            registration,
            StringComparison.Ordinal);

        var processor = Source("src", "Scribe.App", "Infrastructure", "LogTraceProcessor.cs");
        Assert.Contains("public override void OnEnd(Activity activity) => TraceLogBridge.Write(_log, activity);", processor, StringComparison.Ordinal);
        Assert.Contains("loggerFactory.CreateLogger(TraceLogBridge.Category)", processor, StringComparison.Ordinal);

        var app = Source("src", "Scribe.App", "App.xaml.cs");
        Assert.Contains("builder.Services.AddScribeTelemetry(perfFlags, builder.Configuration);", app, StringComparison.Ordinal);
    }

    // ---- Scenarios --------------------------------------------------------------------------------------------------

    // Plays one scenario on the source and describes every activity it tried to start. Times are fixed so the lines, which
    // carry the duration, are the same on both arms.
    private static List<string> Play(string scenario, ActivitySource source)
    {
        var seen = new List<string>();
        switch (scenario)
        {
            case "root":
                seen.Add(Observe(() => source.StartActivity("work")));
                break;
            case "ambient chain":
            {
                var outer = source.StartActivity("outer");
                seen.Add(Describe(outer, null));
                seen.Add(Observe(() => source.StartActivity("inner"), outer));
                Stop(outer);
                break;
            }

            case "recorded local parent":
                seen.Add(Observe(() => source.StartActivity("work", ActivityKind.Internal, new ActivityContext(ParentTrace, ParentSpan, ActivityTraceFlags.Recorded))));
                break;
            case "unrecorded local parent":
                seen.Add(Observe(() => source.StartActivity("work", ActivityKind.Internal, new ActivityContext(ParentTrace, ParentSpan, ActivityTraceFlags.None))));
                break;
            case "recorded remote parent":
                seen.Add(Observe(() => source.StartActivity("work", ActivityKind.Server, new ActivityContext(ParentTrace, ParentSpan, ActivityTraceFlags.Recorded, isRemote: true))));
                break;
            case "unrecorded remote parent":
                seen.Add(Observe(() => source.StartActivity("work", ActivityKind.Server, new ActivityContext(ParentTrace, ParentSpan, ActivityTraceFlags.None, isRemote: true))));
                break;
            case "recorded W3C parent id":
                seen.Add(Observe(() => source.StartActivity("work", ActivityKind.Internal, $"00-{ParentTrace}-{ParentSpan}-01")));
                break;
            case "unrecorded W3C parent id":
                seen.Add(Observe(() => source.StartActivity("work", ActivityKind.Internal, $"00-{ParentTrace}-{ParentSpan}-00")));
                break;
            case "hierarchical parent id":
                seen.Add(Observe(() => source.StartActivity("work", ActivityKind.Internal, "|abc.1.")));
                break;
            case "trace state":
                seen.Add(Observe(() => source.StartActivity(
                    "work", ActivityKind.Internal, new ActivityContext(ParentTrace, ParentSpan, ActivityTraceFlags.Recorded, "vendor=value"))));
                break;
            case "error":
                seen.Add(Observe(() =>
                {
                    var activity = source.StartActivity("work");
                    activity?.SetStatus(ActivityStatusCode.Error, "partial");
                    return activity;
                }));
                break;
            case "unrecorded ambient parent":
            case "recorded ambient parent":
            {
                // An activity no source started (no listener sees it), as the ambient parent.
                var outer = new Activity("outer");
                outer.SetIdFormat(ActivityIdFormat.W3C);
                outer.Start();
                outer.ActivityTraceFlags = scenario == "recorded ambient parent" ? ActivityTraceFlags.Recorded : ActivityTraceFlags.None;
                seen.Add(Observe(() => source.StartActivity("work"), outer));
                outer.Stop();
                break;
            }

            case "tags":
                seen.Add(Observe(() =>
                {
                    var activity = source.StartActivity(ScribeTelemetry.DictationActivity);
                    activity?.SetTag(ScribeTelemetry.TagOutcome, "injected");
                    activity?.SetTag(ScribeTelemetry.TagCaptureSeconds, 9.5);
                    activity?.SetTag("not.allowlisted", "free text a user said");
                    return activity;
                }));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        return seen;
    }

    private static string Observe(Func<Activity?> start, Activity? expectedParent = null)
    {
        var before = Activity.Current;
        var activity = start();
        var during = Activity.Current;
        var description = Describe(activity, expectedParent) +
            $" currentBefore={Name(before)} currentDuring={(during == activity ? "it" : Name(during))}";
        Stop(activity);
        return description + $" currentAfter={(Activity.Current == before ? "before" : Name(Activity.Current))}";
    }

    private static string Describe(Activity? activity, Activity? expectedParent)
    {
        if (activity is null)
        {
            return "created=False";
        }

        var builder = new StringBuilder("created=True");
        builder.Append(" allData=").Append(activity.IsAllDataRequested);
        builder.Append(" recorded=").Append(activity.Recorded);
        builder.Append(" format=").Append(activity.IdFormat);
        builder.Append(" remoteParent=").Append(activity.HasRemoteParent);
        builder.Append(" state=").Append(activity.TraceStateString ?? "null");
        if (expectedParent is not null)
        {
            builder.Append(" parentIsExpected=").Append(activity.Parent == expectedParent);
            builder.Append(" traceIsParents=").Append(activity.TraceId == expectedParent.TraceId);
        }
        else
        {
            builder.Append(" trace=").Append(activity.TraceId == ParentTrace ? "given" : activity.TraceId == default ? "none" : "new");
            builder.Append(" parentSpan=").Append(activity.ParentSpanId == ParentSpan ? "given" : activity.ParentSpanId == default ? "none" : "other");
            builder.Append(" parentId=").Append(activity.ParentId ?? "null");
        }

        return builder.ToString();
    }

    private static void Stop(Activity? activity)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStartTime(Start);
        activity.SetEndTime(Start.AddMilliseconds(12.5));
        activity.Dispose();
    }

    private static string Name(Activity? activity) => activity?.OperationName ?? "null";

    // ---- The two arms -----------------------------------------------------------------------------------------------

    private static void AssertSame((List<string> Observations, string Log) sdk, (List<string> Observations, string Log) listener)
    {
        Assert.True(
            sdk.Observations.SequenceEqual(listener.Observations) && sdk.Log == listener.Log,
            "SDK:\n" + string.Join('\n', sdk.Observations) + "\n" + sdk.Log +
            "\nListener:\n" + string.Join('\n', listener.Observations) + "\n" + listener.Log);
    }

    private enum Arm
    {
        Sdk,
        Listener,
    }

    private static (List<string> Observations, string Log) Run(
        Arm arm, Func<ActivitySource, List<string>> scenario, bool secondListener = false, Func<ActivitySource, List<string>>? afterDisposal = null)
    {
        using var source = new ActivitySource("Scribe.Parity." + Guid.NewGuid().ToString("N"));
        var log = new CapturingLogger();
        using var other = secondListener ? AllDataListener(source.Name) : null;
        List<string> observations;
        IDisposable bridge = arm == Arm.Sdk
            ? Sdk.CreateTracerProviderBuilder().AddSource(source.Name).AddProcessor(new BridgeProcessor(log)).Build()!
            : TraceLogBridge.Listen(log, source.Name);
        using (bridge)
        {
            observations = scenario(source);
        }

        if (afterDisposal is not null)
        {
            observations.AddRange(afterDisposal(source));
        }

        return (observations, log.Text);
    }

    // LogTraceProcessor's shape (it lives in the app): the SDK's processor that hands each ended span to the bridge.
    private sealed class BridgeProcessor(Microsoft.Extensions.Logging.ILogger log) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity) => TraceLogBridge.Write(log, activity);
    }

    private static ActivityListener AllDataListener(string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static ActivityCreationOptions<ActivityContext> CreationOptions(ActivitySource source, ActivityContext parent)
    {
        // The runtime builds these for a listener; a test builds one the same way through a listener that captures it.
        ActivityCreationOptions<ActivityContext> captured = default;
        using var capture = new ActivityListener
        {
            ShouldListenTo = candidate => candidate == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                captured = options;
                return ActivitySamplingResult.None;
            },
        };
        ActivitySource.AddActivityListener(capture);
        _ = source.StartActivity("sample", ActivityKind.Internal, parent);
        return captured;
    }

    private static bool EnvironmentConfiguresOpenTelemetry()
    {
        var names = new List<string>();
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name)
            {
                names.Add(name);
            }
        }

        return TraceLogBridge.OpenTelemetryConfigured(names, [Environment.CurrentDirectory, AppContext.BaseDirectory], File.Exists);
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, "Missing: " + start);
        return text[from..to];
    }

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts])).ReplaceLineEndings("\n");

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    private sealed class ThrowingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("the log is gone");
    }
}
