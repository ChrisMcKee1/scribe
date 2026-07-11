using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using Scribe.Core.Cleanup;
using System.ClientModel.Primitives;

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
        bool disableRetries)
    {
        var credentialOptions = new DefaultAzureCredentialOptions
        {
            ExcludeInteractiveBrowserCredential = true,
        };
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            credentialOptions.TenantId = tenantId.Trim();
        }

        var endpointUri = new Uri(endpoint);
        var accountHost = endpointUri.AbsolutePath.Contains("/api/projects/", StringComparison.OrdinalIgnoreCase)
            ? new Uri($"{endpointUri.Scheme}://{endpointUri.Authority}/")
            : endpointUri;

        var clientOptions = new AzureOpenAIClientOptions
        {
            NetworkTimeout = networkTimeout + TimeSpan.FromSeconds(5),
        };
        if (disableRetries)
        {
            clientOptions.RetryPolicy = new ClientRetryPolicy(maxRetries: 0);
        }

        var azureClient = new AzureOpenAIClient(
            accountHost,
            new DefaultAzureCredential(credentialOptions),
            clientOptions);

        _client = azureClient.GetResponsesClient();
        _deployment = deployment;
        _instructions = instructions;
        _reasoningEffort = reasoningEffort;
        _maxOutputTokens = maxOutputTokens;
    }

    public async Task<(string Text, BenchTokenUsage? Usage)> CleanAsync(
        string transcript,
        CancellationToken cancellationToken)
    {
        var options = new CreateResponseOptions
        {
            Model = _deployment,
            Instructions = _instructions,
            MaxOutputTokenCount = _maxOutputTokens,
        };
        options.InputItems.Add(ResponseItem.CreateUserMessageItem(TextCleanupService.BuildUserMessage(transcript)));

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
        var usage = result.Usage is null
            ? null
            : new BenchTokenUsage(
                result.Usage.InputTokenCount,
                result.Usage.OutputTokenCount,
                result.Usage.OutputTokenDetails?.ReasoningTokenCount,
                result.Usage.TotalTokenCount);

        return (result.GetOutputText(), usage);
    }
}

#pragma warning restore OPENAI001