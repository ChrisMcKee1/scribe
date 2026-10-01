using System.Net;
using System.Text.Json;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

/// <summary>
/// A model on this PC through the service: Ollama's own API with the size Scribe asks for, LM Studio loading the model at a
/// size and Scribe freeing that copy itself, Foundry Local's size read from its model, and every request's vocabulary
/// fitted into the context the app said it loaded the model with, the dictated text first.
/// </summary>
public sealed class LocalContextServiceTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);
    private const string OllamaModel = "gemma4:e4b";
    private const string LmStudioModel = "google/gemma-4-e2b";

    [Fact]
    public async Task Ollama_s_own_API_asks_for_the_size_on_every_request_with_the_rest_of_the_tuning()
    {
        var requests = new List<(string Path, JsonElement Body)>();
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            lock (requests)
            {
                requests.Add((request.RequestUri!.AbsolutePath, body));
            }

            return OllamaAnswer("We need to ship the build by Thursday.");
        });
        await using var harness = new CleanupHarness(http: http);
        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.OllamaAddress, OllamaModel) with
        {
            LocalContextTokens = 32768,
            LocalModelKeepAliveMinutes = 10,
        });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var result = await harness.Service.CleanAsync("um so we need to uh ship the build by friday no thursday").WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
        Assert.Equal("We need to ship the build by Thursday.", result.Text);
        var sent = Snapshot(requests);
        Assert.True(sent.Count >= 2, $"Expected the readiness check and the dictation, saw {sent.Count}.");
        Assert.All(sent, request =>
        {
            Assert.Equal("/api/chat", request.Path);
            var body = request.Body;
            Assert.Equal(OllamaModel, body.GetProperty("model").GetString());
            Assert.False(body.GetProperty("stream").GetBoolean());
            Assert.False(body.GetProperty("think").GetBoolean());
            Assert.Equal("10m", body.GetProperty("keep_alive").GetString());
            var options = body.GetProperty("options");
            Assert.Equal(32768, options.GetProperty("num_ctx").GetInt32());
            Assert.Equal(0.1, options.GetProperty("temperature").GetDouble(), 3);
            Assert.True(options.GetProperty("num_predict").GetInt32() > 0);
        });
        Assert.StartsWith(CleanupPrompt.DefaultLocalPrompt, ChatSystemMessage(sent[^1].Body), StringComparison.Ordinal);
        Assert.Equal(32768, harness.Service.LocalContextTokens);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "The AI service could not find that model (404). Check the model name.")]
    [InlineData(HttpStatusCode.InternalServerError, "The AI service returned a server error (500). This is usually transient.")]
    public async Task An_error_Ollama_answers_on_its_own_API_is_reported_by_its_status_not_as_a_network_failure(
        HttpStatusCode status, string reason)
    {
        // OllamaSharp turns Ollama's error answer into EnsureSuccessStatusCode's HttpRequestException, which carries the
        // status: a running Ollama without the model must not read as "Couldn't reach the AI service".
        await using var harness = new CleanupHarness(http: new ScriptedHttpHandler((_, _) =>
            Task.FromResult(ScriptedHttpHandler.Json(status, """{"error":"model 'missing:1b' not found"}"""))));
        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.OllamaAddress, "missing:1b") with { LocalContextTokens = 8192 });

        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);

        Assert.StartsWith(reason, harness.Service.StatusReason, StringComparison.Ordinal);
        Assert.Equal((int)status, CleanupFailureShape.ExtractHttpStatus(new HttpRequestException("x", null, status)));
        Assert.Contains($"status={(int)status}", CleanupFailureShape.Describe(new HttpRequestException("x", null, status)), StringComparison.Ordinal);
        Assert.DoesNotContain("kind=connectivity", TextCleanupService.DescribeFailureShape(new HttpRequestException("x", null, status)), StringComparison.Ordinal);
        Assert.Contains("kind=connectivity", TextCleanupService.DescribeFailureShape(new HttpRequestException("refused")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ollama_s_setting_sends_requests_to_its_OpenAI_compatible_API_with_no_size()
    {
        var requests = new List<(string Path, JsonElement Body)>();
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            lock (requests)
            {
                requests.Add((request.RequestUri!.AbsolutePath, body));
            }

            return ScriptedHttpHandler.ChatCompletion("Ok.");
        });
        await using var harness = new CleanupHarness(http: http);
        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.OllamaAddress, OllamaModel));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var sent = Snapshot(requests);
        Assert.NotEmpty(sent);
        Assert.All(sent, request =>
        {
            Assert.EndsWith("/chat/completions", request.Path, StringComparison.Ordinal);
            Assert.False(request.Body.TryGetProperty("options", out _));
            Assert.False(request.Body.TryGetProperty("num_ctx", out _));
        });
    }

    [Fact]
    public async Task The_whole_vocabulary_goes_when_the_context_the_app_loaded_holds_it()
    {
        var entries = Vocabulary(400);
        var bodies = new List<JsonElement>();
        await using var harness = OllamaLoadedAt(65536, bodies);
        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.OllamaAddress, OllamaModel) with
        {
            VocabularyMode = CleanupVocabularyMode.Mentioned,
            SendWholeVocabulary = true,
        });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(65536, harness.Service.LocalContextTokens);

        await harness.Service.Admit(Admitted(entries)).CleanAsync("please send the report today").WaitAsync(Bound);

        Assert.Contains(CleanupPrompt.BuildGlossary(entries), SystemMessage(Snapshot(bodies)[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_small_context_keeps_the_dictation_s_terms_then_fills_the_rest_from_the_start()
    {
        var entries = Vocabulary(1000);
        var mentioned = entries[900];
        var bodies = new List<JsonElement>();
        await using var harness = OllamaLoadedAt(4096, bodies);
        var options = CleanupHarness.Custom(LocalAiServer.OllamaAddress, OllamaModel) with
        {
            VocabularyMode = CleanupVocabularyMode.Mentioned,
            SendWholeVocabulary = true,
        };
        harness.Service.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var text = $"please send the {mentioned.Pattern} report today";
        await harness.Service.Admit(Admitted(entries)).CleanAsync(text).WaitAsync(Bound);

        var request = Snapshot(bodies)[^1];
        var system = SystemMessage(request);
        Assert.Contains(Line(entries[0]), system, StringComparison.Ordinal);
        Assert.Contains(Line(mentioned), system, StringComparison.Ordinal);
        Assert.DoesNotContain(Line(entries[899]), system, StringComparison.Ordinal);

        // The whole request, as counted, fits in the context with the longest answer it lets the model give.
        var instructions = TextCleanupService.BuildProbeSystemPrompt(options);
        var vocabulary = system[(instructions.Length + 2)..];
        var declared = request.GetProperty("max_tokens").GetInt32();
        Assert.Equal(TextCleanupService.EstimateMaxTokens(text, CleanupProvider.OpenAiCompatible), declared);
        Assert.True(
            ContextBudget.ChatTemplateTokens + TokenEstimate.Prose(instructions) + TokenEstimate.Vocabulary(vocabulary) +
                TokenEstimate.Transcript(text) + declared + ContextBudget.Margin(4096) <= 4096,
            "The request and its longest answer run past the context the app loaded the model with.");
    }

    [Fact]
    public async Task Without_the_whole_vocabulary_a_model_on_this_PC_gets_only_what_the_dictation_mentions()
    {
        var entries = Vocabulary(400);
        var bodies = new List<JsonElement>();
        await using var harness = OllamaLoadedAt(65536, bodies);
        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.OllamaAddress, OllamaModel) with
        {
            VocabularyMode = CleanupVocabularyMode.Mentioned,
        });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        await harness.Service.Admit(Admitted(entries)).CleanAsync($"the {entries[300].Pattern} launch").WaitAsync(Bound);

        var system = SystemMessage(Snapshot(bodies)[^1]);
        Assert.Contains(Line(entries[300]), system, StringComparison.Ordinal);
        Assert.DoesNotContain(Line(entries[0]), system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_dictation_leaves_less_room_for_vocabulary_than_a_short_one()
    {
        var entries = Vocabulary(1000);
        var bodies = new List<JsonElement>();
        await using var harness = OllamaLoadedAt(4096, bodies);
        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.OllamaAddress, OllamaModel) with
        {
            VocabularyMode = CleanupVocabularyMode.Mentioned,
            SendWholeVocabulary = true,
        });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        await harness.Service.Admit(Admitted(entries)).CleanAsync("ship it today").WaitAsync(Bound);
        var shortLines = GlossaryLines(SystemMessage(Snapshot(bodies)[^1]));
        var longText = string.Join(' ', Enumerable.Repeat("we need to ship the build by thursday and tell the team", 30));
        await harness.Service.Admit(Admitted(entries)).CleanAsync(longText).WaitAsync(Bound);
        var longLines = GlossaryLines(SystemMessage(Snapshot(bodies)[^1]));

        Assert.True(longLines > 0);
        Assert.True(shortLines > longLines, $"A short dictation carried {shortLines} terms and a long one {longLines}.");
    }

    [Fact]
    public async Task The_readying_request_carries_the_start_of_the_whole_vocabulary_that_the_dictation_then_shares()
    {
        var entries = Vocabulary(1000);
        var bodies = new List<JsonElement>();
        await using var harness = OllamaLoadedAt(8192, bodies);
        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.OllamaAddress, OllamaModel) with
        {
            VocabularyMode = CleanupVocabularyMode.Mentioned,
            SendWholeVocabulary = true,
        });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var admitted = Admitted(entries);
        harness.Service.ForgetLastModelAnswerForTesting();
        harness.Service.Admit(admitted).Prewarm();
        await harness.Service.WaitForPrewarmForTesting().WaitAsync(Bound);
        var readying = SystemMessage(Snapshot(bodies)[^1]);
        Assert.Contains(Line(entries[0]), readying, StringComparison.Ordinal);

        await harness.Service.Admit(admitted).CleanAsync("ship it today").WaitAsync(Bound);
        var dictation = SystemMessage(Snapshot(bodies)[^1]);

        // Everything up to the readied run's tenth line is the same text, so the app reads only what follows.
        var tenth = readying.IndexOf(Line(entries[9]), StringComparison.Ordinal);
        Assert.True(tenth > 0);
        Assert.Equal(readying[..tenth], dictation[..tenth]);
    }

    [Fact]
    public async Task LM_Studio_loads_the_model_at_the_chosen_size_before_the_readiness_check()
    {
        var loadsWhenFirstAsked = -1;
        FakeLocalServerClient? servers = null;
        await using var harness = new CleanupHarness(http: new ScriptedHttpHandler((_, _) =>
        {
            Interlocked.CompareExchange(ref loadsWhenFirstAsked, servers!.Loads.Count, -1);
            return Task.FromResult(ScriptedHttpHandler.ChatCompletion("ok"));
        }));
        servers = harness.LocalServers;
        LmStudioHolds(harness, maxContext: 131072);

        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.LmStudioAddress, LmStudioModel) with { LocalContextTokens = 16384 });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal([(LocalAiServer.LmStudioAddress, LmStudioModel, 16384)], harness.LocalServers.Loads);
        Assert.Equal(1, Volatile.Read(ref loadsWhenFirstAsked));
        Assert.Equal(16384, harness.Service.LocalContextTokens);
    }

    [Fact]
    public async Task Scribe_frees_the_copy_it_loaded_at_a_size_after_the_idle_time_and_only_that_copy()
    {
        await using var harness = new CleanupHarness();
        LmStudioHolds(harness, maxContext: 131072);
        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.LmStudioAddress, LmStudioModel) with
        {
            LocalContextTokens = 16384,
            LocalModelKeepAliveMinutes = 10,
        });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        harness.Service.ReleaseModelMemory(ModelMemoryRelease.Idle);

        await WaitUntilAsync(() => harness.LocalServers.InstanceUnloads.Count == 1);
        Assert.Equal([(LocalAiServer.LmStudioAddress, LmStudioModel)], harness.LocalServers.InstanceUnloads);
        Assert.Empty(harness.LocalServers.Unloads);
    }

    [Fact]
    public async Task The_copy_Scribe_loaded_at_a_size_is_freed_as_Scribe_closes_and_nothing_else_is()
    {
        var harness = new CleanupHarness();
        LmStudioHolds(harness, maxContext: 131072);
        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.LmStudioAddress, LmStudioModel) with
        {
            LocalContextTokens = 16384,
            LocalModelKeepAliveMinutes = 10,
            CustomApiKey = "lm-token",
        });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Empty(harness.LocalServers.InstanceUnloads);

        await harness.DisposeAsync();

        Assert.Equal([(LocalAiServer.LmStudioAddress, LmStudioModel)], harness.LocalServers.InstanceUnloads);
        Assert.Equal("lm-token", harness.LocalServers.Keys[^1]);
        Assert.Empty(harness.LocalServers.Unloads);
    }

    [Fact]
    public async Task With_Never_the_copy_Scribe_loaded_is_left_to_LM_Studio_as_Scribe_closes()
    {
        var harness = new CleanupHarness();
        LmStudioHolds(harness, maxContext: 131072);
        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.LmStudioAddress, LmStudioModel) with
        {
            LocalContextTokens = 16384,
            LocalModelKeepAliveMinutes = 0,
        });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        await harness.DisposeAsync();

        Assert.Single(harness.LocalServers.Loads);
        Assert.Empty(harness.LocalServers.InstanceUnloads);
        Assert.Empty(harness.LocalServers.Unloads);
    }

    [Theory]
    [InlineData(LocalAiServer.LmStudioAddress, LmStudioModel)]
    [InlineData(LocalAiServer.OllamaAddress, OllamaModel)]
    public async Task A_model_the_app_loaded_on_demand_is_left_to_its_idle_time_as_Scribe_closes(string endpoint, string model)
    {
        var harness = new CleanupHarness();
        LmStudioHolds(harness, maxContext: 131072);
        harness.Service.Configure(CleanupHarness.Custom(endpoint, model) with { LocalModelKeepAliveMinutes = 10 });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        await harness.DisposeAsync();

        Assert.Empty(harness.LocalServers.Loads);
        Assert.Empty(harness.LocalServers.InstanceUnloads);
        Assert.Empty(harness.LocalServers.Unloads);
    }

    [Fact]
    public async Task Without_a_size_LM_Studio_frees_the_model_on_Scribe_s_idle_time_itself()
    {
        await using var harness = new CleanupHarness();
        LmStudioHolds(harness, maxContext: 131072);
        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.LmStudioAddress, LmStudioModel) with
        {
            LocalModelKeepAliveMinutes = 10,
        });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        harness.Service.ReleaseModelMemory(ModelMemoryRelease.Idle);
        await Task.Delay(200);

        Assert.Empty(harness.LocalServers.Loads);
        Assert.Empty(harness.LocalServers.InstanceUnloads);
        Assert.Empty(harness.LocalServers.Unloads);
    }

    [Fact]
    public async Task A_copy_loaded_by_hand_is_used_at_its_own_size_and_left_alone()
    {
        await using var harness = new CleanupHarness();
        harness.LocalServers.State = new LocalServerState(
            LocalServerReach.Reached,
            [new LocalServerModel(LmStudioModel, "Gemma 4 E2B", 4_000_000_000) { MaxContextTokens = 131072 }],
            [new LocalServerLoadedModel(LmStudioModel, 4_000_000_000) { ContextTokens = 4096, InstanceId = LmStudioModel }]);
        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.LmStudioAddress, LmStudioModel) with
        {
            LocalContextTokens = 16384,
            LocalModelKeepAliveMinutes = 10,
        });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Empty(harness.LocalServers.Loads);
        Assert.Equal(4096, harness.Service.LocalContextTokens);

        harness.Service.ReleaseModelMemory(ModelMemoryRelease.Idle);
        await Task.Delay(200);
        Assert.Empty(harness.LocalServers.InstanceUnloads);
        Assert.Empty(harness.LocalServers.Unloads);
    }

    [Fact]
    public async Task A_copy_LM_Studio_loaded_on_demand_at_another_size_is_loaded_again_at_the_chosen_size()
    {
        await using var harness = new CleanupHarness();
        harness.LocalServers.State = new LocalServerState(
            LocalServerReach.Reached,
            [new LocalServerModel(LmStudioModel, "Gemma 4 E2B", 4_000_000_000) { MaxContextTokens = 131072 }],
            [new LocalServerLoadedModel(LmStudioModel, 4_000_000_000)
            {
                ContextTokens = 4096,
                InstanceId = LmStudioModel,
                RemainingTtlSeconds = 3600,
            }]);
        harness.LocalServers.StateAfterLoad = LoadedAt;
        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.LmStudioAddress, LmStudioModel) with { LocalContextTokens = 16384 });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal([(LocalAiServer.LmStudioAddress, LmStudioModel)], harness.LocalServers.InstanceUnloads);
        Assert.Equal([(LocalAiServer.LmStudioAddress, LmStudioModel, 16384)], harness.LocalServers.Loads);
        Assert.Equal(16384, harness.Service.LocalContextTokens);
    }

    [Fact]
    public async Task The_size_is_capped_at_the_largest_the_model_takes()
    {
        await using var harness = new CleanupHarness();
        LmStudioHolds(harness, maxContext: 32768);
        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.LmStudioAddress, LmStudioModel) with { LocalContextTokens = 131072 });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal([(LocalAiServer.LmStudioAddress, LmStudioModel, 32768)], harness.LocalServers.Loads);
    }

    [Fact]
    public async Task Foundry_Local_s_context_is_what_its_model_folder_says()
    {
        await using var harness = new CleanupHarness();
        harness.State.ModelRoot = harness.Temp.Combine("models");
        var folder = Path.Combine(harness.State.ModelRoot, FakeFoundryModel.SafeName(CleanupHarness.FoundryVariant));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(
            Path.Combine(folder, "genai_config.json"), """{"model":{"context_length":32768},"search":{"max_length":8192}}""");

        harness.Service.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal(8192, harness.Service.LocalContextTokens);
    }

    [Fact]
    public async Task A_cloud_service_has_no_context_on_this_PC()
    {
        await using var harness = new CleanupHarness();
        harness.Service.Configure(CleanupHarness.Custom("https://ai.example.invalid/v1", "some-model") with { LocalContextTokens = 32768 });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal(0, harness.Service.LocalContextTokens);
    }

    // Ollama holding the model at a context, as /api/ps says it, with every chat answered and captured.
    private static CleanupHarness OllamaLoadedAt(int contextTokens, List<JsonElement> bodies)
    {
        var harness = new CleanupHarness(http: new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            lock (bodies)
            {
                bodies.Add(body);
            }

            return ScriptedHttpHandler.ChatCompletion("Please send the report today.");
        }));
        harness.LocalServers.State = new LocalServerState(
            LocalServerReach.Reached,
            [new LocalServerModel(OllamaModel, OllamaModel, 9_600_000_000)],
            [new LocalServerLoadedModel(OllamaModel, 3_000_000_000) { ContextTokens = contextTokens }]);
        return harness;
    }

    // LM Studio has the model and holds nothing; a load at a size leaves a copy at that size, loaded on demand.
    private static void LmStudioHolds(CleanupHarness harness, int maxContext)
    {
        harness.LocalServers.State = new LocalServerState(
            LocalServerReach.Reached,
            [new LocalServerModel(LmStudioModel, "Gemma 4 E2B", 4_000_000_000) { MaxContextTokens = maxContext }],
            []);
        harness.LocalServers.StateAfterLoad = LoadedAt;
    }

    private static LocalServerState LoadedAt(string model, int contextTokens) => new(
        LocalServerReach.Reached,
        [new LocalServerModel(model, "Gemma 4 E2B", 4_000_000_000) { MaxContextTokens = 131072 }],
        [new LocalServerLoadedModel(model, 4_000_000_000)
        {
            ContextTokens = contextTokens,
            InstanceId = model,
            RemainingTtlSeconds = 3600,
        }]);

    // Distinct invented words, far enough apart that a dictation mentioning one mentions no other.
    private static List<DictionaryEntry> Vocabulary(int count)
    {
        var random = new Random(20261001);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<DictionaryEntry>();
        while (entries.Count < count)
        {
            var word = new string([.. Enumerable.Range(0, 10).Select(_ => (char)('a' + random.Next(26)))]);
            if (seen.Add(word))
            {
                entries.Add(DictionaryEntry.New(word, char.ToUpperInvariant(word[0]) + word[1..] + "X"));
            }
        }

        return entries;
    }

    private static CleanupVocabulary Admitted(IReadOnlyList<DictionaryEntry> entries) => new(entries, AiVocabularyScope.None);

    private static string Line(DictionaryEntry entry) => CleanupPrompt.GlossaryLine(entry.Replacement, entry.Pattern);

    private static int GlossaryLines(string system) =>
        system.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal) && line.Contains("(transcribed as", StringComparison.Ordinal));

    private static HttpResponseMessage OllamaAnswer(string content) => ScriptedHttpHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
    {
        model = OllamaModel,
        created_at = "2026-10-01T00:00:00Z",
        message = new { role = "assistant", content },
        done = true,
        done_reason = "stop",
        prompt_eval_count = 40,
        eval_count = 9,
    }));

    private static List<T> Snapshot<T>(List<T> list)
    {
        lock (list)
        {
            return [.. list];
        }
    }

    private static string SystemMessage(JsonElement body) =>
        body.GetProperty("messages").EnumerateArray()
            .First(message => message.GetProperty("role").GetString() == "system")
            .GetProperty("content").GetString()!;

    private static string ChatSystemMessage(JsonElement body) => SystemMessage(body);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition never held.");
            }

            await Task.Delay(10);
        }
    }
}
