using Azure.Core;
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
    private readonly JsonSchemaFormat? _responseSchema;

    public DirectResponsesCleanupClient(
        string endpoint,
        string deployment,
        string? tenantId,
        string instructions,
        ReasoningEffort? reasoningEffort,
        int? maxOutputTokens,
        TimeSpan networkTimeout,
        bool disableRetries,
        TokenCredential? credential = null,
        JsonSchemaFormat? responseSchema = null)
    {
        // A caller that already knows which cached Azure CLI account owns the deployment can hand one
        // in. tools/Scribe.StyleEval does: on a machine signed in to several tenants, only
        // `az account get-access-token --subscription <id>` mints an ai.azure.com token, and
        // AzureCliCredentialOptions.Subscription is the only way to ask for that form.
        var resolved = credential ?? BuildDefaultCredential(tenantId);

        _client = AzureOpenAIResponsesClientFactory.CreateWithTokenCredential(
            new Uri(endpoint),
            resolved,
            networkTimeout + TimeSpan.FromSeconds(5),
            disableRetries);
        _deployment = deployment;
        _instructions = instructions;
        _reasoningEffort = reasoningEffort;
        _maxOutputTokens = maxOutputTokens;
        _responseSchema = responseSchema;
    }

    private static TokenCredential BuildDefaultCredential(string? tenantId)
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

    /// <summary>
    /// Sends an already-composed user message. Used by tools/Scribe.StyleEval, whose user message is
    /// built by <c>TextActionPrompt.BuildUserMessage</c> rather than by the cleanup service, so both
    /// harnesses share one Azure Responses path instead of standing up a second one.
    /// </summary>
    public async Task<(string Text, BenchTokenUsage? Usage)> SendAsync(
        string userMessage,
        CancellationToken cancellationToken)
    {
        var options = new CreateResponseOptions
        {
            Model = _deployment,
            Instructions = _instructions,
            MaxOutputTokenCount = _maxOutputTokens,
        };

        // Structured output when the caller asked for it. Nothing else changes: with no schema the
        // request is byte for byte the one the benchmark has always sent.
        if (_responseSchema is { } schema)
        {
            options.TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                    schema.Name,
                    BinaryData.FromString(schema.SchemaJson),
                    schema.Description,
                    schema.Strict),
            };
        }

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