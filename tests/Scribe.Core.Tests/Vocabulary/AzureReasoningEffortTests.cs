using System.Net;
using System.Text.Json;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// Microsoft Foundry cleanup asks for the least reasoning effort a deployment accepts: none, then low for a model that
/// refuses none, then nothing for a model that takes no reasoning effort. Measured for 0.5.2, thinking bought cleanup no
/// quality on gpt-6-sol or gpt-6.1-sol and cost a third of the time. Through the production service and transport, over
/// the canary network; nothing leaves the process.
/// </summary>
public sealed class AzureReasoningEffortTests
{
    private const string Transcript = "the canary transcript is a light edit";

    [Fact]
    public async Task A_deployment_is_asked_not_to_reason_on_the_probe_and_every_dictation()
    {
        await using var harness = new VocabularyCleanupHarness(source: null);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());

        await harness.Service.CleanAsync(Transcript, CancellationToken.None);

        var sent = harness.Network.Sent;
        Assert.Contains(sent, request => request.IsProbe);
        Assert.Contains(sent, request => request.TranscriptOf() == Transcript);
        Assert.All(sent, request => Assert.Equal("none", EffortOf(request)));
    }

    [Fact]
    public async Task A_deployment_that_refuses_none_is_asked_for_low()
    {
        // gpt-6.1-sol's own answer, measured on 2026-09-29.
        var network = new CanaryNetwork
        {
            Respond = (request, _) => Task.FromResult(EffortOf(request) == "none"
                ? Refusal("Unsupported value: 'none' is not supported with the 'gpt-6.1-sol-2026-09-29' model. Supported values are: 'low', 'medium', 'high', 'xhigh', and 'max'.", "unsupported_value")
                : CanaryNetwork.Echo(request)),
        };
        await using var harness = new VocabularyCleanupHarness(source: null, network);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());

        var result = await harness.Service.CleanAsync(Transcript, CancellationToken.None);

        Assert.NotEqual(CleanupOutcome.Failed, result.Outcome);
        var dictation = Assert.Single(network.Sent, request => request.TranscriptOf() == Transcript);
        Assert.Equal("low", EffortOf(dictation));
        Assert.Equal("none", EffortOf(network.Sent[0]));
    }

    [Fact]
    public async Task A_model_that_takes_no_reasoning_effort_is_sent_none_and_keeps_its_own_behavior()
    {
        var network = new CanaryNetwork
        {
            Respond = (request, _) => Task.FromResult(EffortOf(request) is not null
                ? Refusal("Unsupported parameter: 'reasoning.effort' is not supported with this model.", "unsupported_parameter")
                : CanaryNetwork.Echo(request)),
        };
        await using var harness = new VocabularyCleanupHarness(source: null, network);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());

        var result = await harness.Service.CleanAsync(Transcript, CancellationToken.None);

        Assert.NotEqual(CleanupOutcome.Failed, result.Outcome);
        var dictation = Assert.Single(network.Sent, request => request.TranscriptOf() == Transcript);
        Assert.Null(EffortOf(dictation));
    }

    [Fact]
    public async Task Another_AI_service_is_never_asked_for_a_reasoning_effort()
    {
        await using var harness = new VocabularyCleanupHarness(source: null);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom());

        await harness.Service.CleanAsync(Transcript, CancellationToken.None);

        Assert.NotEmpty(harness.Network.Sent);
        Assert.All(harness.Network.Sent, request => Assert.Null(EffortOf(request)));
    }

    [Theory]
    [InlineData("Unsupported value: 'none' is not supported with the 'x' model. Supported values are: 'low'.", true)]
    [InlineData("Unsupported parameter: 'reasoning.effort' is not supported with this model.", true)]
    [InlineData("The requested operation is unsupported.", false)]
    public void Only_a_400_that_names_the_reasoning_effort_steps_it(string message, bool expected)
    {
        var rejection = new System.ClientModel.ClientResultException(message, new StatusOnly(400));
        Assert.Equal(expected, TextCleanupService.IsReasoningEffortRejection(rejection));
        Assert.False(TextCleanupService.IsReasoningEffortRejection(new System.ClientModel.ClientResultException(message, new StatusOnly(429))));
    }

    // The reasoning effort a request asked for, on either surface: Responses' reasoning.effort, Chat Completions'
    // reasoning_effort. Null when it asked for none.
    private static string? EffortOf(SentRequest request)
    {
        using var body = JsonDocument.Parse(request.Body);
        if (body.RootElement.TryGetProperty("reasoning", out var reasoning) &&
            reasoning.ValueKind == JsonValueKind.Object &&
            reasoning.TryGetProperty("effort", out var effort))
        {
            return effort.GetString();
        }

        return body.RootElement.TryGetProperty("reasoning_effort", out var chat) ? chat.GetString() : null;
    }

    private static HttpResponseMessage Refusal(string message, string code) => ScriptedHttpHandler.Json(
        HttpStatusCode.BadRequest,
        JsonSerializer.Serialize(new { error = new { message, type = "invalid_request_error", param = "reasoning.effort", code } }));

    private sealed class StatusOnly(int status) : System.ClientModel.Primitives.PipelineResponse
    {
        public override int Status => status;

        public override string ReasonPhrase => string.Empty;

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content => BinaryData.FromString(string.Empty);

        protected override System.ClientModel.Primitives.PipelineResponseHeaders HeadersCore => throw new NotSupportedException();

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => new(Content);

        public override void Dispose()
        {
        }
    }
}
