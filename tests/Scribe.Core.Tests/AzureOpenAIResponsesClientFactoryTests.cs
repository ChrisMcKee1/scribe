using Azure.Core;
using Scribe.Core.Cleanup;

#pragma warning disable OPENAI001

namespace Scribe.Core.Tests;

public sealed class AzureOpenAIResponsesClientFactoryTests
{
    [Fact]
    public void Unified_v1_endpoint_uses_the_azure_ai_token_scope()
    {
        Assert.Equal(
            "https://ai.azure.com/.default",
            AzureOpenAIResponsesClientFactory.AzureAIScope);
    }

    [Theory]
    [InlineData("https://example.openai.azure.com/", "https://example.openai.azure.com/openai/v1/")]
    [InlineData("https://example.openai.azure.com/openai/v1/", "https://example.openai.azure.com/openai/v1/")]
    [InlineData(
        "https://example.services.ai.azure.com/api/projects/sample",
        "https://example.services.ai.azure.com/openai/v1/")]
    public void V1_endpoint_uses_the_resource_authority(string endpoint, string expected)
    {
        Assert.Equal(new Uri(expected), AzureOpenAIResponsesClientFactory.GetV1Endpoint(new Uri(endpoint)));
    }

    [Theory]
    [InlineData("https://example.openai.azure.com/", "https://example.openai.azure.com/openai/v1/")]
    [InlineData("https://example.services.ai.azure.com/api/projects/sample", "https://example.services.ai.azure.com/openai/v1/")]
    public void Api_key_client_uses_account_inference_for_either_endpoint_shape(string endpoint, string expected)
    {
        var client = AzureOpenAIResponsesClientFactory.CreateWithApiKey(
            new Uri(endpoint),
            "test-key");

        Assert.Equal(new Uri(expected), client.Endpoint);
    }

    [Theory]
    [InlineData("https://example.openai.azure.com/", "https://example.openai.azure.com/openai/v1/")]
    [InlineData("https://example.services.ai.azure.com/api/projects/sample", "https://example.services.ai.azure.com/openai/v1/")]
    public void Token_client_uses_account_inference_for_either_endpoint_shape(string endpoint, string expected)
    {
        var client = AzureOpenAIResponsesClientFactory.CreateWithTokenCredential(
            new Uri(endpoint),
            new StubTokenCredential());

        Assert.Equal(new Uri(expected), client.Endpoint);
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}

#pragma warning restore OPENAI001
