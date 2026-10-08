using System.Net;
using System.Text.Json;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Evals;
using Scribe.Evals.Benchmark;

namespace Scribe.Core.Tests;

public sealed class EvalProductionGateTests
{
    [Fact]
    public void Production_admission_does_not_change_the_runtime_s_context_or_vocabulary_setting()
    {
        var config = CliOptions.Parse(["--benchmark", "--admitted"]).ToBenchmarkConfig();

        Assert.True(config.Admitted);
        Assert.True(config.AdmitRequests);
        Assert.Null(config.LocalContextTokens);
        Assert.False(config.SendWholeVocabulary);
    }

    [Fact]
    public void Existing_benchmark_paths_keep_their_admission_decision()
    {
        Assert.False(CliOptions.Parse(["--benchmark"]).ToBenchmarkConfig().Admitted);
        Assert.True(CliOptions.Parse(["--benchmark", "--context-size", "32768"]).ToBenchmarkConfig().Admitted);
        Assert.True(CliOptions.Parse(["--benchmark", "--whole-vocabulary"]).ToBenchmarkConfig().Admitted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(180)]
    public void The_requested_timeout_reaches_the_service_configuration(int seconds)
    {
        var options = CliOptions.Parse(["--benchmark", "--clean-timeout", seconds.ToString()]);
        var config = options.ToBenchmarkConfig();

        Assert.Equal(seconds, config.CleanTimeoutSeconds);
        Assert.Equal(seconds == 0 ? null : (TimeSpan?)TimeSpan.FromSeconds(seconds), config.CleanupTimeout);
    }

    [Fact]
    public void An_omitted_timeout_keeps_the_existing_benchmark_default()
    {
        var config = CliOptions.Parse(["--benchmark"]).ToBenchmarkConfig();

        Assert.Equal(180, config.CleanTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(180), config.CleanupTimeout);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("invalid")]
    [InlineData("")]
    public void An_invalid_timeout_is_refused_instead_of_running_a_different_policy(string value) =>
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--benchmark", "--clean-timeout", value]));

    [Fact]
    public void A_direct_request_cannot_claim_the_production_retry_deadlines()
    {
        var options = CliOptions.Parse(["--benchmark", "--direct-responses", "--clean-timeout", "0"]);

        Assert.Throws<ArgumentException>(options.ToBenchmarkConfig);
    }

    [Theory]
    [InlineData("Failed", 1, 0)]
    [InlineData("Skipped", 0, 1)]
    public void One_successful_rewrite_cannot_hide_a_failed_or_skipped_run(
        string unsuccessful, int failed, int skipped)
    {
        var summary = BenchResult.SummarizeOutcomes(
            [Case("first", "Cleaned", unsuccessful), Case("second", "Unchanged")], anyChanged: true);

        Assert.Equal("degraded", summary.Status);
        Assert.Equal($"cleanup failed in {failed} and skipped in {skipped} of 3 timed runs", summary.Error);
    }

    [Fact]
    public void Completed_unchanged_text_is_not_a_failure()
    {
        var summary = BenchResult.SummarizeOutcomes(
            [Case("first", "Cleaned", "Unchanged")], anyChanged: true);

        Assert.Equal("ok", summary.Status);
        Assert.Null(summary.Error);
    }

    [Fact]
    public void An_all_unchanged_run_keeps_the_existing_no_op_classification()
    {
        var summary = BenchResult.SummarizeOutcomes([Case("first", "Unchanged")], anyChanged: false);

        Assert.Equal("degraded", summary.Status);
        Assert.Equal("output identical to raw on every case (no-op / internal fallback)", summary.Error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task The_summary_cannot_hide_a_real_partial_cleanup_or_raw_overflow(bool overflow, bool unchanged)
    {
        var text = string.Join(' ', Enumerable.Repeat(new string('a', 2399), overflow ? 21 : 2));
        var requests = 0;
        var options = CleanupHarness.Custom("https://completion.example.invalid/v1", "test") with
        {
            PromptStyle = CleanupPromptStyle.Local,
        };
        var chunks = TextCleanupService.PrepareChunks(text, options);
        Assert.Equal(overflow ? 21 : 2, chunks.Count);
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var user = document.RootElement.GetProperty("messages").EnumerateArray()
                .Last(message => message.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
            if (user == TextCleanupService.BuildUserMessage("ok"))
            {
                return ScriptedHttpHandler.ChatCompletion("ok");
            }

            var index = requests++;
            var chunk = chunks[index];
            var answer = unchanged ? chunk : char.ToUpperInvariant(chunk[0]) + chunk[1..];
            var reason = !overflow && index == 1 ? "length" : "stop";
            return ScriptedHttpHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                id = "synthetic",
                @object = "chat.completion",
                created = 1700000000,
                model = "test",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = answer }, finish_reason = reason } },
            }));
        });
        await using var harness = new CleanupHarness(http: http);
        harness.Service.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var result = await harness.Service.CleanAsync(text);

        Assert.Equal(unchanged ? CleanupOutcome.Unchanged : CleanupOutcome.Cleaned, result.Outcome);
        Assert.NotNull(result.FailureReason);
        Assert.Equal(overflow ? 20 : 2, requests);
        Assert.EndsWith(chunks[^1], result.Text, StringComparison.Ordinal);
        var recorded = new BenchCaseResult("partial", 1, [1], null, null, [], null, result.Changed, result.Text,
            Outcomes: [result.Outcome.ToString()], PartialFailures: [BenchResult.PartiallyFailed(result)]);
        Assert.True(Assert.Single(recorded.PartialFailures!));
        recorded = JsonSerializer.Deserialize<BenchCaseResult>(JsonSerializer.Serialize(recorded))!;

        var summary = BenchResult.SummarizeOutcomes([recorded, Case("completed", "Cleaned")], anyChanged: true);

        Assert.Equal("degraded", summary.Status);
        Assert.Contains("partially completed in 1 of 2 timed runs", summary.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CleanupOutcome.Cleaned, false, false)]
    [InlineData(CleanupOutcome.Unchanged, false, false)]
    [InlineData(CleanupOutcome.Cleaned, true, true)]
    [InlineData(CleanupOutcome.Unchanged, true, true)]
    [InlineData(CleanupOutcome.Failed, true, false)]
    [InlineData(CleanupOutcome.Skipped, true, false)]
    public void Recording_partial_failure_keeps_only_the_flag(
        CleanupOutcome outcome, bool withFailure, bool partial)
    {
        const string privateDetail = "Synthetic text that must not become benchmark failure metadata";
        var result = new CleanupResult("synthetic", outcome, withFailure ? privateDetail : null)
        {
            DisplayDetail = privateDetail,
        };
        var recorded = Case("test", outcome.ToString()) with
        {
            PartialFailures = [BenchResult.PartiallyFailed(result)],
        };

        Assert.Equal(partial, Assert.Single(recorded.PartialFailures));
        var json = JsonSerializer.Serialize(recorded);
        Assert.DoesNotContain(privateDetail, json, StringComparison.Ordinal);
        Assert.Equal(recorded.PartialFailures, JsonSerializer.Deserialize<BenchCaseResult>(json)!.PartialFailures);
    }

    [Fact]
    public void Counts_stay_per_run_and_partial_flags_do_not_double_count_failed_or_skipped_runs()
    {
        var first = Case("first", "Cleaned", "Unchanged", "Failed", "Skipped") with
        {
            PartialFailures = [true, true, true, true],
        };
        var second = Case("second", "Cleaned", "Unchanged", "Cleaned") with
        {
            PartialFailures = [false, false, true],
        };

        var summary = BenchResult.SummarizeOutcomes([first, second], anyChanged: true);

        Assert.Equal("degraded", summary.Status);
        Assert.Equal("cleanup failed in 1, skipped in 1, and partially completed in 3 of 7 timed runs", summary.Error);
    }

    [Fact]
    public void An_older_case_without_partial_metadata_still_deserializes_without_claiming_a_recorded_flag()
    {
        const string older = """
            {"CaseId":"older","MedianMs":1,"AllMs":[1],"Flags":[],"Changed":true,"Output":"synthetic","Outcomes":["Cleaned"]}
            """;
        var result = JsonSerializer.Deserialize<BenchCaseResult>(older)!;

        Assert.Null(result.PartialFailures);
        Assert.Equal("synthetic", result.Output);
        Assert.NotNull(result.Outcomes);
        Assert.Equal(["Cleaned"], result.Outcomes);
        Assert.Equal("ok", BenchResult.SummarizeOutcomes([result], anyChanged: true).Status);
    }

    [Fact]
    public void Partial_unchanged_output_is_degraded_for_its_partial_failure_not_the_legacy_no_op_heuristic()
    {
        var result = Case("unchanged", "Unchanged") with { PartialFailures = [true] };

        var summary = BenchResult.SummarizeOutcomes([result], anyChanged: false);

        Assert.Equal("degraded", summary.Status);
        Assert.Contains("partially completed in 1 of 1 timed runs", summary.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_runner_uses_the_tested_timeout_and_outcome_decisions()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scribe.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory.FullName, "tools", "Scribe.Evals", "Benchmark", "BenchmarkRunner.cs"));

        Assert.Contains("CleanupTimeoutOverride = _cfg.CleanupTimeout,", source);
        Assert.Contains("CleanTimeoutSeconds = _cfg.CleanTimeoutSeconds,", source);
        Assert.Contains("AdmittedRequests = admitted,", source);
        Assert.Contains("BenchResult.SummarizeOutcomes(caseResults, anyChanged)", source);
        Assert.Contains("Status = outcomeSummary.Status,", source);
        Assert.Contains("Error = outcomeSummary.Error,", source);
        Assert.Contains("partialFailure = BenchResult.PartiallyFailed(cleaned);", source);
        Assert.Contains("partialFailures.Add(partialFailure);", source);
        Assert.Contains("outputs.ToArray(), outcomes.ToArray(), partialFailures.ToArray()", source);
    }

    private static BenchCaseResult Case(string id, params string[] outcomes) =>
        new(id, 1, Enumerable.Repeat(1d, outcomes.Length).ToArray(), null, null, [], null, true, "synthetic", Outcomes: outcomes);
}
