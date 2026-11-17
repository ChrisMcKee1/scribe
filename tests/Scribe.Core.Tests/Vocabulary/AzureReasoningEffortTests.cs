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

    /*
     * What a readiness check learns about a deployment stays in the check until the initialization that owns the
     * generation publishes it. Test connection used to write the one shared slot as its probe stepped, so testing another
     * deployment, or the same one, moved the effort the serving deployment's next dictation asked for.
     */

    [Theory]
    [InlineData("takes low", true)]
    [InlineData("takes no effort", true)]
    [InlineData("fails after none", false)]
    public async Task A_test_of_another_deployment_cannot_change_the_effort_the_serving_one_negotiated(
        string candidate, bool connects)
    {
        var deployments = new SwitchableDeployment();
        await using var harness = new VocabularyCleanupHarness(source: null, deployments.Network);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());
        Assert.Equal("low", EffortOf(deployments.Network.Sent.Last()));

        deployments.Answers = Candidate(candidate);
        var tested = await harness.Service.TestAsync(Deployment(OtherDeployment)).WaitAsync(Bound);
        deployments.Answers = TakesLowOnly;

        Assert.Equal(connects ? CleanupTestOutcome.Connected : CleanupTestOutcome.Failed, tested.Outcome);
        Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
        var result = await harness.Service.CleanAsync(Transcript, CancellationToken.None);

        Assert.NotEqual(CleanupOutcome.Failed, result.Outcome);
        var dictation = deployments.Network.Sent.Last();
        Assert.Equal(Transcript, dictation.TranscriptOf());
        Assert.Equal("low", EffortOf(dictation));
    }

    [Fact]
    public async Task A_test_of_the_serving_deployment_starts_from_the_effort_it_negotiated()
    {
        var deployments = new SwitchableDeployment();
        await using var harness = new VocabularyCleanupHarness(source: null, deployments.Network);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());
        var before = deployments.Network.Sent.Count;

        var tested = await harness.Service.TestAsync(VocabularyCleanupHarness.Azure()).WaitAsync(Bound);

        // As saving the same settings would, the test asks for the effort the deployment settled on, not for none first.
        Assert.Equal(CleanupTestOutcome.Connected, tested.Outcome);
        Assert.Equal("low", EffortOf(Assert.Single(deployments.Network.Sent.Skip(before))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_test_of_the_serving_deployment_never_rewrites_its_effort(bool connects)
    {
        var deployments = new SwitchableDeployment();
        await using var harness = new VocabularyCleanupHarness(source: null, deployments.Network);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());
        var network = deployments.Network;

        // The deployment refuses low as well while it is tested: the test settles on no effort in its own copy, whether it
        // then connects or fails, and the serving configuration, which the test never publishes to, keeps asking for low.
        deployments.Answers = connects ? RefusesLowToo : RefusesLowThenFails;
        var before = network.Sent.Count;
        var tested = await harness.Service.TestAsync(VocabularyCleanupHarness.Azure()).WaitAsync(Bound);
        deployments.Answers = TakesLowOnly;

        Assert.Equal(connects ? CleanupTestOutcome.Connected : CleanupTestOutcome.Failed, tested.Outcome);
        Assert.Equal("low, -", Efforts(network.Sent.Skip(before)));
        var result = await harness.Service.CleanAsync(Transcript, CancellationToken.None);
        Assert.NotEqual(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal("low", EffortOf(network.Sent.Last()));
    }

    [Fact]
    public async Task A_test_that_falls_back_to_Chat_Completions_carries_its_effort_across_and_leaves_the_serving_one_alone()
    {
        var deployments = new SwitchableDeployment();
        await using var harness = new VocabularyCleanupHarness(source: null, deployments.Network);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());
        var before = deployments.Network.Sent.Count;

        deployments.Answers = ChatOnly;
        var tested = await harness.Service.TestAsync(Deployment(OtherDeployment)).WaitAsync(Bound);
        deployments.Answers = TakesLowOnly;

        // The Chat Completions check asks for the effort the Responses check settled on, so it does not meet none again.
        Assert.Equal(CleanupTestOutcome.Connected, tested.Outcome);
        Assert.Equal("responses:none, responses:low, chat:low", Describe(deployments.Network.Sent.Skip(before)));
        var result = await harness.Service.CleanAsync(Transcript, CancellationToken.None);
        Assert.NotEqual(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal("responses:low", Describe([deployments.Network.Sent.Last()]));
    }

    [Fact]
    public async Task An_initialization_that_falls_back_to_Chat_Completions_carries_its_effort_across_and_publishes_it()
    {
        var deployments = new SwitchableDeployment { Answers = ChatOnly };
        await using var harness = new VocabularyCleanupHarness(source: null, deployments.Network);

        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());
        Assert.Equal("responses:none, responses:low, chat:low", Describe(deployments.Network.Sent));

        var result = await harness.Service.CleanAsync(Transcript, CancellationToken.None);

        Assert.NotEqual(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal("chat:low", Describe([deployments.Network.Sent.Last()]));
    }

    [Fact]
    public async Task An_initialization_that_fails_publishes_nothing_it_learned()
    {
        var deployments = new SwitchableDeployment();
        await using var harness = new VocabularyCleanupHarness(source: null, deployments.Network);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());

        deployments.Answers = FailsAfterNone;
        harness.Service.Configure(Deployment(OtherDeployment));
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);

        // Settings go back to the first deployment, which goes on from the effort it negotiated and not from the one the
        // failed initialization tried on the way.
        deployments.Answers = TakesLowOnly;
        var before = deployments.Network.Sent.Count;
        harness.Service.Configure(VocabularyCleanupHarness.Azure());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal("low", EffortOf(Assert.Single(deployments.Network.Sent.Skip(before))));
    }

    [Fact]
    public async Task A_superseded_initialization_publishes_nothing_it_learned()
    {
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = false;
        var deployments = new SwitchableDeployment();
        deployments.Network.Respond = async (request, cancellation) =>
        {
            // The other deployment's second request waits here until the settings supersede its initialization.
            if (holding && EffortOf(request) == "low")
            {
                parked.TrySetResult();
                await Task.Delay(Bound, cancellation);
            }

            return deployments.Answers(request);
        };
        await using var harness = new VocabularyCleanupHarness(source: null, deployments.Network);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());

        holding = true;
        harness.Service.Configure(Deployment(OtherDeployment));
        await parked.Task.WaitAsync(Bound);
        holding = false;
        var before = deployments.Network.Sent.Count;
        harness.Service.Configure(VocabularyCleanupHarness.Azure());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal("low", EffortOf(Assert.Single(deployments.Network.Sent.Skip(before))));
    }

    private const string OtherDeployment = "other-deployment";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static CleanupOptions Deployment(string name) => VocabularyCleanupHarness.Azure() with { AzureDeployment = name };

    // The deployment on the other end of the network, which a test switches between the serving one and a candidate.
    private sealed class SwitchableDeployment
    {
        public SwitchableDeployment()
        {
            Network = new CanaryNetwork { Respond = (request, _) => Task.FromResult(Answers(request)) };
        }

        public CanaryNetwork Network { get; }

        public Func<SentRequest, HttpResponseMessage> Answers { get; set; } = TakesLowOnly;
    }

    // gpt-6.1-sol's own answer: it refuses none and takes low (measured on 2026-09-29, above).
    private static HttpResponseMessage RefusesNone() => Refusal(
        "Unsupported value: 'none' is not supported with the 'gpt-6.1-sol-2026-09-29' model. Supported values are: 'low', 'medium', 'high', 'xhigh', and 'max'.",
        "unsupported_value");

    private static HttpResponseMessage TakesLowOnly(SentRequest request) =>
        EffortOf(request) == "none" ? RefusesNone() : CanaryNetwork.Echo(request);

    private static HttpResponseMessage TakesNoEffort(SentRequest request) => EffortOf(request) is not null
        ? Refusal("Unsupported parameter: 'reasoning.effort' is not supported with this model.", "unsupported_parameter")
        : CanaryNetwork.Echo(request);

    private static HttpResponseMessage RefusesLowToo(SentRequest request) => EffortOf(request) switch
    {
        "none" => RefusesNone(),
        "low" => RefusesLow(),
        _ => CanaryNetwork.Echo(request),
    };

    private static HttpResponseMessage RefusesLowThenFails(SentRequest request) => EffortOf(request) switch
    {
        "none" => RefusesNone(),
        "low" => RefusesLow(),
        _ => Unavailable(),
    };

    private static HttpResponseMessage FailsAfterNone(SentRequest request) =>
        EffortOf(request) == "none" ? RefusesNone() : Unavailable();

    private static HttpResponseMessage RefusesLow() => Refusal(
        "Unsupported value: 'low' is not supported with this model. Supported values are: 'medium'.", "unsupported_value");

    private static HttpResponseMessage Unavailable() => ScriptedHttpHandler.Json(
        HttpStatusCode.ServiceUnavailable, """{"error":{"message":"The service is busy.","type":"server_error"}}""");

    // MAI-Thinking-1's shape: Responses is refused as a surface, and Chat Completions takes low and refuses none.
    private static HttpResponseMessage ChatOnly(SentRequest request) => EffortOf(request) == "none"
        ? RefusesNone()
        : Surface(request) == "responses"
            ? ScriptedHttpHandler.Json(
                HttpStatusCode.BadRequest,
                """{"error":{"message":"The requested operation is unsupported.","type":"invalid_request_error"}}""")
            : CanaryNetwork.Echo(request);

    private static Func<SentRequest, HttpResponseMessage> Candidate(string name) => name switch
    {
        "takes low" => TakesLowOnly,
        "takes no effort" => TakesNoEffort,
        "fails after none" => FailsAfterNone,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such candidate."),
    };

    private static string Surface(SentRequest request) =>
        request.Path.EndsWith("/responses", StringComparison.Ordinal) ? "responses" : "chat";

    private static string Efforts(IEnumerable<SentRequest> requests) =>
        string.Join(", ", requests.Select(request => EffortOf(request) ?? "-"));

    private static string Describe(IEnumerable<SentRequest> requests) =>
        string.Join(", ", requests.Select(request => $"{Surface(request)}:{EffortOf(request) ?? "-"}"));

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
