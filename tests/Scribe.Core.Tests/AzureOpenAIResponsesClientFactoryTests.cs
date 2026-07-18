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

    [Fact]
    public void Current_openai_client_constructs_an_azure_responses_client()
    {
        var client = AzureOpenAIResponsesClientFactory.CreateWithApiKey(
            new Uri("https://example.openai.azure.com/"),
            "test-key");

        Assert.Equal(new Uri("https://example.openai.azure.com/openai/v1/"), client.Endpoint);
    }

    [Fact]
    public void Token_credential_constructs_an_azure_responses_client()
    {
        var client = AzureOpenAIResponsesClientFactory.CreateWithTokenCredential(
            new Uri("https://example.openai.azure.com/"),
            new StubTokenCredential());

        Assert.Equal(new Uri("https://example.openai.azure.com/openai/v1/"), client.Endpoint);
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
