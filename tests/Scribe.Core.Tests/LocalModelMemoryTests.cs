using System.Net;
using System.Text.Json;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

/// <summary>
/// Giving a local model's memory back, and starting it again. Scribe frees the model AI cleanup uses on this PC when
/// cleanup no longer uses it (turned off, or pointed elsewhere), when the user asks (Free memory), and when Scribe frees
/// its own speech models after the idle time the user chose; Ollama and LM Studio are asked to keep the model only that
/// long; and the next dictation waits for the model to start, which the recording indicator names.
/// </summary>
public sealed class LocalModelMemoryTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);
    private const string Ollama = "http://localhost:11434/v1";
    private const string LmStudio = "http://localhost:1234/v1";

    [Theory]
    [InlineData(Ollama, 10, "keep_alive", "10m")]
    [InlineData(LmStudio, 10, "ttl", "600")]
    public async Task Ollama_and_LM_Studio_keep_the_model_as_long_as_Scribe_keeps_its_own(
        string endpoint, int minutes, string field, string expected)
    {
        var bodies = await CaptureAsync(CleanupHarness.Custom(endpoint, "gemma4:e2b") with { LocalModelKeepAliveMinutes = minutes });

        Assert.All(bodies, body =>
        {
            Assert.Equal(expected, body.GetProperty(field).ToString());
            Assert.False(body.TryGetProperty(field == "ttl" ? "keep_alive" : "ttl", out _));
        });
    }

    [Theory]
    [InlineData(Ollama, null)]
    [InlineData(Ollama, 0)]
    [InlineData("http://localhost:8080/v1", 10)]
    [InlineData("https://ai.example.invalid/v1", 10)]
    public async Task Any_other_server_or_a_setting_that_never_frees_memory_keeps_its_own_policy(string endpoint, int? minutes)
    {
        var bodies = await CaptureAsync(CleanupHarness.Custom(endpoint, "gemma4:e2b") with { LocalModelKeepAliveMinutes = minutes });

        Assert.All(bodies, body =>
        {
            Assert.False(body.TryGetProperty("keep_alive", out _));
            Assert.False(body.TryGetProperty("ttl", out _));
        });
    }

    [Fact]
    public async Task Turning_cleanup_off_or_pointing_it_elsewhere_frees_the_model_the_app_held_for_it()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma3"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // A change to what the prompt says, or to how long the model is kept, still uses the model, and so does the same
        // model under its implicit tag.
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma3") with { WritingStyle = "Short.", LocalModelKeepAliveMinutes = 5 });
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma3:latest") with { WritingStyle = "Short.", LocalModelKeepAliveMinutes = 5 });
        Assert.Empty(harness.LocalServers.Unloads);

        svc.Configure(CleanupHarness.Custom(Ollama, "qwen3:4b-instruct"));
        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);
        Assert.Equal((Ollama, "gemma3:latest"), harness.LocalServers.Unloads[0]);

        svc.Configure(CleanupHarness.Custom(Ollama, "qwen3:4b-instruct") with { Enabled = false });
        await harness.LocalServers.WaitForUnloadsAsync(2, Bound);
        Assert.Equal((Ollama, "qwen3:4b-instruct"), harness.LocalServers.Unloads[1]);
    }

    [Fact]
    public async Task A_server_Scribe_does_not_recognize_is_never_asked_to_free_anything()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        svc.Configure(CleanupHarness.Custom("http://localhost:8080/v1", "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        svc.Configure(CleanupHarness.Custom("http://localhost:8080/v1", "gemma4:e2b") with { Enabled = false });
        svc.ReleaseModelMemory();
        await Task.Delay(100);

        Assert.Empty(harness.LocalServers.Unloads);
    }

    [Fact]
    public async Task Freeing_memory_while_idle_asks_the_app_to_free_the_model_cleanup_uses()
    {
        await using var harness = new CleanupHarness();
        harness.Service.Configure(CleanupHarness.Custom(LmStudio, "google/gemma-3-4b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        harness.Service.ReleaseModelMemory();

        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);
        Assert.Equal((LmStudio, "google/gemma-3-4b"), harness.LocalServers.Unloads[0]);
        Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
    }

    [Fact]
    public async Task A_model_the_app_must_load_for_the_dictation_reads_as_starting_until_it_answers()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (body.RootElement.TryGetProperty("max_tokens", out var max) && max.GetInt32() == 1)
            {
                await release.Task.WaitAsync(ct);
            }

            return ScriptedHttpHandler.ChatCompletion("ok");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.LocalServers.State = new LocalServerState(
            LocalServerReach.Reached, [new LocalServerModel("gemma4:e2b", "gemma4:e2b", 1)], []);

        svc.ForgetLastModelAnswerForTesting();
        svc.Admit(CleanupVocabulary.None).Prewarm();
        await WaitUntilAsync(() => svc.IsLocalModelStarting);

        release.SetResult();
        await svc.WaitForPrewarmForTesting().WaitAsync(Bound);
        Assert.False(svc.IsLocalModelStarting);
    }

    [Fact]
    public async Task A_model_the_app_already_holds_is_not_starting()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prewarmSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (body.RootElement.TryGetProperty("max_tokens", out var max) && max.GetInt32() == 1)
            {
                prewarmSent.TrySetResult();
                await release.Task.WaitAsync(ct);
            }

            return ScriptedHttpHandler.ChatCompletion("ok");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.LocalServers.State = new LocalServerState(
            LocalServerReach.Reached,
            [new LocalServerModel("gemma4:e2b", "gemma4:e2b", 1)],
            [new LocalServerLoadedModel("gemma4:e2b", 1)]);

        svc.ForgetLastModelAnswerForTesting();
        svc.Admit(CleanupVocabulary.None).Prewarm();
        await prewarmSent.Task.WaitAsync(Bound);

        Assert.False(svc.IsLocalModelStarting);
        release.SetResult();
        await svc.WaitForPrewarmForTesting().WaitAsync(Bound);
    }

    [Fact]
    public async Task Freeing_Foundry_Local_s_model_keeps_cleanup_on_and_the_next_dictation_loads_it_again()
    {
        await using var harness = new CleanupHarness(http: Cleaned());
        var svc = harness.Service;
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var loads = harness.Qwen.LoadCalls;

        Assert.True(await svc.UnloadFoundryModelAsync(CleanupHarness.FoundryAlias).WaitAsync(Bound));

        // Not a failure: cleanup stays on, and the model loads again when a dictation starts.
        Assert.Equal(CleanupStatus.Ready, svc.Status);
        Assert.Null(await svc.GetLoadedFoundryModelAsync());
        Assert.True(svc.IsLocalModelStarting);

        svc.Admit(CleanupVocabulary.None).Prewarm();
        await WaitUntilAsync(() => !svc.IsLocalModelStarting);
        Assert.Equal(loads + 1, harness.Qwen.LoadCalls);
        Assert.Equal(CleanupHarness.FoundryAlias, await svc.GetLoadedFoundryModelAsync());

        var result = await svc.CleanAsync("um so we ship on friday").WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
    }

    [Fact]
    public async Task A_model_Scribe_loads_again_answers_its_first_request_before_the_dictation_does()
    {
        // The first request after a load pays a one-time cost (4.4 s on TensorRT-RTX with Foundry Local 2.1.0), so the
        // reload pays it with the readiness probe: no dictated text, no vocabulary, while the user is still speaking.
        var bodies = new List<JsonElement>();
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            lock (bodies)
            {
                bodies.Add(json);
            }

            return ScriptedHttpHandler.ChatCompletion("So we ship on Friday.");
        });
        var vocabulary = new CleanupVocabulary(
            [Scribe.Core.Models.DictionaryEntry.New("kes trel", "Kestrel")], Scribe.Core.Libraries.AiVocabularyScope.None);
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.True(await svc.UnloadFoundryModelAsync(CleanupHarness.FoundryAlias).WaitAsync(Bound));
        int Count()
        {
            lock (bodies)
            {
                return bodies.Count;
            }
        }

        var before = Count();
        svc.Admit(vocabulary).Prewarm();
        await WaitUntilAsync(() => !svc.IsLocalModelStarting);

        List<JsonElement> sent;
        lock (bodies)
        {
            sent = [.. bodies];
        }

        Assert.Equal(before + 1, sent.Count);
        var warmUp = sent[^1];
        var user = warmUp.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
        Assert.Contains("ok", user, StringComparison.Ordinal);
        Assert.DoesNotContain("Kestrel", warmUp.ToString(), StringComparison.Ordinal);

        Assert.Equal(CleanupOutcome.Cleaned, (await svc.Admit(vocabulary).CleanAsync("um so we ship on friday").WaitAsync(Bound)).Outcome);
        Assert.Equal(before + 2, Count());
    }

    [Fact]
    public async Task Freeing_memory_while_idle_unloads_Foundry_Local_s_model_and_keeps_cleanup_on()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        svc.ReleaseModelMemory();

        await WaitUntilAsync(() => harness.State.LoadedIds().Length == 0);
        await WaitUntilAsync(() => svc.IsLocalModelStarting);
        Assert.Equal(CleanupStatus.Ready, svc.Status);
    }

    [Fact]
    public async Task A_dictation_waits_for_Foundry_Local_s_model_while_it_loads_and_is_cleaned()
    {
        await using var harness = new CleanupHarness(http: Cleaned());
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Qwen.LoadGate = gate;
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.Qwen.LoadStarted.Task.WaitAsync(Bound);
        harness.Qwen.LoadGate = null;
        Assert.True(svc.IsLocalModelStarting);

        var dictation = svc.CleanAsync("um so we ship on friday");
        await Task.Delay(200);
        Assert.False(dictation.IsCompleted);

        gate.SetResult();
        var result = await dictation.WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
    }

    [Fact]
    public async Task A_dictation_stops_waiting_at_the_bound_and_is_typed_as_heard()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        svc.LocalModelStartWait = TimeSpan.FromMilliseconds(300);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Qwen.LoadGate = gate;
        try
        {
            svc.Configure(CleanupHarness.FoundryOn());
            await harness.Qwen.LoadStarted.Task.WaitAsync(Bound);

            var result = await svc.CleanAsync("um so we ship on friday").WaitAsync(Bound);

            Assert.Equal(CleanupOutcome.Skipped, result.Outcome);
            Assert.Equal("um so we ship on friday", result.Text);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_download_is_no_start_to_wait_for()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        harness.State.SetCached(CleanupHarness.FoundryVariant, false);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Qwen.DownloadGate = gate;
        try
        {
            svc.Configure(CleanupHarness.FoundryOn());
            await WaitUntilAsync(() => svc.Status == CleanupStatus.Downloading);

            Assert.False(svc.IsLocalModelStarting);
            var result = await svc.CleanAsync("um so we ship on friday").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CleanupOutcome.Skipped, result.Outcome);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task Setting_up_the_AI_runtime_is_no_start_to_wait_for()
    {
        // The first setup downloads several GB of Foundry Local's runtime before any model loads: a dictation then is typed
        // as heard at once, as before, rather than waiting on every dictation for the whole download.
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Runtime.EpGate = gate;
        try
        {
            svc.Configure(CleanupHarness.FoundryOn());
            await harness.Runtime.EpStarted.Task.WaitAsync(Bound);

            Assert.Equal(CleanupStatus.Initializing, svc.Status);
            Assert.False(svc.IsLocalModelStarting);
            var result = await svc.CleanAsync("um so we ship on friday").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CleanupOutcome.Skipped, result.Outcome);
        }
        finally
        {
            gate.TrySetResult();
        }

        await harness.WaitForStatusAsync(CleanupStatus.Ready);
    }

    [Fact]
    public async Task A_model_download_a_Load_started_is_no_start_for_the_setup_waiting_behind_it()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        svc.LocalModelStartWait = Bound;

        harness.State.SetCached(CleanupHarness.OtherVariant, false);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Phi.DownloadGate = gate;
        try
        {
            var load = svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias);
            await WaitUntilAsync(() => harness.State.Events.Contains("download:" + CleanupHarness.OtherVariant));

            // Saving the model being downloaded queues its setup behind the download.
            svc.Configure(CleanupHarness.FoundryOn(CleanupHarness.OtherAlias));
            await WaitUntilAsync(() => svc.Status == CleanupStatus.Initializing);

            Assert.False(svc.IsLocalModelStarting);
            var result = await svc.CleanAsync("um so we ship on friday").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CleanupOutcome.Skipped, result.Outcome);

            gate.SetResult();
            await load.WaitAsync(Bound);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_suggestion_or_usage_summary_loads_a_freed_Foundry_Local_model_first()
    {
        // Foundry Local refuses a request for a model it does not hold, as it does once Scribe freed it.
        var state = new FakeFoundryState();
        var http = new ScriptedHttpHandler((_, _) => Task.FromResult(state.IsLoaded(CleanupHarness.FoundryVariant)
            ? ScriptedHttpHandler.ChatCompletion("Suggested.")
            : ScriptedHttpHandler.Json(HttpStatusCode.BadRequest, """{"error":{"message":"Model is not loaded."}}""")));
        await using var harness = new CleanupHarness(http: http, state: state);
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var loads = harness.Qwen.LoadCalls;
        Assert.True(await svc.UnloadFoundryModelAsync(CleanupHarness.FoundryAlias).WaitAsync(Bound));
        var recipient = svc.Recipient;
        Assert.NotNull(recipient);

        var result = await svc.CompleteAsync("Suggest dictionary words.", "some history", recipient!).WaitAsync(Bound);

        Assert.Equal(CompletionOutcome.Completed, result.Outcome);
        Assert.Equal(loads + 1, harness.Qwen.LoadCalls);
        Assert.False(svc.IsLocalModelStarting);
    }

    [Fact]
    public async Task A_setup_that_loads_the_model_again_forgets_that_Scribe_had_freed_it()
    {
        await using var harness = new CleanupHarness(http: Cleaned());
        var svc = harness.Service;
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.True(await svc.UnloadFoundryModelAsync(CleanupHarness.FoundryAlias).WaitAsync(Bound));
        Assert.True(svc.IsLocalModelStarting);

        // Loading another model by hand leaves cleanup unavailable; the same settings saved again set it up once more.
        Assert.True(await svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias).WaitAsync(Bound));
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var loads = harness.Qwen.LoadCalls;

        Assert.False(svc.IsLocalModelStarting);
        Assert.Equal(CleanupOutcome.Cleaned, (await svc.CleanAsync("um so we ship on friday").WaitAsync(Bound)).Outcome);
        Assert.Equal(loads, harness.Qwen.LoadCalls);
    }

    [Theory]
    [InlineData(LmStudio, "google/gemma-4-e2b")]
    [InlineData(Ollama, "gemma4:e2b")]
    public async Task Freeing_or_readying_a_model_uses_the_key_saved_for_that_address(string endpoint, string model)
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        svc.Configure(CleanupHarness.Custom(endpoint, model) with { CustomApiKey = "lm-token" });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        svc.ForgetLastModelAnswerForTesting();
        svc.Admit(CleanupVocabulary.None).Prewarm();
        await svc.WaitForPrewarmForTesting().WaitAsync(Bound);
        svc.ReleaseModelMemory();
        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);
        svc.Configure(CleanupHarness.Custom(endpoint, model) with { CustomApiKey = "lm-token", Enabled = false });
        await harness.LocalServers.WaitForUnloadsAsync(2, Bound);

        Assert.Equal(3, harness.LocalServers.Keys.Count);
        Assert.All(harness.LocalServers.Keys, key => Assert.Equal("lm-token", key));
    }

    // A cleanup the answer guards accept for "um so we ship on friday".
    private static ScriptedHttpHandler Cleaned() =>
        new((_, _) => Task.FromResult(ScriptedHttpHandler.ChatCompletion("So we ship on Friday.")));

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

    private static async Task<List<JsonElement>> CaptureAsync(CleanupOptions options)
    {
        var bodies = new List<JsonElement>();
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var json = await request.Content!.ReadAsStringAsync(ct);
            lock (bodies)
            {
                bodies.Add(JsonDocument.Parse(json).RootElement.Clone());
            }

            return ScriptedHttpHandler.ChatCompletion("We ship on Friday.");
        });

        await using var harness = new CleanupHarness(http: http);
        harness.Service.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(CleanupOutcome.Cleaned, (await harness.Service.CleanAsync("um so we ship on friday")).Outcome);

        lock (bodies)
        {
            Assert.True(bodies.Count >= 2, $"Expected the probe and a cleanup request, saw {bodies.Count}.");
            return [.. bodies];
        }
    }
}
