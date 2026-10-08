using System.Net;
using System.Text.Json;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

public sealed class CleanupPreparationRecoveryTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);
    private const string Model = "gemma4:e2b";
    private const string Raw = "um so we ship on friday";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_cold_preparation_sends_no_dictation_and_the_next_recording_can_recover(bool native)
    {
        var phase = 0;
        var dictations = 0;
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            if (Volatile.Read(ref phase) == 1)
            {
                if (!IsReadying(body))
                {
                    Interlocked.Increment(ref dictations);
                }

                return LoadFailure();
            }

            if (Volatile.Read(ref phase) == 2 && !IsReadying(body))
            {
                Interlocked.Increment(ref dictations);
            }

            return Answer(native, "So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        svc.Configure(Options(native));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.LocalServers.State = new LocalServerState(LocalServerReach.Reached, [], []);
        Volatile.Write(ref phase, 1);
        var admitted = svc.Admit(CleanupVocabulary.None);
        admitted.Prewarm();
        await svc.WaitForPrewarmForTesting().WaitAsync(Bound);
        var failed = await admitted.CleanAsync(Raw).WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Equal(Raw, failed.Text);
        Assert.Equal(0, Volatile.Read(ref dictations));
        Assert.DoesNotContain("usually transient", failed.FailureReason, StringComparison.Ordinal);
        Assert.Equal(CleanupStatus.Ready, svc.Status);

        Volatile.Write(ref phase, 2);
        admitted.Prewarm();
        await svc.WaitForPrewarmForTesting().WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Cleaned, (await admitted.CleanAsync(Raw).WaitAsync(Bound)).Outcome);
        Assert.Equal(1, Volatile.Read(ref dictations));
    }

    [Fact]
    public async Task Returning_to_a_previous_configuration_does_not_reuse_its_failed_preparation()
    {
        var failPreparation = false;
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            return failPreparation && IsReadying(body) ? LoadFailure() : Answer(false, "So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        var options = Options(false);
        svc.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.LocalServers.State = new LocalServerState(LocalServerReach.Reached, [], []);
        failPreparation = true;
        svc.Admit(CleanupVocabulary.None).Prewarm();
        await svc.WaitForPrewarmForTesting().WaitAsync(Bound);

        failPreparation = false;
        svc.Configure(options with { CustomModel = "another-model" });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        svc.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var result = await svc.Admit(CleanupVocabulary.None).CleanAsync(Raw).WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
    }

    [Fact]
    public async Task Returning_to_a_configuration_still_waits_for_its_in_flight_preparation_but_drops_the_old_failure()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readying = false;
        var dictations = 0;
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            if (readying && IsReadying(body))
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
                return LoadFailure();
            }

            if (body.ToString().Contains(Raw, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref dictations);
            }

            return Answer(false, "So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        var options = Options(false);
        svc.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.LocalServers.State = new LocalServerState(LocalServerReach.Reached, [], []);
        readying = true;
        svc.Admit(CleanupVocabulary.None).Prewarm();
        await started.Task.WaitAsync(Bound);
        try
        {
            svc.Configure(options with { CustomModel = "another-model" });
            await harness.WaitForStatusAsync(CleanupStatus.Ready);
            svc.Configure(options);
            await harness.WaitForStatusAsync(CleanupStatus.Ready);
            var cleanup = svc.Admit(CleanupVocabulary.None).CleanAsync(Raw);
            Assert.False(cleanup.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref dictations));
            readying = false;
            release.TrySetResult();
            Assert.Equal(CleanupOutcome.Cleaned, (await cleanup.WaitAsync(Bound)).Outcome);
            Assert.Equal(1, Volatile.Read(ref dictations));
        }
        finally
        {
            release.TrySetResult();
            await svc.WaitForPrewarmForTesting().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task A_completed_preparation_from_an_earlier_owner_cannot_block_a_new_configuration()
    {
        var failPreparation = false;
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            return failPreparation && IsReadying(body) ? LoadFailure() : Answer(false, "So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        var options = Options(false);
        svc.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.LocalServers.State = new LocalServerState(LocalServerReach.Reached, [], []);
        failPreparation = true;
        svc.Admit(CleanupVocabulary.None).Prewarm();
        await svc.WaitForPrewarmForTesting().WaitAsync(Bound);

        failPreparation = false;
        svc.Configure(options with { Enabled = false });
        svc.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var result = await svc.Admit(CleanupVocabulary.None).CleanAsync(Raw).WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_local_server_failure_stops_the_remaining_chunks_even_after_a_stall_retry(bool native, bool stallFirst)
    {
        var failing = false;
        var attempts = 0;
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            if (failing && !IsReadying(body))
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (stallFirst && attempt == 1)
                {
                    throw new OperationCanceledException();
                }

                return LoadFailure();
            }

            return Answer(native, "So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        var options = Options(native) with { PromptStyle = CleanupPromptStyle.Local };
        svc.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var raw = string.Concat(Enumerable.Repeat("we ship on friday. ", 350));
        Assert.True(TextCleanupService.PrepareChunks(raw, options).Count > 1);
        failing = true;

        var result = await svc.Admit(CleanupVocabulary.None).CleanAsync(raw).WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal(raw, result.Text);
        Assert.Equal(stallFirst ? 2 : 1, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task A_one_off_request_waits_for_a_failed_preparation_but_the_next_click_can_retry()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing = false;
        var completions = 0;
        const string input = "A synthetic summary request.";
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            if (failing && IsReadying(body))
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
                return LoadFailure();
            }

            if (body.ToString().Contains(input, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref completions);
            }

            return Answer(false, "A synthetic summary.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        svc.Configure(Options(false));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.LocalServers.State = new LocalServerState(LocalServerReach.Reached, [], []);
        failing = true;
        svc.Admit(CleanupVocabulary.None).Prewarm();
        await started.Task.WaitAsync(Bound);
        try
        {
            var completion = svc.CompleteAsync("Return a short answer.", input, svc.Recipient!);
            Assert.False(completion.IsCompleted);
            release.TrySetResult();
            Assert.Equal(CompletionOutcome.Failed, (await completion.WaitAsync(Bound)).Outcome);
            Assert.Equal(0, Volatile.Read(ref completions));

            failing = false;
            Assert.Equal(CompletionOutcome.Completed,
                (await svc.CompleteAsync("Return a short answer.", input, svc.Recipient!).WaitAsync(Bound)).Outcome);
            Assert.Equal(1, Volatile.Read(ref completions));
        }
        finally
        {
            release.TrySetResult();
            await svc.WaitForPrewarmForTesting().WaitAsync(Bound);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_new_one_off_request_does_not_inherit_a_completed_recording_preparation_failure(bool native)
    {
        var failing = false;
        var completions = 0;
        const string input = "A synthetic summary request.";
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            if (failing && IsReadying(body))
            {
                return LoadFailure();
            }

            if (body.ToString().Contains(input, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref completions);
            }

            return Answer(native, "A synthetic summary.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        svc.Configure(Options(native));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.LocalServers.State = new LocalServerState(LocalServerReach.Reached, [], []);
        failing = true;
        svc.Admit(CleanupVocabulary.None).Prewarm();
        await svc.WaitForPrewarmForTesting().WaitAsync(Bound);

        failing = false;
        var result = await svc.CompleteAsync("Return a short answer.", input, svc.Recipient!).WaitAsync(Bound);
        Assert.Equal(CompletionOutcome.Completed, result.Outcome);
        Assert.Equal(1, Volatile.Read(ref completions));
    }

    [Fact]
    public async Task An_exhausted_preparation_does_not_buy_a_fresh_call_and_retry_budget()
    {
        var dictations = 0;
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            if (IsReadying(body))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            else if (body.ToString().Contains(Raw, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref dictations);
            }

            return Answer(false, "So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        svc.ModelPreparationTimeoutOverride = TimeSpan.FromMilliseconds(30);
        svc.Configure(Options(false));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.LocalServers.State = new LocalServerState(LocalServerReach.Reached, [], []);
        var admitted = svc.Admit(CleanupVocabulary.None);
        admitted.Prewarm();
        await svc.WaitForPrewarmForTesting().WaitAsync(Bound);
        var result = await admitted.CleanAsync(Raw).WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal(Raw, result.Text);
        Assert.Equal(0, Volatile.Read(ref dictations));
        Assert.Equal(CleanupStatus.Ready, svc.Status);
    }

    [Fact]
    public async Task A_resident_model_s_generic_preparation_error_does_not_claim_its_load_failed()
    {
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            return IsReadying(body)
                ? ScriptedHttpHandler.Json(HttpStatusCode.InternalServerError, """{"error":"temporary server failure"}""")
                : Answer(false, "So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        svc.Configure(Options(false));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.LocalServers.State = new LocalServerState(
            LocalServerReach.Reached, [], [new LocalServerLoadedModel(Model, 1) { ContextTokens = 4096 }]);
        svc.ForgetLastModelAnswerForTesting();
        var admitted = svc.Admit(CleanupVocabulary.None);
        admitted.Prewarm();
        await svc.WaitForPrewarmForTesting().WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Cleaned, (await admitted.CleanAsync(Raw).WaitAsync(Bound)).Outcome);
    }

    [Fact]
    public async Task The_total_cleanup_deadline_also_bounds_waiting_for_preparation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dictations = 0;
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            if (IsReadying(body))
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            else if (body.ToString().Contains(Raw, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref dictations);
            }

            return Answer(false, "So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        svc.LocalModelStartWait = Bound;
        svc.Configure(Options(false));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.LocalServers.State = new LocalServerState(LocalServerReach.Reached, [], []);
        try
        {
            var admitted = svc.Admit(CleanupVocabulary.None);
            admitted.Prewarm();
            await started.Task.WaitAsync(Bound);
            svc.CleanupTotalTimeoutOverride = TimeSpan.FromMilliseconds(30);
            var result = await admitted.CleanAsync(Raw).WaitAsync(Bound);
            Assert.Equal(CleanupOutcome.Failed, result.Outcome);
            Assert.Equal("AI cleanup exceeded the total time limit.", result.FailureReason);
            Assert.Equal(0, Volatile.Read(ref dictations));
        }
        finally
        {
            release.TrySetResult();
            await svc.WaitForPrewarmForTesting().WaitAsync(Bound);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_local_server_error_has_an_actionable_safe_cause_without_claiming_it_is_transient(bool ollama)
    {
        var options = CleanupHarness.Custom(ollama ? LocalAiServer.OllamaAddress : LocalAiServer.LmStudioAddress, Model);
        var ex = new HttpRequestException("private request detail", null, HttpStatusCode.InternalServerError);
        var reason = TextCleanupService.DescribeFailureReason(ex, options);
        Assert.StartsWith(ollama ? "Ollama couldn't load or run" : "LM Studio couldn't load or run", reason.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private request detail", reason.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("transient", reason.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("usually transient", TextCleanupService.DescribeFailureReason(ex,
            CleanupHarness.Custom("https://ai.example.invalid/v1", Model)).Diagnostic, StringComparison.Ordinal);
    }

    private static CleanupOptions Options(bool native) =>
        CleanupHarness.Custom(LocalAiServer.OllamaAddress, Model) with
        {
            LocalContextTokens = native ? 8192 : null,
            LocalModelKeepAliveMinutes = 0,
        };

    private static bool IsReadying(JsonElement body) =>
        (body.TryGetProperty("max_tokens", out var max) && max.GetInt32() == 1) ||
        (body.TryGetProperty("options", out var options) &&
            options.TryGetProperty("num_predict", out var predicted) && predicted.GetInt32() == 1);

    private static HttpResponseMessage LoadFailure() => ScriptedHttpHandler.Json(HttpStatusCode.InternalServerError,
        """{"error":"llama-server process has terminated: failed to initialize the context"}""");

    private static HttpResponseMessage Answer(bool native, string content) =>
        native ? ScriptedHttpHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            model = Model,
            created_at = "2026-10-08T00:00:00Z",
            message = new { role = "assistant", content },
            done = true,
            done_reason = "stop",
            prompt_eval_count = 40,
            eval_count = 5,
        })) : ScriptedHttpHandler.ChatCompletion(content);
}
