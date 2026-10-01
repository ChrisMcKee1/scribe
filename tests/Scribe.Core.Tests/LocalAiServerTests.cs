using System.Text.Json;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// A server on this PC (Ollama, LM Studio, llama.cpp's server on localhost) is a model on this PC, and gets what Foundry
/// Local gets: the short instructions under Automatic, their 80-term glossary budget, and a low temperature, plus thinking
/// turned off. Measured against Ollama 0.34.4 with a fresh install's two word packs: the detailed instructions and the
/// whole glossary came to 7,745 tokens, Ollama cut them to the last 2,050 of its 4,096-token context, and the model never
/// saw an instruction. Thinking models on Ollama also ignore /no_think and think for thousands of tokens.
/// </summary>
public sealed class LocalAiServerTests
{
    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://LOCALHOST:1234/v1", true)]
    [InlineData("http://127.0.0.1:1234/v1", true)]
    [InlineData("http://127.4.5.6:8080/v1", true)]
    [InlineData("http://[::1]:11434/v1", true)]
    [InlineData("https://localhost:8443/v1", true)]
    [InlineData("http://ollama.localhost:11434/v1", true)]
    [InlineData(" http://localhost:11434/v1 ", true)]
    [InlineData("http://192.168.1.20:11434/v1", false)]
    [InlineData("http://10.0.0.5:1234/v1", false)]
    [InlineData("https://openrouter.ai/api/v1", false)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("http://localhost.example.com/v1", false)]
    [InlineData("ftp://localhost/v1", false)]
    [InlineData("localhost:11434", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_an_address_on_this_PC_counts(string? endpoint, bool expected) =>
        Assert.Equal(expected, LocalAiServer.IsOnThisPc(endpoint));

    [Theory]
    [InlineData(CleanupProvider.OpenAiCompatible, "http://localhost:11434/v1", CleanupPromptStyle.Auto, CleanupPromptStyle.Local)]
    [InlineData(CleanupProvider.OpenAiCompatible, "http://127.0.0.1:1234/v1", CleanupPromptStyle.Auto, CleanupPromptStyle.Local)]
    [InlineData(CleanupProvider.OpenAiCompatible, "https://openrouter.ai/api/v1", CleanupPromptStyle.Auto, CleanupPromptStyle.Frontier)]
    [InlineData(CleanupProvider.OpenAiCompatible, "http://192.168.1.20:11434/v1", CleanupPromptStyle.Auto, CleanupPromptStyle.Frontier)]
    [InlineData(CleanupProvider.OpenAiCompatible, null, CleanupPromptStyle.Auto, CleanupPromptStyle.Frontier)]
    [InlineData(CleanupProvider.OpenAiCompatible, "http://localhost:11434/v1", CleanupPromptStyle.Frontier, CleanupPromptStyle.Frontier)]
    [InlineData(CleanupProvider.AzureFoundry, "http://localhost:11434/v1", CleanupPromptStyle.Auto, CleanupPromptStyle.Frontier)]
    [InlineData(CleanupProvider.FoundryLocal, null, CleanupPromptStyle.Auto, CleanupPromptStyle.Local)]
    public void Automatic_gives_a_server_on_this_PC_the_short_instructions(
        CleanupProvider provider, string? endpoint, CleanupPromptStyle style, CleanupPromptStyle expected)
    {
        Assert.Equal(expected, CleanupPrompt.ResolvePromptStyle(style, provider, endpoint));
        Assert.Equal(
            expected == CleanupPromptStyle.Local ? CleanupPrompt.MaxGlossaryTermsLocal : CleanupPrompt.MaxGlossaryTermsCloud,
            CleanupPrompt.GlossaryTermBudget(style, provider, endpoint));
    }

    [Fact]
    public void A_server_on_this_PC_gets_the_short_instructions_and_the_small_glossary()
    {
        var entries = Enumerable.Range(0, 400).Select(i => DictionaryEntry.New($"spoken term {i}", $"Term{i}")).ToList();
        var options = CleanupHarness.Custom("http://localhost:11434/v1") with
        {
            Glossary = CleanupPrompt.BuildGlossary(entries, CleanupPrompt.GlossaryTermBudget(
                CleanupPromptStyle.Auto, CleanupProvider.OpenAiCompatible, "http://localhost:11434/v1")),
        };

        var prompt = TextCleanupService.BuildSystemPrompt(options);

        Assert.StartsWith(CleanupPrompt.DefaultLocalPrompt, prompt, StringComparison.Ordinal);
        Assert.Contains("Term79", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Term80", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_on_this_PC_is_sent_a_low_temperature_and_asked_not_to_think()
    {
        var bodies = await CaptureRequestsAsync(CleanupHarness.Custom("http://localhost:11434/v1", "qwen3.5:2b"));

        Assert.All(bodies, body =>
        {
            Assert.Equal(0.1, body.GetProperty("temperature").GetDouble(), 3);
            Assert.Equal("none", body.GetProperty("reasoning_effort").GetString());

            // Ollama and LM Studio read the legacy name, and Ollama ignores the one the SDK sends.
            Assert.Equal(
                body.GetProperty("max_completion_tokens").GetInt32(),
                body.GetProperty("max_tokens").GetInt32());
        });
        Assert.StartsWith(CleanupPrompt.DefaultLocalPrompt, SystemMessage(bodies[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_remote_server_keeps_its_own_temperature_and_reasoning()
    {
        var bodies = await CaptureRequestsAsync(CleanupHarness.Custom("https://ai.example.invalid/v1", "some-model"));

        Assert.All(bodies, body =>
        {
            Assert.False(body.TryGetProperty("temperature", out _));
            Assert.False(body.TryGetProperty("reasoning_effort", out _));
            Assert.False(body.TryGetProperty("max_tokens", out _));
        });
        Assert.StartsWith(CleanupPrompt.DefaultFrontierPrompt, SystemMessage(bodies[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public void The_dictionary_page_counts_the_budget_a_server_on_this_PC_gets()
    {
        var rows = Enumerable.Range(0, 200)
            .Select(i => new DictionaryEntryBuilder.Row(0, $"spoken term {i}", $"Term{i}", true, true))
            .ToList();

        string Describe(string endpoint) => GlossaryHint.Describe(new GlossaryHint.Input(
            rows, [], AiCleanupOn: true, PostProcessingOn: true, CleanupProvider.OpenAiCompatible, CleanupPromptStyle.Auto,
            CustomEndpoint: endpoint));

        Assert.Contains($"a request holds up to {CleanupPrompt.MaxGlossaryTermsLocal} words or phrases", Describe("http://localhost:1234/v1"), StringComparison.Ordinal);
        Assert.Contains("The AI model on this PC receives", Describe("http://localhost:1234/v1"), StringComparison.Ordinal);
        Assert.Contains("Your AI service receives whichever of these 200 words", Describe("https://openrouter.ai/api/v1"), StringComparison.Ordinal);
        Assert.DoesNotContain("a request holds up to", Describe("https://openrouter.ai/api/v1"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Starting_a_dictation_readies_an_idle_server_on_this_PC_with_the_dictation_s_own_instructions()
    {
        var vocabulary = new CleanupVocabulary([DictionaryEntry.New("kes trel", "Kestrel")], Scribe.Core.Libraries.AiVocabularyScope.None);
        var bodies = new List<JsonElement>();
        await using var harness = new CleanupHarness(http: Capturing(bodies));
        harness.Service.Configure(CleanupHarness.Custom("http://127.0.0.1:11434/v1", "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // The readiness probe has just answered, so the model is loaded: nothing to ready.
        harness.Service.Admit(vocabulary).Prewarm();
        await harness.Service.WaitForPrewarmForTesting();
        Assert.Single(Snapshot(bodies));

        harness.Service.ForgetLastModelAnswerForTesting();
        harness.Service.Admit(vocabulary).Prewarm();
        await harness.Service.WaitForPrewarmForTesting();

        var prewarm = Snapshot(bodies)[^1];
        Assert.Equal(1, prewarm.GetProperty("max_tokens").GetInt32());
        Assert.Equal("none", prewarm.GetProperty("reasoning_effort").GetString());
        Assert.Equal($"{TextCleanupService.TranscriptOpenTag}\n\n{TextCleanupService.TranscriptCloseTag}", UserMessage(prewarm));

        // The readying request carries the instructions and writing style and never vocabulary, and they are exactly the
        // start of every dictation's prompt, so the server's cached prefix is the part each dictation shares.
        Assert.DoesNotContain("Kestrel", SystemMessage(prewarm), StringComparison.Ordinal);
        var result = await harness.Service.Admit(vocabulary).CleanAsync("um so we need to uh ship the build by friday no thursday");
        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
        var dictation = SystemMessage(Snapshot(bodies)[^1]);
        Assert.StartsWith(SystemMessage(prewarm), dictation, StringComparison.Ordinal);
        Assert.Contains("Kestrel", dictation, StringComparison.Ordinal);

        // It answered moments ago, so the next dictation's start sends nothing.
        var count = Snapshot(bodies).Count;
        harness.Service.Admit(vocabulary).Prewarm();
        await harness.Service.WaitForPrewarmForTesting();
        Assert.Equal(count, Snapshot(bodies).Count);
    }

    [Fact]
    public async Task Each_dictation_carries_only_the_vocabulary_it_appears_to_mention()
    {
        // The product's mode (DictationController.BuildCleanupOptions): a word pack term goes with the dictation that says
        // it, heard exactly or slightly differently, and stays home otherwise.
        var vocabulary = new CleanupVocabulary(
            [DictionaryEntry.New("kes trel", "Kestrel"), DictionaryEntry.New("cosmos d b", "Cosmos DB")],
            Scribe.Core.Libraries.AiVocabularyScope.None);
        var bodies = new List<JsonElement>();
        await using var harness = new CleanupHarness(http: Capturing(bodies));
        harness.Service.Configure(CleanupHarness.Custom("https://ai.example.invalid/v1", "some-model") with
        {
            VocabularyMode = CleanupVocabularyMode.Mentioned,
        });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        await harness.Service.Admit(vocabulary).CleanAsync("so the kestrel server is up and we need to ship by thursday");
        var mentioned = SystemMessage(Snapshot(bodies)[^1]);
        Assert.Contains("Kestrel", mentioned, StringComparison.Ordinal);
        Assert.DoesNotContain("Cosmos DB", mentioned, StringComparison.Ordinal);

        await harness.Service.Admit(vocabulary).CleanAsync("please send the report to the team before friday afternoon");
        var none = SystemMessage(Snapshot(bodies)[^1]);
        Assert.DoesNotContain("Kestrel", none, StringComparison.Ordinal);
        Assert.DoesNotContain("Cosmos DB", none, StringComparison.Ordinal);
    }

    [Fact]
    public void The_controller_asks_for_the_vocabulary_each_dictation_mentions()
    {
        var controller = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Scribe.App", "Dictation", "DictationController.cs"));
        var start = controller.IndexOf("private CleanupOptions BuildCleanupOptions(AppSettings settings)", StringComparison.Ordinal);
        var end = controller.IndexOf(");", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "BuildCleanupOptions was not found.");
        Assert.Contains("VocabularyMode: CleanupVocabularyMode.Mentioned,", controller[start..end], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://ai.example.invalid/v1")]
    [InlineData("https://192.168.1.20:11434/v1")]
    public async Task Nothing_is_readied_anywhere_but_on_this_PC(string endpoint)
    {
        var bodies = new List<JsonElement>();
        await using var harness = new CleanupHarness(http: Capturing(bodies));
        harness.Service.Configure(CleanupHarness.Custom(endpoint, "some-model"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var afterProbe = Snapshot(bodies).Count;

        harness.Service.ForgetLastModelAnswerForTesting();
        harness.Service.Admit(CleanupVocabulary.None).Prewarm();
        await harness.Service.WaitForPrewarmForTesting();

        Assert.Equal(afterProbe, Snapshot(bodies).Count);
    }

    [Theory]
    [InlineData("reasoning_effort")]
    [InlineData("max_tokens")]
    public async Task A_server_on_this_PC_that_refuses_the_extra_fields_gets_plain_requests(string refusedField)
    {
        // A strict validator (vLLM allowed only low, medium or high for reasoning_effort) answers 400 to a field Ollama and
        // LM Studio accept; cleanup must still start, with the fields left out, rather than fail every dictation.
        var bodies = new List<JsonElement>();
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var json = await request.Content!.ReadAsStringAsync(ct);
            var body = JsonDocument.Parse(json).RootElement.Clone();
            lock (bodies)
            {
                bodies.Add(body);
            }

            return body.TryGetProperty(refusedField, out _)
                ? ScriptedHttpHandler.Json(System.Net.HttpStatusCode.BadRequest,
                    $$$"""{"error":{"message":"{{{refusedField}}}: Input should be 'low', 'medium' or 'high'","type":"BadRequestError"}}""")
                : ScriptedHttpHandler.ChatCompletion("We need to ship the build by Thursday.");
        });

        await using var harness = new CleanupHarness(http: http);
        harness.Service.Configure(CleanupHarness.Custom("http://127.0.0.1:8000/v1", "some-local-model"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var result = await harness.Service.CleanAsync("um so we need to uh ship the build by friday no thursday");
        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);

        var last = Snapshot(bodies)[^1];
        Assert.False(last.TryGetProperty("reasoning_effort", out _));
        Assert.False(last.TryGetProperty("max_tokens", out _));
        Assert.Equal(0.1, last.GetProperty("temperature").GetDouble(), 3);
    }

    [Fact]
    public async Task A_server_on_this_PC_that_refuses_every_request_keeps_its_first_failure()
    {
        var bodies = new List<JsonElement>();
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var json = await request.Content!.ReadAsStringAsync(ct);
            lock (bodies)
            {
                bodies.Add(JsonDocument.Parse(json).RootElement.Clone());
            }

            return ScriptedHttpHandler.Json(System.Net.HttpStatusCode.BadRequest,
                """{"error":{"message":"model 'missing-model' not found","type":"invalid_request_error"}}""");
        });

        await using var harness = new CleanupHarness(http: http);
        harness.Service.Configure(CleanupHarness.Custom("http://localhost:11434/v1", "missing-model"));
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);

        // The probe with the fields and one plain probe, and no more.
        Assert.Equal(2, Snapshot(bodies).Count);
        Assert.True(Snapshot(bodies)[0].TryGetProperty("reasoning_effort", out _));
        Assert.False(Snapshot(bodies)[1].TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void The_privacy_statement_and_the_card_describe_the_readying_request()
    {
        Assert.Contains(CleanupDisclosure.ReadiesALocalServer, CleanupDisclosure.WhatCleanupNeverSends, StringComparison.Ordinal);
        Assert.Contains($"the last {LocalAiServer.PrewarmAfterIdleSeconds} seconds", CleanupDisclosure.ReadiesALocalServer, StringComparison.Ordinal);

        var policy = File.ReadAllText(Path.Combine(RepoRoot(), "PRIVACY.md")).ReplaceLineEndings(" ");
        while (policy.Contains("  ", StringComparison.Ordinal))
        {
            policy = policy.Replace("  ", " ", StringComparison.Ordinal);
        }

        Assert.Contains("starting a dictation also sends that server the cleanup instructions with no dictated text, unless it answered in the last", policy, StringComparison.Ordinal);
        Assert.Contains($"unless it answered in the last {LocalAiServer.PrewarmAfterIdleSeconds} seconds and still holds the model", policy, StringComparison.Ordinal);
        Assert.Contains($"unless it answered in the last {LocalAiServer.PrewarmAfterIdleSeconds} seconds and still holds the model", CleanupDisclosure.ReadiesALocalServer, StringComparison.Ordinal);
        Assert.Contains("It goes only to that server on this PC, never to a service elsewhere.", policy, StringComparison.Ordinal);
        Assert.Contains("none of your vocabulary", CleanupDisclosure.ReadiesALocalServer, StringComparison.Ordinal);

        // With the whole vocabulary on, the readying request carries the leading run of it that fits (GlossaryForLocked),
        // and every request to a model on this PC may carry all of it: said on the card and in the policy.
        Assert.Contains("It carries none of your vocabulary, unless \"Send your whole vocabulary when it fits\" is on for that app, when it also carries as much of your vocabulary as fits.", policy, StringComparison.Ordinal);
        Assert.Contains($"\"{LocalModelTuningText.WholeVocabularyTitle}\" is on for that app, when it carries as much of it as fits.", CleanupDisclosure.ReadiesALocalServer, StringComparison.Ordinal);
        Assert.Contains(CleanupDisclosure.WholeVocabularyOnThisPc, CleanupDisclosure.WhatCleanupNeverSends, StringComparison.Ordinal);
        Assert.Contains("None of it leaves this PC.", CleanupDisclosure.WholeVocabularyOnThisPc, StringComparison.Ordinal);
        Assert.Contains("With \"Send your whole vocabulary when it fits\" on for that app, each cleanup request carries all of the vocabulary described below", policy, StringComparison.Ordinal);
        Assert.Contains("Every request to a model on this PC is also kept to what the model's context holds, the dictation first. None of this leaves this PC.", policy, StringComparison.Ordinal);

        // LM Studio loading the model at a chosen size, through its own chat API with the word "ok".
        Assert.Contains("to load the model at that size with a request holding the word \"ok\"", CleanupDisclosure.ManagesALocalApp, StringComparison.Ordinal);
        Assert.Contains("With a context size chosen for LM Studio, Scribe asks LM Studio to load the model at that size with a request holding the word \"ok\", which it asks LM Studio not to keep, and frees that copy itself after the time you set without a dictation, and when Scribe closes, unless that time is Never.", policy, StringComparison.Ordinal);
        Assert.Contains("Test connection loads the model that way too, to check that size, and Scribe frees a copy it loaded only for a test, or for settings you have since changed, once it is not needed, whatever that time.", policy, StringComparison.Ordinal);

        // What Scribe asks Ollama or LM Studio itself, as each dictation starts too: disclosed on the card and in the policy,
        // and it carries nothing said, only a key saved for that address (an earlier one only to free a copy loaded with it).
        Assert.Contains(CleanupDisclosure.ManagesALocalApp, CleanupDisclosure.WhatCleanupNeverSends, StringComparison.Ordinal);
        Assert.Contains("including as each dictation starts, which models it has, which it holds in memory and how much each reads at once", CleanupDisclosure.ManagesALocalApp, StringComparison.Ordinal);
        Assert.Contains("including as each dictation starts, which models it has, which it holds in memory and how much each reads at once", policy, StringComparison.Ordinal);
        Assert.Contains("carry nothing you said", policy, StringComparison.Ordinal);
        Assert.Contains("asks it to free a model's memory when AI cleanup stops using the model", policy, StringComparison.Ordinal);
        Assert.Contains("only an API key you saved for that address, if any", policy, StringComparison.Ordinal);
        Assert.Contains("to free a copy Scribe loaded with a key you have since replaced, that earlier key", policy, StringComparison.Ordinal);
        Assert.Contains("only an API key you saved for that address, if any", CleanupDisclosure.ManagesALocalApp, StringComparison.Ordinal);
    }

    private static ScriptedHttpHandler Capturing(List<JsonElement> bodies) => new(async (request, ct) =>
    {
        var json = await request.Content!.ReadAsStringAsync(ct);
        lock (bodies)
        {
            bodies.Add(JsonDocument.Parse(json).RootElement.Clone());
        }

        return ScriptedHttpHandler.ChatCompletion("We need to ship the build by Thursday.");
    });

    private static List<JsonElement> Snapshot(List<JsonElement> bodies)
    {
        lock (bodies)
        {
            return [.. bodies];
        }
    }

    private static string UserMessage(JsonElement body) =>
        body.GetProperty("messages").EnumerateArray()
            .Last(message => message.GetProperty("role").GetString() == "user")
            .GetProperty("content").GetString()!;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scribe.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    private static async Task<List<JsonElement>> CaptureRequestsAsync(CleanupOptions options)
    {
        var bodies = new List<JsonElement>();
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var json = await request.Content!.ReadAsStringAsync(ct);
            lock (bodies)
            {
                bodies.Add(JsonDocument.Parse(json).RootElement.Clone());
            }

            return ScriptedHttpHandler.ChatCompletion("We need to ship the build by Thursday.");
        });

        await using var harness = new CleanupHarness(http: http);
        harness.Service.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var result = await harness.Service.CleanAsync("um so we need to uh ship the build by friday no thursday");
        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);

        lock (bodies)
        {
            // The readiness probe and the dictation.
            Assert.True(bodies.Count >= 2, $"Expected the probe and a cleanup request, saw {bodies.Count}.");
            return [.. bodies];
        }
    }

    private static string SystemMessage(JsonElement body) =>
        body.GetProperty("messages").EnumerateArray()
            .First(message => message.GetProperty("role").GetString() == "system")
            .GetProperty("content").GetString()!;
}
