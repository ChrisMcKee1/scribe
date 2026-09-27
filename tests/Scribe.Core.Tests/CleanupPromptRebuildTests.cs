using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Scribe.Core.Cleanup;
using Scribe.Core.Settings;
using Scribe.Core.Tests.Concurrency;
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
        var fake = new FakeProvider();
        var first = fake.Hold(1);
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = fake.Connect;
        svc.Configure(Remote(CleanupProvider.GitHubCopilot) with { Glossary = "Terms: Contoso." });
        await first.Started.Task.WaitAsync(Bound);

        svc.Configure(Remote(CleanupProvider.GitHubCopilot) with { Glossary = "Terms: Fabrikam." });
        first.Release();
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // The readiness probe builds an agent of its own without the glossary, so "every agent carries
        // the new prompt" reads here as "no agent was ever built with the old one": the first handshake
        // is held until after the change has cancelled it, and it observes that cancellation. A
        // superseded handshake that completes anyway is
        // A_prompt_change_after_a_real_reconfiguration_never_brings_back_the_old_agent.
        Assert.Equal(2, fake.Handshakes);
        Assert.DoesNotContain(fake.Built, built => built.Contains("Contoso", StringComparison.Ordinal));
        Assert.Contains(fake.Built, built => built.Contains("Fabrikam", StringComparison.Ordinal));
        Assert.Equal(CleanupOutcome.Cleaned, (await svc.CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Contains("Fabrikam", fake.Client.Instructions[^1], StringComparison.Ordinal);
    }

    /// <summary>How far the initialization a prompt change supersedes gets before it stops.</summary>
    public enum SupersededRun
    {
        /// <summary>
        /// Its handshake is held, as if its thread were descheduled, until the change has cancelled it
        /// and the gate is open (the schedule that used to let it through), and the cancellation stops it.
        /// </summary>
        StopsAtItsHandshake,

        /// <summary>
        /// Its handshake completes after the change anyway, as a real one can when it finishes just as
        /// the token is cancelled, so it builds an agent from the options the change replaced.
        /// </summary>
        FinishesItsHandshake,

        /// <summary>
        /// The change lands while its readiness probe is in flight and the probe answers anyway, so it
        /// reaches the point where it would publish what it built.
        /// </summary>
        FinishesItsProbe,
    }

    [Theory]
    [InlineData(SupersededRun.StopsAtItsHandshake)]
    [InlineData(SupersededRun.FinishesItsHandshake)]
    [InlineData(SupersededRun.FinishesItsProbe)]
    public async Task A_prompt_change_after_a_real_reconfiguration_never_brings_back_the_old_agent(SupersededRun superseded)
    {
        const string chatStyle = "Short and casual, for a chat app.";
        await using var harness = new CleanupHarness();
        var fake = new FakeProvider();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = fake.Connect;
        svc.Configure(Remote(CleanupProvider.GitHubCopilot, model: "model-a") with { Glossary = "Terms: Contoso." });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var builtForA = fake.Agents.Count;

        // Switch model and hold its initialization, and hold the one the prompt change will start, so
        // what happens in between is visible. In the probe case the switched model's probe is held too,
        // and answers when released whatever its token says.
        var switched = fake.Hold(2, finishesDespiteCancellation: superseded != SupersededRun.StopsAtItsHandshake);
        var successor = fake.Hold(3);
        var switchedProbe = fake.Client.Calls + 1;
        var probeInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeAnswers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Client.BeforeAnswer = async (call, _) =>
        {
            if (superseded == SupersededRun.FinishesItsProbe && call == switchedProbe)
            {
                probeInFlight.TrySetResult();
                await probeAnswers.Task;
            }
        };
        svc.Configure(Remote(CleanupProvider.GitHubCopilot, model: "model-b") with { Glossary = "Terms: Contoso." });
        await switched.Started.Task.WaitAsync(Bound);
        if (superseded == SupersededRun.FinishesItsProbe)
        {
            switched.Release();
            await probeInFlight.Task.WaitAsync(Bound);
        }

        var builtBeforeChange = fake.Agents.Count;
        var callsBeforeChange = fake.Client.Calls;
        var statuses = new CleanupStatusRecorder(svc);

        svc.Configure(Remote(CleanupProvider.GitHubCopilot, model: "model-b") with { Glossary = "Terms: Fabrikam." });

        // The change restarts the initialization: nothing is rebuilt in place from what model-a left.
        Assert.DoesNotContain(statuses.Snapshot(), s => s.Status == CleanupStatus.Ready);
        Assert.Equal(builtBeforeChange, fake.Agents.Count);

        // The superseded run goes on to wherever the case lets it. The successor's handshake starts only
        // once that run has finished, because both need the init lock, and until the successor publishes
        // nothing serves and nothing new reaches the provider.
        switched.Release();
        probeAnswers.TrySetResult();
        await successor.Started.Task.WaitAsync(Bound);
        var fromSuperseded = fake.Agents.Skip(builtForA).ToList();
        Assert.NotEqual(CleanupStatus.Ready, svc.Status);
        Assert.Equal(CleanupOutcome.Skipped, (await svc.CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Equal(callsBeforeChange, fake.Client.Calls);

        successor.Release();
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var dictation = await svc.CleanAsync(Dictated).WaitAsync(Bound);
        var styled = await svc.CleanAsync(Dictated, writingStyleOverride: chatStyle).WaitAsync(Bound);

        // Each case got as far as it says, so the contract below is tested against an agent built from
        // the old glossary wherever the case has one.
        var builtStale = fromSuperseded.Any(agent => agent.Instructions.Contains("Contoso", StringComparison.Ordinal));
        Assert.True(
            builtStale == (superseded != SupersededRun.StopsAtItsHandshake),
            $"{superseded}: the superseded run {(builtStale ? "built" : "never built")} an agent from the old glossary.");

        // The contract: nothing built since the switch belongs to model-a, anything built from the
        // replaced options never made a call, the old glossary never reached the provider after the
        // change, and the only Ready after it is the successor's.
        var sinceSwitch = fake.Agents.Skip(builtForA).ToList();
        Assert.All(sinceSwitch, agent => Assert.Equal("model-b", agent.Model));
        var stale = sinceSwitch
            .Where(agent => agent.Instructions.Contains("Contoso", StringComparison.Ordinal))
            .Select(agent => agent.Build)
            .ToHashSet();
        Assert.DoesNotContain(fake.Client.Log, call => stale.Contains(call.Agent));
        Assert.DoesNotContain(
            fake.Client.Log.Skip(callsBeforeChange),
            call => call.Instructions.Contains("Contoso", StringComparison.Ordinal));
        Assert.Single(statuses.Snapshot(), s => s.Status == CleanupStatus.Ready);

        // The dictation and the per-app style dictation (built and cached after Ready) both ran on
        // agents built for model-b with the new glossary.
        Assert.Equal(CleanupOutcome.Cleaned, dictation.Outcome);
        Assert.Equal(CleanupOutcome.Cleaned, styled.Outcome);
        var served = fake.Client.Log
            .TakeLast(2)
            .Select(call => sinceSwitch.Single(agent => agent.Build == call.Agent))
            .ToList();
        Assert.All(served, agent =>
        {
            Assert.Equal("model-b", agent.Model);
            Assert.Contains("Fabrikam", agent.Instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("Contoso", agent.Instructions, StringComparison.Ordinal);
        });
        Assert.Contains(chatStyle, served[1].Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_prompt_change_arriving_while_disposal_drains_rebuilds_nothing()
    {
        await using var harness = new CleanupHarness();
        var fake = new FakeProvider();
        var svc = harness.Service;
        harness.DrainOnManualClock();

        // A dictation is in flight on a call that does not stop when asked, so disposal has to wait; the call is let go in
        // the finally too, since nothing times the drain out.
        var callStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            svc.ProviderFactoryForTesting = fake.Connect;
            svc.Configure(Remote(CleanupProvider.GitHubCopilot) with { Glossary = "Terms: Contoso." });
            await harness.WaitForStatusAsync(CleanupStatus.Ready);

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
        finally
        {
            release.TrySetResult();
        }
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
        using var releaseAtExit = new ReleaseAtExit(resume);
        var pausedOnce = 0;
        svc.StatusChanged += () =>
        {
            if (svc.Status == CleanupStatus.Ready && Interlocked.Exchange(ref pausedOnce, 1) == 0)
            {
                paused.Set();
                resume.Wait();
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
    /// and notes what each agent was built for, so every call can be traced to the agent that made it.
    /// </summary>
    private sealed class FakeProvider
    {
        private readonly ConcurrentQueue<BuiltAgent> _built = new();
        private readonly ConcurrentDictionary<int, HeldHandshake> _held = new();
        private int _handshakes;
        private int _builds;

        public RecordingChatClient Client { get; } = new();

        public int Handshakes => Volatile.Read(ref _handshakes);

        /// <summary>"model|instructions" for every agent built, in order.</summary>
        public IReadOnlyList<string> Built => _built.Select(agent => $"{agent.Model}|{agent.Instructions}").ToArray();

        /// <summary>Every agent built, in order.</summary>
        public IReadOnlyList<BuiltAgent> Agents => _built.ToArray();

        /// <summary>The 1-based build that throws, or 0 for none.</summary>
        public int ThrowOnBuild { get; init; }

        /// <summary>
        /// Holds the given 1-based handshake until the test releases it. A held handshake announces
        /// itself and then goes no further until <see cref="HeldHandshake.Release"/>, whatever its token
        /// says, and every test that holds one releases it. By default it then stops if it was cancelled
        /// while held. With <paramref name="finishesDespiteCancellation"/> it goes on however long before
        /// it was cancelled, the way a real handshake can complete just as its token is cancelled.
        /// </summary>
        public HeldHandshake Hold(int handshake, bool finishesDespiteCancellation = false) =>
            _held[handshake] = new HeldHandshake(finishesDespiteCancellation);

        public async Task<Func<string, AIAgent>> Connect(CleanupOptions options, CancellationToken cancellationToken)
        {
            var handshake = Interlocked.Increment(ref _handshakes);
            if (_held.TryGetValue(handshake, out var held))
            {
                await held.WaitAsync(cancellationToken);
            }

            var model = options.Provider == CleanupProvider.GitHubCopilot ? options.CopilotModel : options.AzureDeployment;
            return instructions =>
            {
                var build = Interlocked.Increment(ref _builds);
                if (build == ThrowOnBuild)
                {
                    throw new InvalidOperationException("Agent construction failed.");
                }

                _built.Enqueue(new BuiltAgent(build, model, instructions));
                return new ChatClientAgent(Client.For(build), instructions: instructions, name: "ScribeCleanup");
            };
        }
    }

    /// <summary>One agent a factory built: its 1-based build number, the model and the instructions.</summary>
    private sealed record BuiltAgent(int Build, string? Model, string Instructions);

    /// <summary>
    /// A handshake the test holds. <see cref="Started"/> is set once it has announced itself, and it goes
    /// no further until <see cref="Release"/>.
    /// </summary>
    private sealed class HeldHandshake(bool finishesDespiteCancellation)
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        /*
         * The wait on the token is registered before the handshake announces itself. Announcing first
         * was a race: a thread descheduled between the two reached the wait only after the test had
         * cancelled it and opened the gate, and Task.WaitAsync returns a task that has already completed
         * without looking at the token, so a superseded initialization got through and built an agent
         * from its old options. Every held handshake now runs that schedule on purpose: after announcing
         * itself it does nothing until the gate opens, as if descheduled, and only then looks at the wait
         * it registered, which kept any cancellation that arrived in between. Whether a superseded
         * handshake completes anyway is the test's choice, made explicit with finishesDespiteCancellation.
         */
        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            var released = finishesDespiteCancellation ? _gate.Task : _gate.Task.WaitAsync(cancellationToken);
            Started.TrySetResult();
            await _gate.Task;
            await released;
        }
    }

    /// <summary>One provider call: the build number of the agent that made it and the instructions it carried.</summary>
    private readonly record struct ProviderCall(int Agent, string Instructions);

    /// <summary>
    /// Answers every call and records, per call, the agent that made it and the instructions it carried.
    /// Agents call through <see cref="For"/>, so a call is attributed to its agent however alike two
    /// agents' instructions are.
    /// </summary>
    private sealed class RecordingChatClient
    {
        private readonly ConcurrentQueue<ProviderCall> _log = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<string> Instructions => _log.Select(call => call.Instructions).ToArray();

        /// <summary>Every call, in order.</summary>
        public IReadOnlyList<ProviderCall> Log => _log.ToArray();

        /// <summary>Runs before answering, with the 1-based call number.</summary>
        public Func<int, CancellationToken, Task>? BeforeAnswer { get; set; }

        public IChatClient For(int agent) => new AgentClient(this, agent);

        private async Task<ChatResponse> AnswerAsync(
            int agent, IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
        {
            var system = messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text);
            _log.Enqueue(new ProviderCall(agent, string.Join("\n", system.Prepend(options?.Instructions ?? string.Empty))));
            var call = Interlocked.Increment(ref _calls);
            if (BeforeAnswer is { } before)
            {
                await before(call, cancellationToken);
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, CleanedText));
        }

        private sealed class AgentClient(RecordingChatClient owner, int agent) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                owner.AnswerAsync(agent, messages, options, cancellationToken);

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}
