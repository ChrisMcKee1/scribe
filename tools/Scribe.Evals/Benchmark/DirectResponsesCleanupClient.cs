using Azure.Identity;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using Scribe.Core.Cleanup;

#pragma warning disable OPENAI001

namespace Scribe.Evals.Benchmark;

/// <summary>Diagnostic client that bypasses Agent Framework while preserving the benchmark request.</summary>
internal sealed class DirectResponsesCleanupClient
{
    private readonly ResponsesClient _client;
    private readonly string _deployment;
    private readonly string _instructions;
    private readonly ReasoningEffort? _reasoningEffort;
    private readonly int? _maxOutputTokens;

    public DirectResponsesCleanupClient(
        string endpoint,
        string deployment,
        string? tenantId,
        string instructions,
        ReasoningEffort? reasoningEffort,
        int? maxOutputTokens,
        TimeSpan networkTimeout,
        bool disableRetries,
        string? subscriptionId = null)
    {
        _client = AzureOpenAIResponsesClientFactory.CreateWithTokenCredential(
            new Uri(endpoint),
            string.IsNullOrWhiteSpace(subscriptionId)
                ? BuildDefaultCredential(tenantId)
                : AzureCredentialFactory.Create(AzureCredentialRequest.Cli(tenantId, subscriptionId)),
            networkTimeout + TimeSpan.FromSeconds(5),
            disableRetries);
        _deployment = deployment;
        _instructions = instructions;
        _reasoningEffort = reasoningEffort;
        _maxOutputTokens = maxOutputTokens;
    }

    private static Azure.Core.TokenCredential BuildDefaultCredential(string? tenantId)
    {
        var credentialOptions = new DefaultAzureCredentialOptions
        {
            // The eval harness is a local developer tool. Keep service-principal environment support,
            // but skip deployed-host credentials so an unavailable IMDS endpoint cannot stop the chain
            // before Azure CLI, Visual Studio, or the other developer credentials are tried.
            ExcludeWorkloadIdentityCredential = true,
            ExcludeManagedIdentityCredential = true,
            ExcludeInteractiveBrowserCredential = true,
        };
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            credentialOptions.TenantId = tenantId.Trim();
        }

        return new DefaultAzureCredential(credentialOptions);
    }

    public Task<(string Text, BenchTokenUsage? Usage)> CleanAsync(
        string transcript,
        CancellationToken cancellationToken) =>
        SendAsync(TextCleanupService.BuildUserMessage(transcript), cancellationToken);

    private async Task<(string Text, BenchTokenUsage? Usage)> SendAsync(
        string userMessage,
        CancellationToken cancellationToken)
    {
        var options = new CreateResponseOptions
        {
            Model = _deployment,
            Instructions = _instructions,
            MaxOutputTokenCount = _maxOutputTokens,
            StoredOutputEnabled = false,
        };

        options.InputItems.Add(ResponseItem.CreateUserMessageItem(userMessage));

        if (_reasoningEffort is { } reasoningEffort)
        {
            options.ReasoningOptions = new ResponseReasoningOptions
            {
                ReasoningEffortLevel = reasoningEffort switch
                {
                    ReasoningEffort.None => ResponseReasoningEffortLevel.None,
                    ReasoningEffort.Low => ResponseReasoningEffortLevel.Low,
                    ReasoningEffort.Medium => ResponseReasoningEffortLevel.Medium,
                    ReasoningEffort.High => ResponseReasoningEffortLevel.High,
                    ReasoningEffort.ExtraHigh => new ResponseReasoningEffortLevel("xhigh"),
                    _ => null,
                },
            };
        }

        var response = await _client.CreateResponseAsync(options, cancellationToken).ConfigureAwait(false);
        var result = response.Value;
        var usage = result.Usage is null ? null : ToUsage(result.Usage);

        return (result.GetOutputText(), usage);
    }

    /// <summary>The benchmark's usage record for one Responses answer: typed counts, and the cache-write count when present.</summary>
    internal static BenchTokenUsage ToUsage(ResponseTokenUsage usage) => new(
        usage.InputTokenCount,
        usage.OutputTokenCount,
        usage.OutputTokenDetails?.ReasoningTokenCount,
        usage.TotalTokenCount,
        usage.InputTokenDetails?.CachedTokenCount,
        CacheWriteTokens(usage.InputTokenDetails));

    // OpenAI 2.12.0 has no typed cache_write_tokens; the service's field arrives among the details' unknown properties,
    // which only the (evaluation-only) JsonPatch exposes. Tools only; a failed read leaves the count null.
#pragma warning disable SCME0001
    private static long? CacheWriteTokens(ResponseInputTokenUsageDetails? details) =>
        details is not null && details.Patch.TryGetValue("$.cache_write_tokens"u8, out long writes) ? writes : null;
#pragma warning restore SCME0001
}

#pragma warning restore OPENAI001