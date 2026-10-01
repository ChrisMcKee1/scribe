using System.Net;
using System.Text.Json;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests;

/// <summary>
/// Which context a request to a model on this PC is fitted into, and how a dictation is planned to fit it. Ollama with a
/// size keeps the size Scribe asks for, capped at what the model takes, whatever another app loaded the model at; with
/// Ollama's own setting a reading before Scribe's own request can only lower what is known. LM Studio's copies are Scribe's
/// only once LM Studio has said so, and stop being Scribe's only once LM Studio has unloaded them. Every request, with the
/// longest answer it declares, fits the context, under the instructions it runs with.
/// </summary>
public sealed class LocalContextRulesTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);
    private const string OllamaModel = "gemma4:e4b";
    private const string LmStudioModel = "google/gemma-4-e2b";

    [Fact]
    public async Task Ollama_with_a_size_keeps_it_when_another_app_loaded_the_model_at_another()
    {
        var entries = Vocabulary(1200);
        var bodies = new List<JsonElement>();
        await using var harness = OllamaNative(bodies);
        harness.LocalServers.State = OllamaHolds(65536);
        harness.Service.Configure(Ollama(16384) with
        {
            VocabularyMode = CleanupVocabularyMode.Mentioned,
            SendWholeVocabulary = true,
        });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(16384, harness.Service.LocalContextTokens);

        // The recording finds the model held at another size, which Scribe's next request loads again at its own, so the
        // readying request goes out although the model answered a moment ago.
        var before = Snapshot(bodies).Count;
        harness.Service.Admit(Admitted(entries)).Prewarm();
        await harness.Service.WaitForPrewarmForTesting().WaitAsync(Bound);
        var readying = Assert.Single(Snapshot(bodies).Skip(before));
        Assert.Equal(1, Options(readying).GetProperty("num_predict").GetInt32());
        AssertFits(readying, 16384);

        await harness.Service.Admit(Admitted(entries)).CleanAsync("please send the report today").WaitAsync(Bound);

        var dictation = Snapshot(bodies)[^1];
        AssertFits(dictation, 16384);
        Assert.Contains(Line(entries[0]), SystemMessage(dictation), StringComparison.Ordinal);
        Assert.DoesNotContain(Line(entries[^1]), SystemMessage(dictation), StringComparison.Ordinal);
        Assert.Equal(16384, harness.Service.LocalContextTokens);
    }

    [Fact]
    public async Task Ollama_s_size_is_capped_at_the_largest_the_model_takes()
    {
        var bodies = new List<JsonElement>();
        await using var harness = OllamaNative(bodies);
        harness.LocalServers.MaxContextTokens = 8192;
        harness.Service.Configure(Ollama(32768));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        await harness.Service.CleanAsync("please send the report today").WaitAsync(Bound);

        Assert.All(Snapshot(bodies), body => Assert.Equal(8192, Options(body).GetProperty("num_ctx").GetInt32()));
        Assert.Equal(8192, harness.Service.LocalContextTokens);
    }

    [Fact]
    public async Task Without_the_model_s_details_what_Ollama_loaded_for_Scribe_s_own_request_caps_the_size()
    {
        var bodies = new List<JsonElement>();
        await using var harness = OllamaNative(bodies);
        harness.LocalServers.State = OllamaHolds(8192);
        harness.Service.Configure(Ollama(32768));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal(8192, harness.Service.LocalContextTokens);
        await harness.Service.CleanAsync("please send the report today").WaitAsync(Bound);
        AssertFits(Snapshot(bodies)[^1], 8192);
    }

    [Fact]
    public async Task With_Ollama_s_setting_a_reading_before_Scribe_s_own_request_only_lowers_the_context()
    {
        await using var harness = OllamaCompatible([]);
        harness.LocalServers.State = OllamaHolds(32768);
        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.OllamaAddress, OllamaModel));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(32768, harness.Service.LocalContextTokens);

        // Another app's copy at a larger size: Scribe's next request, at Ollama's setting, loads the model again at that.
        harness.LocalServers.State = OllamaHolds(65536);
        await PrewarmAsync(harness);
        Assert.Equal(32768, harness.Service.LocalContextTokens);

        // At a smaller one, the next dictation is fitted into it.
        harness.LocalServers.State = OllamaHolds(8192);
        await PrewarmAsync(harness);
        Assert.Equal(8192, harness.Service.LocalContextTokens);

        // What Ollama holds the model with after a readying request of Scribe's own is what its setting gives Scribe.
        harness.LocalServers.State = OllamaHolds(65536);
        harness.Service.ForgetLastModelAnswerForTesting();
        await PrewarmAsync(harness);
        Assert.Equal(65536, harness.Service.LocalContextTokens);
    }

    [Fact]
    public async Task A_model_Ollama_evicted_is_readied_at_the_next_recording_even_right_after_an_answer()
    {
        var bodies = new List<JsonElement>();
        await using var harness = OllamaCompatible(bodies);
        harness.LocalServers.State = OllamaHolds(32768);
        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.OllamaAddress, OllamaModel));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var before = Snapshot(bodies).Count;

        // Held, and answered a moment ago: nothing is sent.
        await PrewarmAsync(harness);
        Assert.Equal(before, Snapshot(bodies).Count);

        // Evicted since: the readying request loads it again while the user speaks.
        harness.LocalServers.State = OllamaHasModel();
        await PrewarmAsync(harness);
        Assert.Equal(before + 1, Snapshot(bodies).Count);
    }

    [Fact]
    public async Task A_copy_LM_Studio_evicted_is_loaded_again_at_the_next_recording_even_right_after_an_answer()
    {
        await using var harness = new CleanupHarness();
        harness.LocalServers.State = LmStudioHasModel();
        harness.LocalServers.StateAfterLoad = LmStudioLoadedAt;
        harness.Service.Configure(LmStudio(16384));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Single(harness.LocalServers.Loads);

        harness.LocalServers.State = LmStudioHasModel();
        await PrewarmAsync(harness);

        Assert.Equal(
            [(LocalAiServer.LmStudioAddress, LmStudioModel, 16384), (LocalAiServer.LmStudioAddress, LmStudioModel, 16384)],
            harness.LocalServers.Loads);
        Assert.Equal(16384, harness.Service.LocalContextTokens);
    }

    [Fact]
    public async Task A_copy_at_another_size_LM_Studio_will_not_unload_is_used_and_no_second_copy_is_loaded()
    {
        await using var harness = new CleanupHarness();
        harness.LocalServers.State = LmStudioHoldsOnDemand(4096);
        harness.LocalServers.StateAfterLoad = LmStudioLoadedAt;
        harness.LocalServers.InstanceUnloadAnswer = _ => false;

        harness.Service.Configure(LmStudio(16384));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal([(LocalAiServer.LmStudioAddress, LmStudioModel)], harness.LocalServers.InstanceUnloads);
        Assert.Empty(harness.LocalServers.Loads);
        Assert.Equal(4096, harness.Service.LocalContextTokens);
    }

    [Fact]
    public async Task A_size_LM_Studio_refused_is_not_asked_for_again_at_every_recording()
    {
        await using var harness = new CleanupHarness();
        harness.LocalServers.State = LmStudioHoldsOnDemand(4096);
        harness.LocalServers.LoadAnswer = (_, _) => null;

        harness.Service.Configure(LmStudio(16384));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Single(harness.LocalServers.Loads);
        Assert.Single(harness.LocalServers.InstanceUnloads);

        // The request that follows loads the model at LM Studio's own size, and every recording after it finds that copy.
        harness.LocalServers.State = LmStudioHoldsOnDemand(4096);
        for (var recording = 0; recording < 2; recording++)
        {
            harness.Service.ForgetLastModelAnswerForTesting();
            await PrewarmAsync(harness);
        }

        Assert.Single(harness.LocalServers.Loads);
        Assert.Single(harness.LocalServers.InstanceUnloads);
        Assert.Equal(4096, harness.Service.LocalContextTokens);

        // Another size is a new question.
        harness.Service.Configure(LmStudio(8192));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(2, harness.LocalServers.Loads.Count);
    }

    [Fact]
    public async Task Scribe_owns_the_copy_it_loaded_until_LM_Studio_has_unloaded_it()
    {
        var harness = new CleanupHarness();
        harness.LocalServers.State = LmStudioHasModel();
        harness.LocalServers.StateAfterLoad = LmStudioLoadedAt;
        harness.LocalServers.LoadAnswer = Instance;
        harness.Service.Configure(LmStudio(16384) with { LocalModelKeepAliveMinutes = 10 });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var scribes = (LocalAiServer.LmStudioAddress, Instance(LmStudioModel, 16384));

        // Back to LM Studio's own setting: Scribe unloads its copy, and keeps it as its own while LM Studio refuses.
        harness.LocalServers.InstanceUnloadAnswer = _ => false;
        harness.Service.Configure(LmStudio(null) with { LocalModelKeepAliveMinutes = 10 });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal([scribes], harness.LocalServers.InstanceUnloads);

        // At the next recording LM Studio takes the unload; the copy is then no longer Scribe's to free as Scribe closes.
        harness.LocalServers.InstanceUnloadAnswer = _ => true;
        harness.Service.ForgetLastModelAnswerForTesting();
        await PrewarmAsync(harness);
        Assert.Equal([scribes, scribes], harness.LocalServers.InstanceUnloads);
        Assert.Empty(harness.LocalServers.State.Loaded);

        await harness.DisposeAsync();
        Assert.Equal([scribes, scribes], harness.LocalServers.InstanceUnloads);
    }

    [Fact]
    public async Task A_copy_loaded_for_settings_replaced_meanwhile_is_freed_rather_than_left_for_LM_Studio_s_hour()
    {
        await using var harness = new CleanupHarness();
        harness.LocalServers.State = LmStudioHasModel();
        harness.LocalServers.StateAfterLoad = LmStudioLoadedAt;
        harness.LocalServers.LoadAnswer = Instance;
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.LocalServers.LoadGate = load;
        harness.LocalServers.LoadStarted += () => loadStarted.TrySetResult();

        harness.Service.Configure(LmStudio(16384));
        await loadStarted.Task.WaitAsync(Bound);
        harness.Service.Configure(LmStudio(16384) with { Enabled = false });
        await harness.WaitForStatusAsync(CleanupStatus.Disabled);
        load.SetResult();

        await harness.LocalServers.WaitForInstanceUnloadsAsync(1, Bound);
        Assert.Equal(
            [(LocalAiServer.LmStudioAddress, Instance(LmStudioModel, 16384))], harness.LocalServers.InstanceUnloads);
    }

    [Fact]
    public async Task A_copy_whose_load_ends_as_the_settings_change_is_freed_too()
    {
        await using var harness = new CleanupHarness();
        harness.LocalServers.State = LmStudioHasModel();
        harness.LocalServers.StateAfterLoad = LmStudioLoadedAt;
        harness.LocalServers.LoadAnswer = Instance;
        harness.LocalServers.LoadStarted += () => harness.Service.Configure(LmStudio(16384) with { Enabled = false });

        harness.Service.Configure(LmStudio(16384));

        await harness.LocalServers.WaitForInstanceUnloadsAsync(1, Bound);
        Assert.Equal(
            [(LocalAiServer.LmStudioAddress, Instance(LmStudioModel, 16384))], harness.LocalServers.InstanceUnloads);
        Assert.Equal(CleanupStatus.Disabled, harness.Service.Status);
    }

    [Fact]
    public async Task An_unload_on_its_way_goes_before_the_copy_a_new_initialization_loads()
    {
        await using var harness = new CleanupHarness();
        harness.LocalServers.State = LmStudioHasModel();
        harness.LocalServers.StateAfterLoad = LmStudioLoadedAt;
        harness.LocalServers.LoadAnswer = Instance;
        harness.Service.Configure(LmStudio(16384));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // A pause frees the model, and LM Studio is slow to answer.
        var unload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.LocalServers.UnloadGate = unload;
        harness.Service.ReleaseModelMemory(ModelMemoryRelease.Pause);
        await harness.LocalServers.WaitForUnloadsAsync(1, Bound);

        // A new size meanwhile: its load waits for the unload, which therefore cannot take the copy it loads.
        harness.Service.Configure(LmStudio(32768));
        await Task.Delay(300);
        Assert.Single(harness.LocalServers.Loads);

        harness.LocalServers.UnloadGate = null;
        unload.SetResult();
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(32768, harness.LocalServers.Loads[^1].ContextTokens);
        Assert.Equal(32768, harness.Service.LocalContextTokens);
    }

    [Fact]
    public async Task A_dictation_too_long_for_one_request_is_cleaned_in_chunks_that_each_fit()
    {
        var bodies = new List<JsonElement>();
        await using var harness = OllamaNative(bodies, echo: true);
        var instructions = TextCleanupService.BuildProbeSystemPrompt(Ollama(8192));
        var context = Math.Max(ContextBudget.MinimumSize, TokenEstimate.Prose(instructions) + 1100);
        harness.Service.Configure(Ollama(context));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var text = string.Join(' ', Enumerable.Repeat("we need to ship the build by thursday and tell the team.", 42)).Trim();
        var before = Snapshot(bodies).Count;

        var result = await harness.Service.CleanAsync(text).WaitAsync(Bound);

        Assert.True(result.Outcome is CleanupOutcome.Cleaned or CleanupOutcome.Unchanged, result.Outcome.ToString());
        var sent = Snapshot(bodies).Skip(before).ToList();
        Assert.True(sent.Count >= 2, $"Expected the dictation in more than one request, saw {sent.Count}.");
        Assert.All(sent, body => AssertFits(body, context));
        Assert.Equal(text, string.Join(' ', sent.Select(body => Transcript(UserMessage(body)))));
    }

    [Fact]
    public async Task A_dictation_whose_instructions_leave_no_room_is_typed_as_heard_and_nothing_is_sent()
    {
        var bodies = new List<JsonElement>();
        await using var harness = OllamaNative(bodies);
        harness.Service.Configure(Ollama(ContextBudget.MinimumSize));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var before = Snapshot(bodies).Count;
        var longStyle = string.Join(' ', Enumerable.Repeat("Keep every sentence short and plain.", 300));

        var result = await harness.Service.CleanAsync("please send the report today", writingStyleOverride: longStyle).WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Skipped, result.Outcome);
        Assert.Equal("please send the report today", result.Text);
        Assert.Contains("reads at once", result.DisplayDetail, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(bodies).Count);
    }

    [Fact]
    public async Task A_per_app_writing_style_takes_its_room_before_the_vocabulary()
    {
        var entries = Vocabulary(1200);
        var bodies = new List<JsonElement>();
        await using var harness = OllamaNative(bodies);
        harness.Service.Configure(Ollama(8192) with
        {
            VocabularyMode = CleanupVocabularyMode.Mentioned,
            SendWholeVocabulary = true,
        });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var style = string.Join(' ', Enumerable.Repeat("Keep every sentence short and plain.", 300));
        Assert.True(style.Length > CleanupPrompt.ResolveWritingStyle(null).Length + 2000);

        await harness.Service.Admit(Admitted(entries)).CleanAsync("please send the report today").WaitAsync(Bound);
        var plain = Snapshot(bodies)[^1];
        await harness.Service.Admit(Admitted(entries))
            .CleanAsync("please send the report today", writingStyleOverride: style)
            .WaitAsync(Bound);
        var styled = Snapshot(bodies)[^1];

        AssertFits(plain, 8192);
        AssertFits(styled, 8192, style);
        Assert.True(GlossaryLines(SystemMessage(styled)) < GlossaryLines(SystemMessage(plain)));
    }

    [Fact]
    public async Task The_chunk_that_costs_the_most_decides_the_vocabulary_every_chunk_carries()
    {
        // The second chunk is the shorter one, but every Cyrillic letter is a token, so it costs the most.
        var entries = Vocabulary(1200);
        var bodies = new List<JsonElement>();
        await using var harness = OllamaNative(bodies, echo: true);
        harness.Service.Configure(Ollama(8192) with
        {
            VocabularyMode = CleanupVocabularyMode.Mentioned,
            SendWholeVocabulary = true,
        });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var english = string.Join(' ', Enumerable.Repeat("we ship the build on thursday and tell the team.", 47));
        var russian = string.Join(' ', Enumerable.Repeat("мы выпускаем сборку в четверг и говорим команде.", 40));
        var before = Snapshot(bodies).Count;

        await harness.Service.Admit(Admitted(entries)).CleanAsync(english + " " + russian).WaitAsync(Bound);

        var sent = Snapshot(bodies).Skip(before).ToList();
        Assert.True(sent.Count >= 2, $"Expected two requests, saw {sent.Count}.");
        Assert.Single(sent.Select(SystemMessage).Distinct(StringComparer.Ordinal));
        Assert.All(sent, body => AssertFits(body, 8192));
    }

    [Fact]
    public async Task A_failed_read_at_a_recording_forgets_the_size_learned_for_an_earlier_copy()
    {
        var entries = Vocabulary(1500);
        var bodies = new List<JsonElement>();
        await using var harness = OllamaCompatible(bodies);
        harness.LocalServers.State = LmStudioHoldsOnDemand(32768);
        var options = LmStudio(null) with
        {
            VocabularyMode = CleanupVocabularyMode.Mentioned,
            SendWholeVocabulary = true,
        };
        harness.Service.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(32768, harness.Service.LocalContextTokens);

        // The user swapped the copy for a smaller one, and LM Studio does not answer the next recording's read.
        harness.LocalServers.State = LocalServerState.Failed;
        await PrewarmAsync(harness);
        Assert.Equal(0, harness.Service.LocalContextTokens);

        await harness.Service.Admit(Admitted(entries)).CleanAsync("please send the report today").WaitAsync(Bound);
        AssertFitsCompatible(Snapshot(bodies)[^1], options, ContextBudget.AssumedContextTokens);
    }

    [Fact]
    public async Task A_model_Ollama_no_longer_holds_is_fitted_to_what_is_known_until_Scribe_s_request_reaches_it()
    {
        await using var harness = OllamaCompatible([]);
        harness.LocalServers.State = OllamaHolds(32768);
        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.OllamaAddress, OllamaModel));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(32768, harness.Service.LocalContextTokens);

        // Evicted: whatever loads it next decides its size.
        harness.LocalServers.State = OllamaHasModel();
        await PrewarmAsync(harness);
        Assert.Equal(0, harness.Service.LocalContextTokens);

        // Held again: a reading before Scribe's own request only lowers what is known, and the one after it settles it.
        harness.LocalServers.State = OllamaHolds(32768);
        harness.Service.ForgetLastModelAnswerForTesting();
        await PrewarmAsync(harness);
        Assert.Equal(32768, harness.Service.LocalContextTokens);
    }

    [Fact]
    public async Task A_one_off_request_to_a_small_context_lowers_its_answer_ceiling_to_fit()
    {
        var bodies = new List<JsonElement>();
        await using var harness = OllamaCompatible(bodies);
        var options = CleanupHarness.Custom(LocalAiServer.OllamaAddress, OllamaModel);
        harness.Service.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var sample = Sample(AiDictionarySuggester.DefaultMaxSampleChars);

        var result = await harness.Service
            .CompleteAsync(AiDictionarySuggester.SystemPrompt, sample, harness.Service.Recipient!)
            .WaitAsync(Bound);

        Assert.Equal(CompletionOutcome.Completed, result.Outcome);
        var body = Snapshot(bodies)[^1];
        var system = SystemMessage(body);
        var context = ContextBudget.AssumedContextTokens;
        var room = context - ContextBudget.ChatTemplateTokens - TokenEstimate.Prose(system) -
            TokenEstimate.Transcript(UserMessage(body)) - ContextBudget.Margin(context);
        Assert.True(room < 2048, $"The sample leaves {room} tokens, which this test needs to be below the usual 2,048.");
        Assert.Equal(room, body.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task A_one_off_request_that_leaves_its_answer_too_little_room_is_not_sent()
    {
        var bodies = new List<JsonElement>();
        await using var harness = OllamaCompatible(bodies);
        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.OllamaAddress, OllamaModel));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var before = Snapshot(bodies).Count;

        var result = await harness.Service
            .CompleteAsync(AiDictionarySuggester.SystemPrompt, Sample(14_000), harness.Service.Recipient!)
            .WaitAsync(Bound);

        Assert.Equal(CompletionOutcome.Failed, result.Outcome);
        Assert.Equal(before, Snapshot(bodies).Count);
    }

    [Fact]
    public async Task Setup_says_when_the_instructions_leave_no_room_and_loads_nothing_for_them()
    {
        var bodies = new List<JsonElement>();
        await using var harness = OllamaNative(bodies);

        harness.Service.Configure(Ollama(ContextBudget.MinimumSize) with { WritingStyle = LongStyle(8000) });
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);

        Assert.Contains("don't fit in what the AI model on this PC reads at once", harness.Service.StatusDetail, StringComparison.Ordinal);
        Assert.Contains("A larger context size", harness.Service.StatusDetail, StringComparison.Ordinal);
        Assert.Empty(Snapshot(bodies));
    }

    [Fact]
    public async Task Setup_says_so_once_it_has_read_a_context_that_leaves_the_instructions_no_room()
    {
        await using var harness = OllamaCompatible([]);
        harness.LocalServers.State = LmStudioHoldsOnDemand(ContextBudget.MinimumSize);

        harness.Service.Configure(LmStudio(null) with { WritingStyle = LongStyle(6000) });
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);

        Assert.Contains("don't fit in what the AI model on this PC reads at once", harness.Service.StatusDetail, StringComparison.Ordinal);

        // What the model reads stays known, so Settings can say why.
        Assert.Equal(ContextBudget.MinimumSize, harness.Service.LocalContextTokens);
    }

    [Fact]
    public async Task A_writing_style_that_no_longer_fits_makes_cleanup_say_so_rather_than_stay_ready()
    {
        var bodies = new List<JsonElement>();
        await using var harness = OllamaNative(bodies);
        harness.Service.Configure(Ollama(ContextBudget.MinimumSize));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // Only what the prompt says changed, which normally rebuilds in place and stays ready.
        harness.Service.Configure(Ollama(ContextBudget.MinimumSize) with { WritingStyle = LongStyle(8000) });
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);
        Assert.Contains("don't fit in what the AI model on this PC reads at once", harness.Service.StatusDetail, StringComparison.Ordinal);

        // A style that fits again brings it back.
        harness.Service.Configure(Ollama(ContextBudget.MinimumSize));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
    }

    [Fact]
    public async Task Foundry_Local_setup_says_the_instructions_leave_no_room_before_any_readiness_check()
    {
        var requests = 0;
        await using var harness = new CleanupHarness(http: new ScriptedHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(ScriptedHttpHandler.ChatCompletion("ok"));
        }));
        harness.State.ModelRoot = harness.Temp.Combine("models");
        var folder = Path.Combine(harness.State.ModelRoot, FakeFoundryModel.SafeName(CleanupHarness.FoundryVariant));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "genai_config.json"), """{"search":{"max_length":2048}}""");

        harness.Service.Configure(CleanupHarness.FoundryOn() with { WritingStyle = LongStyle(8000) });
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);

        Assert.Contains("don't fit in what the AI model on this PC reads at once", harness.Service.StatusDetail, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task A_recording_readies_nothing_when_its_app_profile_s_style_leaves_no_room()
    {
        var bodies = new List<JsonElement>();
        await using var harness = OllamaNative(bodies);
        harness.Service.Configure(Ollama(4096));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var before = Snapshot(bodies).Count;

        harness.Service.ForgetLastModelAnswerForTesting();
        harness.Service.Admit(CleanupVocabulary.None).Prewarm(LongStyle(16_000));
        await harness.Service.WaitForPrewarmForTesting().WaitAsync(Bound);
        Assert.Equal(before, Snapshot(bodies).Count);

        // The settings' own style leaves room, so the same recording without the profile readies the model.
        harness.Service.ForgetLastModelAnswerForTesting();
        await PrewarmAsync(harness);
        Assert.Equal(before + 1, Snapshot(bodies).Count);
    }

    [Fact]
    public async Task Test_connection_says_when_the_instructions_leave_no_room_without_sending_anything()
    {
        var bodies = new List<JsonElement>();
        await using var harness = OllamaNative(bodies);

        var result = await harness.Service
            .TestAsync(Ollama(ContextBudget.MinimumSize) with { WritingStyle = LongStyle(8000) })
            .WaitAsync(Bound);

        Assert.Equal(CleanupTestOutcome.Failed, result.Outcome);
        Assert.Contains("don't fit in what the AI model on this PC reads at once", result.SafeReason, StringComparison.Ordinal);
        Assert.Empty(Snapshot(bodies));
    }

    [Fact]
    public async Task Test_connection_checks_the_instructions_against_the_context_the_model_answered_with()
    {
        await using var harness = OllamaCompatible([]);
        harness.LocalServers.State = OllamaHolds(ContextBudget.MinimumSize);

        var tight = await harness.Service
            .TestAsync(CleanupHarness.Custom(LocalAiServer.OllamaAddress, OllamaModel) with { WritingStyle = LongStyle(6000) })
            .WaitAsync(Bound);
        var roomy = await harness.Service
            .TestAsync(CleanupHarness.Custom(LocalAiServer.OllamaAddress, OllamaModel))
            .WaitAsync(Bound);

        Assert.Equal(CleanupTestOutcome.Failed, tight.Outcome);
        Assert.Contains("don't fit", tight.SafeReason, StringComparison.Ordinal);
        Assert.Equal(CleanupTestOutcome.Connected, roomy.Outcome);
    }

    private static CleanupOptions Ollama(int size) =>
        CleanupHarness.Custom(LocalAiServer.OllamaAddress, OllamaModel) with { LocalContextTokens = size };

    private static CleanupOptions LmStudio(int? size) =>
        CleanupHarness.Custom(LocalAiServer.LmStudioAddress, LmStudioModel) with { LocalContextTokens = size };

    private static string Instance(string model, int contextTokens) => $"{model}:{contextTokens}";

    private static async Task PrewarmAsync(CleanupHarness harness)
    {
        harness.Service.Admit(CleanupVocabulary.None).Prewarm();
        await harness.Service.WaitForPrewarmForTesting().WaitAsync(Bound);
    }

    // Ollama's own chat API, every request captured; the answer is the dictated text when echo is set.
    private static CleanupHarness OllamaNative(List<JsonElement> bodies, bool echo = false) =>
        new(http: new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            Assert.Equal("/api/chat", request.RequestUri!.AbsolutePath);
            lock (bodies)
            {
                bodies.Add(body);
            }

            var content = echo ? Transcript(UserMessage(body)) : "Please send the report today.";
            return ScriptedHttpHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                model = OllamaModel,
                created_at = "2026-10-01T00:00:00Z",
                message = new { role = "assistant", content },
                done = true,
                done_reason = "stop",
                prompt_eval_count = 40,
                eval_count = 9,
            }));
        }));

    // Ollama's OpenAI-compatible API, every chat request captured.
    private static CleanupHarness OllamaCompatible(List<JsonElement> bodies) =>
        new(http: new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            lock (bodies)
            {
                bodies.Add(body);
            }

            return ScriptedHttpHandler.ChatCompletion("Please send the report today.");
        }));

    private static LocalServerState OllamaHolds(int contextTokens) => new(
        LocalServerReach.Reached,
        [new LocalServerModel(OllamaModel, OllamaModel, 9_600_000_000)],
        [new LocalServerLoadedModel(OllamaModel, 3_000_000_000) { ContextTokens = contextTokens }]);

    private static LocalServerState OllamaHasModel() => new(
        LocalServerReach.Reached, [new LocalServerModel(OllamaModel, OllamaModel, 9_600_000_000)], []);

    private static LocalServerState LmStudioHasModel() => new(
        LocalServerReach.Reached,
        [new LocalServerModel(LmStudioModel, "Gemma 4 E2B", 4_000_000_000) { MaxContextTokens = 131072 }],
        []);

    // A copy LM Studio loaded on demand at its own size, which it will unload on its own after its idle time.
    private static LocalServerState LmStudioHoldsOnDemand(int contextTokens) => LmStudioHasModel() with
    {
        Loaded =
        [
            new LocalServerLoadedModel(LmStudioModel, 4_000_000_000)
            {
                ContextTokens = contextTokens,
                InstanceId = LmStudioModel,
                RemainingTtlSeconds = 3600,
            },
        ],
    };

    // What a load at a size leaves: the copy Scribe loaded, named as LoadAnswer = Instance names it.
    private static LocalServerState LmStudioLoadedAt(string model, int contextTokens) => LmStudioHasModel() with
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

    // The request, as Scribe counts it, and the longest answer it lets the model give fit in the context it asks Ollama for.
    private static void AssertFits(JsonElement body, int context, string? writingStyle = null)
    {
        var instructions = TextCleanupService.BuildProbeSystemPrompt(Ollama(context) with { WritingStyle = writingStyle });
        var system = SystemMessage(body);
        Assert.StartsWith(instructions, system, StringComparison.Ordinal);
        var glossary = system.Length > instructions.Length ? system[(instructions.Length + 2)..] : string.Empty;
        var options = Options(body);
        Assert.Equal(context, options.GetProperty("num_ctx").GetInt32());
        var cost = ContextBudget.ChatTemplateTokens + TokenEstimate.Prose(instructions) + TokenEstimate.Vocabulary(glossary) +
            TokenEstimate.Transcript(Transcript(UserMessage(body))) + options.GetProperty("num_predict").GetInt32() +
            ContextBudget.Margin(context);
        Assert.True(cost <= context, $"The request and its longest answer take {cost} of {context} tokens.");
    }

    private static JsonElement Options(JsonElement body) => body.GetProperty("options");

    // An OpenAI-compatible request (Ollama's or LM Studio's /v1), as Scribe counts it, with the longest answer it declares
    // (max_tokens), fits in the context.
    private static void AssertFitsCompatible(JsonElement body, CleanupOptions options, int context)
    {
        var instructions = TextCleanupService.BuildProbeSystemPrompt(options);
        var system = SystemMessage(body);
        Assert.StartsWith(instructions, system, StringComparison.Ordinal);
        var glossary = system.Length > instructions.Length ? system[(instructions.Length + 2)..] : string.Empty;
        var cost = ContextBudget.ChatTemplateTokens + TokenEstimate.Prose(instructions) + TokenEstimate.Vocabulary(glossary) +
            TokenEstimate.Transcript(Transcript(UserMessage(body))) + body.GetProperty("max_tokens").GetInt32() +
            ContextBudget.Margin(context);
        Assert.True(cost <= context, $"The request and its longest answer take {cost} of {context} tokens.");
    }

    // A writing style of about this many characters.
    private static string LongStyle(int chars)
    {
        const string Sentence = "Keep every sentence short and plain. ";
        return string.Concat(Enumerable.Repeat(Sentence, (chars / Sentence.Length) + 1)).Trim();
    }

    // Recent dictations, as the dictionary suggestions send them, of this many characters.
    private static string Sample(int chars)
    {
        const string Line = "we shipped the kubernetes upgrade and told the team on friday\n";
        return string.Concat(Enumerable.Repeat(Line, (chars / Line.Length) + 1))[..chars];
    }

    private static string SystemMessage(JsonElement body) => Message(body, "system");

    private static string UserMessage(JsonElement body) => Message(body, "user");

    private static string Message(JsonElement body, string role) =>
        body.GetProperty("messages").EnumerateArray()
            .First(message => message.GetProperty("role").GetString() == role)
            .GetProperty("content").GetString()!;

    // The dictated text between the transcript tags.
    private static string Transcript(string user)
    {
        var start = user.IndexOf('\n', StringComparison.Ordinal) + 1;
        var end = user.LastIndexOf('\n');
        return end > start ? user[start..end] : string.Empty;
    }

    private static int GlossaryLines(string system) =>
        system.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal) && line.Contains("(transcribed as", StringComparison.Ordinal));

    // Distinct invented words, far enough apart that a dictation mentioning one mentions no other.
    private static List<DictionaryEntry> Vocabulary(int count)
    {
        var random = new Random(20261002);
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

    private static List<T> Snapshot<T>(List<T> list)
    {
        lock (list)
        {
            return [.. list];
        }
    }
}
