using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using Scribe.Evals.Benchmark;

namespace Scribe.Core.Tests;

/// <summary>
/// PLAT-O-05+PLAT-A-08: the eval harness records the prompt cache's read and write counts, so a production-shaped run
/// (`--glossary-libraries`) can show whether the stable prefix is ever read back. A count the service did not report
/// stays null, never 0. Offline: fixtures only, no endpoint.
/// </summary>
#pragma warning disable OPENAI001
public sealed class EvalUsageCountsTests
{
    [Fact]
    public void The_agent_path_records_cached_tokens_and_keeps_unreported_counts_null()
    {
        var counted = BenchmarkRunner.AddUsage(null, new UsageDetails
        {
            InputTokenCount = 7000,
            OutputTokenCount = 120,
            TotalTokenCount = 7120,
            CachedInputTokenCount = 6912,
            ReasoningTokenCount = 80,
        });

        Assert.Equal(6912, counted.CachedInputTokens);
        Assert.Equal(80, counted.ReasoningTokens);
        Assert.Null(counted.CacheWriteTokens);

        var unreported = BenchmarkRunner.AddUsage(null, new UsageDetails { InputTokenCount = 1000 });
        Assert.Null(unreported.CachedInputTokens);
        Assert.Null(unreported.ReasoningTokens);
        Assert.Null(unreported.CacheWriteTokens);
    }

    [Fact]
    public void Chunks_add_up_and_a_passed_through_cache_write_count_is_kept()
    {
        var first = BenchmarkRunner.AddUsage(null, new UsageDetails
        {
            CachedInputTokenCount = 100,
            AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["InputTokenDetails.CacheWriteTokenCount"] = 40 },
        });
        var both = BenchmarkRunner.AddUsage(first, new UsageDetails { CachedInputTokenCount = 50 });

        Assert.Equal(150, both.CachedInputTokens);
        Assert.Equal(40, both.CacheWriteTokens);
    }

    [Fact]
    public void The_direct_path_reads_cached_and_cache_write_tokens_from_the_response()
    {
        var written = Read("{\"cached_tokens\":6912,\"cache_write_tokens\":88}");
        var usage = DirectResponsesCleanupClient.ToUsage(written.Usage!);
        Assert.Equal(6912, usage.CachedInputTokens);
        Assert.Equal(88, usage.CacheWriteTokens);
        Assert.Equal(80, usage.ReasoningTokens);

        var plain = DirectResponsesCleanupClient.ToUsage(Read("{\"cached_tokens\":0}").Usage!);
        Assert.Equal(0, plain.CachedInputTokens);
        Assert.Null(plain.CacheWriteTokens);
    }

    private static ResponseResult Read(string inputTokenDetails) =>
        ModelReaderWriter.Read<ResponseResult>(BinaryData.FromString(
            "{\"id\":\"resp_usage\",\"object\":\"response\",\"created_at\":1700000000,\"status\":\"completed\"," +
            "\"model\":\"gpt-6-astra\",\"output\":[{\"type\":\"message\",\"id\":\"msg_1\",\"status\":\"completed\"," +
            "\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hello.\",\"annotations\":[]}]}]," +
            "\"parallel_tool_calls\":false,\"tool_choice\":\"auto\",\"tools\":[]," +
            "\"usage\":{\"input_tokens\":7000,\"input_tokens_details\":" + inputTokenDetails + ",\"output_tokens\":120," +
            "\"output_tokens_details\":{\"reasoning_tokens\":80},\"total_tokens\":7120}}"))!;
}
#pragma warning restore OPENAI001
