using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Scribe.Core.Cleanup;
using Scribe.Core.Tests.CleanupLogging;
using Scribe.Core.Infrastructure;
using ManualTimeProvider = Scribe.Core.Tests.Concurrency.ManualTimeProvider;

namespace Scribe.Core.Tests;

/// <summary>Builds a <see cref="TextCleanupService"/> wired entirely to fakes and temp directories.</summary>
internal sealed class CleanupHarness : IAsyncDisposable
{
    public const string FoundryAlias = "qwen3-1.7b";
    public const string FoundryVariant = "qwen3-1.7b-generic-cpu:2";
    public const string OtherAlias = "phi-4";
    public const string OtherVariant = "phi-4-generic-cpu:1";
    public const string ThirdAlias = "mistral-nemo-12b-instruct";
    public const string ThirdVariant = "mistral-nemo-12b-instruct-generic-cpu:1";

    public CleanupHarness(bool armStorage = false, HttpMessageHandler? http = null, FakeFoundryState? state = null)
    {
        Temp = new TempDirectory();
        State = state ?? new FakeFoundryState();
        Qwen = FakeFoundryModel.Family(State, FoundryAlias, FoundryVariant);
        Phi = FakeFoundryModel.Family(State, OtherAlias, OtherVariant);
        Mistral = FakeFoundryModel.Family(State, ThirdAlias, ThirdVariant);
        Catalog = new FakeFoundryCatalog(State, [Qwen, Phi, Mistral]);
        Runtime = new FakeFoundryRuntime(State, Catalog);
        Host = new FakeFoundryHost(() => Runtime);
        Http = http ?? new ScriptedHttpHandler((_, _) => Task.FromResult(ScriptedHttpHandler.ChatCompletion("ok")));
        Paths = new AppPaths(Temp.Combine("data"));

        Storage = armStorage ? FoundryLocalStorage.For(Paths) : null;

        Service = new TextCleanupService(Log, Paths, Host, Storage)
        {
            OpenAIClientOptionsOverride = ScriptedHttpHandler.Install(Http),
        };
    }

    public TempDirectory Temp { get; }

    /// <summary>An isolated profile, the way tests and <c>SCRIBE_DATA_DIR</c> run.</summary>
    public AppPaths Paths { get; }

    /// <summary>The Foundry Local directory this profile resolves to, armed or not.</summary>
    public string FoundryDir => FoundryLocalStorage.ResolveAppDataDir(Paths)!;

    public string InFoundryDir(string relative) => Path.Combine(FoundryDir, relative);

    public FakeFoundryState State { get; }

    public FakeFoundryModel Qwen { get; }

    public FakeFoundryModel Phi { get; }

    public FakeFoundryModel Mistral { get; }

    public FakeFoundryCatalog Catalog { get; }

    public FakeFoundryRuntime Runtime { get; }

    public FakeFoundryHost Host { get; }

    public HttpMessageHandler Http { get; }

    public FoundryLocalStorage? Storage { get; }

    public CapturingLogger<TextCleanupService> Log { get; } = new();

    public TextCleanupService Service { get; }

    /// <summary>
    /// Runs the disposal drain on a clock only the test moves, keeping production's timeout. A test that expects the drain to
    /// end because the work in flight stopped then waits for that however long a loaded machine takes to unwind it, instead
    /// of racing the real timeout; the work still has to stop within the test's own bound. Nothing ever times that drain
    /// out, so a test that uses this opens every gate it shut in a finally, before this harness disposes the service: work
    /// left parked behind a gate after a failed wait or assertion would otherwise hold the teardown for ever.
    /// </summary>
    public ManualTimeProvider DrainOnManualClock()
    {
        var clock = new ManualTimeProvider();
        Service.DisposalDrainClock = clock;
        return clock;
    }

    public static CleanupOptions FoundryOn(string alias = FoundryAlias) =>
        new(true, CleanupProvider.FoundryLocal, alias, null, null);

    public static CleanupOptions OtherProviderOff { get; } =
        CleanupOptions.Disabled with { Provider = CleanupProvider.AzureFoundry };

    public static CleanupOptions Custom(string endpoint, string model = "llama3") =>
        CleanupOptions.Disabled with
        {
            Enabled = true,
            Provider = CleanupProvider.OpenAiCompatible,
            CustomEndpoint = endpoint,
            CustomModel = model,
        };

    public Task WaitForStatusAsync(CleanupStatus status) => WaitForStatusAsync(s => s == status);

    public async Task WaitForStatusAsync(Func<CleanupStatus, bool> predicate)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (predicate(Service.Status))
            {
                reached.TrySetResult();
            }
        }

        Service.StatusChanged += Check;
        try
        {
            Check();
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            Service.StatusChanged -= Check;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        Temp.Dispose();
    }
}

/// <summary>
/// R02: a custom endpoint's host name reaches the settings window, where it helps, and never the
/// log, an Activity tag or the overlay, which is everything the dictation pipeline sends a
/// <see cref="CleanupResult"/>'s reasons to.
/// </summary>
public sealed class CleanupDiagnosticsPrivacyTests
{
    private const string Host = "scribe-leak-canary-7f3a.example.invalid";
    private const string HostLabel = "scribe-leak-canary-7f3a";
    private static readonly string Endpoint = $"https://{Host}/v1";

    private static void AssertNoHost(string? text, string what)
    {
        Assert.False(
            text is not null && text.Contains(HostLabel, StringComparison.OrdinalIgnoreCase),
            $"{what} leaked the endpoint host: {text}");
    }

    private static HttpResponseMessage ServerErrorQuotingTheHost(HttpStatusCode status) => ScriptedHttpHandler.Json(
        status,
        "{\"error\":{\"message\":\"upstream https://" + Host + "/v1/chat/completions failed\"," +
        "\"type\":\"server_error\",\"code\":\"upstream_failure\"}}");

    [Fact]
    public async Task While_connecting_the_skip_reason_is_safe_and_the_settings_text_names_the_host()
    {
        var requestArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new ScriptedHttpHandler(async (_, ct) =>
        {
            requestArrived.TrySetResult();
            await release.Task.WaitAsync(ct);
            return ServerErrorQuotingTheHost(HttpStatusCode.InternalServerError);
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;

        svc.Configure(CleanupHarness.Custom(Endpoint));
        await requestArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(CleanupStatus.Initializing, svc.Status);
        Assert.Contains(Host, svc.StatusDetail);
        AssertNoHost(svc.StatusReason, "StatusReason");

        var skipped = await svc.CleanAsync("please book the demo room");
        Assert.Equal(CleanupOutcome.Skipped, skipped.Outcome);
        Assert.True(skipped.SkippedUnexpectedly);
        AssertNoHost(skipped.SkipReason, "SkipReason");
        Assert.Contains(Host, skipped.DisplayDetail);

        release.TrySetResult();
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);

        var failed = await svc.CleanAsync("please book the demo room");
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Equal("please book the demo room", failed.Text);
        AssertNoHost(failed.FailureReason, "FailureReason");
        Assert.Contains("500", failed.FailureReason);
        Assert.Contains(Host, failed.DisplayDetail);
        Assert.Contains(Host, svc.StatusDetail);

        AssertNoHost(harness.Log.AllText, "The log");
    }

    [Fact]
    public async Task A_transport_failure_that_embeds_the_host_is_logged_by_its_shape()
    {
        // HttpRequestException appends "(host:port)" to its message, and the file sink writes an
        // exception's ToString(). The log gets the enum-valued shape, which is the actual diagnosis.
        var http = new ScriptedHttpHandler((_, _) => throw new HttpRequestException(
            HttpRequestError.NameResolutionError,
            $"No such host is known. ({Host}:443)",
            new SocketException((int)SocketError.HostNotFound)));
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;

        svc.Configure(CleanupHarness.Custom(Endpoint));
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);
        var failed = await svc.CleanAsync("hello there");

        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        AssertNoHost(failed.FailureReason, "FailureReason");
        AssertNoHost(harness.Log.AllText, "The log");
        Assert.Contains("NameResolutionError", harness.Log.AllText);
        Assert.Contains("HostNotFound", harness.Log.AllText);
        Assert.All(harness.Log.Entries, entry => Assert.Null(entry.Exception));
    }

    [Fact]
    public async Task A_dictation_failure_after_ready_keeps_the_host_out_of_the_reasons_and_the_log()
    {
        var phase = 0;
        var http = new ScriptedHttpHandler((_, _) => Task.FromResult(Volatile.Read(ref phase) == 0
            ? ScriptedHttpHandler.ChatCompletion("ok")
            : ServerErrorQuotingTheHost(HttpStatusCode.NotFound)));
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;

        svc.Configure(CleanupHarness.Custom(Endpoint));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Contains(Host, svc.StatusDetail);
        AssertNoHost(svc.StatusReason, "StatusReason");
        Assert.Contains("llama3", svc.StatusReason, StringComparison.Ordinal);

        Volatile.Write(ref phase, 1);
        var failed = await svc.CleanAsync("this dictation will fail");
        var completion = await svc.CompleteAsync("system", "user", svc.Recipient!);

        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Equal("this dictation will fail", failed.Text);
        AssertNoHost(failed.FailureReason, "FailureReason");
        Assert.Contains("404", failed.FailureReason);
        Assert.Contains(Host, failed.DisplayDetail);
        Assert.Equal(CompletionOutcome.Failed, completion.Outcome);
        Assert.Null(completion.Text);
        AssertNoHost(harness.Log.AllText, "The log");
        Assert.Contains("status=404", harness.Log.AllText);
        Assert.Contains("code=upstream_failure", harness.Log.AllText);
    }

    // A second distinctive endpoint, host and port both, for the per-segment failure path.
    private const string SegmentHost = "scribe-segment-canary-9c1d.example.invalid";
    private const string SegmentPort = "48213";

    // Every entry, at every level: the rendered message, every structured value, and the logged
    // exception. The last is asserted absent altogether, because ToString() is where an
    // HttpRequestException's "(host:port)" would otherwise reach the file.
    private static void AssertNothingLeaks(CapturingLogger<TextCleanupService> log, params string[] canaries)
    {
        Assert.NotEmpty(log.Entries);
        foreach (var entry in log.Entries)
        {
            Assert.Null(entry.Exception);
            foreach (var canary in canaries)
            {
                Assert.DoesNotContain(canary, entry.Message, StringComparison.OrdinalIgnoreCase);
                Assert.All(entry.State, value => Assert.DoesNotContain(canary, value, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public static TheoryData<string> SegmentFailures => new() { "transport", "timeout", "server-body" };

    [Theory]
    [MemberData(nameof(SegmentFailures))]
    public async Task A_failed_segment_logs_its_shape_and_never_its_endpoint_at_any_level(string failure)
    {
        var endpoint = $"https://{SegmentHost}:{SegmentPort}/v1";
        var dictating = 0;
        var http = new ScriptedHttpHandler((_, _) => Volatile.Read(ref dictating) == 0
            ? Task.FromResult(ScriptedHttpHandler.ChatCompletion("ok"))
            : failure switch
            {
                "transport" => throw new HttpRequestException(
                    HttpRequestError.ConnectionError,
                    $"No connection could be made because the target machine actively refused it. ({SegmentHost}:{SegmentPort})",
                    new SocketException((int)SocketError.ConnectionRefused)),
                "timeout" => throw new TimeoutException($"No answer from {SegmentHost}:{SegmentPort} in time."),
                _ => Task.FromResult(ScriptedHttpHandler.Json(
                    HttpStatusCode.BadGateway,
                    "{\"error\":{\"message\":\"upstream " + endpoint + " refused\",\"type\":\"server_error\"}}")),
            });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.Custom(endpoint));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Volatile.Write(ref dictating, 1);
        var result = await svc.CleanAsync("this segment will fail");

        Assert.Equal(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal("this segment will fail", result.Text);

        // The per-segment failure is logged, at Debug, and it is the shape that says what happened.
        var segment = Assert.Single(harness.Log.Entries, e => e.Message.Contains("for a segment"));
        Assert.Equal(LogLevel.Debug, segment.Level);
        Assert.Contains("OpenAiCompatible", segment.Message);
        Assert.Contains(
            failure switch
            {
                "transport" => "SocketException(ConnectionRefused)",
                "timeout" => "kind=timeout",
                _ => "status=502",
            },
            segment.Message);

        AssertNothingLeaks(harness.Log, SegmentHost, SegmentPort);
        AssertNoHost(result.FailureReason, "FailureReason");
    }

    [Fact]
    public async Task No_foundry_local_failure_path_logs_its_endpoint_or_a_download_host()
    {
        // Scribe's own lines about Foundry Local failures follow the one rule for every provider:
        // shape only. That is a consistency rule, not a claim that these are secrets: the loopback
        // port and Microsoft's download hosts are not user data, and the SDK's own log lines keep
        // them (FoundrySdkLogger folds only the user profile; see the test below). The host here is
        // a canary standing in for any endpoint a provider failure might quote.
        const string LoopbackPort = "59317";
        var dictating = 0;
        var http = new ScriptedHttpHandler((_, _) => Volatile.Read(ref dictating) == 0
            ? Task.FromResult(ScriptedHttpHandler.ChatCompletion("ok"))
            : throw new HttpRequestException(
                HttpRequestError.ConnectionError,
                $"An error occurred while sending the request. (127.0.0.1:{LoopbackPort})",
                new SocketException((int)SocketError.ConnectionReset)));
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        harness.Runtime.Url = $"http://127.0.0.1:{LoopbackPort}";

        // An execution-provider download that fails naming where it downloads from: listing still works.
        harness.Runtime.EpFailure = new HttpRequestException(
            HttpRequestError.NameResolutionError,
            $"No such host is known. ({Host}:443)",
            new SocketException((int)SocketError.HostNotFound));
        Assert.Equal(3, (await svc.ListFoundryModelsAsync()).Count);

        // A model download that fails the same way.
        harness.Phi.LoadFailure = new HttpRequestException($"Download from https://{Host}/models failed ({Host}:443).");
        Assert.False(await svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias));

        // Cleanup that becomes ready, then a segment whose call to the loopback service fails.
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Volatile.Write(ref dictating, 1);
        var result = await svc.CleanAsync("a local segment that fails");

        Assert.Equal(CleanupOutcome.Failed, result.Outcome);
        var segment = Assert.Single(harness.Log.Entries, e => e.Message.Contains("for a segment"));
        Assert.Equal(LogLevel.Debug, segment.Level);
        Assert.Contains("FoundryLocal", segment.Message);
        Assert.Contains("SocketException(ConnectionReset)", segment.Message);
        Assert.Contains(harness.Log.Entries, e =>
            e.Message.Contains("execution-provider setup was skipped") && e.Message.Contains("NameResolutionError"));
        Assert.Contains(harness.Log.Entries, e => e.Message.Contains("Loading Foundry Local model"));

        AssertNothingLeaks(harness.Log, HostLabel, LoopbackPort);
    }

    [Fact]
    public void A_foundry_local_failure_keeps_its_execution_provider_diagnosis_as_identifiers()
    {
        var ex = new InvalidOperationException(
            "Cannot load model 'qwen3-1.7b-cuda-gpu:2': it requires the 'CUDAExecutionProvider' execution provider, " +
            @"which is not available. Available EPs: [CPUExecutionProvider, DmlExecutionProvider, C:\Users\someone\evil]. " +
            $"Tried {Host}.");

        var shape = TextCleanupService.DescribeFailureShape(ex);

        Assert.Contains("kind=execution-provider-unavailable", shape);
        Assert.Contains("requires=CUDAExecutionProvider", shape);
        Assert.Contains("available=CPUExecutionProvider,DmlExecutionProvider", shape);
        Assert.DoesNotContain("someone", shape);
        AssertNoHost(shape, "Shape");
    }

    [Fact]
    public void No_cleanup_log_call_passes_an_exception_or_its_text()
    {
        // The guard for everything above: no log call in Cleanup takes an exception object, reads an
        // exception's text (.Message, .StackTrace, inner exceptions), renders an object with
        // ToString(), or interpolates an exception. Failures go in as CleanupFailureShape or
        // DescribeFailureShape text; the raw ILogger.Log overload (a forwarding logger) passes null.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var offenders = new List<string>();
        var calls = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "Scribe.Core", "Cleanup"), "*.cs"))
        {
            var source = File.ReadAllText(file);
            calls += LogCallScanner.Find(source).Count();
            foreach (var offence in LogCallScanner.Check(source))
            {
                offenders.Add($"{Path.GetFileName(file)}: {offence.Reason}: {offence.Call}");
            }
        }

        Assert.True(calls > 50, $"The scanner found only {calls} log calls, so it is not reading the source.");
        Assert.Empty(offenders);
    }

    // Verbatim from the baseline this program started from, one per way exception text reached the
    // log there, plus the shapes a future change could reach for.
    [Theory]
    [InlineData("_log.LogWarning(\n                \"Azure sign-in probe: the service principal was rejected. {Message}\",\n                ex.Message);")] // AzureFoundryDiscovery.cs:285
    [InlineData("_log.LogWarning(ex, \"Resource Graph account discovery failed; falling back to per-subscription enumeration.\");")] // AzureFoundryDiscovery.cs:248
    [InlineData("_log.LogWarning(\"AI cleanup initialization probe failed ({Provider}): {Message}\", options.Provider, probeFailure.Message);")] // TextCleanupService.cs:1814
    [InlineData("_log.LogDebug(\n                    \"Chat Completions fallback also failed for {Deployment}: {Message}\",\n                    options.AzureDeployment,\n                    stillFailing.Message);")] // TextCleanupService.cs:1931
    [InlineData("log?.LogDebug(ex, \"Could not clear the Foundry Local demotion markers.\");")] // FoundryDemotionReset.cs:75
    [InlineData("_log.LogDebug(\"Failed: {Error}\", ex.ToString());")]
    [InlineData("_log.LogDebug(\"Failed: {Error}\", exception);")]
    [InlineData("_log.LogDebug(\"Failed: {Error}\", result.Exception);")]
    [InlineData("_log.LogDebug($\"Failed: {ex}\");")]
    [InlineData("_log.LogDebug($\"Failed: {ex,20}\");")]
    [InlineData("_log.LogDebug($\"Failed: {ex.Source} {ex.GetType().Name}\");")]
    [InlineData("_log.LogDebug(\"Inner: {Inner}\", ex.InnerException?.Message);")]
    [InlineData("_log.Log(level, exception, \"Failed.\");")]
    [InlineData("_inner.Log(level, eventId, state, exception, formatter);")]
    public void The_guard_catches_every_shape_that_put_exception_text_in_the_log(string call)
    {
        var source = "class C { void M(object options, object probeFailure, object stillFailing, object result) " +
            "{ try { } catch (System.Exception ex) { var exception = ex; " + call + " } } }";

        Assert.NotEmpty(LogCallScanner.Check(source));
    }

    [Theory]
    [InlineData("_log.LogInformation(\"Listing failed ({Failure}).\", DescribeFailureShape(ex));")]
    [InlineData("_log.Log(level, \"{Message} ({Failure})\", message, CleanupFailureShape.Describe(exception));")]
    [InlineData("_log.Log(level, \"{Message} ({Provider}; {Failure})\", message, provider, DescribeFailureShape(exception));")]
    [InlineData("_inner.Log(logLevel, eventId, message, null, static (text, _) => text);")]
    [InlineData("_log.LogInformation(\"Text may say ex.Message or ToString() (with, commas) {X}\", Foo(1, (2), \"a,b)\"));")]
    [InlineData("_log.LogDebug(\n    \"Fell back: {Failure}\",\n    stillFailing.Exception is { } fallbackEx\n        ? DescribeFailureShape(fallbackEx)\n        : stillFailing.Reason.Diagnostic);")]
    [InlineData("_log.LogWarning(\n    \"Multi-line {A} {B}\",\n    a, // a comment, with a comma and ex.Message\n    b);")]
    [InlineData("_log.LogDebug($\"Shape: {DescribeFailureShape(ex)}\");")]
    [InlineData("_log.LogDebug($\"Failed with {ex.GetType().Name} (0x{ex.HResult:X8}).\");")]
    [InlineData("_log.LogInformation(\"Chars '(' and '\\\"' are not code {X}\", ',');")]
    public void The_guard_allows_what_cleanup_actually_logs(string call)
    {
        var source = "class C { void M(object a, object b, object message, object stillFailing) " +
            "{ try { } catch (System.Exception ex) { var exception = ex; " + call + " } } }";

        Assert.Empty(LogCallScanner.Check(source));
    }
    [Fact]
    public void The_two_forms_of_a_failure_differ_only_by_the_endpoints_own_text()
    {
        var ex = new HttpRequestException(
            HttpRequestError.ConnectionError, $"Connection refused ({Host}:443)", new SocketException(10061));

        var reason = TextCleanupService.DescribeFailureReason(ex, CleanupProvider.OpenAiCompatible);
        var shape = TextCleanupService.DescribeFailureShape(ex);

        AssertNoHost(reason.Diagnostic, "Diagnostic");
        AssertNoHost(shape, "Shape");
        Assert.Contains("HttpRequestException(ConnectionError)", shape);
        Assert.Contains("SocketException(ConnectionRefused)", shape);
        Assert.Contains("kind=connectivity", shape);
    }

    [Theory]
    [InlineData("invalid_api_key", "invalid_api_key")]
    [InlineData("DeploymentNotFound", "DeploymentNotFound")]
    [InlineData("https://evil.example.com/x", null)]
    [InlineData("host.example.com", null)]
    [InlineData("a sentence with spaces", null)]
    [InlineData("", null)]
    public void Only_identifier_shaped_service_codes_reach_the_log(string code, string? expected)
    {
        Assert.Equal(expected, CleanupFailureShape.SanitizeCode(code));
    }

    [Fact]
    public void A_deeply_nested_failure_costs_a_fixed_amount_of_work()
    {
        // The status search used to recurse into each aggregate's list and then carry on down the
        // same chain, which is exponential in its depth bound. Ten thousand levels would never finish.
        Exception current = new InvalidOperationException("leaf");
        for (var i = 0; i < 10_000; i++)
        {
            current = new AggregateException(current, new InvalidOperationException("sibling"));
        }

        Assert.Equal(0, TextCleanupService.ExtractHttpStatus(current));
        Assert.StartsWith("AggregateException", CleanupFailureShape.Describe(current));
        Assert.StartsWith("AggregateException", TextCleanupService.DescribeFailureShape(current));
        Assert.True(CleanupFailureShape.Walk(current).Count() <= 32);
    }

    [Fact]
    public void A_status_behind_an_aggregate_sibling_is_still_found()
    {
        var wrapped = new InvalidOperationException(
            "agent run failed",
            new AggregateException(new TimeoutException("first"), new System.ClientModel.ClientResultException("second")));

        Assert.Equal("InvalidOperationException inner=AggregateException>TimeoutException>ClientResultException",
            CleanupFailureShape.Describe(wrapped));
    }
}
