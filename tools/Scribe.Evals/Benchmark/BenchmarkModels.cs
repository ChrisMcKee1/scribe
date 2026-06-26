using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;

namespace Scribe.Evals.Benchmark;

/// <summary>Builds the model roster: cloud via live Azure discovery, local via a curated alias set.</summary>
internal static class BenchmarkModels
{
    // Curated Foundry Local roster spanning families and sizes (0.5B → 14B), ordered small → large so
    // a long run yields many results early. Aliases resolve to this machine's optimal GPU variant
    // (TRT-RTX / CUDA) automatically. A few reasoning models are included on purpose to show they are
    // unsuitable for real-time cleanup (slow + tend to "think"/answer rather than edit). Vision-capable
    // aliases that also accept plain text are kept; pure coder/vision models are skipped.
    private static readonly (string Alias, string? Note)[] DefaultLocal =
    [
        ("qwen3-0.6b", null),
        ("qwen2.5-0.5b", null),
        ("qwen3.5-0.8b", null),
        ("qwen2.5-1.5b", null),
        ("qwen3-1.7b", null),
        ("deepseek-r1-1.5b", "reasoning"),
        ("qwen3.5-2b-text", null),
        ("smollm3-3b", null),
        ("phi-3.5-mini", null),
        ("phi-3-mini-4k", null),
        ("ministral-3-3b-instruct-2512", null),
        ("qwen3-4b", null),
        ("phi-4-mini", null),
        ("mistral-7b-v0.2", null),
        ("qwen2.5-7b", null),
        ("olmo-3-7b-instruct", null),
        ("qwen3-8b", null),
        ("deepseek-r1-7b", "reasoning"),
        ("mistral-nemo-12b-instruct", null),
        ("phi-4", null),
        ("qwen2.5-14b", null),
        ("qwen3-14b", "reasoning"),
    ];

    // Cloud model families that "think" before answering — generous latency expected, and a real risk
    // of answering/executing the dictation instead of editing it.
    private static readonly string[] CloudReasoning =
        ["gpt-5", "gpt-5.1", "gpt-5.2", "gpt-5.4", "gpt-5.4-pro", "gpt-5.3-codex"];

    public static async Task<IReadOnlyList<BenchModel>> BuildCloudAsync(
        string? tenantId, IReadOnlyList<string>? only, int max, ILogger log, CancellationToken ct)
    {
        var discovery = new AzureFoundryDiscovery(NullLogger<AzureFoundryDiscovery>.Instance);
        var deployments = await discovery.DiscoverAsync(tenantId, ct).ConfigureAwait(false);
        log.LogInformation("Azure discovery returned {Count} text-capable deployments.", deployments.Count);

        // De-dupe by underlying model name, preferring the project resource that hosts the widest set
        // (it tends to have the best quota), then any classic account.
        var deduped = deployments
            .GroupBy(d => d.ModelName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(d => d.Endpoint.Contains("mtech-project-resource", StringComparison.OrdinalIgnoreCase))
                .First())
            .ToList();

        var models = new List<BenchModel>();
        foreach (var d in deduped)
        {
            var id = string.IsNullOrWhiteSpace(d.ModelName) ? d.DeploymentName : d.ModelName;
            if (only is { Count: > 0 } && !only.Any(o => id.Contains(o, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var note = CloudReasoning.Any(r => string.Equals(r, id, StringComparison.OrdinalIgnoreCase))
                ? "reasoning"
                : id.Contains("audio", StringComparison.OrdinalIgnoreCase) ? "audio model" : null;

            models.Add(new BenchModel(
                BenchGroup.Cloud, id, CleanupProvider.AzureFoundry, d.Endpoint, d.DeploymentName, d.ModelName, note));
        }

        models = models.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
        if (max > 0 && models.Count > max)
        {
            models = models.Take(max).ToList();
        }

        return models;
    }

    public static IReadOnlyList<BenchModel> BuildLocal(IReadOnlyList<string>? overrideAliases, int max)
    {
        var source = overrideAliases is { Count: > 0 }
            ? overrideAliases.Select(a => (Alias: a, Note: (string?)null)).ToArray()
            : DefaultLocal;

        var models = source
            .Select(x => new BenchModel(
                BenchGroup.Local, x.Alias, CleanupProvider.FoundryLocal, null, x.Alias, null, x.Note))
            .ToList();

        if (max > 0 && models.Count > max)
        {
            models = models.Take(max).ToList();
        }

        return models;
    }
}
