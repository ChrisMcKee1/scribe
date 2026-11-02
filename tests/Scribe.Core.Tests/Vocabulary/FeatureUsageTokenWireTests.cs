using System.ClientModel;
using System.Net;
using OpenAI;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;
using Scribe.Core.Vocabulary;
using static Scribe.Core.Tests.Vocabulary.TestVocabularies;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// Agent Framework's feature-usage token never reaches Microsoft Foundry, read from the User-Agent on the wire of the
/// production transport: the cleanup service the app builds, sending to a canary network at an Azure OpenAI address, where
/// the framework adds its tokens. The contract test below pins that the packages add the token on their own there, which is
/// what makes the wire tests measure <see cref="FeatureUsageTokenHandler"/> rather than a framework that stopped sending it.
/// </summary>
public sealed class FeatureUsageTokenWireTests
{
    // An address the framework treats as first party (OpenAIUserAgentPolicies: openai.azure.com, cognitiveservices.azure.com,
    // services.ai.azure.com, over HTTPS). Nothing resolves it: every request ends at the canary network.
    private const string AzureAddress = "https://scribe-wire.openai.azure.com/";
    private const string FrameworkProduct = "agent-framework-dotnet/";
    private const string FeatureToken = "(feat=";
    private const string Dictated = "please ship the harbour lanterns today";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static readonly LibraryVocabulary Permitted = Of(1, new Library("harbourline-team", H1, true, Entry("kay storm", "Quaystorm")));

    [Fact]
    public async Task On_the_responses_surface_no_request_carries_the_feature_token()
    {
        var source = new TestVocabularySource(Permitted);
        await using var harness = new VocabularyCleanupHarness(source);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure() with { AzureEndpoint = AzureAddress });

        var cleaned = await harness.Service.Admit(GenerationOf(Permitted).Cleanup).CleanAsync(Dictated).WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Unchanged, cleaned.Outcome);
        var sent = harness.Network.Sent;
        Assert.Equal(2, sent.Count);
        Assert.All(sent, request =>
        {
            Assert.EndsWith("/openai/v1/responses", request.Path, StringComparison.Ordinal);
            AssertFrameworkNamedWithoutFeatures(request);
        });
    }

    [Fact]
    public async Task On_the_chat_completions_fallback_no_request_carries_the_feature_token()
    {
        // A deployment that rejects the Responses API, which buys the Chat Completions surface and its own agent.
        var source = new TestVocabularySource(Permitted);
        await using var harness = new VocabularyCleanupHarness(source);
        harness.Network.Respond = (request, _) => Task.FromResult(
            request.Path.EndsWith("/responses", StringComparison.Ordinal)
                ? CanaryNetwork.Json(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"The requested operation is unsupported.\"}}")
                : CanaryNetwork.Echo(request));
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure() with { AzureEndpoint = AzureAddress });

        var cleaned = await harness.Service.Admit(GenerationOf(Permitted).Cleanup).CleanAsync(Dictated).WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Unchanged, cleaned.Outcome);
        var sent = harness.Network.Sent;
        Assert.Collection(
            sent,
            responses => Assert.EndsWith("/openai/v1/responses", responses.Path, StringComparison.Ordinal),
            probe => Assert.EndsWith("/openai/v1/chat/completions", probe.Path, StringComparison.Ordinal),
            dictation => Assert.EndsWith("/openai/v1/chat/completions", dictation.Path, StringComparison.Ordinal));
        Assert.All(sent, AssertFrameworkNamedWithoutFeatures);
    }

    [Fact]
    public async Task The_packages_add_the_feature_token_at_an_azure_address_and_neither_token_elsewhere()
    {
        // The Responses agent exactly as the service builds it, over a transport that is not Scribe's.
        var azure = await SendOnceAsync(AzureAddress + "openai/v1/");
        Assert.True(
            Environment.GetEnvironmentVariable("AGENT_FRAMEWORK_FEATURE_MASK_DISABLED") is null,
            "AGENT_FRAMEWORK_FEATURE_MASK_DISABLED is set for this test process, so the framework sends no feature token and the wire tests prove nothing.");
        Assert.Contains(FrameworkProduct, azure, StringComparison.Ordinal);
        Assert.Contains(FeatureToken + "v1.", azure, StringComparison.Ordinal);

        var elsewhere = await SendOnceAsync("https://wire-test.example.invalid/openai/v1/");
        Assert.DoesNotContain(FrameworkProduct, elsewhere, StringComparison.Ordinal);
        Assert.DoesNotContain(FeatureToken, elsewhere, StringComparison.Ordinal);

        // What every request's User-Agent names, wherever it goes (CleanupDisclosure.AiClientRequestMetadata): the OpenAI
        // client with the .NET and Windows versions, and Microsoft.Extensions.AI.
        Assert.All(new[] { azure, elsewhere }, userAgent =>
        {
            Assert.StartsWith("OpenAI/", userAgent, StringComparison.Ordinal);
            Assert.Contains("(.NET ", userAgent, StringComparison.Ordinal);
            Assert.Contains("Windows", userAgent, StringComparison.Ordinal);
            Assert.Contains("MEAI/", userAgent, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(
        "OpenAI/2.14.0 (.NET 10.0.7; Microsoft Windows 10.0.26300) agent-framework-dotnet/1.24.0 (feat=v1.40000000000000)",
        "OpenAI/2.14.0 (.NET 10.0.7; Microsoft Windows 10.0.26300) agent-framework-dotnet/1.24.0")]
    [InlineData(
        "OpenAI/2.14.0 (feat=v1.4000000000000000000000000000001) agent-framework-dotnet/1.24.0",
        "OpenAI/2.14.0 agent-framework-dotnet/1.24.0")]
    [InlineData("OpenAI/2.14.0 agent-framework-dotnet/1.24.0", "OpenAI/2.14.0 agent-framework-dotnet/1.24.0")]
    [InlineData("OpenAI/2.14.0 (feature=wanted)", "OpenAI/2.14.0 (feature=wanted)")]
    [InlineData("(feat=v1.1)", null)]
    public void The_handler_removes_the_token_and_keeps_the_rest_of_the_user_agent(string sent, string? kept)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AzureAddress);
        Assert.True(request.Headers.TryAddWithoutValidation("User-Agent", sent));

        FeatureUsageTokenHandler.RemoveFeatureToken(request);

        Assert.Equal(kept, request.Headers.NonValidated.TryGetValues("User-Agent", out var values) ? string.Join(' ', values) : null);
    }

    [Fact]
    public void The_handler_leaves_a_request_without_a_user_agent_alone()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AzureAddress);

        FeatureUsageTokenHandler.RemoveFeatureToken(request);

        Assert.False(request.Headers.NonValidated.Contains("User-Agent"));
    }

    private static void AssertFrameworkNamedWithoutFeatures(SentRequest request)
    {
        Assert.NotNull(request.UserAgent);

        // The framework's name and version prove its policies ran on this request, so the token was added before the handler.
        Assert.Contains(FrameworkProduct, request.UserAgent, StringComparison.Ordinal);
        Assert.DoesNotContain(FeatureToken, request.UserAgent, StringComparison.Ordinal);
    }

    private static async Task<string> SendOnceAsync(string endpoint)
    {
        var userAgents = new List<string>();
        var handler = new ScriptedHttpHandler((request, _) =>
        {
            lock (userAgents)
            {
                userAgents.Add(request.Headers.NonValidated.TryGetValues("User-Agent", out var values) ? string.Join(' ', values) : string.Empty);
            }

            return Task.FromResult(CanaryNetwork.Responses("Hello."));
        });
        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
        ScriptedHttpHandler.Install(handler)(options);
        var client = new OpenAIClient(new ApiKeyCredential("not-a-real-key"), options);
#pragma warning disable OPENAI001
        var agent = TextCleanupService.CreateAzureResponsesAgent(client.GetResponsesClient(), "gpt-6-astra", "Clean up the text.");
#pragma warning restore OPENAI001

        var answer = await agent.RunAsync(TextCleanupService.BuildUserMessage("hello"));

        Assert.Equal("Hello.", answer.Text);
        return Assert.Single(userAgents);
    }

    private static VocabularyGeneration GenerationOf(LibraryVocabulary libraries) =>
        new(libraries.Generation, [], libraries, CompiledDictionaryRules.Empty);
}
