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
    [InlineData(Ollama, 0, "keep_alive", "-1m")]
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
    [InlineData(LmStudio, 0)]
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
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma3") with { LocalModelKeepAliveMinutes = 10 });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // A change to what the prompt says, or a longer idle time, still uses the model as it is, and so does the same
        // model under its implicit tag.
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma3") with { WritingStyle = "Short.", LocalModelKeepAliveMinutes = 30 });
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma3:latest") with { WritingStyle = "Short.", LocalModelKeepAliveMinutes = 30 });
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma3:latest") with { WritingStyle = "Short." }); // no idle time
        await Task.Delay(100);
        Assert.Empty(harness.LocalServers.Unloads);

        svc.Configure(CleanupHarness.Custom(Ollama, "qwen3:4b-instruct"));
        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);
        Assert.Equal((Ollama, "gemma3:latest"), harness.LocalServers.Unloads[0]);

        svc.Configure(CleanupHarness.Custom(Ollama, "qwen3:4b-instruct") with { Enabled = false });
        await harness.LocalServers.WaitForUnloadsAsync(2, Bound);
        Assert.Equal((Ollama, "qwen3:4b-instruct"), harness.LocalServers.Unloads[1]);
    }

    /// <summary>
    /// A model loaded under the old idle time can keep that time until it is loaded again (LM Studio sets it when it loads
    /// a model on demand, Ollama only when a request reaches it), so a shorter time, or one turned on, frees the model once,
    /// and it loads with the new time when it is next used. Nothing is loaded again in the meantime.
    /// </summary>
    [Theory]
    [InlineData(LmStudio, "google/gemma-4-e2b", 30, 5)]
    [InlineData(LmStudio, "google/gemma-4-e2b", null, 10)]
    [InlineData(Ollama, "gemma4:e2b", 0, 10)]
    public async Task A_shorter_idle_time_frees_the_model_once_so_it_loads_with_the_new_time(
        string endpoint, string model, int? before, int after)
    {
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
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.Custom(endpoint, model) with { LocalModelKeepAliveMinutes = before });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        int Sent()
        {
            lock (bodies)
            {
                return bodies.Count;
            }
        }

        var sent = Sent();
        svc.Configure(CleanupHarness.Custom(endpoint, model) with { LocalModelKeepAliveMinutes = after });

        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);
        await Task.Delay(100);
        Assert.Equal([(endpoint, model)], harness.LocalServers.Unloads);
        Assert.Equal(sent, Sent());
        Assert.Equal(CleanupStatus.Ready, svc.Status);

        Assert.Equal(CleanupOutcome.Cleaned, (await svc.CleanAsync("um so we ship on friday").WaitAsync(Bound)).Outcome);
        JsonElement last;
        lock (bodies)
        {
            last = bodies[^1];
        }

        if (endpoint == Ollama)
        {
            Assert.Equal($"{after}m", last.GetProperty("keep_alive").GetString());
        }
        else
        {
            Assert.Equal(after * 60, last.GetProperty("ttl").GetInt32());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Choosing_Never_keeps_the_loaded_Ollama_model_and_applies_indefinite_retention_on_the_next_request(bool nativeApi)
    {
        var bodies = new List<JsonElement>();
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            lock (bodies)
            {
                bodies.Add(body);
            }

            return ModelAnswer("So we ship on Friday.", nativeApi);
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        var finite = CleanupHarness.Custom(Ollama, "gemma4:e2b") with
        {
            LocalModelKeepAliveMinutes = 10,
            LocalContextTokens = nativeApi ? 8192 : null,
        };
        svc.Configure(finite);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        svc.Configure(finite with { LocalModelKeepAliveMinutes = 0 });
        Assert.Equal(CleanupOutcome.Cleaned, (await svc.CleanAsync("um so we ship on friday").WaitAsync(Bound)).Outcome);
        Assert.Empty(harness.LocalServers.Unloads);
        lock (bodies)
        {
            Assert.Equal("-1m", bodies[^1].GetProperty("keep_alive").GetString());
        }
    }

    [Fact]
    public async Task A_server_Scribe_does_not_recognize_is_never_asked_to_free_anything()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        svc.Configure(CleanupHarness.Custom("http://localhost:8080/v1", "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        svc.Configure(CleanupHarness.Custom("http://localhost:8080/v1", "gemma4:e2b") with { Enabled = false });
        svc.ReleaseModelMemory(ModelMemoryRelease.Pause);
        await Task.Delay(100);

        Assert.Empty(harness.LocalServers.Unloads);
    }

    [Fact]
    public async Task A_pause_asks_the_app_to_free_the_model_cleanup_uses()
    {
        await using var harness = new CleanupHarness();
        harness.Service.Configure(CleanupHarness.Custom(LmStudio, "google/gemma-3-4b") with { LocalModelKeepAliveMinutes = 10 });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        harness.Service.ReleaseModelMemory(ModelMemoryRelease.Pause);

        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);
        Assert.Equal((LmStudio, "google/gemma-3-4b"), harness.LocalServers.Unloads[0]);
        Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
    }

    /// <summary>
    /// Each request gives the app the retention choice, a finite idle time or Ollama's indefinite keep. Scribe does not
    /// repeat that idle decision, which could free a model someone else used since. A pause still explicitly frees it.
    /// </summary>
    [Theory]
    [InlineData(Ollama, "gemma4:e2b", 10)]
    [InlineData(Ollama, "gemma4:e2b", 0)]
    [InlineData(LmStudio, "google/gemma-3-4b", 10)]
    public async Task Idle_release_leaves_the_requested_retention_to_the_app_and_pause_still_frees_the_model(
        string endpoint, string model, int minutes)
    {
        await using var harness = new CleanupHarness();
        harness.Service.Configure(CleanupHarness.Custom(endpoint, model) with { LocalModelKeepAliveMinutes = minutes });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        harness.Service.ReleaseModelMemory(ModelMemoryRelease.Idle);
        harness.Service.ReleaseModelMemory(ModelMemoryRelease.Pause); // a pause right after still asks
        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);
        await Task.Delay(100);

        Assert.Equal([(endpoint, model)], harness.LocalServers.Unloads);
    }

    [Fact]
    public async Task At_the_idle_time_Scribe_asks_the_app_itself_when_its_requests_could_not_say_how_long()
    {
        await using var harness = new CleanupHarness();
        harness.Service.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        harness.Service.ReleaseModelMemory(ModelMemoryRelease.Idle);

        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);
        Assert.Equal((Ollama, "gemma4:e2b"), harness.LocalServers.Unloads[0]);
    }

    /// <summary>
    /// A release goes out in the background, and a recording can start using the model before it reaches the app: it then
    /// stays home, rather than freeing the model the recording has just readied.
    /// </summary>
    [Fact]
    public async Task A_release_decided_before_a_recording_started_stays_home()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // A release of another model is still on its way, so the next one queues behind it.
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.LocalServers.UnloadGate = slow;
        var other = svc.FreeLocalAppModelAsync(Ollama, "gemma3");
        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);

        svc.ReleaseModelMemory(ModelMemoryRelease.Pause);
        svc.ForgetLastModelAnswerForTesting();
        svc.Admit(CleanupVocabulary.None).Prewarm(); // the next recording starts
        harness.LocalServers.UnloadGate = null;
        slow.SetResult();
        Assert.True(await other.WaitAsync(Bound));
        await svc.WaitForPrewarmForTesting().WaitAsync(Bound);
        await Task.Delay(200);

        Assert.Equal([(Ollama, "gemma3")], harness.LocalServers.Unloads);
    }

    [Fact]
    public async Task Switching_back_before_the_release_reaches_the_app_keeps_the_model()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma3"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        svc.Configure(CleanupHarness.Custom(Ollama, "phi4-mini"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // gemma3's release goes out before the app holds the next one: it runs beside phi4-mini's readiness check, and on a
        // cold thread pool that check can finish first (this test failed in isolation on 0.5.2 for that reason).
        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);
        harness.LocalServers.UnloadGate = slow;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b")); // frees phi4-mini, held by the app
        await harness.LocalServers.WaitForUnloadsAsync(2, Bound);

        svc.Configure(CleanupHarness.Custom(Ollama, "gemma3") with { Enabled = false }); // frees gemma4:e2b, queued
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b")); // back on it before that release went out
        harness.LocalServers.UnloadGate = null;
        slow.SetResult();
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        await Task.Delay(200);

        Assert.DoesNotContain((Ollama, "gemma4:e2b"), harness.LocalServers.Unloads);
    }

    [Fact]
    public async Task A_recording_after_a_release_readies_the_model_again_and_says_it_is_starting()
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
        await harness.WaitForStatusAsync(CleanupStatus.Ready); // the readiness check answered just now
        harness.LocalServers.State = new LocalServerState(
            LocalServerReach.Reached, [new LocalServerModel("gemma4:e2b", "gemma4:e2b", 1)], []);

        Assert.True(await svc.FreeLocalAppModelAsync(Ollama, "gemma4:e2b").WaitAsync(Bound));
        svc.Admit(CleanupVocabulary.None).Prewarm();

        await WaitUntilAsync(() => svc.IsLocalModelStarting);
        release.SetResult();
        await svc.WaitForPrewarmForTesting().WaitAsync(Bound);
    }

    [Fact]
    public async Task A_dictation_waits_for_the_readying_request_that_is_loading_the_model()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readying = 0; // 0 not sent, 1 loading the model, 2 answered
        var sentWhileLoading = 0;
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (body.RootElement.TryGetProperty("max_tokens", out var max) && max.GetInt32() == 1)
            {
                Volatile.Write(ref readying, 1);
                await release.Task.WaitAsync(ct);
                Volatile.Write(ref readying, 2);
                return ScriptedHttpHandler.ChatCompletion("ok");
            }

            if (Volatile.Read(ref readying) == 1)
            {
                Interlocked.Increment(ref sentWhileLoading);
            }

            return ScriptedHttpHandler.ChatCompletion("So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.LocalServers.State = new LocalServerState(
            LocalServerReach.Reached, [new LocalServerModel("gemma4:e2b", "gemma4:e2b", 1)], []);

        svc.ForgetLastModelAnswerForTesting();
        var admitted = svc.Admit(CleanupVocabulary.None);
        admitted.Prewarm();
        await WaitUntilAsync(() => svc.IsLocalModelStarting);

        var dictation = admitted.CleanAsync("um so we ship on friday");
        await Task.Delay(200);
        Assert.False(dictation.IsCompleted);

        release.SetResult();
        var result = await dictation.WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
        Assert.Equal(0, Volatile.Read(ref sentWhileLoading));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_dictation_waits_for_readied_instructions_even_when_the_model_was_already_loaded(bool nativeApi)
    {
        var readyingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishReadying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dictationSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            if (IsReadying(body))
            {
                readyingStarted.TrySetResult();
                await finishReadying.Task.WaitAsync(ct);
                return ModelAnswer("ok", nativeApi);
            }

            if (body.Contains("um so we ship on friday", StringComparison.Ordinal))
            {
                dictationSent.TrySetResult();
            }

            return ModelAnswer("So we ship on Friday.", nativeApi);
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b") with { LocalContextTokens = nativeApi ? 8192 : null });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.LocalServers.State = new LocalServerState(
            LocalServerReach.Reached,
            [new LocalServerModel("gemma4:e2b", "gemma4:e2b", 1)],
            [new LocalServerLoadedModel("gemma4:e2b", 1)]);

        try
        {
            svc.ForgetLastModelAnswerForTesting();
            var admitted = svc.Admit(CleanupVocabulary.None);
            admitted.Prewarm();
            await readyingStarted.Task.WaitAsync(Bound);
            Assert.False(svc.IsLocalModelStarting);

            var dictation = admitted.CleanAsync("um so we ship on friday");
            Assert.False(dictationSent.Task.IsCompleted);
            Assert.False(dictation.IsCompleted);

            finishReadying.SetResult();
            Assert.Equal(CleanupOutcome.Cleaned, (await dictation.WaitAsync(Bound)).Outcome);
            Assert.True(dictationSent.Task.IsCompleted);
        }
        finally
        {
            finishReadying.TrySetResult();
        }
    }

    [Theory]
    [InlineData(Ollama)]
    [InlineData("http://localhost:8080/v1")]
    public async Task Instruction_readying_that_outlasts_the_wait_is_not_reported_as_a_model_start_and_recovers(string endpoint)
    {
        var readyingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishReadying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dictationRequests = 0;
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            if (IsReadying(body))
            {
                readyingStarted.TrySetResult();
                await finishReadying.Task.WaitAsync(ct);
                return ScriptedHttpHandler.ChatCompletion("ok");
            }

            if (body.Contains("um so we ship on friday", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref dictationRequests);
            }

            return ScriptedHttpHandler.ChatCompletion("So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.LocalModelStartWait = TimeSpan.FromMilliseconds(20);
        svc.Configure(CleanupHarness.Custom(endpoint, "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.LocalServers.State = new LocalServerState(
            LocalServerReach.Reached, [], [new LocalServerLoadedModel("gemma4:e2b", 1)]);
        try
        {
            svc.ForgetLastModelAnswerForTesting();
            var admitted = svc.Admit(CleanupVocabulary.None);
            admitted.Prewarm();
            await readyingStarted.Task.WaitAsync(Bound);
            Assert.False(svc.IsLocalModelStarting);
            var skipped = await admitted.CleanAsync("um so we ship on friday").WaitAsync(Bound);
            Assert.Equal(CleanupOutcome.Skipped, skipped.Outcome);
            Assert.Equal("um so we ship on friday", skipped.Text);
            Assert.Contains("getting ready", skipped.DisplayDetail, StringComparison.Ordinal);
            Assert.DoesNotContain("starting", skipped.DisplayDetail, StringComparison.Ordinal);
            Assert.Equal(0, Volatile.Read(ref dictationRequests));
            finishReadying.SetResult();
            await svc.WaitForPrewarmForTesting().WaitAsync(Bound);
            Assert.Equal(CleanupOutcome.Cleaned, (await admitted.CleanAsync("um so we ship on friday").WaitAsync(Bound)).Outcome);
            Assert.Equal(1, Volatile.Read(ref dictationRequests));
        }
        finally
        {
            finishReadying.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Free_memory_after_recording_readied_the_model_checks_and_loads_it_before_cleanup(bool nativeApi)
    {
        var readyingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishReadying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dictationSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            if (IsReadying(body))
            {
                readyingStarted.TrySetResult();
                await finishReadying.Task.WaitAsync(ct);
                return ModelAnswer("ok", nativeApi);
            }

            if (body.Contains("um so we ship on friday", StringComparison.Ordinal))
            {
                dictationSent.TrySetResult();
            }

            return ModelAnswer("So we ship on Friday.", nativeApi);
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b") with { LocalContextTokens = nativeApi ? 8192 : null });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.LocalServers.State = new LocalServerState(
            LocalServerReach.Reached,
            [new LocalServerModel("gemma4:e2b", "gemma4:e2b", 1)],
            [new LocalServerLoadedModel("gemma4:e2b", 1)]);
        var admitted = svc.Admit(CleanupVocabulary.None);
        admitted.Prewarm();
        await svc.WaitForPrewarmForTesting().WaitAsync(Bound);
        var before = harness.LocalServers.Reads;
        Assert.True(await svc.FreeLocalAppModelAsync(Ollama, "gemma4:e2b").WaitAsync(Bound));
        harness.LocalServers.State = harness.LocalServers.State with { Loaded = [] };

        try
        {
            var dictation = admitted.CleanAsync("um so we ship on friday");
            await readyingStarted.Task.WaitAsync(Bound);
            Assert.True(harness.LocalServers.Reads > before);
            Assert.True(svc.IsLocalModelStarting);
            Assert.False(dictationSent.Task.IsCompleted);
            Assert.Equal(CleanupStatus.Ready, svc.Status);

            finishReadying.SetResult();
            Assert.Equal(CleanupOutcome.Cleaned, (await dictation.WaitAsync(Bound)).Outcome);
            Assert.True(dictationSent.Task.IsCompleted);
        }
        finally
        {
            finishReadying.TrySetResult();
        }
    }

    [Fact]
    public async Task A_model_answering_outside_a_dictation_is_reported_so_the_idle_countdown_starts_again()
    {
        await using var harness = new CleanupHarness(http: Cleaned());
        var svc = harness.Service;
        var uses = 0;
        svc.LocalModelUsed += () => Interlocked.Increment(ref uses);
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready); // the readiness check answered
        await WaitUntilAsync(() => Volatile.Read(ref uses) >= 1);

        var recipient = svc.Recipient!;
        Assert.Equal(CompletionOutcome.Completed, (await svc.CompleteAsync("Suggest.", "history", recipient).WaitAsync(Bound)).Outcome);
        Assert.True(Volatile.Read(ref uses) >= 2);

        // Another AI service is no model on this PC.
        var before = Volatile.Read(ref uses);
        svc.Configure(CleanupHarness.Custom("https://ai.example.invalid/v1", "gpt-x"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(before, Volatile.Read(ref uses));
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

        svc.ReleaseModelMemory(ModelMemoryRelease.Idle);

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
        svc.ReleaseModelMemory(ModelMemoryRelease.Pause);
        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);
        svc.Configure(CleanupHarness.Custom(endpoint, model) with { CustomApiKey = "lm-token", Enabled = false });
        await harness.LocalServers.WaitForUnloadsAsync(2, Bound);

        // The read after the readiness check (what the app loaded the model with), the readying request's reads before it
        // (whether the app holds the model) and after it (what it holds the model with), and the two unloads.
        Assert.Equal(5, harness.LocalServers.Keys.Count);
        Assert.All(harness.LocalServers.Keys, key => Assert.Equal("lm-token", key));
    }

    /// <summary>
    /// Plain requests leave out the generation fields a strict server refused, never how long Ollama or LM Studio keeps
    /// the model: it is what gives the memory back at the idle time, so Scribe does not ask the app itself then.
    /// </summary>
    [Fact]
    public async Task Plain_requests_still_say_how_long_the_app_keeps_the_model()
    {
        var bodies = new List<JsonElement>();
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            lock (bodies)
            {
                bodies.Add(body);
            }

            return body.TryGetProperty("reasoning_effort", out _)
                ? ScriptedHttpHandler.Json(HttpStatusCode.BadRequest,
                    """{"error":{"message":"reasoning_effort: Input should be 'low', 'medium' or 'high'","type":"BadRequestError"}}""")
                : ScriptedHttpHandler.ChatCompletion("So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b") with { LocalModelKeepAliveMinutes = 10 });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal(CleanupOutcome.Cleaned, (await svc.CleanAsync("um so we ship on friday").WaitAsync(Bound)).Outcome);
        JsonElement last;
        lock (bodies)
        {
            last = bodies[^1];
        }

        Assert.False(last.TryGetProperty("reasoning_effort", out _));
        Assert.False(last.TryGetProperty("max_tokens", out _));
        Assert.Equal("10m", last.GetProperty("keep_alive").GetString());

        svc.ReleaseModelMemory(ModelMemoryRelease.Idle);
        await Task.Delay(100);
        Assert.Empty(harness.LocalServers.Unloads);
    }

    [Fact]
    public async Task Test_connection_asks_the_app_to_keep_the_model_only_the_idle_time_too()
    {
        var bodies = new List<JsonElement>();
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            lock (bodies)
            {
                bodies.Add(body);
            }

            return ScriptedHttpHandler.ChatCompletion("ok");
        });
        await using var harness = new CleanupHarness(http: http);

        var candidate = CleanupHarness.Custom(LmStudio, "google/gemma-4-e2b") with
        {
            LocalModelKeepAliveMinutes = LocalAiServer.KeepAliveMinutes(CleanupProvider.OpenAiCompatible, LmStudio, 10),
        };
        await harness.Service.TestAsync(candidate).WaitAsync(Bound);

        lock (bodies)
        {
            Assert.NotEmpty(bodies);
            Assert.All(bodies, body => Assert.Equal(600, body.GetProperty("ttl").GetInt32()));
        }
    }

    [Fact]
    public void The_idle_time_goes_only_to_Ollama_and_LM_Studio_at_their_own_address_from_dictation_and_Test_connection()
    {
        Assert.Equal(10, LocalAiServer.KeepAliveMinutes(CleanupProvider.OpenAiCompatible, Ollama, 10));
        Assert.Equal(0, LocalAiServer.KeepAliveMinutes(CleanupProvider.OpenAiCompatible, LmStudio, 0));
        Assert.Null(LocalAiServer.KeepAliveMinutes(CleanupProvider.OpenAiCompatible, "http://localhost:8080/v1", 10));
        Assert.Null(LocalAiServer.KeepAliveMinutes(CleanupProvider.OpenAiCompatible, "https://ai.example.invalid/v1", 10));
        Assert.Null(LocalAiServer.KeepAliveMinutes(CleanupProvider.FoundryLocal, Ollama, 10));

        var root = RepositoryRoot();
        var controller = File.ReadAllText(Path.Combine(root, "src", "Scribe.App", "Dictation", "DictationController.cs"));
        var settings = File.ReadAllText(Path.Combine(root, "src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs"));
        Assert.Contains("LocalModelKeepAliveMinutes: LocalAiServer.KeepAliveMinutes(", controller, StringComparison.Ordinal);
        Assert.Contains("LocalModelKeepAliveMinutes: LocalAiServer.KeepAliveMinutes(", settings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Freeing_memory_waits_for_a_request_that_is_using_the_model()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var text = await request.Content!.ReadAsStringAsync(ct);
            if (text.Contains("so we ship", StringComparison.Ordinal))
            {
                inFlight.TrySetResult();
                await answer.Task.WaitAsync(ct);
            }

            return ScriptedHttpHandler.ChatCompletion("So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var dictation = svc.CleanAsync("um so we ship on friday");
        await inFlight.Task.WaitAsync(Bound);
        var free = svc.FreeLocalAppModelAsync(Ollama, "gemma4:e2b");
        await Task.Delay(200);
        Assert.Empty(harness.LocalServers.Unloads);

        answer.SetResult();
        Assert.Equal(CleanupOutcome.Cleaned, (await dictation.WaitAsync(Bound)).Outcome);
        Assert.True(await free.WaitAsync(Bound));
        Assert.Equal([(Ollama, "gemma4:e2b")], harness.LocalServers.Unloads);
    }

    [Fact]
    public async Task A_dictation_that_begins_while_the_model_is_being_freed_waits_for_it()
    {
        var unloading = 0;
        var sentWhileUnloading = 0;
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            if ((await request.Content!.ReadAsStringAsync(ct)).Contains("so we ship", StringComparison.Ordinal) &&
                Volatile.Read(ref unloading) == 1)
            {
                Interlocked.Increment(ref sentWhileUnloading);
            }

            return ScriptedHttpHandler.ChatCompletion("So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.LocalServers.UnloadGate = gate;
        harness.LocalServers.Unloaded += () => Volatile.Write(ref unloading, 1);

        var free = svc.FreeLocalAppModelAsync(Ollama, "gemma4:e2b");
        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);
        var dictation = svc.CleanAsync("um so we ship on friday");
        await Task.Delay(200);
        Assert.False(dictation.IsCompleted);

        Volatile.Write(ref unloading, 0);
        gate.SetResult();
        Assert.True(await free.WaitAsync(Bound));
        Assert.Equal(CleanupOutcome.Cleaned, (await dictation.WaitAsync(Bound)).Outcome);
        Assert.Equal(0, Volatile.Read(ref sentWhileUnloading));
    }

    [Fact]
    public async Task A_readiness_check_in_flight_holds_a_release_back_too()
    {
        var hold = 0;
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new ScriptedHttpHandler(async (_, ct) =>
        {
            if (Volatile.Read(ref hold) == 1)
            {
                inFlight.TrySetResult();
                await answer.Task.WaitAsync(ct);
            }

            return ScriptedHttpHandler.ChatCompletion("ok");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        svc.ModelUseDrainBound = TimeSpan.FromMilliseconds(300);

        Volatile.Write(ref hold, 1);
        var test = svc.TestAsync(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        await inFlight.Task.WaitAsync(Bound);

        Assert.False(await svc.FreeLocalAppModelAsync(Ollama, "gemma4:e2b").WaitAsync(Bound));
        Assert.Empty(harness.LocalServers.Unloads);

        answer.SetResult();
        await test.WaitAsync(Bound);
        Assert.True(await svc.FreeLocalAppModelAsync(Ollama, "gemma4:e2b").WaitAsync(Bound));
    }

    /// <summary>
    /// Releases go one at a time, so the unload a use waits for is the only one in flight: a second release queued behind
    /// the first never lets a dictation through while the first is still freeing the model.
    /// </summary>
    [Fact]
    public async Task Releases_go_one_at_a_time_and_a_dictation_waits_for_the_one_in_flight()
    {
        var unloading = 0;
        var sentWhileUnloading = 0;
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            if ((await request.Content!.ReadAsStringAsync(ct)).Contains("so we ship", StringComparison.Ordinal) &&
                Volatile.Read(ref unloading) == 1)
            {
                Interlocked.Increment(ref sentWhileUnloading);
            }

            return ScriptedHttpHandler.ChatCompletion("So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.LocalServers.UnloadGate = first;
        harness.LocalServers.Unloaded += () => Volatile.Write(ref unloading, 1);

        svc.ReleaseModelMemory(ModelMemoryRelease.Pause);
        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);
        var free = svc.FreeLocalAppModelAsync(Ollama, "gemma4:e2b");
        var dictation = svc.CleanAsync("um so we ship on friday");
        await Task.Delay(200);
        Assert.False(dictation.IsCompleted);
        Assert.Single(harness.LocalServers.Unloads);

        harness.LocalServers.UnloadGate = null;
        Volatile.Write(ref unloading, 0);
        first.SetResult();
        Assert.Equal(CleanupOutcome.Cleaned, (await dictation.WaitAsync(Bound)).Outcome);
        Assert.True(await free.WaitAsync(Bound));
        Assert.Equal(2, harness.LocalServers.Unloads.Count);
        Assert.Equal(0, Volatile.Read(ref sentWhileUnloading));
    }

    [Fact]
    public async Task A_pause_s_release_that_is_no_longer_wanted_stays_home()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        svc.ReleaseModelMemory(ModelMemoryRelease.Pause, stillWanted: () => false); // resumed before it went out
        await Task.Delay(200);

        Assert.Empty(harness.LocalServers.Unloads);
    }

    /// <summary>
    /// The readying request is published before it has asked the app whether it holds the model, so a dictation that stops
    /// that soon waits for it rather than sending its own request into a model the app may be loading.
    /// </summary>
    [Fact]
    public async Task A_dictation_that_stops_before_the_app_answered_waits_for_the_readying_request()
    {
        var order = new List<string>();
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            lock (order)
            {
                order.Add(IsReadying(body) ? "readying" : body.Contains("so we ship", StringComparison.Ordinal) ? "dictation" : "other");
            }

            return ScriptedHttpHandler.ChatCompletion("So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.LocalServers.State = new LocalServerState(
            LocalServerReach.Reached, [new LocalServerModel("gemma4:e2b", "gemma4:e2b", 1)], []);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.LocalServers.ReadGate = read;

        svc.ForgetLastModelAnswerForTesting();
        var admitted = svc.Admit(CleanupVocabulary.None);
        admitted.Prewarm();
        var dictation = admitted.CleanAsync("um so we ship on friday");
        await Task.Delay(200);
        Assert.False(dictation.IsCompleted);

        read.SetResult();
        Assert.Equal(CleanupOutcome.Cleaned, (await dictation.WaitAsync(Bound)).Outcome);
        lock (order)
        {
            Assert.True(order.IndexOf("readying") >= 0 && order.IndexOf("readying") < order.IndexOf("dictation"));
        }
    }

    [Fact]
    public async Task A_new_configuration_neither_waits_for_nor_lets_through_the_old_one_s_readying_request()
    {
        var readying = 0;
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            if (IsReadying(await request.Content!.ReadAsStringAsync(ct)))
            {
                Interlocked.Increment(ref readying);
            }

            return ScriptedHttpHandler.ChatCompletion("So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.LocalServers.ReadGate = read;
        harness.LocalServers.ReadStarted += () => readStarted.TrySetResult();

        svc.ForgetLastModelAnswerForTesting();
        svc.Admit(CleanupVocabulary.None).Prewarm();
        await readStarted.Task.WaitAsync(Bound);
        svc.Configure(CleanupHarness.Custom(Ollama, "qwen3:4b-instruct"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var cleaned = await svc.CleanAsync("um so we ship on friday").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CleanupOutcome.Cleaned, cleaned.Outcome);

        read.SetResult();
        await svc.WaitForPrewarmForTesting().WaitAsync(Bound);
        Assert.Equal(0, Volatile.Read(ref readying));
        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);
        Assert.Equal((Ollama, "gemma4:e2b"), harness.LocalServers.Unloads[0]);
    }

    [Fact]
    public async Task Turning_cleanup_off_while_the_readying_request_waits_for_an_unload_readies_nothing()
    {
        var readying = 0;
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            if (IsReadying(await request.Content!.ReadAsStringAsync(ct)))
            {
                Interlocked.Increment(ref readying);
            }

            return ScriptedHttpHandler.ChatCompletion("ok");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var unload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.LocalServers.UnloadGate = unload;

        svc.ReleaseModelMemory(ModelMemoryRelease.Pause);
        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);
        svc.ForgetLastModelAnswerForTesting();
        svc.Admit(CleanupVocabulary.None).Prewarm();
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b") with { Enabled = false });

        harness.LocalServers.UnloadGate = null;
        unload.SetResult();
        await svc.WaitForPrewarmForTesting().WaitAsync(Bound);

        Assert.Equal(0, Volatile.Read(ref readying));
    }

    /// <summary>
    /// Foundry Local runs in Scribe's own process, so the idle release frees whatever model it holds, whatever cleanup is
    /// set to: a model Settings loaded while cleanup was off stays no longer than the idle time either.
    /// </summary>
    [Fact]
    public async Task At_the_idle_time_Scribe_frees_a_Foundry_Local_model_Settings_loaded_while_cleanup_was_off()
    {
        await using var harness = new CleanupHarness(http: Cleaned());
        var svc = harness.Service;
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        svc.Configure(CleanupHarness.FoundryOn() with { Enabled = false });
        await harness.WaitForStatusAsync(CleanupStatus.Disabled);
        Assert.True(await svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias).WaitAsync(Bound));
        Assert.NotEmpty(harness.State.LoadedIds());

        svc.ReleaseModelMemory(ModelMemoryRelease.Idle);

        await WaitUntilAsync(() => harness.State.LoadedIds().Length == 0);
    }

    /// <summary>
    /// A release waits for the requests using the model and never frees the model under one. Free memory, asked for by the
    /// user, reports that it could not when a request outlasts its wait; a release Scribe decided on itself (a pause here)
    /// is deferred, not dropped, and goes out once the request has finished.
    /// </summary>
    [Fact]
    public async Task A_release_never_frees_the_model_under_a_request_and_a_pause_s_goes_out_once_it_ends()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            if ((await request.Content!.ReadAsStringAsync(ct)).Contains("so we ship", StringComparison.Ordinal))
            {
                inFlight.TrySetResult();
                await answer.Task.WaitAsync(ct);
            }

            return ScriptedHttpHandler.ChatCompletion("So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        svc.ModelUseDrainBound = TimeSpan.FromMilliseconds(300);
        svc.AutomaticReleaseDrainBound = Bound;

        var dictation = svc.CleanAsync("um so we ship on friday");
        await inFlight.Task.WaitAsync(Bound);

        Assert.False(await svc.FreeLocalAppModelAsync(Ollama, "gemma4:e2b").WaitAsync(Bound));
        svc.ReleaseModelMemory(ModelMemoryRelease.Pause);
        await Task.Delay(500);
        Assert.Empty(harness.LocalServers.Unloads);

        answer.SetResult();
        Assert.Equal(CleanupOutcome.Cleaned, (await dictation.WaitAsync(Bound)).Outcome);
        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);
        await Task.Delay(100);
        Assert.Equal([(Ollama, "gemma4:e2b")], harness.LocalServers.Unloads);
    }

    [Fact]
    public async Task A_dictation_whose_model_is_still_being_freed_past_the_wait_is_typed_as_heard()
    {
        var sent = 0;
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            if ((await request.Content!.ReadAsStringAsync(ct)).Contains("so we ship", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref sent);
            }

            return ScriptedHttpHandler.ChatCompletion("So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        svc.UnloadWaitBound = TimeSpan.FromMilliseconds(300);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.LocalServers.UnloadGate = gate;

        var free = svc.FreeLocalAppModelAsync(Ollama, "gemma4:e2b");
        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);
        var result = await svc.CleanAsync("um so we ship on friday").WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Skipped, result.Outcome);
        Assert.Equal("um so we ship on friday", result.Text);
        Assert.Equal(0, Volatile.Read(ref sent));
        harness.LocalServers.UnloadGate = null;
        gate.SetResult();
        Assert.True(await free.WaitAsync(Bound));
    }

    [Fact]
    public async Task Test_connection_and_the_readying_request_wait_for_an_unload_already_on_its_way()
    {
        var unloading = 0;
        var sentWhileUnloading = 0;
        var http = new ScriptedHttpHandler((_, _) =>
        {
            if (Volatile.Read(ref unloading) == 1)
            {
                Interlocked.Increment(ref sentWhileUnloading);
            }

            return Task.FromResult(ScriptedHttpHandler.ChatCompletion("ok"));
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.LocalServers.UnloadGate = gate;
        harness.LocalServers.Unloaded += () => Volatile.Write(ref unloading, 1);

        var free = svc.FreeLocalAppModelAsync(Ollama, "gemma4:e2b");
        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);
        var reads = harness.LocalServers.Reads;
        var test = svc.TestAsync(CleanupHarness.Custom(Ollama, "gemma4:e2b"));
        svc.ForgetLastModelAnswerForTesting();
        svc.Admit(CleanupVocabulary.None).Prewarm();
        await Task.Delay(300);

        Assert.False(test.IsCompleted);
        Assert.Equal(reads, harness.LocalServers.Reads);
        Assert.Equal(0, Volatile.Read(ref sentWhileUnloading));

        Volatile.Write(ref unloading, 0);
        harness.LocalServers.UnloadGate = null;
        gate.SetResult();
        Assert.True(await free.WaitAsync(Bound));
        await test.WaitAsync(Bound);
        await svc.WaitForPrewarmForTesting().WaitAsync(Bound);
        Assert.True(harness.LocalServers.Reads > reads);
    }

    /// <summary>
    /// An idle release carries the authority of its claim: a one-off request that was already running when the idle time
    /// ran out, and answers while the release waits for it, starts a new idle period, and the release stays home.
    /// </summary>
    [Fact]
    public async Task An_idle_release_withdrawn_while_it_waits_for_a_request_stays_home()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            if ((await request.Content!.ReadAsStringAsync(ct)).Contains("some history", StringComparison.Ordinal))
            {
                inFlight.TrySetResult();
                await answer.Task.WaitAsync(ct);
            }

            return ScriptedHttpHandler.ChatCompletion("Suggested.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var current = 1;
        svc.LocalModelUsed += () => Volatile.Write(ref current, 0); // what the controller's NoteActivity does to the ticket

        var suggestion = svc.CompleteAsync("Suggest dictionary words.", "some history", svc.Recipient!);
        await inFlight.Task.WaitAsync(Bound);
        svc.ReleaseModelMemory(ModelMemoryRelease.Idle, stillWanted: () => Volatile.Read(ref current) == 1);
        await Task.Delay(200);
        answer.SetResult();
        Assert.Equal(CompletionOutcome.Completed, (await suggestion.WaitAsync(Bound)).Outcome);
        await Task.Delay(300);

        Assert.NotEmpty(harness.State.LoadedIds());
    }

    /// <summary>
    /// A released Foundry Local model's reload is one use of the model, its load and its warm-up alike: a pause made while
    /// it loads waits for all of it, is not withdrawn by the warm-up, and then frees the model.
    /// </summary>
    [Fact]
    public async Task A_pause_s_release_waits_for_a_reload_and_its_warm_up_then_frees_the_model()
    {
        var holdProbe = 0;
        var probeHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new ScriptedHttpHandler(async (_, ct) =>
        {
            if (Volatile.Read(ref holdProbe) == 1)
            {
                probeHeld.TrySetResult();
                await probeRelease.Task.WaitAsync(ct);
            }

            return ScriptedHttpHandler.ChatCompletion("So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.AutomaticReleaseDrainBound = Bound;
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.True(await svc.UnloadFoundryModelAsync(CleanupHarness.FoundryAlias).WaitAsync(Bound));

        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Qwen.LoadGate = load;
        harness.Qwen.LoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.Admit(CleanupVocabulary.None).Prewarm(); // a recording starts: the model loads again
        await harness.Qwen.LoadStarted.Task.WaitAsync(Bound);

        svc.ReleaseModelMemory(ModelMemoryRelease.Pause, stillWanted: () => true); // paused while it loads
        Volatile.Write(ref holdProbe, 1);
        harness.Qwen.LoadGate = null;
        load.SetResult();
        await probeHeld.Task.WaitAsync(Bound); // its warm-up has begun
        await Task.Delay(200);
        Assert.NotEmpty(harness.State.LoadedIds());

        Volatile.Write(ref holdProbe, 0);
        probeRelease.SetResult();
        await WaitUntilAsync(() => harness.State.LoadedIds().Length == 0);
    }

    // The readying request: a one-token ceiling, which no other request has.
    private static bool IsReadying(string body)
    {
        using var json = JsonDocument.Parse(body);
        return (json.RootElement.TryGetProperty("max_tokens", out var max) && max.GetInt32() == 1) ||
            (json.RootElement.TryGetProperty("options", out var options) &&
                options.TryGetProperty("num_predict", out var predicted) && predicted.GetInt32() == 1);
    }

    private static HttpResponseMessage ModelAnswer(string content, bool nativeApi) =>
        nativeApi
            ? ScriptedHttpHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                model = "gemma4:e2b",
                created_at = "2026-10-08T00:00:00Z",
                message = new { role = "assistant", content },
                done = true,
                done_reason = "stop",
                prompt_eval_count = 40,
                eval_count = 1,
            }))
            : ScriptedHttpHandler.ChatCompletion(content);

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
