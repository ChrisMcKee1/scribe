using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

/// <summary>
/// The readiness probe runs every time cleanup connects (at startup, on a provider or model change,
/// on the retry after a failure), before anything is dictated, so it must not carry the user's
/// vocabulary. It still carries the real guardrails and writing style, so it reasons the way a
/// cleanup call does, and every cleanup call still carries the glossary, which is what the AI
/// cleanup page tells the user. Each test runs the production initialization over a fake transport
/// or a fake provider factory and reads what was actually sent; nothing leaves the process.
/// </summary>
public sealed class CleanupProbeVocabularyTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const string Dictated = "hello there";
    private const string CleanedText = "Hello there.";

    // Letters and spaces only, so JSON escaping in a request body can never hide them.
    private const string VocabularyCanary = "Zebraquill Kestrelmoor";
    private const string SpokenCanary = "zebra quill kestrel moor";
    private const string StyleCanary = "Write like a harbour pilot keeping a tidy log";

    private static readonly string Glossary = CleanupPrompt.BuildGlossary(
        [DictionaryEntry.New(SpokenCanary, VocabularyCanary), DictionaryEntry.New("contoso", "Contoso")]);

    private sealed record SentRequest(string Path, string Body);

    [Fact]
    public void The_probe_prompt_keeps_the_guardrails_and_writing_style_and_drops_only_the_glossary()
    {
        foreach (var options in new[]
        {
            Custom() with { Glossary = Glossary, WritingStyle = StyleCanary },
            CleanupHarness.FoundryOn() with { Glossary = Glossary, WritingStyle = StyleCanary },
            Azure() with { Glossary = Glossary, WritingStyle = StyleCanary, PromptStyle = CleanupPromptStyle.Local },
        })
        {
            var probe = TextCleanupService.BuildProbeSystemPrompt(options);
            var cleanup = TextCleanupService.BuildSystemPrompt(options);

            Assert.Contains(VocabularyCanary, cleanup, StringComparison.Ordinal);
            Assert.DoesNotContain(VocabularyCanary, probe, StringComparison.Ordinal);
            Assert.DoesNotContain("Preferred vocabulary", probe, StringComparison.Ordinal);
            Assert.Equal(TextCleanupService.BuildSystemPrompt(options with { Glossary = null }), probe);
            Assert.Contains(StyleCanary, probe, StringComparison.Ordinal);
        }

        // The qwen3 directive belongs to the model, not the glossary, so the probe keeps it.
        Assert.EndsWith(" /no_think", TextCleanupService.BuildProbeSystemPrompt(
            CleanupHarness.FoundryOn("qwen3-1.7b") with { Glossary = Glossary }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_custom_endpoint_probe_carries_no_vocabulary_but_each_cleanup_does()
    {
        var requests = new ConcurrentQueue<SentRequest>();
        await using var harness = new CleanupHarness(http: Recording(requests, _ => ScriptedHttpHandler.ChatCompletion(CleanedText)));

        harness.Service.Configure(Custom() with { Glossary = Glossary, WritingStyle = StyleCanary });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        AssertProbe(Assert.Single(requests));
        await AssertCleanupCarriesTheGlossaryAsync(harness, requests);
    }

    [Fact]
    public async Task A_foundry_local_probe_carries_no_vocabulary_but_each_cleanup_does()
    {
        var requests = new ConcurrentQueue<SentRequest>();
        await using var harness = new CleanupHarness(http: Recording(requests, _ => ScriptedHttpHandler.ChatCompletion(CleanedText)));

        harness.Service.Configure(CleanupHarness.FoundryOn() with { Glossary = Glossary, WritingStyle = StyleCanary });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        AssertProbe(Assert.Single(requests));
        await AssertCleanupCarriesTheGlossaryAsync(harness, requests);
    }

    [Fact]
    public async Task An_azure_responses_probe_carries_no_vocabulary_but_each_cleanup_does()
    {
        var requests = new ConcurrentQueue<SentRequest>();
        await using var harness = new CleanupHarness(http: Recording(requests, _ => ResponsesAnswer(CleanedText)));

        harness.Service.Configure(Azure() with { Glossary = Glossary, WritingStyle = StyleCanary });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var probe = Assert.Single(requests);
        Assert.EndsWith("/openai/v1/responses", probe.Path, StringComparison.Ordinal);
        AssertProbe(probe);
        AssertStoreFalse(probe.Body);

        var cleanup = await AssertCleanupCarriesTheGlossaryAsync(harness, requests);
        AssertStoreFalse(cleanup.Body);
    }

    [Fact]
    public async Task The_chat_completions_fallback_probe_carries_no_vocabulary_either()
    {
        // MAI-Thinking-1's answer to a Responses call: a 400, which buys one probe on Chat Completions.
        var requests = new ConcurrentQueue<SentRequest>();
        await using var harness = new CleanupHarness(http: Recording(requests, path => path.EndsWith("/responses", StringComparison.Ordinal)
            ? ScriptedHttpHandler.Json(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"The requested operation is unsupported.\"}}")
            : ScriptedHttpHandler.ChatCompletion(CleanedText)));

        harness.Service.Configure(Azure() with { Glossary = Glossary, WritingStyle = StyleCanary });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var probes = requests.ToArray();
        Assert.Collection(
            probes,
            responses => Assert.EndsWith("/openai/v1/responses", responses.Path, StringComparison.Ordinal),
            chat => Assert.EndsWith("/openai/v1/chat/completions", chat.Path, StringComparison.Ordinal));
        Assert.All(probes, AssertProbe);

        var cleanup = await AssertCleanupCarriesTheGlossaryAsync(harness, requests);
        Assert.EndsWith("/openai/v1/chat/completions", cleanup.Path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CleanupProvider.GitHubCopilot)]
    [InlineData(CleanupProvider.AzureFoundry)]
    public async Task A_provider_reached_through_its_agent_factory_is_probed_without_vocabulary(CleanupProvider provider)
    {
        // The Copilot CLI and Azure's client are the provider-specific half; the probe itself is the
        // same code for every provider, so a recording agent factory stands in for the connection.
        await using var harness = new CleanupHarness();
        var client = new RecordingChatClient();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = (_, _) =>
            Task.FromResult<Func<string, AIAgent>>(instructions => new ChatClientAgent(client, instructions: instructions, name: "ScribeCleanup"));

        svc.Configure(Remote(provider) with { Glossary = Glossary, WritingStyle = StyleCanary });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var probe = Assert.Single(client.Instructions);
        Assert.Contains(StyleCanary, probe, StringComparison.Ordinal);
        Assert.DoesNotContain(VocabularyCanary, probe, StringComparison.Ordinal);

        Assert.Equal(CleanupOutcome.Cleaned, (await svc.CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Contains(VocabularyCanary, client.Instructions[^1], StringComparison.Ordinal);
    }

    // The probe: the real instructions, a one-word transcript, and none of the vocabulary.
    private static void AssertProbe(SentRequest probe)
    {
        Assert.Contains(StyleCanary, probe.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(VocabularyCanary, probe.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("zebra quill", probe.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Preferred vocabulary", probe.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Dictated, probe.Body, StringComparison.Ordinal);
    }

    // The control: the same canary is what every cleanup call carries, so a probe that carried it
    // would have been seen above.
    private static async Task<SentRequest> AssertCleanupCarriesTheGlossaryAsync(
        CleanupHarness harness, ConcurrentQueue<SentRequest> requests)
    {
        var before = requests.Count;
        var result = await harness.Service.CleanAsync(Dictated).WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
        Assert.Equal(before + 1, requests.Count);
        var cleanup = requests.Last();
        Assert.Contains(VocabularyCanary, cleanup.Body, StringComparison.Ordinal);
        Assert.Contains(StyleCanary, cleanup.Body, StringComparison.Ordinal);
        Assert.Contains(Dictated, cleanup.Body, StringComparison.Ordinal);
        return cleanup;
    }

    private static void AssertStoreFalse(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.TryGetProperty("store", out var store), $"No \"store\" field was sent: {body}");
        Assert.Equal(JsonValueKind.False, store.ValueKind);
    }

    private static ScriptedHttpHandler Recording(
        ConcurrentQueue<SentRequest> requests, Func<string, HttpResponseMessage> respond) => new(async (request, ct) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        requests.Enqueue(new SentRequest(path, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct)));
        return respond(path);
    });

    // A minimal Responses API answer carrying one output message.
    private static HttpResponseMessage ResponsesAnswer(string text) => ScriptedHttpHandler.Json(
        HttpStatusCode.OK,
        "{\"id\":\"resp_test\",\"object\":\"response\",\"created_at\":1700000000,\"status\":\"completed\"," +
        "\"model\":\"test\",\"output\":[{\"type\":\"message\",\"id\":\"msg_test\",\"status\":\"completed\"," +
        "\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"" + text + "\",\"annotations\":[]}]}]," +
        "\"parallel_tool_calls\":false,\"tool_choice\":\"auto\",\"tools\":[]," +
        "\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}");

    private static CleanupOptions Custom() => CleanupHarness.Custom("https://probe-canary.example.test/v1");

    // API-key authentication, so the real Azure initializer builds its clients over the fake transport
    // without asking any credential for a token.
    private static CleanupOptions Azure() => new(
        true,
        CleanupProvider.AzureFoundry,
        CleanupModelCatalog.DefaultAlias,
        "https://probe-canary.example.invalid/",
        "cleanup-deployment",
        AzureApiKey: "not-a-real-key");

    private static CleanupOptions Remote(CleanupProvider provider) => provider == CleanupProvider.GitHubCopilot
        ? new CleanupOptions(true, CleanupProvider.GitHubCopilot, CleanupModelCatalog.DefaultAlias, null, null, CopilotModel: "cleanup-model")
        : Azure();

    /// <summary>Answers every call with a clean of <see cref="Dictated"/> and records the instructions it carried.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private readonly ConcurrentQueue<string> _instructions = new();

        public IReadOnlyList<string> Instructions => _instructions.ToArray();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var system = messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text);
            _instructions.Enqueue(string.Join("\n", system.Prepend(options?.Instructions ?? string.Empty)));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, CleanedText)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
