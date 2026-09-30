using Microsoft.Extensions.Logging;
using Scribe.Core.Cleanup;

namespace Scribe.Evals.Benchmark;

/// <summary>
/// Where the local servers a roster entry can name are listening. The defaults are the ports Ollama
/// and LM Studio use out of the box, the same addresses Settings suggests.
/// </summary>
internal sealed record LocalEndpoints(string Ollama, string LmStudio)
{
    public const string DefaultOllama = "http://127.0.0.1:11434/v1";
    public const string DefaultLmStudio = "http://127.0.0.1:1234/v1";

    public static LocalEndpoints Default { get; } = new(DefaultOllama, DefaultLmStudio);
}

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

    // Cloud model families that "think" before answering; generous latency expected, and a real risk
    // of answering/executing the dictation instead of editing it.
    private static readonly string[] CloudReasoning =
        ["gpt-5", "gpt-5.1", "gpt-5.2", "gpt-5.4", "gpt-5.4-pro", "gpt-5.3-codex"];

    public static async Task<IReadOnlyList<BenchModel>> BuildCloudAsync(
        string? tenantId,
        string? explicitEndpoint,
        IReadOnlyList<string>? only,
        int max,
        ILogger log,
        CancellationToken ct,
        string? subscriptionId = null)
    {
        var discovery = new AzureFoundryDiscovery(new VerboseConsoleLogger<AzureFoundryDiscovery>());
        var deployments = await discovery.DiscoverAsync(
            tenantId,
            subscriptionId: subscriptionId,
            cancellationToken: ct).ConfigureAwait(false);
        log.LogInformation("Azure discovery returned {Count} text-capable deployments.", deployments.Count);

        IReadOnlyList<AzureFoundryDeployment> selected;
        IReadOnlyList<string> missing = [];
        if (only is { Count: > 0 })
        {
            // An explicit --endpoint is a deliberate instruction about WHICH resource to measure, so
            // it outranks the convenience preference below. Without this, a deployment name that also
            // exists on the preferred resource silently redirected the run there and the reported
            // latency belonged to a different region and SKU than the one requested. Discovery
            // returns account endpoints while a user may pass a project endpoint, so the resources
            // are matched on the first host label (the account name), which both forms share.
            var pinnedAccount = ExtractAccountLabel(explicitEndpoint);

            selected = only
                .Select(requested => deployments
                    .Where(d => string.Equals(d.DeploymentName, requested, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(d.ModelName, requested, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d =>
                        string.Equals(d.DeploymentName, requested, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(d =>
                        pinnedAccount is not null &&
                        string.Equals(ExtractAccountLabel(d.Endpoint), pinnedAccount, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(d =>
                        pinnedAccount is null &&
                        d.Endpoint.Contains("mtech-project-resource", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault())
                .Where(d => d is not null)
                .Cast<AzureFoundryDeployment>()
                .DistinctBy(d => $"{d.Endpoint}/{d.DeploymentName}", StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (pinnedAccount is not null)
            {
                foreach (var d in selected)
                {
                    var actual = ExtractAccountLabel(d.Endpoint);
                    if (!string.Equals(actual, pinnedAccount, StringComparison.OrdinalIgnoreCase))
                    {
                        log.LogWarning(
                            "Deployment {Deployment} was not found on the requested resource '{Pinned}'; " +
                            "measuring '{Actual}' instead. Latency will describe that resource.",
                            d.DeploymentName, pinnedAccount, actual);
                    }
                }
            }

            missing = only.Where(requested => !selected.Any(d =>
                string.Equals(d.DeploymentName, requested, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.ModelName, requested, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (missing.Count > 0 && string.IsNullOrWhiteSpace(explicitEndpoint))
            {
                log.LogWarning("Requested cloud deployments were not discovered: {Missing}.", string.Join(", ", missing));
            }
        }
        else
        {
            // De-dupe the broad roster by underlying model name, preferring the project resource that
            // hosts the widest set (it tends to have the best quota), then any classic account.
            selected = deployments
                .GroupBy(d => d.ModelName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g
                    .OrderByDescending(d =>
                        d.Endpoint.Contains("mtech-project-resource", StringComparison.OrdinalIgnoreCase))
                    .First())
                .ToList();
        }

        var models = new List<BenchModel>();
        foreach (var d in selected)
        {
            var id = only is { Count: > 0 } || string.IsNullOrWhiteSpace(d.ModelName)
                ? d.DeploymentName
                : d.ModelName;

            var note = CloudReasoning.Any(r => string.Equals(r, id, StringComparison.OrdinalIgnoreCase))
                ? "reasoning"
                : id.Contains("audio", StringComparison.OrdinalIgnoreCase) ? "audio model" : null;

            models.Add(new BenchModel(
                BenchGroup.Cloud, id, CleanupProvider.AzureFoundry, d.Endpoint, d.DeploymentName, d.ModelName, note));
        }

        if (missing.Count > 0 && !string.IsNullOrWhiteSpace(explicitEndpoint))
        {
            foreach (var deployment in missing)
            {
                models.Add(new BenchModel(
                    BenchGroup.Cloud,
                    deployment,
                    CleanupProvider.AzureFoundry,
                    explicitEndpoint.Trim(),
                    deployment,
                    null,
                    "explicit endpoint; not returned by ARM discovery"));
            }
        }

        models = models.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
        if (max > 0 && models.Count > max)
        {
            models = models.Take(max).ToList();
        }

        return models;
    }

    public static IReadOnlyList<BenchModel> BuildLocal(
        IReadOnlyList<string>? overrideAliases, int max, LocalEndpoints? endpoints = null)
    {
        var servers = endpoints ?? LocalEndpoints.Default;
        var source = overrideAliases is { Count: > 0 }
            ? overrideAliases.Select(a => (Alias: a, Note: (string?)null)).ToArray()
            : DefaultLocal;

        var models = source
            .Select(x => ParseLocal(x.Alias, x.Note, servers))
            .ToList();

        if (max > 0 && models.Count > max)
        {
            models = models.Take(max).ToList();
        }

        return models;
    }

    /// <summary>
    /// Reads one local roster entry. A bare name is a Foundry Local alias, as it always was, so an
    /// existing <c>results.json</c> keeps its keys. A runtime prefix selects a local server reached
    /// through the OpenAI-compatible provider, exactly as a user configures Ollama or LM Studio in
    /// Settings: <c>ollama:qwen3:1.7b</c>, <c>lmstudio:google/gemma-3-1b</c>, or
    /// <c>openai:http://host:port/v1|model</c> for any other server. The prefix stays in the id, so the
    /// same model on two runtimes is two rows rather than one overwriting the other.
    /// </summary>
    internal static BenchModel ParseLocal(string spec, string? note, LocalEndpoints endpoints)
    {
        var trimmed = spec.Trim();
        if (TryStripPrefix(trimmed, "foundry:", out var alias))
        {
            return new BenchModel(BenchGroup.Local, trimmed, CleanupProvider.FoundryLocal, null, alias, null, note);
        }

        if (TryStripPrefix(trimmed, "ollama:", out var ollamaModel))
        {
            return new BenchModel(
                BenchGroup.Local, trimmed, CleanupProvider.OpenAiCompatible, endpoints.Ollama, ollamaModel, ollamaModel, note);
        }

        if (TryStripPrefix(trimmed, "lmstudio:", out var lmStudioModel))
        {
            return new BenchModel(
                BenchGroup.Local, trimmed, CleanupProvider.OpenAiCompatible, endpoints.LmStudio, lmStudioModel, lmStudioModel, note);
        }

        if (TryStripPrefix(trimmed, "openai:", out var rest))
        {
            var bar = rest.LastIndexOf('|');
            if (bar <= 0 || bar == rest.Length - 1)
            {
                throw new ArgumentException(
                    $"'{trimmed}' needs the form openai:<endpoint>|<model>, for example openai:http://127.0.0.1:8080/v1|my-model.");
            }

            var endpoint = rest[..bar].Trim();
            var model = rest[(bar + 1)..].Trim();
            return new BenchModel(BenchGroup.Local, trimmed, CleanupProvider.OpenAiCompatible, endpoint, model, model, note);
        }

        return new BenchModel(BenchGroup.Local, trimmed, CleanupProvider.FoundryLocal, null, trimmed, null, note);
    }

    private static bool TryStripPrefix(string value, string prefix, out string rest)
    {
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && value.Length > prefix.Length)
        {
            rest = value[prefix.Length..].Trim();
            return rest.Length > 0;
        }

        rest = string.Empty;
        return false;
    }

    // First host label of an endpoint, which is the Cognitive Services account name and is shared by
    // both the account form (https://NAME.cognitiveservices.azure.com/) and the Foundry project form
    // (https://NAME.services.ai.azure.com/api/projects/x). Returns null when there is nothing to pin.
    private static string? ExtractAccountLabel(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host;
        var dot = host.IndexOf('.');
        return dot > 0 ? host[..dot] : host;
    }
}
