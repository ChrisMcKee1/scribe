using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

/// <summary>
/// The LM Studio copies Scribe loads at a size: one load at a time, a load whose settings were replaced settled before the
/// next settings read LM Studio and before a release frees the model, a copy no settings use asked for again until LM Studio
/// has unloaded it, the copy a request is using never changed under it, and Test connection testing the size Save would
/// load without leaving its copy behind or touching another.
/// </summary>
public sealed class LocalModelCopiesTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);
    private const string Model = "google/gemma-4-e2b";

    [Fact]
    public async Task A_new_size_waits_for_the_load_it_replaced_and_never_loads_beside_it()
    {
        await using var harness = new CleanupHarness();
        var (load, loadStarted) = GatedLoads(harness);

        harness.Service.Configure(LmStudio(16384));
        await loadStarted.Task.WaitAsync(Bound);
        harness.Service.Configure(LmStudio(32768));
        await Task.Delay(300);

        Assert.Single(harness.LocalServers.Loads);
        Assert.NotEqual(CleanupStatus.Ready, harness.Service.Status);

        load.SetResult();
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal(
            [$"load {Model} 16384", $"unload {Instance(Model, 16384)}", $"load {Model} 32768"],
            harness.LocalServers.Operations);
        Assert.Equal(32768, harness.Service.LocalContextTokens);
    }

    [Fact]
    public async Task A_replaced_load_LM_Studio_would_not_unload_is_asked_for_again_by_the_next_settings()
    {
        await using var harness = new CleanupHarness();
        var (load, loadStarted) = GatedLoads(harness);
        var unloads = 0;
        harness.LocalServers.InstanceUnloadAnswer = _ => Interlocked.Increment(ref unloads) > 1;

        harness.Service.Configure(LmStudio(16384));
        await loadStarted.Task.WaitAsync(Bound);

        // The same model with another key: settings in their own right, which no longer use the copy being loaded.
        harness.Service.Configure(LmStudio(null) with { CustomApiKey = "lm-token" });
        load.SetResult();
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var stale = $"unload {Instance(Model, 16384)}";
        Assert.Equal([$"load {Model} 16384", stale, stale], harness.LocalServers.Operations);
        Assert.Empty(harness.LocalServers.State.Loaded);
    }

    [Fact]
    public async Task Test_connection_loads_LM_Studio_at_the_chosen_size_and_frees_that_copy_once_done()
    {
        await using var harness = new CleanupHarness();
        LmStudioReady(harness);

        var result = await harness.Service.TestAsync(LmStudio(32768)).WaitAsync(Bound);

        Assert.Equal(CleanupTestOutcome.Connected, result.Outcome);
        await harness.LocalServers.WaitForInstanceUnloadsAsync(1, Bound);
        Assert.Equal([$"load {Model} 32768", $"unload {Instance(Model, 32768)}"], harness.LocalServers.Operations);
        Assert.Empty(harness.LocalServers.State.Loaded);
    }

    [Fact]
    public async Task Test_connection_fails_when_LM_Studio_cannot_load_the_chosen_size()
    {
        var requests = 0;
        await using var harness = new CleanupHarness(http: new ScriptedHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(ScriptedHttpHandler.ChatCompletion("ok"));
        }));
        LmStudioReady(harness);
        harness.LocalServers.LoadAnswer = (_, _) => null;

        var result = await harness.Service.TestAsync(LmStudio(32768)).WaitAsync(Bound);

        Assert.Equal(CleanupTestOutcome.Failed, result.Outcome);
        Assert.Contains("couldn't load the model with the context size you chose", result.SafeReason, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task Test_connection_leaves_a_copy_LM_Studio_already_holds_alone()
    {
        await using var harness = new CleanupHarness();
        LmStudioReady(harness);
        harness.LocalServers.State = LmStudioHoldsOnDemand(8192);

        var result = await harness.Service.TestAsync(LmStudio(32768)).WaitAsync(Bound);

        Assert.Equal(CleanupTestOutcome.Connected, result.Outcome);
        await Task.Delay(200);
        Assert.Empty(harness.LocalServers.Operations);
    }

    [Fact]
    public async Task A_copy_Test_connection_loads_for_the_settings_in_use_is_kept_as_theirs()
    {
        var harness = new CleanupHarness();
        LmStudioReady(harness);
        var settings = LmStudio(32768) with { LocalModelKeepAliveMinutes = 10 };
        harness.Service.Configure(settings);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // LM Studio evicted it; testing the same settings loads it again, for them.
        harness.LocalServers.State = LmStudioHasModel();
        var result = await harness.Service.TestAsync(settings).WaitAsync(Bound);

        Assert.Equal(CleanupTestOutcome.Connected, result.Outcome);
        await Task.Delay(200);
        var copy = Instance(Model, 32768);
        Assert.Equal([$"load {Model} 32768", $"load {Model} 32768"], harness.LocalServers.Operations);
        Assert.Equal(32768, harness.Service.LocalContextTokens);

        // Theirs, so Scribe frees it as it closes.
        await harness.DisposeAsync();
        Assert.Equal($"unload {copy}", harness.LocalServers.Operations[^1]);
    }

    [Fact]
    public async Task A_test_copy_LM_Studio_would_not_unload_is_asked_for_again_at_the_next_release()
    {
        await using var harness = new CleanupHarness();
        LmStudioReady(harness);
        harness.LocalServers.InstanceUnloadAnswer = _ => false;

        Assert.Equal(CleanupTestOutcome.Connected, (await harness.Service.TestAsync(LmStudio(32768)).WaitAsync(Bound)).Outcome);
        await harness.LocalServers.WaitForInstanceUnloadsAsync(1, Bound);
        Assert.Single(harness.LocalServers.State.Loaded);

        harness.LocalServers.InstanceUnloadAnswer = _ => true;
        harness.Service.ReleaseModelMemory(ModelMemoryRelease.Pause);

        await harness.LocalServers.WaitForInstanceUnloadsAsync(2, Bound);
        Assert.All(harness.LocalServers.InstanceUnloads, unload => Assert.Equal(Instance(Model, 32768), unload.InstanceId));
        Assert.Empty(harness.LocalServers.State.Loaded);
    }

    [Fact]
    public async Task A_copy_no_settings_use_is_freed_as_Scribe_closes_whatever_the_idle_time()
    {
        var harness = new CleanupHarness();
        LmStudioReady(harness);
        harness.LocalServers.InstanceUnloadAnswer = _ => false;
        Assert.Equal(CleanupTestOutcome.Connected, (await harness.Service.TestAsync(LmStudio(32768)).WaitAsync(Bound)).Outcome);
        await harness.LocalServers.WaitForInstanceUnloadsAsync(1, Bound);

        harness.LocalServers.InstanceUnloadAnswer = _ => true;
        await harness.DisposeAsync();

        Assert.Equal(2, harness.LocalServers.InstanceUnloads.Count);
        Assert.Empty(harness.LocalServers.State.Loaded);
    }

    [Fact]
    public async Task A_copy_LM_Studio_no_longer_lists_is_forgotten_without_asking_again()
    {
        await using var harness = new CleanupHarness();
        LmStudioReady(harness);
        harness.LocalServers.InstanceUnloadAnswer = _ => false;
        Assert.Equal(CleanupTestOutcome.Connected, (await harness.Service.TestAsync(LmStudio(32768)).WaitAsync(Bound)).Outcome);
        await harness.LocalServers.WaitForInstanceUnloadsAsync(1, Bound);

        // LM Studio dropped it on its own; the next release reads that and asks nothing.
        harness.LocalServers.State = LmStudioHasModel();
        harness.Service.ReleaseModelMemory(ModelMemoryRelease.Pause);
        await Task.Delay(500);
        harness.Service.ReleaseModelMemory(ModelMemoryRelease.Pause);
        await Task.Delay(500);

        Assert.Single(harness.LocalServers.InstanceUnloads);
    }

    [Fact]
    public async Task A_new_size_leaves_the_copy_a_request_is_using_and_the_next_recording_replaces_it()
    {
        const string Dictated = "the quarterly numbers look good so far";
        var dictationSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerDictation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new CleanupHarness(http: new ScriptedHttpHandler(async (request, ct) =>
        {
            if ((await request.Content!.ReadAsStringAsync(ct)).Contains(Dictated, StringComparison.Ordinal))
            {
                dictationSent.TrySetResult();
                await answerDictation.Task.WaitAsync(ct);
            }

            return ScriptedHttpHandler.ChatCompletion("The quarterly numbers look good so far.");
        }));
        LmStudioReady(harness);
        harness.Service.Configure(LmStudio(16384));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var cleaning = harness.Service.CleanAsync(Dictated);
        await dictationSent.Task.WaitAsync(Bound);

        // A new size while the dictation's request reaches the copy: it is not unloaded under the request, and requests are
        // fitted to the copy they reach.
        harness.Service.Configure(LmStudio(32768));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal([$"load {Model} 16384"], harness.LocalServers.Operations);
        Assert.Equal(16384, harness.Service.LocalContextTokens);

        answerDictation.SetResult();
        Assert.Equal("The quarterly numbers look good so far.", (await cleaning.WaitAsync(Bound)).Text);

        // The next recording finds it at another size and replaces it.
        harness.Service.ForgetLastModelAnswerForTesting();
        harness.Service.Admit(CleanupVocabulary.None).Prewarm();
        await harness.Service.WaitForPrewarmForTesting().WaitAsync(Bound);
        Assert.Equal(
            [$"load {Model} 16384", $"unload {Instance(Model, 16384)}", $"load {Model} 32768"],
            harness.LocalServers.Operations);
        Assert.Equal(32768, harness.Service.LocalContextTokens);
    }

    [Fact]
    public async Task A_dictation_waits_for_a_copy_being_loaded_at_its_size_as_long_as_for_a_model_starting()
    {
        await using var harness = new CleanupHarness(http: new ScriptedHttpHandler((_, _) =>
            Task.FromResult(ScriptedHttpHandler.ChatCompletion("Please send the report today."))));
        LmStudioReady(harness);
        harness.Service.Configure(LmStudio(16384));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.Service.UnloadWaitBound = TimeSpan.FromMilliseconds(200);
        harness.Service.LocalModelStartWait = TimeSpan.FromSeconds(15);

        // LM Studio evicted the copy; the recording loads it again at the size, slowly.
        harness.LocalServers.State = LmStudioHasModel();
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.LocalServers.LoadGate = load;
        harness.LocalServers.LoadStarted += () => loadStarted.TrySetResult();
        harness.Service.ForgetLastModelAnswerForTesting();
        harness.Service.Admit(CleanupVocabulary.None).Prewarm();
        await loadStarted.Task.WaitAsync(Bound);

        var cleaning = harness.Service.CleanAsync("please send the report today");
        await Task.Delay(600);
        Assert.False(cleaning.IsCompleted);

        load.SetResult();
        var result = await cleaning.WaitAsync(Bound);
        Assert.Equal("Please send the report today.", result.Text);
        Assert.NotEqual(CleanupOutcome.Skipped, result.Outcome);
    }

    [Fact]
    public async Task A_release_waits_for_a_load_handed_to_the_background_before_it_frees_the_model()
    {
        await using var harness = new CleanupHarness();
        var (load, loadStarted) = GatedLoads(harness);
        harness.Service.Configure(LmStudio(16384));
        await loadStarted.Task.WaitAsync(Bound);

        // Settings that no longer use the copy being loaded: the load is settled in the background, as one use of the model,
        // which both the release for turning cleanup off and Free memory wait for.
        harness.Service.Configure(LmStudio(16384) with { Enabled = false });
        await harness.WaitForStatusAsync(CleanupStatus.Disabled);
        var freeing = harness.Service.FreeLocalAppModelAsync(LocalAiServer.LmStudioAddress, Model);
        await Task.Delay(300);
        Assert.Equal([$"load {Model} 16384"], harness.LocalServers.Operations);

        load.SetResult();
        Assert.True(await freeing.WaitAsync(Bound));
        await harness.LocalServers.WaitForUnloadsAsync(2, Bound);
        Assert.Equal(
            [$"load {Model} 16384", $"unload {Instance(Model, 16384)}", $"unload-model {Model}", $"unload-model {Model}"],
            harness.LocalServers.Operations);
    }

    [Fact]
    public async Task A_one_off_request_loads_a_copy_LM_Studio_evicted_at_the_size_first()
    {
        await using var harness = new CleanupHarness();
        LmStudioReady(harness);
        harness.Service.Configure(LmStudio(16384));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        harness.LocalServers.State = LmStudioHasModel();
        var result = await harness.Service
            .CompleteAsync("List the terms.", "we shipped the kubernetes upgrade", harness.Service.Recipient!)
            .WaitAsync(Bound);

        Assert.Equal(CompletionOutcome.Completed, result.Outcome);
        Assert.Equal([$"load {Model} 16384", $"load {Model} 16384"], harness.LocalServers.Operations);
        Assert.Equal(16384, harness.Service.LocalContextTokens);
    }

    [Fact]
    public async Task A_one_off_request_forgets_the_size_of_a_copy_LM_Studio_no_longer_holds()
    {
        await using var harness = new CleanupHarness();
        harness.LocalServers.State = LmStudioHoldsOnDemand(32768);
        harness.Service.Configure(LmStudio(null));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(32768, harness.Service.LocalContextTokens);

        // Evicted: the request loads the model at whatever size LM Studio now loads it at, so the old size no longer holds.
        harness.LocalServers.State = LmStudioHasModel();
        var result = await harness.Service
            .CompleteAsync("List the terms.", "we shipped the kubernetes upgrade", harness.Service.Recipient!)
            .WaitAsync(Bound);

        Assert.Equal(CompletionOutcome.Completed, result.Outcome);
        Assert.Equal(0, harness.Service.LocalContextTokens);
        Assert.Empty(harness.LocalServers.Operations);
    }

    [Fact]
    public async Task Freeing_an_Ollama_model_of_the_same_name_leaves_Scribe_s_LM_Studio_copy_its_own()
    {
        var harness = new CleanupHarness();
        LmStudioReady(harness);
        harness.Service.Configure(LmStudio(16384) with { LocalModelKeepAliveMinutes = 10 });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.True(await harness.Service.FreeLocalAppModelAsync(LocalAiServer.OllamaAddress, Model).WaitAsync(Bound));
        Assert.Equal(16384, harness.Service.LocalContextTokens);

        // Still Scribe's, so Scribe frees it as it closes.
        await harness.DisposeAsync();
        Assert.Equal(
            [$"load {Model} 16384", $"unload-model {Model}", $"unload {Instance(Model, 16384)}"],
            harness.LocalServers.Operations);
    }

    [Fact]
    public async Task A_copy_an_earlier_size_loaded_is_freed_when_LM_Studio_already_holds_one_at_the_new_size()
    {
        await using var harness = new CleanupHarness();
        LmStudioReady(harness);
        harness.Service.Configure(LmStudio(16384));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // The model loaded by hand at the new size, listed first, so requests by its name reach it.
        harness.LocalServers.State = LmStudioHasModel() with
        {
            Loaded =
            [
                new LocalServerLoadedModel(Model, 4_000_000_000) { ContextTokens = 32768, InstanceId = "by-hand" },
                new LocalServerLoadedModel(Model, 4_000_000_000)
                {
                    ContextTokens = 16384,
                    InstanceId = Instance(Model, 16384),
                    RemainingTtlSeconds = 3600,
                },
            ],
        };
        harness.Service.Configure(LmStudio(32768));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal([$"load {Model} 16384", $"unload {Instance(Model, 16384)}"], harness.LocalServers.Operations);
        Assert.Equal(["by-hand"], harness.LocalServers.State.Loaded.Select(loaded => loaded.InstanceId));
        Assert.Equal(32768, harness.Service.LocalContextTokens);
    }

    [Fact]
    public async Task Every_copy_no_settings_use_is_asked_for_until_LM_Studio_frees_it()
    {
        var harness = new CleanupHarness();
        var models = Enumerable.Range(1, 10).Select(i => $"vendor/model-{i}").ToList();
        harness.LocalServers.State = new LocalServerState(
            LocalServerReach.Reached, [.. models.Select(model => new LocalServerModel(model, model, 1_000_000_000))], []);
        harness.LocalServers.LoadAnswer = Instance;
        harness.LocalServers.StateAfterLoad = (model, tokens) => harness.LocalServers.State with
        {
            Loaded =
            [
                .. harness.LocalServers.State.Loaded,
                new LocalServerLoadedModel(model, 1_000_000_000)
                {
                    ContextTokens = tokens,
                    InstanceId = Instance(model, tokens),
                    RemainingTtlSeconds = 3600,
                },
            ],
        };
        harness.LocalServers.InstanceUnloadAnswer = _ => false;

        foreach (var model in models)
        {
            var candidate = CleanupHarness.Custom(LocalAiServer.LmStudioAddress, model) with { LocalContextTokens = 16384 };
            Assert.Equal(CleanupTestOutcome.Connected, (await harness.Service.TestAsync(candidate).WaitAsync(Bound)).Outcome);
        }

        Assert.Equal(models.Count, harness.LocalServers.State.Loaded.Count);
        harness.LocalServers.InstanceUnloadAnswer = _ => true;
        await harness.DisposeAsync();

        Assert.Empty(harness.LocalServers.State.Loaded);
    }

    [Fact]
    public async Task Test_connection_at_a_size_fails_when_LM_Studio_cannot_be_reached()
    {
        var requests = 0;
        await using var harness = new CleanupHarness(http: new ScriptedHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(ScriptedHttpHandler.ChatCompletion("ok"));
        }));

        var result = await harness.Service.TestAsync(LmStudio(32768)).WaitAsync(Bound);

        Assert.Equal(CleanupTestOutcome.Failed, result.Outcome);
        Assert.Contains("couldn't reach LM Studio", result.SafeReason, StringComparison.Ordinal);
        Assert.Empty(harness.LocalServers.Operations);
        Assert.Equal(0, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task A_copy_earlier_settings_loaded_is_not_unloaded_while_a_request_of_theirs_is_in_flight()
    {
        var dictation = new GatedDictation();
        await using var harness = new CleanupHarness(http: dictation.Handler());
        TwoModelsReady(harness);
        harness.Service.Configure(LmStudio(16384));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var cleaning = harness.Service.CleanAsync(GatedDictation.Text);
        await dictation.Sent.Task.WaitAsync(Bound);

        // The other model, loaded by hand at the size the new settings ask for, is what their requests reach, so the copy the
        // dictation is using is one they do not: it stays while that request is in flight.
        harness.LocalServers.State = harness.LocalServers.State with
        {
            Loaded =
            [
                .. harness.LocalServers.State.Loaded,
                new LocalServerLoadedModel(Other, 4_000_000_000) { ContextTokens = 16384, InstanceId = Other },
            ],
        };
        harness.Service.Configure(LmStudio(16384) with { CustomModel = Other });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal([$"load {Model} 16384"], harness.LocalServers.Operations);

        dictation.Answer.SetResult();
        await cleaning.WaitAsync(Bound);

        // Once nothing uses it, it is freed: with the model AI cleanup stopped using, or at the next recording.
        harness.Service.ForgetLastModelAnswerForTesting();
        harness.Service.Admit(CleanupVocabulary.None).Prewarm();
        await harness.Service.WaitForPrewarmForTesting().WaitAsync(Bound);
        await WaitUntilAsync(() => harness.LocalServers.Operations.Any(operation =>
            operation == $"unload {Instance(Model, 16384)}" || operation == $"unload-model {Model}"));
    }

    [Fact]
    public async Task A_dictation_waits_for_the_copy_Test_connection_is_loading()
    {
        var dictation = new GatedDictation(hold: false);
        await using var harness = new CleanupHarness(http: dictation.Handler());
        TwoModelsReady(harness);
        harness.Service.Configure(LmStudio(16384));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var (load, loadStarted) = GateLoads(harness);

        var testing = harness.Service.TestAsync(LmStudio(16384) with { CustomModel = Other });
        await loadStarted.Task.WaitAsync(Bound);
        var cleaning = harness.Service.CleanAsync(GatedDictation.Text);
        await Task.Delay(300);
        Assert.Equal(0, dictation.Requests);

        load.SetResult();
        Assert.Equal(GatedDictation.Cleaned, (await cleaning.WaitAsync(Bound)).Text);
        Assert.Equal(CleanupTestOutcome.Connected, (await testing.WaitAsync(Bound)).Outcome);
    }

    [Fact]
    public async Task A_cancelled_test_s_load_holds_back_requests_until_LM_Studio_has_loaded_it()
    {
        var dictation = new GatedDictation(hold: false);
        await using var harness = new CleanupHarness(http: dictation.Handler());
        TwoModelsReady(harness);
        harness.Service.Configure(LmStudio(16384));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var (load, loadStarted) = GateLoads(harness);

        using var cancel = new CancellationTokenSource();
        var testing = harness.Service.TestAsync(LmStudio(16384) with { CustomModel = Other }, cancel.Token);
        await loadStarted.Task.WaitAsync(Bound);
        cancel.Cancel();
        Assert.Equal(CleanupTestOutcome.Cancelled, (await testing.WaitAsync(Bound)).Outcome);

        var cleaning = harness.Service.CleanAsync(GatedDictation.Text);
        await Task.Delay(300);
        Assert.Equal(0, dictation.Requests);

        // Once it has loaded, the copy no settings use is freed, and the dictation goes.
        load.SetResult();
        Assert.Equal(GatedDictation.Cleaned, (await cleaning.WaitAsync(Bound)).Text);
        await WaitUntilAsync(() => harness.LocalServers.Operations.Contains($"unload {Instance(Other, 16384)}"));
    }

    [Fact]
    public async Task Test_connection_at_a_size_loads_nothing_while_AI_cleanup_is_using_the_model()
    {
        var dictation = new GatedDictation();
        await using var harness = new CleanupHarness(http: dictation.Handler());
        TwoModelsReady(harness);
        harness.Service.Configure(LmStudio(16384));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var cleaning = harness.Service.CleanAsync(GatedDictation.Text);
        await dictation.Sent.Task.WaitAsync(Bound);

        var result = await harness.Service.TestAsync(LmStudio(16384) with { CustomModel = Other }).WaitAsync(Bound);

        Assert.Equal(CleanupTestOutcome.Failed, result.Outcome);
        Assert.Contains("using the model in LM Studio right now", result.SafeReason, StringComparison.Ordinal);
        Assert.Equal([$"load {Model} 16384"], harness.LocalServers.Operations);
        dictation.Answer.SetResult();
        await cleaning.WaitAsync(Bound);
    }

    [Fact]
    public async Task Test_connection_at_a_size_says_the_instructions_leave_no_room_before_loading_anything()
    {
        var requests = 0;
        await using var harness = new CleanupHarness(http: new ScriptedHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(ScriptedHttpHandler.ChatCompletion("ok"));
        }));
        LmStudioReady(harness);

        var result = await harness.Service
            .TestAsync(LmStudio(ContextBudget.MinimumSize) with { WritingStyle = LongStyle(8000) })
            .WaitAsync(Bound);

        Assert.Equal(CleanupTestOutcome.Failed, result.Outcome);
        Assert.Contains("don't fit in what the AI model on this PC reads at once", result.SafeReason, StringComparison.Ordinal);
        Assert.Empty(harness.LocalServers.Operations);
        Assert.Equal(0, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task A_one_off_request_that_ran_out_of_time_behind_LM_Studio_work_is_not_sent()
    {
        var completions = 0;
        await using var harness = new CleanupHarness(http: new ScriptedHttpHandler(async (request, ct) =>
        {
            if ((await request.Content!.ReadAsStringAsync(ct)).Contains("List the terms.", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref completions);
            }

            return ScriptedHttpHandler.ChatCompletion("ok");
        }));
        LmStudioReady(harness);
        var settings = LmStudio(16384);
        harness.Service.Configure(settings);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.Service.LocalServerPrepareBound = TimeSpan.FromMilliseconds(300);

        // A Test connection of the same settings reads LM Studio slowly, keeping LM Studio's lane meanwhile.
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.LocalServers.ReadGate = read;
        harness.LocalServers.ReadStarted += () => readStarted.TrySetResult();
        var testing = harness.Service.TestAsync(settings);
        await readStarted.Task.WaitAsync(Bound);

        var result = await harness.Service
            .CompleteAsync("List the terms.", "we shipped the kubernetes upgrade", harness.Service.Recipient!)
            .WaitAsync(Bound);

        Assert.Equal(CompletionOutcome.NotReady, result.Outcome);
        Assert.Equal(0, Volatile.Read(ref completions));
        read.SetResult();
        Assert.Equal(CleanupTestOutcome.Connected, (await testing.WaitAsync(Bound)).Outcome);
    }

    [Fact]
    public async Task A_recording_during_a_sized_test_s_check_fits_its_dictation_to_the_copy_the_test_loaded()
    {
        var holdNext = 0;
        var checkHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new CleanupHarness(http: new ScriptedHttpHandler(async (_, ct) =>
        {
            if (Interlocked.Exchange(ref holdNext, 0) == 1)
            {
                checkHeld.TrySetResult();
                await answerCheck.Task.WaitAsync(ct);
            }

            return ScriptedHttpHandler.ChatCompletion(GatedDictation.Cleaned);
        }));
        LmStudioReady(harness);
        harness.Service.Configure(LmStudio(32768));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // LM Studio evicted the copy; a test of a smaller size loads one and is slow to answer its check.
        harness.LocalServers.State = LmStudioHasModel();
        Volatile.Write(ref holdNext, 1);
        var testing = harness.Service.TestAsync(LmStudio(ContextBudget.MinimumSize * 2));
        await checkHeld.Task.WaitAsync(Bound);

        // A recording meanwhile reads the copy the test loaded, which its requests would reach, and fits to it.
        harness.Service.ForgetLastModelAnswerForTesting();
        harness.Service.Admit(CleanupVocabulary.None).Prewarm();
        await harness.Service.WaitForPrewarmForTesting().WaitAsync(Bound);
        Assert.Equal(ContextBudget.MinimumSize * 2, harness.Service.LocalContextTokens);

        answerCheck.SetResult();
        Assert.Equal(CleanupTestOutcome.Connected, (await testing.WaitAsync(Bound)).Outcome);
    }

    [Fact]
    public async Task Test_connection_fits_a_copy_whose_size_LM_Studio_does_not_say_to_what_is_assumed()
    {
        var requests = 0;
        await using var harness = new CleanupHarness(http: new ScriptedHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(ScriptedHttpHandler.ChatCompletion("ok"));
        }));
        LmStudioReady(harness);
        harness.LocalServers.State = LmStudioHasModel() with
        {
            Loaded = [new LocalServerLoadedModel(Model, 4_000_000_000) { InstanceId = Model }],
        };

        // Instructions that fit the size asked for, but not the context assumed for a copy of unknown size.
        var result = await harness.Service
            .TestAsync(LmStudio(32768) with { WritingStyle = LongStyle(12_000) })
            .WaitAsync(Bound);

        Assert.Equal(CleanupTestOutcome.Failed, result.Outcome);
        Assert.Contains("don't fit in what the AI model on this PC reads at once", result.SafeReason, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task A_copy_loaded_with_a_key_LM_Studio_no_longer_takes_is_freed_with_the_settings_new_key()
    {
        await using var harness = new CleanupHarness();
        TwoModelsReady(harness);
        harness.Service.Configure(LmStudio(16384) with { CustomApiKey = "old-key" });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // The key changed in LM Studio and in the settings, which now ask for a size the model is held at by hand, listed
        // first: the copy Scribe loaded is one they no longer reach.
        harness.LocalServers.KeyAccepted = key => key == "new-key";
        harness.LocalServers.State = harness.LocalServers.State with
        {
            Loaded =
            [
                new LocalServerLoadedModel(Model, 4_000_000_000) { ContextTokens = 32768, InstanceId = "by-hand" },
                .. harness.LocalServers.State.Loaded,
            ],
        };
        harness.Service.Configure(LmStudio(32768) with { CustomApiKey = "new-key" });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal(["by-hand"], harness.LocalServers.State.Loaded.Select(loaded => loaded.InstanceId));
    }

    [Fact]
    public async Task A_test_copy_loaded_with_a_key_LM_Studio_no_longer_takes_is_freed_with_the_settings_key()
    {
        await using var harness = new CleanupHarness();
        TwoModelsReady(harness);
        harness.Service.Configure(LmStudio(null) with { CustomModel = Other, CustomApiKey = "new-key" });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // A test with an older key loads a copy no settings use; LM Studio refuses to unload it the first time Scribe asks,
        // which Scribe does with the settings' key.
        var unloadCalls = 0;
        harness.LocalServers.InstanceUnloadAnswer = _ => Interlocked.Increment(ref unloadCalls) > 1;
        var tested = await harness.Service.TestAsync(LmStudio(32768) with { CustomApiKey = "old-key" }).WaitAsync(Bound);
        Assert.Equal(CleanupTestOutcome.Connected, tested.Outcome);
        await harness.LocalServers.WaitForInstanceUnloadsAsync(1, Bound);

        // LM Studio now takes only the settings' key, and the settings move to another service: the copy is still asked for
        // with the key that last opened LM Studio.
        harness.LocalServers.KeyAccepted = key => key == "new-key";
        harness.Service.Configure(CleanupHarness.OtherProviderOff);
        await harness.WaitForStatusAsync(CleanupStatus.Disabled);
        harness.Service.ReleaseModelMemory(ModelMemoryRelease.Pause);

        await WaitUntilAsync(() => harness.LocalServers.State.Loaded.All(loaded => loaded.InstanceId != Instance(Model, 32768)));
    }

    [Fact]
    public async Task A_model_cleanup_stops_using_is_freed_with_the_key_LM_Studio_takes_now()
    {
        await using var harness = new CleanupHarness();
        TwoModelsReady(harness);
        harness.LocalServers.State = harness.LocalServers.State with
        {
            Loaded =
            [
                new LocalServerLoadedModel(Model, 4_000_000_000) { ContextTokens = 4096, InstanceId = Model, RemainingTtlSeconds = 3600 },
            ],
        };
        harness.Service.Configure(LmStudio(null) with { CustomApiKey = "old-key" });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        harness.LocalServers.KeyAccepted = key => key == "new-key";
        harness.Service.Configure(LmStudio(null) with { CustomModel = Other, CustomApiKey = "new-key" });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        await WaitUntilAsync(() => harness.LocalServers.State.Loaded.All(loaded => !LocalServerClient.SameModel(loaded.Id, Model)));
    }

    private static CleanupOptions LmStudio(int? size) =>
        CleanupHarness.Custom(LocalAiServer.LmStudioAddress, Model) with { LocalContextTokens = size };

    private const string Other = "qwen/qwen3-4b";

    private static string Instance(string model, int contextTokens) => $"{model}:{contextTokens}";

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not hold in time.");
            }

            await Task.Delay(10);
        }
    }

    private static string LongStyle(int chars)
    {
        const string Sentence = "Keep every sentence short and plain. ";
        return string.Concat(Enumerable.Repeat(Sentence, (chars / Sentence.Length) + 1)).Trim();
    }

    // LM Studio has two models and holds neither; each load at a size adds a copy named for its model and size.
    private static void TwoModelsReady(CleanupHarness harness)
    {
        harness.LocalServers.State = new LocalServerState(
            LocalServerReach.Reached,
            [
                new LocalServerModel(Model, "Gemma 4 E2B", 4_000_000_000) { MaxContextTokens = 131072 },
                new LocalServerModel(Other, "Qwen3 4B", 4_000_000_000) { MaxContextTokens = 131072 },
            ],
            []);
        harness.LocalServers.LoadAnswer = Instance;
        harness.LocalServers.StateAfterLoad = (model, tokens) => harness.LocalServers.State with
        {
            Loaded =
            [
                .. harness.LocalServers.State.Loaded,
                new LocalServerLoadedModel(model, 4_000_000_000)
                {
                    ContextTokens = tokens,
                    InstanceId = Instance(model, tokens),
                    RemainingTtlSeconds = 3600,
                },
            ],
        };
    }

    // Holds every load from now on until the test lets it go.
    private static (TaskCompletionSource Load, TaskCompletionSource Started) GateLoads(CleanupHarness harness)
    {
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.LocalServers.LoadGate = load;
        harness.LocalServers.LoadStarted += () => started.TrySetResult();
        return (load, started);
    }

    // A dictation's requests, counted, and held until the test answers them unless hold is false; every request is answered
    // with the cleaned dictation.
    private sealed class GatedDictation(bool hold = true)
    {
        public const string Text = "the quarterly numbers look good so far";
        public const string Cleaned = "The quarterly numbers look good so far.";
        private int _requests;

        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Requests => Volatile.Read(ref _requests);

        public ScriptedHttpHandler Handler() => new(async (request, ct) =>
        {
            if ((await request.Content!.ReadAsStringAsync(ct)).Contains(Text, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _requests);
                Sent.TrySetResult();
                if (hold)
                {
                    await Answer.Task.WaitAsync(ct);
                }
            }

            return ScriptedHttpHandler.ChatCompletion(Cleaned);
        });
    }

    // LM Studio has the model and holds nothing; a load at a size leaves a copy named for its size.
    private static void LmStudioReady(CleanupHarness harness)
    {
        harness.LocalServers.State = LmStudioHasModel();
        harness.LocalServers.StateAfterLoad = LoadedAt;
        harness.LocalServers.LoadAnswer = Instance;
    }

    private static (TaskCompletionSource Load, TaskCompletionSource Started) GatedLoads(CleanupHarness harness)
    {
        LmStudioReady(harness);
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.LocalServers.LoadGate = load;
        harness.LocalServers.LoadStarted += () => started.TrySetResult();
        return (load, started);
    }

    private static LocalServerState LmStudioHasModel() => new(
        LocalServerReach.Reached,
        [new LocalServerModel(Model, "Gemma 4 E2B", 4_000_000_000) { MaxContextTokens = 131072 }],
        []);

    private static LocalServerState LmStudioHoldsOnDemand(int contextTokens) => LmStudioHasModel() with
    {
        Loaded =
        [
            new LocalServerLoadedModel(Model, 4_000_000_000)
            {
                ContextTokens = contextTokens,
                InstanceId = Model,
                RemainingTtlSeconds = 3600,
            },
        ],
    };

    private static LocalServerState LoadedAt(string model, int contextTokens) => LmStudioHasModel() with
    {
        Loaded =
        [
            new LocalServerLoadedModel(model, 4_000_000_000)
            {
                ContextTokens = contextTokens,
                InstanceId = Instance(model, contextTokens),
                RemainingTtlSeconds = 3600,
            },
        ],
    };
}
