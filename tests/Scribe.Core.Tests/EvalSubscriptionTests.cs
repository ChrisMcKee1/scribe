using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Responses;
using Scribe.Evals;
using Scribe.Evals.Benchmark;

#pragma warning disable OPENAI001

namespace Scribe.Core.Tests;

public sealed class EvalSubscriptionTests
{
    [Fact]
    public void Subscription_pins_benchmark_eval_and_judge()
    {
        var options = CliOptions.Parse(["--provider", "azure", "--subscription", "cleanup-sub"]);
        var config = options.ToBenchmarkConfig();

        Assert.Equal("cleanup-sub", config.SubscriptionId);
        Assert.Equal("cleanup-sub", config.JudgeSubscriptionId);
        Assert.Equal("cleanup-sub", options.BuildOptions("model", "style").AzureSubscriptionId);
    }

    [Fact]
    public void Judge_subscription_can_be_overridden_independently()
    {
        var options = CliOptions.Parse(
            ["--subscription", "cleanup-sub", "--judge-subscription", "judge-sub"]);
        var config = options.ToBenchmarkConfig();

        Assert.Equal("cleanup-sub", config.SubscriptionId);
        Assert.Equal("judge-sub", config.JudgeSubscriptionId);
    }

    [Fact]
    public void Omitted_subscription_preserves_unpinned_behavior()
    {
        var options = CliOptions.Parse(["--provider", "azure"]);
        var config = options.ToBenchmarkConfig();

        Assert.Null(config.SubscriptionId);
        Assert.Null(config.JudgeSubscriptionId);
        Assert.Null(options.BuildOptions("model", "style").AzureSubscriptionId);
    }

    [Theory]
    [InlineData("--subscription")]
    [InlineData("--judge-subscription")]
    public void Missing_subscription_value_fails_explicitly(string flag)
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse([flag]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse([flag, "--benchmark"]));
    }

    [Fact]
    public void Judge_requests_disable_storage_without_setting_temperature()
    {
        var options = QualityJudge.BuildChatOptions();
        var raw = Assert.IsType<CreateResponseOptions>(options.RawRepresentationFactory!(null!));

        Assert.False(raw.StoredOutputEnabled);
        Assert.Null(options.Temperature);
        Assert.Equal(ChatResponseFormat.Json, options.ResponseFormat);
    }

    [Fact]
    public async Task Empty_roster_does_not_report_a_successful_benchmark()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"scribe-eval-{Guid.NewGuid():N}");
        try
        {
            var runner = new BenchmarkRunner(new BenchmarkConfig
            {
                OutDir = directory,
                IncludeCloud = false,
                IncludeLocal = false,
                UseJudge = false,
                Synthesize = false,
            }, NullLogger.Instance);

            Assert.Equal(2, await runner.RunAsync(CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(directory, "results.json")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
