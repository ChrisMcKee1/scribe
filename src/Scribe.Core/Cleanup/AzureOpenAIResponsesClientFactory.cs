using Azure.Core;
using OpenAI;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;

#pragma warning disable MAAI001, OPENAI001

namespace Scribe.Core.Cleanup;

internal static class AzureOpenAIResponsesClientFactory
{
    // Unified Azure OpenAI v1 endpoints use the Azure AI audience. The legacy deployments API
    // used the Cognitive Services audience.
    internal const string AzureAIScope = "https://ai.azure.com/.default";

    public static ResponsesClient CreateWithApiKey(
        Uri resourceEndpoint,
        string apiKey,
        TimeSpan? networkTimeout = null,
        bool disableRetries = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var client = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            CreateOptions(resourceEndpoint, networkTimeout, disableRetries));
        return client.GetResponsesClient();
    }

    /*
     * The same configured client, handed back whole so a caller can ask it for a Chat Completions
     * client instead of a Responses one.
     *
     * Not every Foundry deployment serves Responses: MAI-Thinking-1 answers it with HTTP 400
     * "operation unsupported" and serves Chat Completions on the same /openai/v1 base. Both surfaces
     * therefore have to be reachable, and they must be reachable through ONE configured client so
     * the endpoint shaping, the auth policy, the timeout and the retry policy cannot drift apart
     * between them.
     */
    public static OpenAIClient CreateClientWithApiKey(
        Uri resourceEndpoint,
        string apiKey,
        TimeSpan? networkTimeout = null,
        bool disableRetries = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        return new OpenAIClient(
            new ApiKeyCredential(apiKey),
            CreateOptions(resourceEndpoint, networkTimeout, disableRetries));
    }

    /// <inheritdoc cref="CreateClientWithApiKey"/>
    public static OpenAIClient CreateClientWithTokenCredential(
        Uri resourceEndpoint,
        TokenCredential credential,
        TimeSpan? networkTimeout = null,
        bool disableRetries = false)
    {
        ArgumentNullException.ThrowIfNull(credential);

        return new OpenAIClient(
            new BearerTokenPolicy(credential, AzureAIScope),
            CreateOptions(resourceEndpoint, networkTimeout, disableRetries));
    }

    public static ResponsesClient CreateWithTokenCredential(
        Uri resourceEndpoint,
        TokenCredential credential,
        TimeSpan? networkTimeout = null,
        bool disableRetries = false)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var client = new OpenAIClient(
            new BearerTokenPolicy(credential, AzureAIScope),
            CreateOptions(resourceEndpoint, networkTimeout, disableRetries));
        return client.GetResponsesClient();
    }

    internal static Uri GetV1Endpoint(Uri resourceEndpoint)
    {
        ArgumentNullException.ThrowIfNull(resourceEndpoint);
        if (!resourceEndpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("Azure OpenAI endpoint must be absolute.", nameof(resourceEndpoint));
        }

        return new Uri($"{resourceEndpoint.GetLeftPart(UriPartial.Authority).TrimEnd('/')}/openai/v1/");
    }

    private static OpenAIClientOptions CreateOptions(
        Uri resourceEndpoint,
        TimeSpan? networkTimeout,
        bool disableRetries)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = GetV1Endpoint(resourceEndpoint),
        };

        if (networkTimeout is { } timeout)
        {
            options.NetworkTimeout = timeout;
        }

        if (disableRetries)
        {
            options.RetryPolicy = new ClientRetryPolicy(maxRetries: 0);
        }

        return options;
    }
}

#pragma warning restore MAAI001, OPENAI001
