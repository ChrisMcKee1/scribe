using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

public sealed class LocalReasoningBudgetTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);
    private const string Model = "test-model";
    private const string Dictated = "please send the report to the team tomorrow morning";
    private const string ThoughtCanary = "Private reasoning must never become the dictation";
    private const string VocabularyCanary = "KestrelmoorVocabularyCanary";
    private const string CutReason = "AI cleanup's answer was cut off before it finished.";
    private const int Room = TextCleanupService.LocalReasoningHeadroomTokens;

    // The benchmark's synthetic regional-voice dictation, and what DeepSeek-R1 8B answered for it: a cut reply that read
    // like a finished sentence was once typed in place of all 225 characters, and the rewrite it gave given the room.
    private const string RegionalVoice =
        "Hey y'all I'm fixin' to push the change tonight it's gonna take about 20 minutes and I wanna make sure nobody is " +
        "deploying at the same time cause that's kinda how we broke it last time so just holler at me if you're in there.";
    private const string RegionalVoicePartial = "Hey y'all, I'm going to push the change tonight. It's going to take about";
    private const string RegionalVoiceFullLooking = "Hey y'all, I'm going to push the change tonight.";
    private const string RegionalVoiceCleaned =
        "Hey, everyone. I'm fixing to push the change tonight. It will take approximately 20 minutes. I want to make sure " +
        "no one is deploying at the same time, because that's how we broke it last time. Please contact me if you're " +
        "working on anything.";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_capped_empty_probe_verifies_a_rewrite_then_reserves_room_for_real_dictations(bool native, bool plain)
    {
        var wire = new Wire(sent => Task.FromResult(
            plain && sent.Body.TryGetProperty("reasoning_effort", out _)
                ? RejectedFields()
                : ThinkingAnswer(sent)));
        await using var harness = Harness(wire, 8192);
        var options = Ollama(native) with { Glossary = VocabularyCanary };
        harness.Service.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var probes = wire.Sent;
        Assert.Equal(plain ? 3 : 2, probes.Length);
        Assert.Equal(16, probes[0].Ceiling);
        Assert.Equal("ok", probes[0].Text);
        var expanded = probes[^1];
        Assert.Equal(Visible(expanded.Text) + TextCleanupService.LocalReasoningHeadroomTokens, expanded.Ceiling);
        Assert.True(expanded.Text.Split(' ').Length >= 8, "The enlarged probe must verify a rewrite, not another 'ok'.");
        Assert.All(probes, sent =>
        {
            Assert.Equal(CleanupRequestKind.Probe, sent.Admission.Kind);
            Assert.Same(AiVocabularyScope.None, sent.Admission.Scope);
            Assert.Same(probes[0].Admission, sent.Admission);
            Assert.DoesNotContain(VocabularyCanary, sent.System, StringComparison.Ordinal);
            Assert.DoesNotContain(Dictated, sent.Text, StringComparison.Ordinal);
            AssertFits(sent, options, native ? 8192 : 4096);
        });
        Assert.Equal(probes.Length, expanded.Admission.HandedOver);

        var actual = await harness.Service.CleanAsync(Dictated).WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Cleaned, actual.Outcome);
        Assert.Equal(Rewrite(Dictated), actual.Text);
        Assert.DoesNotContain(ThoughtCanary, actual.Text, StringComparison.Ordinal);
        var dictation = wire.Sent[^1];
        var reserve = expanded.Ceiling - Visible(expanded.Text);
        Assert.Equal(TextCleanupService.LocalReasoningHeadroomTokens, reserve);
        Assert.True(reserve > 1024, "The room to think is sized on the varied benchmark, past the rejected 1,024.");
        Assert.Equal(Visible(Dictated) + reserve, dictation.Ceiling);
        Assert.Equal(probes.Length + 1, wire.Sent.Length);
        Assert.Equal(CleanupRequestKind.Dictation, dictation.Admission.Kind);
        Assert.All(wire.Sent.Skip(plain ? 1 : 0), sent => AssertGeneration(sent, native, plain, 8192));

        // Counterfactual: the unchanged service declared Ready after the first empty response, then sent this small
        // allowance for the dictation. The very same scripted model returns no final text at that allowance.
        Assert.True(Visible(Dictated) < 280);
        using var oldAnswer = ThinkingAnswer(dictation, Visible(Dictated));
        using var oldJson = JsonDocument.Parse(await oldAnswer.Content.ReadAsStringAsync());
        var oldText = native
            ? oldJson.RootElement.GetProperty("message").GetProperty("content").GetString()
            : oldJson.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        Assert.False(TextCleanupService.TrySanitize(oldText, Dictated, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fast_model_keeps_the_16_token_probe_and_the_original_dictation_allowance(bool native)
    {
        var wire = new Wire(sent => Task.FromResult(Answer(sent, Rewrite(sent.Text), 1)));
        await using var harness = Harness(wire, 8192);
        harness.Service.Configure(Ollama(native));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var probe = Assert.Single(wire.Sent);
        Assert.Equal(16, probe.Ceiling);
        Assert.Equal(TextCleanupService.BuildUserMessage("ok"), probe.User);
        Assert.Equal(TextCleanupService.BuildProbeSystemPrompt(Ollama(native)), probe.System);

        Assert.Equal(CleanupOutcome.Cleaned, (await harness.Service.CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Equal(Visible(Dictated), wire.Sent[^1].Ceiling);
        Assert.Equal(2, wire.Sent.Length);
        Assert.All(wire.Sent, sent => AssertGeneration(sent, native, plain: false, 8192));
    }

    [Fact]
    public async Task Readiness_waits_for_the_enlarged_answer_in_the_same_model_use()
    {
        var arrived = Gate();
        var release = Gate();
        var uses = new ConcurrentQueue<int>();
        TextCleanupService? service = null;
        var wire = new Wire(async sent =>
        {
            uses.Enqueue((int)typeof(TextCleanupService).GetField("_activeModelUses",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(service)!);
            if (sent.Ceiling > 16)
            {
                arrived.TrySetResult();
                await release.Task.WaitAsync(Bound);
            }

            return ThinkingAnswer(sent);
        });
        await using var harness = Harness(wire);
        service = harness.Service;
        try
        {
            service.Configure(Ollama());
            await arrived.Task.WaitAsync(Bound);
            Assert.Equal(CleanupStatus.Initializing, service.Status);
            Assert.Null(service.Recipient);
            Assert.Equal([1, 1], uses.ToArray());
            Assert.Same(wire.Sent[0].Admission, wire.Sent[1].Admission);

            release.TrySetResult();
            await harness.WaitForStatusAsync(CleanupStatus.Ready);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData("", "ran out of room before it finished a short rewrite")]
    [InlineData("<think>unfinished work</think>", "did not return a usable rewrite during validation")]
    [InlineData("```\n```", "did not return a usable rewrite during validation")]
    [InlineData("I'm sorry, but I cannot assist with that request.", "did not return a usable rewrite during validation")]
    [InlineData(
        "Sure! I will send the updated report to the team tomorrow morning and copy you on it, then follow up with everyone " +
        "who has questions about the numbers, set up a meeting to walk through the changes in detail with finance, and " +
        "write a short summary of what changed since last quarter so that nobody has to read the whole thing to catch up.",
        "did not return a usable rewrite during validation")]
    public async Task An_enlarged_probe_without_a_usable_rewrite_fails_once_and_an_identical_save_can_recover(
        string badAnswer, string reason)
    {
        var recover = false;
        var wire = new Wire(sent => Task.FromResult(
            sent.Ceiling == 16 ? Answer(sent, string.Empty, 16)
                : recover ? ThinkingAnswer(sent) : Answer(sent, badAnswer, sent.Ceiling)));
        await using var harness = Harness(wire);
        harness.Service.Configure(Ollama());
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);

        Assert.Equal(2, wire.Sent.Length);
        Assert.Contains(reason, harness.Service.StatusReason, StringComparison.Ordinal);
        Assert.Equal(CleanupOutcome.Failed, (await harness.Service.CleanAsync(Dictated)).Outcome);
        Assert.Equal(2, wire.Sent.Length);
        Assert.DoesNotContain(badAnswer.Length > 0 ? badAnswer : ThoughtCanary, harness.Service.StatusReason, StringComparison.Ordinal);
        Assert.DoesNotContain(ThoughtCanary, harness.Service.StatusDetail ?? string.Empty, StringComparison.Ordinal);

        recover = true;
        harness.Service.Configure(Ollama());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(CleanupOutcome.Cleaned, (await harness.Service.CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Equal(5, wire.Sent.Length);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(0, "")]
    [InlineData(15, "")]
    [InlineData(null, "<think>only reasoning</think>")]
    [InlineData(null, "```\n```")]
    public async Task Missing_or_uncapped_usage_is_not_evidence_for_a_larger_probe(int? tokens, string text)
    {
        var wire = new Wire(sent => Task.FromResult(Answer(sent, text, tokens, finishReason: "stop")));
        await using var harness = Harness(wire);
        harness.Service.Configure(Ollama());
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);

        Assert.Equal(16, Assert.Single(wire.Sent).Ceiling);
        Assert.Contains("validation", harness.Service.StatusReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_SDK_s_length_finish_is_evidence_even_when_the_server_omits_usage(bool native)
    {
        var wire = new Wire(sent => Task.FromResult(sent.Ceiling == 16
            ? Answer(sent, string.Empty, generated: null, finishReason: "length")
            : ThinkingAnswer(sent)));
        await using var harness = Harness(wire);
        harness.Service.Configure(Ollama(native));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal(2, wire.Sent.Length);
        Assert.True(wire.Sent[1].Ceiling > 16);
        Assert.Equal(CleanupOutcome.Cleaned, (await harness.Service.CleanAsync(Dictated)).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_empty_probe_with_omitted_finish_and_usage_at_the_cap_can_request_the_bounded_proof(bool native)
    {
        var wire = new Wire(sent => Task.FromResult(sent.Ceiling == 16
            ? Answer(sent, string.Empty, sent.Ceiling, omitFinish: true)
            : ThinkingAnswer(sent)));
        await using var harness = Harness(wire, 8192);
        harness.Service.Configure(Ollama(native));
        await harness.WaitForStatusAsync(status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
        Assert.Equal(2, wire.Sent.Length);
        Assert.Equal(16, wire.Sent[0].Ceiling);
        Assert.Equal(Visible(wire.Sent[1].Text) + Room, wire.Sent[1].Ceiling);
        Assert.Same(wire.Sent[0].Admission, wire.Sent[1].Admission);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_usable_final_answer_at_the_cap_does_not_enable_the_reserve_and_a_cut_dictation_is_typed_as_heard(bool native)
    {
        var wire = new Wire(sent => Task.FromResult(Answer(sent, Rewrite(sent.Text), sent.Ceiling, finishReason: "length")));
        await using var harness = Harness(wire, 8192);
        harness.Service.Configure(Ollama(native));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Single(wire.Sent);

        // The same scripted model cuts the dictation's answer at its ceiling too: a complete-looking sentence the server
        // says was cut is never typed as cleaned, and nothing is sent again for it.
        var result = await harness.Service.CleanAsync(Dictated).WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal(Dictated, result.Text);
        Assert.Equal("AI cleanup's answer was cut off before it finished.", result.FailureReason);
        Assert.Equal(result.FailureReason, result.DisplayDetail);
        Assert.Equal(2, wire.Sent.Length);
        Assert.Equal(Visible(Dictated), wire.Sent[^1].Ceiling);
    }

    [Theory]
    [InlineData("length", true)]
    [InlineData("stop", false)]
    public async Task A_closed_think_block_at_the_cap_is_not_a_final_answer_but_only_a_length_finish_shows_starvation(
        string finishReason, bool escalates)
    {
        var wire = new Wire(sent => Task.FromResult(sent.Ceiling == 16
            ? Answer(sent, $"<think>{ThoughtCanary}</think>", 16, finishReason)
            : ThinkingAnswer(sent)));
        await using var harness = Harness(wire);
        harness.Service.Configure(Ollama());
        await harness.WaitForStatusAsync(escalates ? CleanupStatus.Ready : CleanupStatus.Unavailable);

        // A count at the ceiling is no evidence when the server says it stopped on its own.
        Assert.Equal(escalates ? 2 : 1, wire.Sent.Length);
        if (escalates)
        {
            Assert.True(wire.Sent[1].Ceiling > 16);
            Assert.Equal(CleanupOutcome.Cleaned, (await harness.Service.CleanAsync(Dictated)).Outcome);
        }
        else
        {
            Assert.Contains("did not return a usable rewrite during validation", harness.Service.StatusReason, StringComparison.Ordinal);
            Assert.DoesNotContain(ThoughtCanary, harness.Service.StatusDetail ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("https://remote.example.invalid/v1")]
    [InlineData("https://192.168.1.20:11434/v1")]
    [InlineData("http://127.0.0.1:8000/v1")]
    [InlineData("http://localhost:1234/v1")]
    public async Task Servers_outside_the_recognized_Ollama_app_keep_their_existing_probe_and_budget(string endpoint)
    {
        var wire = new Wire(sent => Task.FromResult(ThinkingAnswer(sent)));
        await using var harness = Harness(wire);
        harness.Service.Configure(CleanupHarness.Custom(endpoint, Model));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal(16, Assert.Single(wire.Sent).Ceiling);
        await harness.Service.CleanAsync(Dictated).WaitAsync(Bound);
        Assert.Equal(Visible(Dictated), wire.Sent[^1].Ceiling);
        Assert.Equal(2, wire.Sent.Length);
    }

    [Theory]
    [InlineData(false, 4096)]
    [InlineData(true, 4096)]
    [InlineData(false, 8192)]
    [InlineData(true, 8192)]
    public async Task Fitting_uses_the_learned_ceiling_for_every_chunk_and_keeps_auxiliary_and_readying_requests_fitted(
        bool native, int context)
    {
        var wire = new Wire(sent => Task.FromResult(ThinkingAnswer(sent)));
        await using var harness = Harness(wire, context);
        var options = Ollama(native) with { SendWholeVocabulary = true, VocabularyMode = CleanupVocabularyMode.Mentioned };
        var vocabulary = new CleanupVocabulary(
            Enumerable.Range(0, 1200).Select(i => DictionaryEntry.New($"spoken term {i}", $"WrittenTerm{i}")).ToArray(),
            AiVocabularyScope.None);
        harness.Service.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var reserve = wire.Sent[^1].Ceiling - Visible(wire.Sent[^1].Text);

        var text = string.Join(' ', Enumerable.Repeat("please send the report to the team tomorrow morning", 140));
        var result = await harness.Service.Admit(vocabulary).CleanAsync(text).WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
        var chunks = wire.Sent.Where(sent => sent.Admission.Kind == CleanupRequestKind.Dictation).ToArray();
        Assert.True(chunks.Length > 1);
        Assert.All(chunks, sent =>
        {
            Assert.Equal(Visible(sent.Text) + reserve, sent.Ceiling);
            AssertFits(sent, options, context);
        });

        // The dictated text and its whole room to think come before any vocabulary: in Ollama's own 4,096 that leaves a
        // chunk next to none, and a larger context carries the leading run of it that fits.
        if (context > 4096)
        {
            Assert.Contains(chunks, sent => sent.System.Contains("WrittenTerm", StringComparison.Ordinal));
            Assert.DoesNotContain("WrittenTerm1199", chunks[0].System, StringComparison.Ordinal);
        }

        harness.Service.ForgetLastModelAnswerForTesting();
        harness.Service.Admit(vocabulary).Prewarm();
        await harness.Service.WaitForPrewarmForTesting().WaitAsync(Bound);
        var warm = Assert.Single(wire.Sent, sent => sent.Admission.Kind == CleanupRequestKind.Prewarm);
        Assert.Equal(1, warm.Ceiling);
        AssertFits(warm, options, context);

        var auxiliary = await harness.Service.CompleteAsync(
            "Write one short sentence about these counts.", "There are two items.", harness.Service.Recipient!).WaitAsync(Bound);
        Assert.Equal(CompletionOutcome.Completed, auxiliary.Outcome);
        var completion = Assert.Single(wire.Sent, sent => sent.Admission.Kind == CleanupRequestKind.Completion);
        Assert.InRange(completion.Ceiling, TextCleanupService.AuxiliaryMinOutputTokens, 2048);
        AssertFits(completion, options, context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_candidate_uses_its_own_learned_allowance_without_changing_the_serving_one(bool servingThinks)
    {
        var testing = false;
        var wire = new Wire(sent => Task.FromResult(
            (testing && sent.Admission.Kind == CleanupRequestKind.Probe) != servingThinks
                ? ThinkingAnswer(sent)
                : Answer(sent, Rewrite(sent.Text), 1)));
        await using var harness = Harness(wire);
        harness.Service.Configure(Ollama());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var generation = harness.Service.InitGenerationForTesting;
        var agent = harness.Service.ServingAgentForTesting;
        await harness.Service.CleanAsync(Dictated);
        var before = wire.Sent[^1].Ceiling;

        testing = true;
        var tested = await harness.Service.TestAsync(Ollama()).WaitAsync(Bound);
        testing = false;

        Assert.Equal(CleanupTestOutcome.Connected, tested.Outcome);
        Assert.Equal(generation, harness.Service.InitGenerationForTesting);
        Assert.Same(agent, harness.Service.ServingAgentForTesting);
        await harness.Service.CleanAsync(Dictated);
        Assert.Equal(before, wire.Sent[^1].Ceiling);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_candidate_s_unconfirmed_proof_cannot_qualify_or_replace_the_serving_allowance(
        bool native, bool servingThinks)
    {
        var testing = false;
        var wire = new Wire(sent => Task.FromResult(testing && sent.Admission.Kind == CleanupRequestKind.Probe
            ? sent.Ceiling == 16 ? Answer(sent, string.Empty, 16) : Answer(sent, Rewrite(sent.Text), sent.Ceiling, omitFinish: true)
            : servingThinks ? ThinkingAnswer(sent) : Answer(sent, Rewrite(sent.Text), 1)));
        await using var harness = Harness(wire, 8192);
        var options = Ollama(native);
        harness.Service.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var generation = harness.Service.InitGenerationForTesting;
        var serving = harness.Service.ServingAgentForTesting;
        var before = wire.Sent.Length;

        testing = true;
        var tested = await harness.Service.TestAsync(options).WaitAsync(Bound);
        testing = false;

        Assert.NotEqual(CleanupTestOutcome.Connected, tested.Outcome);
        Assert.Equal(before + 2, wire.Sent.Length);
        Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
        Assert.Equal(generation, harness.Service.InitGenerationForTesting);
        Assert.Same(serving, harness.Service.ServingAgentForTesting);
        Assert.Equal(CleanupOutcome.Cleaned, (await harness.Service.CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Equal(Visible(Dictated) + (servingThinks ? Room : 0), wire.Sent[^1].Ceiling);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_plain_candidate_cannot_change_live_requests_even_while_its_escalation_is_in_flight(bool cancel)
    {
        var testing = false;
        var arrived = Gate();
        var release = Gate();
        var wire = new Wire(async sent =>
        {
            if (testing && sent.Admission.Kind == CleanupRequestKind.Probe)
            {
                if (sent.Body.TryGetProperty("reasoning_effort", out _))
                {
                    return RejectedFields();
                }

                if (sent.Ceiling > 16)
                {
                    arrived.TrySetResult();
                    await release.Task.WaitAsync(Bound);
                }

                return ThinkingAnswer(sent);
            }

            return Answer(sent, Rewrite(sent.Text), 1);
        });
        await using var harness = Harness(wire);
        using var cancellation = new CancellationTokenSource();
        try
        {
            harness.Service.Configure(Ollama());
            await harness.WaitForStatusAsync(CleanupStatus.Ready);
            var generation = harness.Service.InitGenerationForTesting;
            testing = true;
            var pending = harness.Service.TestAsync(Ollama(), cancellation.Token);
            await arrived.Task.WaitAsync(Bound);

            await harness.Service.CleanAsync(Dictated);
            AssertGeneration(wire.Sent[^1], native: false, plain: false, 4096);
            Assert.Equal(Visible(Dictated), wire.Sent[^1].Ceiling);
            if (cancel)
            {
                cancellation.Cancel();
            }

            release.TrySetResult();
            var result = await pending.WaitAsync(Bound);
            Assert.Equal(cancel ? CleanupTestOutcome.Cancelled : CleanupTestOutcome.Connected, result.Outcome);
            Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
            Assert.Equal(generation, harness.Service.InitGenerationForTesting);
            testing = false;
            await harness.Service.CleanAsync(Dictated);
            AssertGeneration(wire.Sent[^1], native: false, plain: false, 4096);
            Assert.Equal(Visible(Dictated), wire.Sent[^1].Ceiling);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task A_cancelled_initialization_cannot_publish_its_hint_after_the_same_settings_are_selected_again()
    {
        var arrived = Gate();
        var release = Gate();
        var oldProbe = true;
        var wire = new Wire(async sent =>
        {
            if (oldProbe)
            {
                if (sent.Ceiling > 16)
                {
                    arrived.TrySetResult();
                    await release.Task.WaitAsync(Bound);
                    oldProbe = false;
                }

                return ThinkingAnswer(sent);
            }

            return Answer(sent, Rewrite(sent.Text), 1);
        });
        await using var harness = Harness(wire);
        try
        {
            harness.Service.Configure(Ollama());
            await arrived.Task.WaitAsync(Bound);
            var originalGeneration = harness.Service.InitGenerationForTesting;
            harness.Service.Configure(Ollama() with { Enabled = false });
            Assert.Equal(CleanupStatus.Disabled, harness.Service.Status);
            harness.Service.Configure(Ollama());
            release.TrySetResult();
            await harness.WaitForStatusAsync(CleanupStatus.Ready);

            Assert.True(harness.Service.InitGenerationForTesting > originalGeneration);
            await harness.Service.CleanAsync(Dictated);
            Assert.Equal(Visible(Dictated), wire.Sent[^1].Ceiling);
            Assert.Equal(4, wire.Sent.Length);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_changed_model_address_or_native_API_context_does_not_inherit_the_reserve(int change)
    {
        var thinks = true;
        var wire = new Wire(sent => Task.FromResult(thinks ? ThinkingAnswer(sent) : Answer(sent, Rewrite(sent.Text), 1)));
        await using var harness = Harness(wire);
        var options = Ollama();
        harness.Service.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        await harness.Service.CleanAsync(Dictated);
        Assert.True(wire.Sent[^1].Ceiling > Visible(Dictated));

        thinks = false;
        var changed = change switch
        {
            0 => options with { CustomModel = "other-model" },
            1 => options with { CustomEndpoint = "http://127.0.0.1:11434/v1" },
            _ => options with { LocalContextTokens = 8192 },
        };
        harness.Service.Configure(changed);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(16, wire.Sent[^1].Ceiling);
        await harness.Service.CleanAsync(Dictated);
        Assert.Equal(Visible(Dictated), wire.Sent[^1].Ceiling);
    }

    [Fact]
    public async Task Test_connection_caps_its_native_context_without_changing_the_serving_context()
    {
        var wire = new Wire(sent => Task.FromResult(ThinkingAnswer(sent)));
        await using var harness = Harness(wire, 4096);
        harness.Service.Configure(Ollama());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var before = harness.Service.LocalContextTokens;
        var count = wire.Sent.Length;

        var result = await harness.Service.TestAsync(Ollama(native: true) with { LocalContextTokens = 32768 }).WaitAsync(Bound);

        Assert.Equal(CleanupTestOutcome.Connected, result.Outcome);
        Assert.Equal(before, harness.Service.LocalContextTokens);
        Assert.All(wire.Sent.Skip(count), sent =>
        {
            Assert.Equal(4096, sent.Body.GetProperty("options").GetProperty("num_ctx").GetInt32());
            AssertFits(sent, Ollama(native: true), 4096);
        });
    }

    [Fact]
    public async Task Every_chunk_keeps_its_planned_allowance_and_native_context_across_reconfiguration()
    {
        var arrived = Gate();
        var release = Gate();
        var changed = false;
        var firstDictationChunk = true;
        var wire = new Wire(async sent =>
        {
            if (sent.Admission.Kind == CleanupRequestKind.Dictation && firstDictationChunk)
            {
                firstDictationChunk = false;
                arrived.TrySetResult();
                await release.Task.WaitAsync(Bound);
            }

            return changed ? Answer(sent, Rewrite(sent.Text), 1) : ThinkingAnswer(sent);
        });
        await using var harness = Harness(wire, 4096);
        try
        {
            var options = Ollama(native: true);
            harness.Service.Configure(options);
            await harness.WaitForStatusAsync(CleanupStatus.Ready);
            var reserve = wire.Sent[^1].Ceiling - Visible(wire.Sent[^1].Text);
            var text = string.Join(' ', Enumerable.Repeat(Dictated, 140));
            var cleaning = harness.Service.CleanAsync(text);
            await arrived.Task.WaitAsync(Bound);

            changed = true;
            harness.LocalServers.State = Held(16384);
            harness.LocalServers.MaxContextTokens = 16384;
            harness.Service.Configure(options with { LocalContextTokens = 16384 });
            await harness.WaitForStatusAsync(CleanupStatus.Ready);
            release.TrySetResult();
            Assert.Equal(CleanupOutcome.Cleaned, (await cleaning.WaitAsync(Bound)).Outcome);

            var chunks = wire.Sent.Where(sent => sent.Admission.Kind == CleanupRequestKind.Dictation).ToArray();
            Assert.True(chunks.Length > 1);
            Assert.All(chunks, sent =>
            {
                Assert.Equal(Visible(sent.Text) + reserve, sent.Ceiling);
                Assert.Equal(4096, sent.Body.GetProperty("options").GetProperty("num_ctx").GetInt32());
                AssertFits(sent, options, 4096);
            });
            await harness.Service.CleanAsync(Dictated);
            Assert.Equal(Visible(Dictated), wire.Sent[^1].Ceiling);
            Assert.Equal(16384, wire.Sent[^1].Body.GetProperty("options").GetProperty("num_ctx").GetInt32());
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_context_that_cannot_fit_a_dictation_with_the_reserve_never_reports_ready(bool testConnection)
    {
        var options = Ollama();
        var instructions = TextCleanupService.BuildProbeSystemPrompt(options);
        var shortest = string.Join(' ', Enumerable.Repeat("word", TextCleanupService.MinLocalChunkChars / 5));
        var small = Enumerable.Range(512, 4096).First(context =>
            ContextBudget.TextFits(context, instructions, shortest, Visible(shortest)) &&
            !ContextBudget.TextFits(context, instructions, shortest, Visible(shortest) + TextCleanupService.LocalReasoningHeadroomTokens));
        var wire = new Wire(sent => Task.FromResult(ThinkingAnswer(sent)));
        await using var harness = Harness(wire, small);

        string reason;
        if (testConnection)
        {
            var result = await harness.Service.TestAsync(options).WaitAsync(Bound);
            Assert.Equal(CleanupTestOutcome.Failed, result.Outcome);
            reason = result.SafeReason ?? string.Empty;
        }
        else
        {
            harness.Service.Configure(options);
            await harness.WaitForStatusAsync(CleanupStatus.Unavailable);
            reason = harness.Service.StatusReason ?? string.Empty;
        }

        // Said before anything larger is sent: the rewrite that would need the room is never asked for.
        Assert.Contains("thinks before it answers", reason, StringComparison.Ordinal);
        Assert.Contains("don't fit", reason, StringComparison.Ordinal);
        Assert.Contains("A larger context size", reason, StringComparison.Ordinal);
        var only = Assert.Single(wire.Sent);
        Assert.Equal(16, only.Ceiling);
        AssertFits(only, options, small);
        Assert.Null(harness.Service.Recipient);
        if (!testConnection)
        {
            Assert.Equal(CleanupOutcome.Failed, (await harness.Service.CleanAsync(Dictated)).Outcome);
            Assert.Single(wire.Sent);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_context_is_rechecked_after_learning_before_a_ready_or_connected_result(bool testConnection)
    {
        var options = Ollama();
        var instructions = TextCleanupService.BuildProbeSystemPrompt(options);
        var shortest = string.Join(' ', Enumerable.Repeat("word", TextCleanupService.MinLocalChunkChars / 5));
        var smaller = Enumerable.Range(512, 4096).First(context =>
            ContextBudget.TextFits(context, instructions, shortest, Visible(shortest)) &&
            !ContextBudget.TextFits(context, instructions, shortest, Visible(shortest) + TextCleanupService.LocalReasoningHeadroomTokens));
        CleanupHarness? current = null;
        var wire = new Wire(sent =>
        {
            if (sent.Ceiling > 16)
            {
                current!.LocalServers.State = Held(smaller);
            }

            return Task.FromResult(ThinkingAnswer(sent));
        });
        await using var harness = Harness(wire);
        current = harness;
        string reason;
        if (testConnection)
        {
            var result = await harness.Service.TestAsync(options).WaitAsync(Bound);
            Assert.Equal(CleanupTestOutcome.Failed, result.Outcome);
            reason = result.SafeReason ?? string.Empty;
        }
        else
        {
            harness.Service.Configure(options);
            await harness.WaitForStatusAsync(CleanupStatus.Unavailable);
            reason = harness.Service.StatusReason ?? string.Empty;
        }

        // The instructions and a dictation still fit the smaller context; the room to think is what does not.
        Assert.Contains("thinks before it answers", reason, StringComparison.Ordinal);
        Assert.Contains("don't fit", reason, StringComparison.Ordinal);
        Assert.Null(harness.Service.Recipient);
    }

    [Theory]
    [InlineData("I'm sorry, but I cannot assist with that request.", "stop", "The AI model declined to clean up this dictation.")]
    [InlineData("I'm sorry, but I cannot assist with that request.", "length", "AI cleanup's answer was cut off before it finished.")]
    [InlineData(
        "Sure! I'll send the report to the team tomorrow morning, and I can also draft a summary, schedule a meeting with " +
        "everyone involved, follow up on any questions, and remind you about it again tomorrow afternoon if that helps.",
        "stop",
        "AI cleanup returned unusable output.")]
    public async Task More_headroom_does_not_relax_the_rewrite_guards_or_retry_a_bad_dictation(
        string answer, string finishReason, string reason)
    {
        var wire = new Wire(sent => Task.FromResult(sent.Admission.Kind == CleanupRequestKind.Dictation
            ? Answer(sent, answer, 280, finishReason)
            : ThinkingAnswer(sent)));
        await using var harness = Harness(wire);
        harness.Service.Configure(Ollama());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var result = await harness.Service.CleanAsync(Dictated);

        Assert.Equal(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal(Dictated, result.Text);
        Assert.Equal(reason, result.FailureReason);
        Assert.Single(wire.Sent, sent => sent.Admission.Kind == CleanupRequestKind.Dictation);
        Assert.DoesNotContain(answer, harness.Log.AllText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    public async Task A_reply_cut_at_its_length_limit_is_typed_as_heard_however_finished_it_reads(
        bool native, bool thinking, bool fullLooking)
    {
        var cut = fullLooking ? RegionalVoiceFullLooking : RegionalVoicePartial;
        var wire = new Wire(sent => Task.FromResult(sent.Admission.Kind == CleanupRequestKind.Dictation
            ? Answer(sent, cut, sent.Ceiling, finishReason: "length")
            : thinking ? ThinkingAnswer(sent) : Answer(sent, Rewrite(sent.Text), 1)));
        await using var harness = Harness(wire, 8192);
        harness.Service.Configure(Ollama(native));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var result = await harness.Service.CleanAsync(RegionalVoice).WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal(RegionalVoice, result.Text);
        Assert.Equal(CutReason, result.FailureReason);
        Assert.Equal(CutReason, result.DisplayDetail);
        var dictation = Assert.Single(wire.Sent, sent => sent.Admission.Kind == CleanupRequestKind.Dictation);
        Assert.Equal(Visible(RegionalVoice) + (thinking ? Room : 0), dictation.Ceiling);
        Assert.Contains("did not use an answer that was Cut", harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("push the change", harness.Log.AllText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_length_finish_without_any_usage_is_still_a_cut(bool native)
    {
        var wire = new Wire(sent => Task.FromResult(sent.Admission.Kind == CleanupRequestKind.Dictation
            ? Answer(sent, RegionalVoicePartial, generated: null, finishReason: "length")
            : ThinkingAnswer(sent)));
        await using var harness = Harness(wire, 8192);
        harness.Service.Configure(Ollama(native));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var result = await harness.Service.CleanAsync(RegionalVoice).WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal(RegionalVoice, result.Text);
        Assert.Equal(CutReason, result.FailureReason);
    }

    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    public async Task A_legitimate_answer_that_stops_at_or_just_under_its_ceiling_is_cleaned(bool native, bool thinking, int under)
    {
        var wire = new Wire(sent => Task.FromResult(sent.Admission.Kind == CleanupRequestKind.Dictation
            ? Answer(sent, RegionalVoiceCleaned, sent.Ceiling - under, finishReason: "stop")
            : thinking ? ThinkingAnswer(sent) : Answer(sent, Rewrite(sent.Text), 1)));
        await using var harness = Harness(wire, 8192);
        harness.Service.Configure(Ollama(native));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var result = await harness.Service.CleanAsync(RegionalVoice).WaitAsync(Bound);

        // A count at the ceiling is no proof of a cut when the server says it stopped on its own.
        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
        Assert.Equal(RegionalVoiceCleaned, result.Text);
        Assert.Null(result.FailureReason);
        var dictation = Assert.Single(wire.Sent, sent => sent.Admission.Kind == CleanupRequestKind.Dictation);
        Assert.Equal(Visible(RegionalVoice) + (thinking ? Room : 0), dictation.Ceiling);
    }

    [Theory]
    [InlineData(true, true, null, CleanupOutcome.Failed, "The AI model didn't say whether its answer was complete.")]
    [InlineData(true, true, 5, CleanupOutcome.Failed, "The AI model didn't say whether its answer was complete.")]
    [InlineData(true, true, -1, CleanupOutcome.Failed, "AI cleanup's answer was cut off before it finished.")]
    [InlineData(true, false, null, CleanupOutcome.Cleaned, null)]
    [InlineData(true, false, -1, CleanupOutcome.Failed, "AI cleanup's answer was cut off before it finished.")]
    [InlineData(false, true, null, CleanupOutcome.Failed, "The AI model didn't say whether its answer was complete.")]
    [InlineData(false, true, 5, CleanupOutcome.Failed, "The AI model didn't say whether its answer was complete.")]
    [InlineData(false, true, -1, CleanupOutcome.Failed, "AI cleanup's answer was cut off before it finished.")]
    [InlineData(false, false, null, CleanupOutcome.Cleaned, null)]
    [InlineData(false, false, -1, CleanupOutcome.Failed, "AI cleanup's answer was cut off before it finished.")]
    public async Task Without_a_finish_reason_a_thinking_model_s_answer_is_not_trusted_and_a_count_at_the_ceiling_is_a_cut(
        bool native, bool thinking, int? generated, CleanupOutcome expected, string? reason)
    {
        var wire = new Wire(sent => Task.FromResult(sent.Admission.Kind == CleanupRequestKind.Dictation
            ? Answer(sent, RegionalVoiceCleaned, generated == -1 ? sent.Ceiling : generated, omitFinish: true)
            : thinking ? ThinkingAnswer(sent) : Answer(sent, Rewrite(sent.Text), 1)));
        await using var harness = Harness(wire, 8192);
        harness.Service.Configure(Ollama(native));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var result = await harness.Service.CleanAsync(RegionalVoice).WaitAsync(Bound);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(expected == CleanupOutcome.Cleaned ? RegionalVoiceCleaned : RegionalVoice, result.Text);
        Assert.Equal(reason, result.FailureReason);
        Assert.Single(wire.Sent, sent => sent.Admission.Kind == CleanupRequestKind.Dictation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_partial_regional_rewrite_with_usage_at_the_cap_and_no_finish_is_never_typed(bool native)
    {
        Assert.True(TextCleanupService.TrySanitize(RegionalVoicePartial, RegionalVoice, out _));
        var wire = new Wire(sent => Task.FromResult(sent.Admission.Kind == CleanupRequestKind.Dictation
            ? Answer(sent, RegionalVoicePartial, sent.Ceiling, omitFinish: true)
            : ThinkingAnswer(sent)));
        await using var harness = Harness(wire, 8192);
        harness.Service.Configure(Ollama(native));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var result = await harness.Service.CleanAsync(RegionalVoice).WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal(RegionalVoice, result.Text);
        Assert.Equal(CutReason, result.FailureReason);
        Assert.Equal(CutReason, result.DisplayDetail);
        Assert.Single(wire.Sent, sent => sent.Admission.Kind == CleanupRequestKind.Dictation);
        Assert.DoesNotContain(RegionalVoicePartial, harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(ThoughtCanary, harness.Log.AllText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Each_chunk_and_the_next_dictation_use_their_own_completion_evidence(bool native)
    {
        var calls = 0;
        var wire = new Wire(sent => Task.FromResult(sent.Admission.Kind == CleanupRequestKind.Dictation
            ? Answer(sent, Rewrite(sent.Text), sent.Ceiling, "stop", omitFinish: ++calls == 2)
            : ThinkingAnswer(sent)));
        await using var harness = Harness(wire, 8192);
        harness.Service.Configure(Ollama(native));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var text = string.Join(' ', Enumerable.Repeat(new string('a', 2399), 2));

        var result = await harness.Service.CleanAsync(text).WaitAsync(Bound);

        var sent = wire.Sent.Where(sent => sent.Admission.Kind == CleanupRequestKind.Dictation).ToArray();
        Assert.Equal(2, sent.Length);
        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
        Assert.Equal(Rewrite(sent[0].Text) + " " + sent[1].Text, result.Text);
        Assert.Contains("1 of 2 segments failed", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains(CutReason, result.FailureReason, StringComparison.Ordinal);
        Assert.Same(sent[0].Admission, sent[1].Admission);
        Assert.Equal(2, sent[0].Admission.HandedOver);

        var next = await harness.Service.CleanAsync(Dictated).WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Cleaned, next.Outcome);
        Assert.Null(next.FailureReason);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_retry_gets_its_own_finish_and_a_cut_reply_is_not_retried(bool native, bool sdkRetry)
    {
        var calls = 0;
        var wire = new Wire(sent =>
        {
            if (sent.Admission.Kind != CleanupRequestKind.Dictation)
            {
                return Task.FromResult(ThinkingAnswer(sent));
            }

            if (++calls == 1)
            {
                return sdkRetry
                    ? Task.FromResult(ScriptedHttpHandler.Json(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"busy"}}"""))
                    : Task.FromException<HttpResponseMessage>(new TaskCanceledException("Synthetic stalled attempt"));
            }

            return Task.FromResult(Answer(sent, RegionalVoicePartial, sent.Ceiling, omitFinish: true));
        });
        await using var harness = Harness(wire, 8192);
        if (sdkRetry)
        {
            harness.Service.DisableRetries = false;
            harness.Service.OpenAIClientOptionsOverride = options => options.RetryPolicy = new ImmediateRetryPolicy();
        }

        harness.Service.Configure(Ollama(native));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var result = await harness.Service.CleanAsync(RegionalVoice).WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal(RegionalVoice, result.Text);
        Assert.Equal(CutReason, result.FailureReason);
        Assert.Equal(2, calls);
        var attempts = wire.Sent.Where(sent => sent.Admission.Kind == CleanupRequestKind.Dictation).ToArray();
        Assert.Equal(attempts[0].Ceiling, attempts[1].Ceiling);
        Assert.Same(attempts[0].Admission, attempts[1].Admission);
        Assert.Equal(2, attempts[0].Admission.HandedOver);
        Assert.Null(harness.Service.CleanupTimeoutOverride);
    }

    [Theory]
    [InlineData("https://remote.example.invalid/v1", "length")]
    [InlineData("https://remote.example.invalid/v1", "content_filter")]
    [InlineData("http://localhost:11434/v1", "content_filter")]
    public async Task Outside_a_thinking_model_an_answer_the_server_cut_is_still_never_typed(string endpoint, string finishReason)
    {
        var wire = new Wire(sent => Task.FromResult(sent.Admission.Kind == CleanupRequestKind.Dictation
            ? Answer(sent, RegionalVoicePartial, 40, finishReason)
            : Answer(sent, Rewrite(sent.Text), 1)));
        await using var harness = Harness(wire, 8192);
        harness.Service.Configure(CleanupHarness.Custom(endpoint, Model));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var result = await harness.Service.CleanAsync(RegionalVoice).WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal(RegionalVoice, result.Text);
        Assert.Equal(CutReason, result.FailureReason);
        Assert.Equal(Visible(RegionalVoice), Assert.Single(wire.Sent, sent => sent.Admission.Kind == CleanupRequestKind.Dictation).Ceiling);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task An_expanded_probe_cut_at_its_ceiling_never_qualifies_however_complete_it_reads(bool native, bool fullLooking)
    {
        var recover = false;
        var wire = new Wire(sent => Task.FromResult(
            sent.Ceiling == 16 ? Answer(sent, string.Empty, 16)
                : recover ? ThinkingAnswer(sent)
                : Answer(sent, fullLooking ? Rewrite(sent.Text) : "Please send the updated report to the", sent.Ceiling, "length")));
        await using var harness = Harness(wire, 8192);
        harness.Service.Configure(Ollama(native));
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);

        Assert.Equal(2, wire.Sent.Length);
        Assert.Contains("ran out of room before it finished a short rewrite", harness.Service.StatusReason, StringComparison.Ordinal);
        Assert.Null(harness.Service.Recipient);
        Assert.Equal(CleanupOutcome.Failed, (await harness.Service.CleanAsync(Dictated)).Outcome);
        Assert.Equal(2, wire.Sent.Length);

        recover = true;
        harness.Service.Configure(Ollama(native));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(CleanupOutcome.Cleaned, (await harness.Service.CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Equal(Visible(Dictated) + Room, wire.Sent[^1].Ceiling);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_expanded_probe_that_stops_exactly_at_its_ceiling_qualifies(bool native)
    {
        var wire = new Wire(sent => Task.FromResult(
            sent.Ceiling == 16 ? Answer(sent, string.Empty, 16)
                : sent.Admission.Kind == CleanupRequestKind.Probe ? Answer(sent, Rewrite(sent.Text), sent.Ceiling, "stop")
                : ThinkingAnswer(sent)));
        await using var harness = Harness(wire, 8192);
        harness.Service.Configure(Ollama(native));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal(2, wire.Sent.Length);
        Assert.Equal(CleanupOutcome.Cleaned, (await harness.Service.CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Equal(Visible(Dictated) + Room, wire.Sent[^1].Ceiling);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task An_expanded_probe_without_a_finish_reason_never_qualifies(bool native, bool atCap)
    {
        var wire = new Wire(sent => Task.FromResult(sent.Ceiling == 16
            ? Answer(sent, string.Empty, 16)
            : Answer(sent, Rewrite(sent.Text), atCap ? sent.Ceiling : 280, omitFinish: true)));
        await using var harness = Harness(wire, 8192);
        harness.Service.Configure(Ollama(native));
        await harness.WaitForStatusAsync(status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Unavailable, harness.Service.Status);
        Assert.Equal(2, wire.Sent.Length);
        Assert.Contains(atCap
            ? "ran out of room before it finished a short rewrite"
            : "didn't say whether its rewrite during validation was complete", harness.Service.StatusReason, StringComparison.Ordinal);
        Assert.Null(harness.Service.Recipient);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Thinking_past_the_old_allowance_cleans_and_a_runaway_answer_is_typed_as_heard(bool native)
    {
        // A scripted thinking model: each dictation needs its own number of tokens to think and answer, and a ceiling under
        // that gets only the start of its rewrite, cut by the server, as DeepSeek-R1 8B's were.
        string[] cleaned =
        [
            "can you send the quarterly numbers to finance before the review on tuesday",
            "the build broke again after the merge so lets hold the release until we know why",
            "remind me to call the vendor about the renewal and ask about the support plan",
        ];
        const string runaway = "so honestly the vendor demo was bad we are not going with them the pricing is way off";
        var needs = new Dictionary<string, int>
        {
            [cleaned[0]] = Visible(cleaned[0]) + 1024 + 120,
            [cleaned[1]] = Visible(cleaned[1]) + 1024 + 500,
            [cleaned[2]] = Visible(cleaned[2]) + 1024 + 900,
            [runaway] = Visible(runaway) + Room + 1500,
        };
        var wire = new Wire(sent =>
        {
            if (sent.Admission.Kind != CleanupRequestKind.Dictation)
            {
                return Task.FromResult(ThinkingAnswer(sent));
            }

            var rewrite = Rewrite(sent.Text);
            return Task.FromResult(needs[sent.Text] <= sent.Ceiling
                ? Answer(sent, rewrite, needs[sent.Text], "stop")
                : Answer(sent, rewrite[..(rewrite.Length / 2)], sent.Ceiling, "length"));
        });
        await using var harness = Harness(wire, 8192);
        harness.Service.Configure(Ollama(native));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        foreach (var text in cleaned)
        {
            // The rejected 1,024 would have cut every one of these.
            Assert.True(needs[text] > Visible(text) + 1024);
            var result = await harness.Service.CleanAsync(text).WaitAsync(Bound);
            Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
            Assert.Equal(Rewrite(text), result.Text);
            Assert.Equal(Visible(text) + Room, wire.Sent[^1].Ceiling);
        }

        var cut = await harness.Service.CleanAsync(runaway).WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Failed, cut.Outcome);
        Assert.Equal(runaway, cut.Text);
        Assert.Equal(CutReason, cut.FailureReason);
        Assert.Single(wire.Sent, sent => sent.Admission.Kind == CleanupRequestKind.Dictation && sent.Text == runaway);
    }

    [Theory]
    [InlineData(4096, false)]
    [InlineData(4096, true)]
    [InlineData(8192, false)]
    [InlineData(8192, true)]
    [InlineData(32768, false)]
    [InlineData(32768, true)]
    public async Task Every_request_fits_the_context_with_the_whole_room_to_think_at_4K_8K_and_32K(int context, bool native)
    {
        var options = native ? Ollama(native: true) with { LocalContextTokens = context } : Ollama();
        var wire = new Wire(sent => Task.FromResult(ThinkingAnswer(sent)));
        await using var harness = Harness(wire, context);
        harness.Service.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var text = string.Join(' ', Enumerable.Repeat(Dictated, 24));
        var result = await harness.Service.CleanAsync(text).WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
        Assert.All(wire.Sent, sent =>
        {
            AssertFits(sent, options, context);
            if (sent.Ceiling > 16)
            {
                Assert.Equal(Visible(sent.Text) + Room, sent.Ceiling);
            }

            if (native)
            {
                Assert.Equal(context, sent.Body.GetProperty("options").GetProperty("num_ctx").GetInt32());
            }
        });
        var chunks = wire.Sent.Where(sent => sent.Admission.Kind == CleanupRequestKind.Dictation).ToArray();
        Assert.Equal(context == 4096, chunks.Length > 1);
    }

    [Fact]
    public async Task An_app_writing_style_that_leaves_no_room_to_think_types_the_dictation_as_heard_without_sending_it()
    {
        var wire = new Wire(sent => Task.FromResult(ThinkingAnswer(sent)));
        await using var harness = Harness(wire, ContextBudget.AssumedContextTokens);
        harness.Service.Configure(Ollama());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var before = wire.Sent.Length;

        // About 560 tokens longer than the default style: with the room to think, even this short dictation has no room.
        var style = CleanupPrompt.DefaultWritingStyle + " " +
            string.Concat(Enumerable.Repeat("Keep every sentence formal, plain and short. ", 45));
        var result = await harness.Service.CleanAsync(Dictated, writingStyleOverride: style).WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Skipped, result.Outcome);
        Assert.Equal(Dictated, result.Text);
        Assert.Contains("don't fit", result.DisplayDetail, StringComparison.Ordinal);
        Assert.Equal(before, wire.Sent.Length);
        Assert.Equal(CleanupOutcome.Cleaned, (await harness.Service.CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
    }

    [Fact]
    public async Task The_room_to_think_fits_beside_the_shortest_dictation_in_Ollama_s_own_default_and_dictations_here_run_production_s_call_budget()
    {
        var options = Ollama();
        var instructions = TextCleanupService.BuildProbeSystemPrompt(options);
        var shortest = string.Join(' ', Enumerable.Repeat("word", TextCleanupService.MinLocalChunkChars / 5));
        Assert.True(ContextBudget.TextFits(ContextBudget.AssumedContextTokens, instructions, shortest, Visible(shortest) + Room));

        // A dictation's call: a 25 s first attempt, then once more with the rest of 45 s, all within 90 s. The readiness
        // check holds a thinking model's rewrite to that first attempt. A pinned timeout (the benchmark's) is one attempt.
        Assert.Equal(
            (TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(25), true),
            TextCleanupService.CallBudget(CleanupProvider.OpenAiCompatible, timeoutOverride: null));
        Assert.Equal(
            (TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(25), true),
            TextCleanupService.CallBudget(CleanupProvider.AzureFoundry, timeoutOverride: null));
        Assert.Equal(
            (TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(120), false),
            TextCleanupService.CallBudget(CleanupProvider.GitHubCopilot, timeoutOverride: null));
        Assert.Equal(
            (TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(12), false),
            TextCleanupService.CallBudget(CleanupProvider.FoundryLocal, timeoutOverride: null));
        Assert.Equal(
            (TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(45), false),
            TextCleanupService.CallBudget(CleanupProvider.OpenAiCompatible, TimeSpan.FromSeconds(45)));
        Assert.Equal(TimeSpan.FromSeconds(90), TextCleanupService.TotalBudgetFor(CleanupProvider.OpenAiCompatible));

        // Every test in this class runs that policy: nothing pins a timeout, a total or a ceiling.
        await using var harness = Harness(new Wire(sent => Task.FromResult(ThinkingAnswer(sent))));
        Assert.Null(harness.Service.CleanupTimeoutOverride);
        Assert.Null(harness.Service.CleanupTotalTimeoutOverride);
        Assert.Null(harness.Service.MaxOutputTokensOverride);
    }

    [Theory]
    [InlineData("stop", 100, 100, "Finished")]
    [InlineData("stop", null, 100, "Finished")]
    [InlineData("length", 3, 100, "Cut")]
    [InlineData("length", null, 100, "Cut")]
    [InlineData("content_filter", 5, 100, "Cut")]
    [InlineData("tool_calls", 5, 100, "Unconfirmed")]
    [InlineData(null, 100, 100, "Cut")]
    [InlineData(null, 101, 100, "Cut")]
    [InlineData(null, 99, 100, "Unconfirmed")]
    [InlineData(null, 0, 100, "Unconfirmed")]
    [InlineData(null, null, 100, "Unconfirmed")]
    [InlineData(null, 500, null, "Unconfirmed")]
    public void An_answer_ends_as_the_SDK_reports_it(string? finish, int? generated, int? ceiling, string expected)
    {
        var response = new Microsoft.Extensions.AI.ChatResponse(
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, RegionalVoicePartial))
        {
            FinishReason = finish is null ? null : new Microsoft.Extensions.AI.ChatFinishReason(finish),
            Usage = generated is null ? null : new Microsoft.Extensions.AI.UsageDetails { OutputTokenCount = generated },
        };

        Assert.Equal(
            expected, TextCleanupService.EndOf(new Microsoft.Agents.AI.AgentResponse(response), ceiling).ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_explicit_stop_and_an_omitted_finish_stay_distinct_even_without_usage(bool omitFinish)
    {
        var wire = new Wire(sent => Task.FromResult(sent.Admission.Kind == CleanupRequestKind.Dictation
            ? Answer(sent, RegionalVoiceCleaned, generated: null, finishReason: "stop", omitFinish)
            : ThinkingAnswer(sent)));
        await using var harness = Harness(wire, 8192);
        harness.Service.Configure(Ollama());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var result = await harness.Service.CleanAsync(RegionalVoice).WaitAsync(Bound);

        Assert.Equal(omitFinish ? CleanupOutcome.Failed : CleanupOutcome.Cleaned, result.Outcome);
        Assert.Equal(omitFinish ? RegionalVoice : RegionalVoiceCleaned, result.Text);
        Assert.Equal(omitFinish ? "The AI model didn't say whether its answer was complete." : null, result.FailureReason);
    }

    private static CleanupOptions Ollama(bool native = false) =>
        CleanupHarness.Custom(LocalAiServer.OllamaAddress, Model) with { LocalContextTokens = native ? 8192 : null };

    private static CleanupHarness Harness(Wire wire, int context = 4096)
    {
        var harness = new CleanupHarness(http: wire.Handler);
        harness.Service.OpenAIClientOptionsOverride = null;
        harness.Service.DisableRetries = true;
        harness.LocalServers.State = Held(context);
        harness.LocalServers.MaxContextTokens = context;
        return harness;
    }

    private static LocalServerState Held(int context) => new(
        LocalServerReach.Reached,
        [new LocalServerModel(Model, Model, 1)],
        [new LocalServerLoadedModel(Model, 1) { ContextTokens = context }]);

    private static int Visible(string text) => TextCleanupService.EstimateMaxTokens(text, CleanupProvider.OpenAiCompatible);

    private static string Rewrite(string text)
    {
        var words = text.StartsWith("um ", StringComparison.Ordinal) ? text[3..] : text;
        return words.Length == 0 ? string.Empty : char.ToUpperInvariant(words[0]) + words[1..] + ".";
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static HttpResponseMessage ThinkingAnswer(Sent sent, int? ceiling = null) =>
        (ceiling ?? sent.Ceiling) < 280
            ? Answer(sent, string.Empty, ceiling ?? sent.Ceiling)
            : Answer(sent, Rewrite(sent.Text), 280);

    private static HttpResponseMessage RejectedFields() =>
        ScriptedHttpHandler.Json(HttpStatusCode.BadRequest, """{"error":{"message":"reasoning_effort not supported"}}""");

    private static HttpResponseMessage Answer(
        Sent sent, string text, int? generated, string? finishReason = null, bool omitFinish = false)
    {
        var body = new Dictionary<string, object?>();
        if (sent.Native)
        {
            body["model"] = Model;
            body["created_at"] = "2026-10-07T00:00:00Z";
            body["message"] = new { role = "assistant", content = text, thinking = ThoughtCanary };
            body["done"] = true;
            if (!omitFinish)
            {
                body["done_reason"] = finishReason ?? (string.IsNullOrEmpty(text) ? "length" : "stop");
            }

            body["prompt_eval_count"] = 100;
            if (generated is { } count)
            {
                body["eval_count"] = count;
            }
        }
        else
        {
            body["id"] = "test";
            body["object"] = "chat.completion";
            body["created"] = 1700000000;
            body["model"] = Model;
            var choice = new Dictionary<string, object?>
            {
                ["index"] = 0,
                ["message"] = new { role = "assistant", content = text, reasoning_content = ThoughtCanary },
            };
            if (!omitFinish)
            {
                choice["finish_reason"] = finishReason ?? (string.IsNullOrEmpty(text) ? "length" : "stop");
            }

            body["choices"] = new[] { choice };
            if (generated is { } count)
            {
                body["usage"] = new { prompt_tokens = 100, completion_tokens = count, total_tokens = 100 + count };
            }
        }

        return ScriptedHttpHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(body));
    }

    private static void AssertGeneration(Sent sent, bool native, bool plain, int context)
    {
        Assert.Equal(native, sent.Native);
        if (native)
        {
            Assert.False(sent.Body.GetProperty("think").GetBoolean());
            Assert.Equal(context, sent.Body.GetProperty("options").GetProperty("num_ctx").GetInt32());
            Assert.Equal(0.1, sent.Body.GetProperty("options").GetProperty("temperature").GetDouble(), 3);
        }
        else
        {
            Assert.Equal(!plain, sent.Body.TryGetProperty("reasoning_effort", out var effort));
            Assert.Equal(!plain, sent.Body.TryGetProperty("max_tokens", out var maximum));
            if (!plain)
            {
                Assert.Equal("none", effort.GetString());
                Assert.Equal(sent.Ceiling, maximum.GetInt32());
            }

            Assert.Equal(0.1, sent.Body.GetProperty("temperature").GetDouble(), 3);
            Assert.False(sent.Body.TryGetProperty("num_ctx", out _));
        }
    }

    private static void AssertFits(Sent sent, CleanupOptions options, int context)
    {
        var instructions = TextCleanupService.BuildProbeSystemPrompt(options);
        var glossary = string.Empty;
        if (sent.System.StartsWith(instructions, StringComparison.Ordinal))
        {
            glossary = sent.System[instructions.Length..];
        }
        else
        {
            instructions = sent.System;
        }

        var tokens = ContextBudget.ChatTemplateTokens + TokenEstimate.Prose(instructions) +
            TokenEstimate.Vocabulary(glossary.Trim()) + TokenEstimate.Transcript(sent.Text) + sent.Ceiling +
            ContextBudget.Margin(context);
        Assert.True(tokens <= context, $"The {sent.Admission.Kind} request declares {tokens} tokens in a context of {context}.");
    }

    private sealed record Sent(string Path, JsonElement Body, CleanupAdmission Admission)
    {
        public bool Native => Path == "/api/chat";
        public int Ceiling => Native
            ? Body.GetProperty("options").GetProperty("num_predict").GetInt32()
            : Body.GetProperty("max_completion_tokens").GetInt32();
        public string System => Message("system");
        public string User => Message("user");
        public string Text => User.StartsWith(TextCleanupService.TranscriptOpenTag, StringComparison.Ordinal)
            ? User[(TextCleanupService.TranscriptOpenTag.Length + 1)..^(TextCleanupService.TranscriptCloseTag.Length + 1)]
            : User;

        private string Message(string role) => string.Join('\n', Body.GetProperty("messages").EnumerateArray()
            .Where(message => message.GetProperty("role").GetString() == role)
            .Select(message => message.GetProperty("content").GetString()));
    }

    private sealed class Wire
    {
        private readonly ConcurrentQueue<Sent> _sent = new();

        public Wire(Func<Sent, Task<HttpResponseMessage>> respond)
        {
            Handler = new ScriptedHttpHandler(async (request, ct) =>
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                var sent = new Sent(request.RequestUri!.AbsolutePath, body, Assert.IsType<CleanupAdmission>(CleanupAdmission.Current));
                _sent.Enqueue(sent);
                return await respond(sent);
            });
        }

        public ScriptedHttpHandler Handler { get; }
        public Sent[] Sent => _sent.ToArray();
    }

    private sealed class ImmediateRetryPolicy() : System.ClientModel.Primitives.ClientRetryPolicy(1)
    {
        protected override TimeSpan GetNextDelay(System.ClientModel.Primitives.PipelineMessage message, int tryCount) =>
            TimeSpan.Zero;
    }
}
