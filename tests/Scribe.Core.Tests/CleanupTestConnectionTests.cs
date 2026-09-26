using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class CleanupTestConnectionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const string VocabularyCanary = "Zebraquill Kestrelmoor";
    private const string SpokenCanary = "zebra quill kestrel moor";
    private static readonly string Glossary = CleanupPrompt.BuildGlossary(
        [DictionaryEntry.New(SpokenCanary, VocabularyCanary)]);

    private sealed record SentRequest(string Path, string Body);

    [Fact]
    public async Task Custom_test_uses_probe_prompt_and_keeps_the_running_service_unchanged()
    {
        var requests = new ConcurrentQueue<SentRequest>();
        await using var harness = new CleanupHarness(http: Recording(requests, _ => ScriptedHttpHandler.ChatCompletion("ok")));
        var svc = harness.Service;
        svc.Configure(Custom() with { Glossary = Glossary });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var beforeGeneration = svc.InitGenerationForTesting;
        var beforeAgent = svc.ServingAgentForTesting;

        var result = await svc.TestAsync(Custom() with { Glossary = Glossary }).WaitAsync(Bound);

        Assert.Equal(CleanupTestOutcome.Connected, result.Outcome);
        Assert.Equal(CleanupStatus.Ready, svc.Status);
        Assert.Equal(beforeGeneration, svc.InitGenerationForTesting);
        Assert.Same(beforeAgent, svc.ServingAgentForTesting);
        var sent = requests.Last();
        AssertProbe(sent.Body);
    }

    [Fact]
    public async Task Failed_custom_test_keeps_the_running_service_unchanged()
    {
        var calls = 0;
        await using var harness = new CleanupHarness(http: new ScriptedHttpHandler((_, _) => Task.FromResult(
            Interlocked.Increment(ref calls) == 1
                ? ScriptedHttpHandler.ChatCompletion("ok")
                : ScriptedHttpHandler.Json(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"bad key\"}}"))));
        var svc = harness.Service;
        svc.Configure(Custom());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var beforeGeneration = svc.InitGenerationForTesting;
        var beforeAgent = svc.ServingAgentForTesting;

        var result = await svc.TestAsync(Custom()).WaitAsync(Bound);

        Assert.Equal(CleanupTestOutcome.Failed, result.Outcome);
        Assert.Contains("credentials", result.SafeReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CleanupStatus.Ready, svc.Status);
        Assert.Equal(beforeGeneration, svc.InitGenerationForTesting);
        Assert.Same(beforeAgent, svc.ServingAgentForTesting);
    }

    [Fact]
    public async Task Azure_responses_test_sends_store_false_and_no_vocabulary()
    {
        var requests = new ConcurrentQueue<SentRequest>();
        await using var harness = new CleanupHarness(http: Recording(requests, _ => ResponsesAnswer("ok")));

        var result = await harness.Service.TestAsync(Azure() with { Glossary = Glossary }).WaitAsync(Bound);

        Assert.Equal(CleanupTestOutcome.Connected, result.Outcome);
        var sent = Assert.Single(requests);
        Assert.EndsWith("/openai/v1/responses", sent.Path, StringComparison.Ordinal);
        AssertProbe(sent.Body);
        AssertStoreFalse(sent.Body);
    }

    [Fact]
    public async Task Azure_chat_fallback_test_sends_no_store_and_no_vocabulary()
    {
        var requests = new ConcurrentQueue<SentRequest>();
        await using var harness = new CleanupHarness(http: Recording(requests, path => path.EndsWith("/responses", StringComparison.Ordinal)
            ? ScriptedHttpHandler.Json(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"unsupported\"}}")
            : ScriptedHttpHandler.ChatCompletion("ok")));

        var result = await harness.Service.TestAsync(Azure() with { Glossary = Glossary }).WaitAsync(Bound);

        Assert.Equal(CleanupTestOutcome.Connected, result.Outcome);
        var sent = requests.ToArray();
        Assert.Equal(2, sent.Length);
        Assert.EndsWith("/openai/v1/responses", sent[0].Path, StringComparison.Ordinal);
        Assert.EndsWith("/openai/v1/chat/completions", sent[1].Path, StringComparison.Ordinal);
        AssertProbe(sent[0].Body);
        AssertProbe(sent[1].Body);
        AssertStoreFalse(sent[0].Body);
        AssertNoStore(sent[1].Body);
    }

    [Theory]
    [InlineData(CleanupProvider.FoundryLocal)]
    [InlineData(CleanupProvider.GitHubCopilot)]
    public async Task Local_and_copilot_tests_are_not_applicable_and_do_no_io(CleanupProvider provider)
    {
        await using var harness = new CleanupHarness(http: new ScriptedHttpHandler((_, _) =>
            throw new InvalidOperationException("No request should be sent.")));

        var result = await harness.Service.TestAsync(Remote(provider)).WaitAsync(Bound);

        Assert.Equal(CleanupTestOutcome.NotApplicable, result.Outcome);
        Assert.Equal(0, ((ScriptedHttpHandler)harness.Http).Requests);
    }

    [Fact]
    public async Task Disposal_during_a_connection_test_waits_and_cancels_it()
    {
        var requestArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unwind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new CleanupHarness(http: new ScriptedHttpHandler(async (_, ct) =>
        {
            requestArrived.TrySetResult();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetResult()))
            {
                await cancelled.Task;
            }

            cancelSeen.TrySetResult();
            await unwind.Task;
            throw new OperationCanceledException(ct);
        }));
        var svc = harness.Service;
        try
        {
            var test = svc.TestAsync(Custom());
            await requestArrived.Task.WaitAsync(Bound);
            var dispose = svc.DisposeAsync().AsTask();
            await cancelSeen.Task.WaitAsync(Bound);
            Assert.False(dispose.IsCompleted);

            unwind.SetResult();
            Assert.Equal(CleanupTestOutcome.Cancelled, (await test.WaitAsync(Bound)).Outcome);
            await dispose.WaitAsync(Bound);
        }
        finally
        {
            unwind.TrySetResult();
        }
    }

    [Fact]
    public async Task Result_recipient_identifies_stale_fields()
    {
        await using var harness = new CleanupHarness();

        var result = await harness.Service.TestAsync(Custom(model: "qwen")).WaitAsync(Bound);

        Assert.True(result.Recipient.Matches(Custom(model: "qwen")));
        Assert.False(result.Recipient.Matches(Custom(model: "llama")));
    }

    [Fact]
    public async Task Result_recipient_matches_a_page_shaped_unchanged_candidate_with_blank_fields()
    {
        await using var harness = new CleanupHarness();
        var pageCandidate = Custom(model: "qwen") with
        {
            AzureEndpoint = string.Empty,
            AzureDeployment = " ",
            AzureApiKey = string.Empty,
            AzureTenantId = " ",
            CustomApiKey = string.Empty,
            CopilotModel = " ",
        };

        var result = await harness.Service.TestAsync(pageCandidate).WaitAsync(Bound);

        Assert.Equal(CleanupTestOutcome.Connected, result.Outcome);
        Assert.True(result.Recipient.Matches(CleanupConnectionTestPolicy.Canonicalize(pageCandidate)));
    }

    private const string TenantGuid = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
    private const string ClientGuid = "9b1deb4d-3b7d-4bad-9bdd-2b0d7b3dcb6d";

    public static TheoryData<CleanupOptions, bool, bool> AvailabilityCases => new()
    {
        { Azure() with { AzureAuthMode = AzureAuthMode.ServicePrincipal, AzureTenantId = TenantGuid, AzureClientId = ClientGuid, AzureClientSecret = "secret" }, false, true },
        { Azure() with { AzureAuthMode = AzureAuthMode.ServicePrincipal, AzureTenantId = "contoso.onmicrosoft.com", AzureClientId = ClientGuid, AzureClientSecret = "secret" }, false, true },
        { Azure() with { AzureAuthMode = AzureAuthMode.ServicePrincipal, AzureTenantId = TenantGuid, AzureClientId = ClientGuid, AzureClientSecret = "secret", AzureEndpoint = null }, false, false },
        { Azure() with { AzureAuthMode = AzureAuthMode.ServicePrincipal, AzureTenantId = TenantGuid, AzureClientId = ClientGuid, AzureClientSecret = null }, false, false },
        { Azure() with { AzureAuthMode = AzureAuthMode.ServicePrincipal, AzureTenantId = TenantGuid, AzureClientId = "not-a-client-guid", AzureClientSecret = "secret" }, false, false },
        { Azure(), false, true },
        { Azure() with { AzureApiKey = null }, false, true },
        { Azure(), true, true },
        { Azure() with { AzureApiKey = null }, true, false },
        { Azure() with { AzureApiKey = string.Empty }, true, false },
        { Azure() with { AzureEndpoint = null }, false, false },
        { Azure() with { AzureDeployment = null }, false, false },
        { Custom(), false, true },
        { Custom(model: " "), false, false },
        { Custom(endpoint: " "), false, false },
    };

    [Theory]
    [MemberData(nameof(AvailabilityCases))]
    public void Availability_is_decided_from_provider_prerequisites(CleanupOptions candidate, bool apiKeySelected, bool expected) =>
        Assert.Equal(expected, CleanupConnectionTestPolicy.CanTest(candidate, apiKeySelected));

    private static void AssertProbe(string body)
    {
        Assert.DoesNotContain(VocabularyCanary, body, StringComparison.Ordinal);
        Assert.DoesNotContain("zebra quill", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Preferred vocabulary", body, StringComparison.Ordinal);
    }

    private static void AssertStoreFalse(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.TryGetProperty("store", out var store), $"No store field was sent: {body}");
        Assert.Equal(JsonValueKind.False, store.ValueKind);
    }

    private static void AssertNoStore(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.False(document.RootElement.TryGetProperty("store", out _), $"Chat Completions sent store: {body}");
    }

    private static ScriptedHttpHandler Recording(
        ConcurrentQueue<SentRequest> requests, Func<string, HttpResponseMessage> respond) => new(async (request, ct) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        requests.Enqueue(new SentRequest(path, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct)));
        return respond(path);
    });

    private static HttpResponseMessage ResponsesAnswer(string text) => ScriptedHttpHandler.Json(
        HttpStatusCode.OK,
        "{\"id\":\"resp_test\",\"object\":\"response\",\"created_at\":1700000000,\"status\":\"completed\"," +
        "\"model\":\"test\",\"output\":[{\"type\":\"message\",\"id\":\"msg_test\",\"status\":\"completed\"," +
        "\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"" + text + "\",\"annotations\":[]}]}]," +
        "\"parallel_tool_calls\":false,\"tool_choice\":\"auto\",\"tools\":[]," +
        "\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}");

    private static CleanupOptions Custom(string endpoint = "https://test-connection.example.invalid/v1", string model = "llama3") =>
        CleanupHarness.Custom(endpoint, model);

    private static CleanupOptions Azure() => new(
        true,
        CleanupProvider.AzureFoundry,
        CleanupModelCatalog.DefaultAlias,
        "https://test-connection.example.invalid/",
        "cleanup-deployment",
        AzureApiKey: "not-a-real-key");

    private static CleanupOptions Remote(CleanupProvider provider) => provider == CleanupProvider.GitHubCopilot
        ? new CleanupOptions(true, CleanupProvider.GitHubCopilot, CleanupModelCatalog.DefaultAlias, null, null, CopilotModel: "cleanup-model")
        : CleanupHarness.FoundryOn();
}
