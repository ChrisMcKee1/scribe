using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Scribe.Core.Cleanup;
using Scribe.Core.Settings;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Scribe.Core.Tests;

/// <summary>
/// P1: a change confined to what the prompt says (a dictionary term, a library, the writing style,
/// the prompt style or a guardrail prompt) rebuilds the agent in place from the factory the running
/// initialization left behind. Cleanup stays Ready, nothing reconnects and nothing is re-probed; any
/// other change, or a prompt change while not serving, still re-initializes. Copilot and Azure run
/// through a fake provider factory (their real initializers need the CLI or the network); Foundry
/// Local and custom endpoints run the real path against the fake runtime and transport.
/// </summary>
public sealed class CleanupPromptRebuildTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const string Dictated = "hello there";
    private const string CleanedText = "Hello there.";

    public static TheoryData<CleanupProvider> RemoteProviders => new()
    {
        CleanupProvider.GitHubCopilot,
        CleanupProvider.AzureFoundry,
    };

    [Theory]
    [MemberData(nameof(RemoteProviders))]
    public async Task A_prompt_only_change_rebuilds_the_agent_in_place_without_reconnecting(CleanupProvider provider)
    {
        await using var harness = new CleanupHarness();
        var fake = new FakeProvider();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = fake.Connect;
        var initial = Remote(provider) with { Glossary = "Terms: Contoso." };

        svc.Configure(initial);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(1, fake.Handshakes);
        Assert.Equal(1, fake.Client.Calls);
        var statuses = new CleanupStatusRecorder(svc);

        svc.Configure(initial with { Glossary = "Terms: Contoso, Fabrikam." });

        Assert.Empty(statuses.Snapshot());
        Assert.Equal(CleanupStatus.Ready, svc.Status);
        Assert.Equal(1, fake.Handshakes);
        Assert.Equal(1, fake.Client.Calls);

        var result = await svc.CleanAsync(Dictated).WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
        Assert.Contains("Fabrikam", fake.Client.Instructions[^1], StringComparison.Ordinal);
        Assert.Equal(2, fake.Client.Calls);
    }

    [Fact]
    public async Task A_prompt_only_change_on_foundry_local_keeps_the_loaded_model_and_sends_no_probe()
    {
        var bodies = new ConcurrentQueue<string>();
        await using var harness = new CleanupHarness(http: Capturing(bodies));
        var svc = harness.Service;

        svc.Configure(CleanupHarness.FoundryOn() with { WritingStyle = "Formal and complete." });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Single(bodies);
        var statuses = new CleanupStatusRecorder(svc);

        svc.Configure(CleanupHarness.FoundryOn() with { WritingStyle = "Casual and brief." });

        Assert.Empty(statuses.Snapshot());
        Assert.Equal(CleanupStatus.Ready, svc.Status);
        Assert.Single(bodies);
        Assert.Equal(1, harness.Qwen.LoadCalls);

        Assert.Equal(CleanupOutcome.Cleaned, (await svc.CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Equal(2, bodies.Count);
        Assert.Contains("Casual and brief.", bodies.Last(), StringComparison.Ordinal);
        Assert.DoesNotContain("Formal and complete.", bodies.Last(), StringComparison.Ordinal);
    }

    public static TheoryData<string, CleanupOptions, CleanupOptions, string> PromptOnlyChanges
    {
        get
        {
            var frontier = Custom() with
            {
                PromptStyle = CleanupPromptStyle.Frontier,
                FrontierPrompt = "FRONTIER-GUARDRAIL-ONE",
                LocalPrompt = "LOCAL-GUARDRAIL-ONE",
            };
            var local = frontier with { PromptStyle = CleanupPromptStyle.Local };
            return new()
            {
                { nameof(CleanupOptions.WritingStyle), frontier, frontier with { WritingStyle = "Write like a pirate." }, "Write like a pirate." },
                { nameof(CleanupOptions.Glossary), frontier, frontier with { Glossary = "Always spell it Fabrikam." }, "Always spell it Fabrikam." },
                { nameof(CleanupOptions.PromptStyle), frontier, local, "LOCAL-GUARDRAIL-ONE" },
                { nameof(CleanupOptions.FrontierPrompt), frontier, frontier with { FrontierPrompt = "FRONTIER-GUARDRAIL-TWO" }, "FRONTIER-GUARDRAIL-TWO" },
                { nameof(CleanupOptions.LocalPrompt), local, local with { LocalPrompt = "LOCAL-GUARDRAIL-TWO" }, "LOCAL-GUARDRAIL-TWO" },
            };
        }
    }

    [Theory]
    [MemberData(nameof(PromptOnlyChanges))]
    public async Task Every_prompt_field_is_applied_in_place_on_a_custom_endpoint(
        string field, CleanupOptions before, CleanupOptions after, string expectedInPrompt)
    {
        var bodies = new ConcurrentQueue<string>();
        await using var harness = new CleanupHarness(http: Capturing(bodies));
        var svc = harness.Service;
        svc.Configure(before);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var statuses = new CleanupStatusRecorder(svc);

        svc.Configure(after);

        Assert.True(statuses.Snapshot().Count == 0, $"{field}: the change published a status instead of staying Ready.");
        Assert.Single(bodies);
        Assert.Equal(CleanupOutcome.Cleaned, (await svc.CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Contains(expectedInPrompt, bodies.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Per_app_style_agents_are_rebuilt_with_the_new_prompt()
    {
        await using var harness = new CleanupHarness();
        var fake = new FakeProvider();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = fake.Connect;
        svc.Configure(Remote(CleanupProvider.GitHubCopilot) with { Glossary = "Terms: Contoso." });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        const string chatStyle = "Short and casual, for a chat app.";

        await svc.CleanAsync(Dictated, writingStyleOverride: chatStyle).WaitAsync(Bound);
        Assert.Contains("Contoso", fake.Client.Instructions[^1], StringComparison.Ordinal);

        svc.Configure(Remote(CleanupProvider.GitHubCopilot) with { Glossary = "Terms: Fabrikam." });
        await svc.CleanAsync(Dictated, writingStyleOverride: chatStyle).WaitAsync(Bound);

        Assert.Contains(chatStyle, fake.Client.Instructions[^1], StringComparison.Ordinal);
        Assert.Contains("Fabrikam", fake.Client.Instructions[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("Contoso", fake.Client.Instructions[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_beyond_the_prompt_still_reinitializes()
    {
        await using var harness = new CleanupHarness();
        var fake = new FakeProvider();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = fake.Connect;
        svc.Configure(Remote(CleanupProvider.GitHubCopilot, model: "model-a"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var statuses = new CleanupStatusRecorder(svc);

        svc.Configure(Remote(CleanupProvider.GitHubCopilot, model: "model-b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal(2, fake.Handshakes);
        Assert.Equal(2, fake.Client.Calls);
        Assert.Contains(statuses.Snapshot(), s => s.Status == CleanupStatus.Initializing);
        Assert.StartsWith("model-b|", fake.Built[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_prompt_change_during_initialization_restarts_it_with_the_new_prompt()
    {
        // The options an initialization publishes are the ones it started with, so while one is running
        // the rebuild does not apply; the change restarts it and the result carries the latest prompt.
        await using var harness = new CleanupHarness();
        var fake = new FakeProvider { HandshakeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = fake.Connect;
        svc.Configure(Remote(CleanupProvider.GitHubCopilot) with { Glossary = "Terms: Contoso." });
        await fake.HandshakeStarted.Task.WaitAsync(Bound);

        svc.Configure(Remote(CleanupProvider.GitHubCopilot) with { Glossary = "Terms: Fabrikam." });
        fake.HandshakeGate.SetResult();
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // The readiness probe builds an agent of its own without the glossary, so "every agent carries
        // the new prompt" reads here as "no agent was ever built with the old one".
        Assert.Equal(2, fake.Handshakes);
        Assert.DoesNotContain(fake.Built, built => built.Contains("Contoso", StringComparison.Ordinal));
        Assert.Contains(fake.Built, built => built.Contains("Fabrikam", StringComparison.Ordinal));
        Assert.Equal(CleanupOutcome.Cleaned, (await svc.CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Contains("Fabrikam", fake.Client.Instructions[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_prompt_change_after_a_real_reconfiguration_never_brings_back_the_old_agent()
    {
        await using var harness = new CleanupHarness();
        var fake = new FakeProvider();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = fake.Connect;
        svc.Configure(Remote(CleanupProvider.GitHubCopilot, model: "model-a") with { Glossary = "Terms: Contoso." });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var builtForA = fake.Built.Count;

        // Switch model; its initialization is still connecting when the prompt changes too.
        fake.HandshakeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.HandshakeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.Configure(Remote(CleanupProvider.GitHubCopilot, model: "model-b") with { Glossary = "Terms: Contoso." });
        await fake.HandshakeStarted.Task.WaitAsync(Bound);
        var statuses = new CleanupStatusRecorder(svc);

        svc.Configure(Remote(CleanupProvider.GitHubCopilot, model: "model-b") with { Glossary = "Terms: Fabrikam." });

        Assert.DoesNotContain(statuses.Snapshot(), s => s.Status == CleanupStatus.Ready);
        Assert.Equal(builtForA, fake.Built.Count);
        fake.HandshakeGate.SetResult();
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // The probe's own agent has no glossary, so the serving agent is the one that must carry it.
        Assert.All(fake.Built.Skip(builtForA), built => Assert.StartsWith("model-b|", built, StringComparison.Ordinal));
        Assert.Contains(fake.Built.Skip(builtForA), built => built.Contains("Fabrikam", StringComparison.Ordinal));
        Assert.DoesNotContain(fake.Built.Skip(builtForA), built => built.Contains("Contoso", StringComparison.Ordinal));
        Assert.Equal(CleanupOutcome.Cleaned, (await svc.CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Contains("Fabrikam", fake.Client.Instructions[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_prompt_change_arriving_while_disposal_drains_rebuilds_nothing()
    {
        await using var harness = new CleanupHarness();
        var fake = new FakeProvider();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = fake.Connect;
        svc.Configure(Remote(CleanupProvider.GitHubCopilot) with { Glossary = "Terms: Contoso." });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // A dictation is in flight on a call that does not stop when asked, so disposal has to wait.
        var callStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Client.BeforeAnswer = async (call, _) =>
        {
            if (call == 2)
            {
                callStarted.TrySetResult();
                await release.Task;
            }
        };
        var dictation = svc.CleanAsync(Dictated);
        await callStarted.Task.WaitAsync(Bound);
        var builtBefore = fake.Built.Count;
        var statuses = new CleanupStatusRecorder(svc);

        var dispose = svc.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted, "Disposal waits for the dictation in flight.");
        svc.Configure(Remote(CleanupProvider.GitHubCopilot) with { Glossary = "Terms: Fabrikam." });

        Assert.Equal(builtBefore, fake.Built.Count);
        release.SetResult();
        await dictation.WaitAsync(Bound);
        await dispose.WaitAsync(Bound);
        Assert.Equal(CleanupDisposalOutcome.Released, svc.DisposalOutcome);
        Assert.Empty(statuses.Snapshot());
        Assert.Equal(builtBefore, fake.Built.Count);

        // And once disposed, a prompt change is simply refused.
        svc.Configure(Remote(CleanupProvider.GitHubCopilot) with { Glossary = "Terms: Northwind." });
        Assert.Equal(builtBefore, fake.Built.Count);
        Assert.Equal(1, fake.Handshakes);
    }

    [Fact]
    public async Task A_prompt_change_while_an_initialization_is_finishing_rebuilds_from_what_it_published()
    {
        await using var harness = new CleanupHarness();
        var fake = new FakeProvider();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = fake.Connect;

        // Pause the initialization inside its Ready notification: it has published its agent and Ready
        // but has not returned yet.
        using var paused = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var pausedOnce = 0;
        svc.StatusChanged += () =>
        {
            if (svc.Status == CleanupStatus.Ready && Interlocked.Exchange(ref pausedOnce, 1) == 0)
            {
                paused.Set();
                resume.Wait(Bound);
            }
        };
        svc.Configure(Remote(CleanupProvider.AzureFoundry) with { Glossary = "Terms: Contoso." });
        Assert.True(paused.Wait(Bound));
        var statuses = new CleanupStatusRecorder(svc);

        svc.Configure(Remote(CleanupProvider.AzureFoundry) with { Glossary = "Terms: Fabrikam." });
        resume.Set();

        Assert.Equal(CleanupOutcome.Cleaned, (await svc.CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Contains("Fabrikam", fake.Client.Instructions[^1], StringComparison.Ordinal);
        Assert.Empty(statuses.Snapshot());
        Assert.Equal(1, fake.Handshakes);
        Assert.Equal(2, fake.Client.Calls);
    }

    [Fact]
    public async Task A_factory_that_fails_to_rebuild_falls_back_to_reinitializing()
    {
        // Builds 1 and 2 are the first initialization's serving agent and its probe's own agent, so
        // build 3 is the in-place rebuild.
        await using var harness = new CleanupHarness();
        var fake = new FakeProvider { ThrowOnBuild = 3 };
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = fake.Connect;
        svc.Configure(Remote(CleanupProvider.GitHubCopilot) with { Glossary = "Terms: Contoso." });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        svc.Configure(Remote(CleanupProvider.GitHubCopilot) with { Glossary = "Terms: Fabrikam." });
        Assert.NotEqual(CleanupStatus.Ready, svc.Status);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal(2, fake.Handshakes);
        Assert.Contains(harness.Log.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("re-initializing instead", StringComparison.Ordinal));
        Assert.Equal(CleanupOutcome.Cleaned, (await svc.CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Contains("Fabrikam", fake.Client.Instructions[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_prompt_change_while_unavailable_with_an_agent_still_attached_retries_instead()
    {
        // A failed eviction reload reports Unavailable without dropping the agent. Rebuilding that in
        // place would keep the prompt current and leave cleanup Unavailable; the change is the retry.
        var evicted = 0;
        var bodies = new ConcurrentQueue<string>();
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            bodies.Enqueue(await request.Content!.ReadAsStringAsync(ct));
            return Volatile.Read(ref evicted) == 1
                ? ScriptedHttpHandler.Json(System.Net.HttpStatusCode.BadRequest,
                    "{\"error\":{\"message\":\"Model 'qwen3-1.7b-generic-cpu:2' is not loaded. Please load the model before getting a ChatClient.\"}}")
                : ScriptedHttpHandler.ChatCompletion(CleanedText);
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.FoundryOn() with { Glossary = "Terms: Contoso." });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Volatile.Write(ref evicted, 1);
        harness.Qwen.LoadFailure = new InvalidOperationException("The model file is damaged.");
        Assert.Equal(CleanupOutcome.Failed, (await svc.CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Equal(CleanupStatus.Unavailable, svc.Status);

        Volatile.Write(ref evicted, 0);
        harness.Qwen.LoadFailure = null;
        var probesBefore = bodies.Count;
        svc.Configure(CleanupHarness.FoundryOn() with { Glossary = "Terms: Fabrikam." });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // One new probe, which carries no glossary; the next cleanup carries the new one.
        Assert.Equal(probesBefore + 1, bodies.Count);
        Assert.DoesNotContain("Fabrikam", bodies.Last(), StringComparison.Ordinal);
        Assert.Equal(CleanupOutcome.Cleaned, (await svc.CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Contains("Fabrikam", bodies.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_prompt_fields_are_ignored_when_comparing_configurations()
    {
        var baseline = new CleanupOptions(
            Enabled: true,
            Provider: CleanupProvider.AzureFoundry,
            FoundryModelAlias: "alias",
            AzureEndpoint: "https://account.example.invalid/",
            AzureDeployment: "deployment",
            AzureApiKey: "key",
            AzureTenantId: "tenant",
            WritingStyle: "style",
            Glossary: "glossary",
            CustomEndpoint: "https://custom.example.invalid/v1",
            CustomModel: "model",
            CustomApiKey: "custom-key",
            PromptStyle: CleanupPromptStyle.Frontier,
            FrontierPrompt: "frontier",
            LocalPrompt: "local",
            AzureSubscriptionId: "subscription",
            AzureAuthMode: AzureAuthMode.AzureCli,
            AzureClientId: "client",
            AzureClientSecret: "secret",
            CopilotModel: "copilot-model");
        var variants = new Dictionary<string, CleanupOptions>
        {
            [nameof(CleanupOptions.Enabled)] = baseline with { Enabled = false },
            [nameof(CleanupOptions.Provider)] = baseline with { Provider = CleanupProvider.OpenAiCompatible },
            [nameof(CleanupOptions.FoundryModelAlias)] = baseline with { FoundryModelAlias = "other" },
            [nameof(CleanupOptions.AzureEndpoint)] = baseline with { AzureEndpoint = "https://other.example.invalid/" },
            [nameof(CleanupOptions.AzureDeployment)] = baseline with { AzureDeployment = "other" },
            [nameof(CleanupOptions.AzureApiKey)] = baseline with { AzureApiKey = "other" },
            [nameof(CleanupOptions.AzureTenantId)] = baseline with { AzureTenantId = "other" },
            [nameof(CleanupOptions.WritingStyle)] = baseline with { WritingStyle = "other" },
            [nameof(CleanupOptions.Glossary)] = baseline with { Glossary = "other" },
            [nameof(CleanupOptions.CustomEndpoint)] = baseline with { CustomEndpoint = "https://other.example.invalid/v1" },
            [nameof(CleanupOptions.CustomModel)] = baseline with { CustomModel = "other" },
            [nameof(CleanupOptions.CustomApiKey)] = baseline with { CustomApiKey = "other" },
            [nameof(CleanupOptions.PromptStyle)] = baseline with { PromptStyle = CleanupPromptStyle.Local },
            [nameof(CleanupOptions.FrontierPrompt)] = baseline with { FrontierPrompt = "other" },
            [nameof(CleanupOptions.LocalPrompt)] = baseline with { LocalPrompt = "other" },
            [nameof(CleanupOptions.AzureSubscriptionId)] = baseline with { AzureSubscriptionId = "other" },
            [nameof(CleanupOptions.AzureAuthMode)] = baseline with { AzureAuthMode = AzureAuthMode.ServicePrincipal },
            [nameof(CleanupOptions.AzureClientId)] = baseline with { AzureClientId = "other" },
            [nameof(CleanupOptions.AzureClientSecret)] = baseline with { AzureClientSecret = "other" },
            [nameof(CleanupOptions.CopilotModel)] = baseline with { CopilotModel = "other" },
        };
        string[] promptFields =
        [
            nameof(CleanupOptions.WritingStyle),
            nameof(CleanupOptions.Glossary),
            nameof(CleanupOptions.PromptStyle),
            nameof(CleanupOptions.FrontierPrompt),
            nameof(CleanupOptions.LocalPrompt),
        ];

        // A field added later has to be classified here: treated as part of the connection until it is.
        var settable = typeof(CleanupOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(settable, variants.Keys.Order(StringComparer.Ordinal));

        foreach (var (field, variant) in variants)
        {
            Assert.True(
                promptFields.Contains(field) == baseline.MatchesIgnoringPrompt(variant),
                $"{field} is classified wrongly.");
        }

        Assert.True(baseline.MatchesIgnoringPrompt(baseline with { }));
        Assert.False(baseline.MatchesIgnoringPrompt(null));
    }

    private static CleanupOptions Remote(CleanupProvider provider, string model = "cleanup-model") => provider switch
    {
        CleanupProvider.GitHubCopilot => new CleanupOptions(
            true, CleanupProvider.GitHubCopilot, CleanupModelCatalog.DefaultAlias, null, null, CopilotModel: model),

        // Never contacted: the fake provider factory stands in for the whole Azure client.
        _ => new CleanupOptions(
            true, CleanupProvider.AzureFoundry, CleanupModelCatalog.DefaultAlias, "https://cleanup-canary.example.invalid/", model),
    };

    private static CleanupOptions Custom() => CleanupHarness.Custom("https://cleanup.example.test/v1");

    // Answers every call with a plausible clean of Dictated and keeps each request body.
    private static ScriptedHttpHandler Capturing(ConcurrentQueue<string> bodies) => new(async (request, ct) =>
    {
        bodies.Enqueue(await request.Content!.ReadAsStringAsync(ct));
        return ScriptedHttpHandler.ChatCompletion(CleanedText);
    });

    /// <summary>
    /// Stands in for a remote provider's initializer. Each connect is one handshake (the Copilot CLI
    /// start, the Azure client build); the factory it returns builds agents over one recording client
    /// and notes what each agent was built for.
    /// </summary>
    private sealed class FakeProvider
    {
        private readonly ConcurrentQueue<string> _built = new();
        private int _handshakes;
        private int _builds;

        public RecordingChatClient Client { get; } = new();

        public int Handshakes => Volatile.Read(ref _handshakes);

        /// <summary>"model|instructions" for every agent built, in order.</summary>
        public IReadOnlyList<string> Built => _built.ToArray();

        /// <summary>When set, a handshake waits for it, honouring cancellation.</summary>
        public TaskCompletionSource? HandshakeGate { get; set; }

        public TaskCompletionSource HandshakeStarted { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The 1-based build that throws, or 0 for none.</summary>
        public int ThrowOnBuild { get; init; }

        public async Task<Func<string, AIAgent>> Connect(CleanupOptions options, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _handshakes);
            HandshakeStarted.TrySetResult();
            if (HandshakeGate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            var model = options.Provider == CleanupProvider.GitHubCopilot ? options.CopilotModel : options.AzureDeployment;
            return instructions =>
            {
                if (Interlocked.Increment(ref _builds) == ThrowOnBuild)
                {
                    throw new InvalidOperationException("Agent construction failed.");
                }

                _built.Enqueue($"{model}|{instructions}");
                return new ChatClientAgent(Client, instructions: instructions, name: "ScribeCleanup");
            };
        }
    }

    /// <summary>A chat client that answers every call and records the instructions each call carried.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private readonly ConcurrentQueue<string> _instructions = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<string> Instructions => _instructions.ToArray();

        /// <summary>Runs before answering, with the 1-based call number.</summary>
        public Func<int, CancellationToken, Task>? BeforeAnswer { get; set; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var system = messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text);
            _instructions.Enqueue(string.Join("\n", system.Prepend(options?.Instructions ?? string.Empty)));
            var call = Interlocked.Increment(ref _calls);
            if (BeforeAnswer is { } before)
            {
                await before(call, cancellationToken);
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, CleanedText));
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
