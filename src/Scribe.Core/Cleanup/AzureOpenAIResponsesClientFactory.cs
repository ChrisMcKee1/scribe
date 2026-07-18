using Azure.Core;
using OpenAI;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;

#pragma warning disable OPENAI001

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

#pragma warning restore OPENAI001
