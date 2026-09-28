using System.Net;
using System.Text.Json;
using Scribe.Core.Cleanup;
using Scribe.Core.Tests.CleanupLogging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// <c>AppSettings.AiCleanupPromptCaching</c> through the production service, transport and hand-off, over the canary
/// network: with caching off every Microsoft Foundry request of the configuration carries the prompt cache options (the
/// probe, dictations, one-off completions and Test connection), a deployment that refuses them leaves cleanup unavailable
/// with that cause and Scribe types what it heard, and no request is ever sent without the option to get past a refusal.
/// Another AI service never gets the field. Nothing leaves the process.
/// </summary>
public sealed class PromptCacheServiceTests
{
    private const string Transcript = "the canary transcript asks for no cache at all";

    private static CleanupOptions AzureCachingOff() => VocabularyCleanupHarness.Azure() with { PromptCaching = false };

    [Fact]
    public async Task Caching_off_puts_the_cache_options_on_the_probe_the_dictation_and_a_one_off_completion()
    {
        await using var harness = new VocabularyCleanupHarness(source: null);
        await harness.ConfigureAndWaitAsync(AzureCachingOff());

        var result = await harness.Service.CleanAsync(Transcript, CancellationToken.None);
        var completion = await harness.Service.CompleteAsync("system", "user", harness.Service.Recipient!);

        Assert.NotEqual(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal(CompletionOutcome.Completed, completion.Outcome);
        var sent = harness.Network.Sent;
        Assert.Contains(sent, request => request.IsProbe);
        Assert.Contains(sent, request => request.TranscriptOf() == Transcript);
        Assert.True(sent.Count >= 3);
        Assert.All(sent, request => AssertCarriesCacheOptions(request, expected: true));
    }

    [Fact]
    public async Task Caching_on_is_today_s_request()
    {
        await using var harness = new VocabularyCleanupHarness(source: null);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());

        await harness.Service.CleanAsync(Transcript, CancellationToken.None);

        Assert.NotEmpty(harness.Network.Sent);
        Assert.All(harness.Network.Sent, request => AssertCarriesCacheOptions(request, expected: false));
    }

    [Fact]
    public async Task Another_ai_service_never_gets_the_cache_options()
    {
        await using var harness = new VocabularyCleanupHarness(source: null);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom() with { PromptCaching = false });

        await harness.Service.CleanAsync(Transcript, CancellationToken.None);

        Assert.NotEmpty(harness.Network.Sent);
        Assert.All(harness.Network.Sent, request => AssertCarriesCacheOptions(request, expected: false));
    }

    [Fact]
    public async Task A_deployment_that_refuses_the_option_leaves_cleanup_unavailable_with_that_cause_and_types_what_it_heard()
    {
        var network = new CanaryNetwork { Respond = (request, _) => Task.FromResult(RefuseTheOption(request)) };
        await using var harness = new VocabularyCleanupHarness(source: null, network);

        harness.Service.Configure(AzureCachingOff());
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);

        Assert.Contains("can't turn caching off", harness.Service.StatusDetail, StringComparison.Ordinal);
        Assert.Contains(PromptCachePolicy.SettingName, harness.Service.StatusDetail, StringComparison.Ordinal);

        // Refused for the option itself, so the other surface was not tried: that is the model, whichever surface asks.
        var probes = network.Sent;
        Assert.Single(probes);
        Assert.EndsWith("/responses", probes[0].Path, StringComparison.Ordinal);

        var result = await harness.Service.CleanAsync(Transcript, CancellationToken.None);
        Assert.Equal(Transcript, result.Text);
        Assert.NotEqual(CleanupOutcome.Cleaned, result.Outcome);

        // Nothing was ever sent without the option to get past the refusal.
        Assert.All(network.Sent, request => AssertCarriesCacheOptions(request, expected: true));
        Assert.DoesNotContain(network.Sent, request => request.Carries(Transcript));
    }

    [Fact]
    public async Task A_400_that_names_nothing_with_caching_off_names_the_cache_as_a_possible_cause_on_both_surfaces()
    {
        var network = new CanaryNetwork
        {
            Respond = (request, _) => Task.FromResult(ScriptedHttpHandler.Json(HttpStatusCode.BadRequest, GenericRefusal)),
        };
        await using var harness = new VocabularyCleanupHarness(source: null, network);

        harness.Service.Configure(AzureCachingOff());
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);

        Assert.Contains("this can mean this deployment can't turn caching off", harness.Service.StatusDetail, StringComparison.Ordinal);

        // A 400 that names nothing is also the surface refusal, so Chat Completions was tried, with the option too.
        Assert.Contains(network.Sent, request => request.Path.EndsWith("/chat/completions", StringComparison.Ordinal));
        Assert.All(network.Sent, request => AssertCarriesCacheOptions(request, expected: true));
    }

    [Fact]
    public async Task A_chat_only_deployment_that_refuses_the_option_is_reported_by_that_refusal()
    {
        var network = new CanaryNetwork
        {
            Respond = (request, _) => Task.FromResult(request.Path.EndsWith("/responses", StringComparison.Ordinal)
                ? ScriptedHttpHandler.Json(HttpStatusCode.BadRequest, GenericRefusal)
                : RefuseTheOption(request)),
        };
        await using var harness = new VocabularyCleanupHarness(source: null, network);

        harness.Service.Configure(AzureCachingOff());
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);

        Assert.Contains("can't turn caching off", harness.Service.StatusDetail, StringComparison.Ordinal);
        Assert.All(network.Sent, request => AssertCarriesCacheOptions(request, expected: true));
    }

    [Fact]
    public async Task A_chat_only_deployment_that_honors_the_option_serves_through_chat_completions_with_it()
    {
        var network = new CanaryNetwork
        {
            Respond = (request, _) => Task.FromResult(request.Path.EndsWith("/responses", StringComparison.Ordinal)
                ? ScriptedHttpHandler.Json(HttpStatusCode.BadRequest, GenericRefusal)
                : CanaryNetwork.Echo(request)),
        };
        await using var harness = new VocabularyCleanupHarness(source: null, network);
        await harness.ConfigureAndWaitAsync(AzureCachingOff());

        await harness.Service.CleanAsync(Transcript, CancellationToken.None);

        Assert.Contains(network.Sent, request =>
            request.TranscriptOf() == Transcript && request.Path.EndsWith("/chat/completions", StringComparison.Ordinal));
        Assert.All(network.Sent, request => AssertCarriesCacheOptions(request, expected: true));
    }

    [Fact]
    public async Task A_dictation_refused_after_ready_types_what_it_heard_and_is_not_sent_again_without_the_option()
    {
        var network = new CanaryNetwork();
        await using var harness = new VocabularyCleanupHarness(source: null, network);
        await harness.ConfigureAndWaitAsync(AzureCachingOff());
        network.Respond = (request, _) => Task.FromResult(request.IsProbe ? CanaryNetwork.Echo(request) : RefuseTheOption(request));

        var result = await harness.Service.CleanAsync(Transcript, CancellationToken.None);

        Assert.Equal(Transcript, result.Text);
        Assert.Equal(CleanupOutcome.Failed, result.Outcome);
        Assert.Contains("can't turn caching off", result.FailureReason, StringComparison.Ordinal);
        Assert.All(network.Sent, request => AssertCarriesCacheOptions(request, expected: true));
    }

    [Fact]
    public async Task Test_connection_carries_the_option_and_names_the_refusal()
    {
        var network = new CanaryNetwork { Respond = (request, _) => Task.FromResult(RefuseTheOption(request)) };
        await using var harness = new VocabularyCleanupHarness(source: null, network);

        var test = await harness.Service.TestAsync(AzureCachingOff());

        Assert.Equal(CleanupTestOutcome.Failed, test.Outcome);
        Assert.Contains("can't turn caching off", test.SafeReason, StringComparison.Ordinal);
        Assert.NotEmpty(network.Sent);
        Assert.All(network.Sent, request => AssertCarriesCacheOptions(request, expected: true));
    }

    [Fact]
    public async Task Turning_caching_off_reconnects_probes_with_the_option_and_changes_the_recipient()
    {
        await using var harness = new VocabularyCleanupHarness(source: null);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());
        var cachingOn = harness.Service.Recipient!;
        var before = harness.Network.Sent.Count;

        await ConfigureAndWaitUntilServingAsync(harness, AzureCachingOff());

        var probe = Assert.Single(harness.Network.Sent.Skip(before));
        Assert.True(probe.IsProbe);
        AssertCarriesCacheOptions(probe, expected: true);
        Assert.False(cachingOn.Matches(AzureCachingOff()));
        Assert.Equal(CompletionOutcome.RecipientChanged, (await harness.Service.CompleteAsync("system", "user", cachingOn)).Outcome);
    }

    [Fact]
    public async Task A_cache_read_reported_with_caching_off_logs_a_warning_with_the_count_only()
    {
        var network = new CanaryNetwork
        {
            Respond = (request, _) => Task.FromResult(request.IsProbe ? CanaryNetwork.Echo(request) : ResponsesWithCacheRead(request)),
        };
        await using var harness = new VocabularyCleanupHarness(source: null, network);
        await harness.ConfigureAndWaitAsync(AzureCachingOff());

        await harness.Service.CleanAsync(Transcript, CancellationToken.None);

        var warning = Assert.Single(harness.Log.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("cached input tokens with prompt caching off", StringComparison.Ordinal));
        Assert.Contains("1408", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("canary", warning.AllText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_warning_with_caching_on_whatever_the_cache_reports()
    {
        var network = new CanaryNetwork
        {
            Respond = (request, _) => Task.FromResult(request.IsProbe ? CanaryNetwork.Echo(request) : ResponsesWithCacheRead(request)),
        };
        await using var harness = new VocabularyCleanupHarness(source: null, network);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());

        await harness.Service.CleanAsync(Transcript, CancellationToken.None);

        Assert.DoesNotContain(harness.Log.Entries, entry => entry.Message.Contains("with prompt caching off", StringComparison.Ordinal));
    }

    // Waits for Ready on the new configuration itself: the previous one was Ready too, so its status alone proves nothing.
    private static async Task ConfigureAndWaitUntilServingAsync(VocabularyCleanupHarness harness, CleanupOptions options)
    {
        var serving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (harness.Service.Status == CleanupStatus.Ready && harness.Service.Recipient?.Matches(options) == true)
            {
                serving.TrySetResult();
            }
        }

        harness.Service.StatusChanged += Check;
        try
        {
            harness.Service.Configure(options);
            Check();
            await serving.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            harness.Service.StatusChanged -= Check;
        }
    }

    private const string GenericRefusal =
        "{\"error\":{\"message\":\"The requested operation is unsupported.\",\"type\":\"invalid_request_error\"," +
        "\"param\":null,\"code\":\"OperationNotSupported\"}}";

    // What an earlier model or a provisioned deployment answers the option: https://learn.microsoft.com/azure/foundry/openai/
    // how-to/prompt-caching, "Models before the GPT-5.6 family don't support prompt_cache_options or prompt_cache_breakpoint.
    // Requests that include these parameters return a 400 error." (The error body's shape is Azure's usual one.)
    private static HttpResponseMessage RefuseTheOption(SentRequest request) =>
        request.Body.Contains(PromptCachePolicy.FieldName, StringComparison.Ordinal)
            ? ScriptedHttpHandler.Json(
                HttpStatusCode.BadRequest,
                "{\"error\":{\"message\":\"Unsupported parameter: 'prompt_cache_options' is not supported with this model.\"," +
                "\"type\":\"invalid_request_error\",\"param\":\"prompt_cache_options\",\"code\":\"unsupported_parameter\"}}")
            : CanaryNetwork.Echo(request);

    private static HttpResponseMessage ResponsesWithCacheRead(SentRequest request) => ScriptedHttpHandler.Json(
        HttpStatusCode.OK,
        "{\"id\":\"resp_cache\",\"object\":\"response\",\"created_at\":1700000000,\"status\":\"completed\"," +
        "\"model\":\"test\",\"output\":[{\"type\":\"message\",\"id\":\"msg_cache\",\"status\":\"completed\"," +
        "\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":" +
        JsonSerializer.Serialize(request.TranscriptOf() ?? "ok") + ",\"annotations\":[]}]}]," +
        "\"parallel_tool_calls\":false,\"tool_choice\":\"auto\",\"tools\":[]," +
        "\"usage\":{\"input_tokens\":1566,\"input_tokens_details\":{\"cached_tokens\":1408}," +
        "\"output_tokens\":12,\"output_tokens_details\":{\"reasoning_tokens\":0},\"total_tokens\":1578}}");

    private static void AssertCarriesCacheOptions(SentRequest request, bool expected)
    {
        using var document = JsonDocument.Parse(request.Body);
        var has = document.RootElement.TryGetProperty(PromptCachePolicy.FieldName, out var options);
        Assert.True(expected == has, $"{request.Path}: {(expected ? "missing" : "unexpected")} {PromptCachePolicy.FieldName}");
        Assert.DoesNotContain("prompt_cache_breakpoint", request.Body, StringComparison.Ordinal);
        if (has)
        {
            Assert.Equal("explicit", options.GetProperty("mode").GetString());
        }
    }
}
