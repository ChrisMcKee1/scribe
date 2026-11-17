using System.Net;
using System.Text.Json;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// A server on this PC that refuses reasoning_effort or max_tokens is sent plain requests, which its readiness check
/// finds. What a check finds stays in the check until the initialization that owns the generation publishes it: Test
/// connection used to write the one shared slot as its probe went, so testing another server, or the same one, set or
/// cleared the choice the serving server's next dictation was sent with. Through the production service and transport,
/// over the canary network; nothing leaves the process.
/// </summary>
public sealed class PlainRequestNegotiationTests
{
    private const string Transcript = "the canary transcript is a light edit";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // Two models on one server on this PC at an address Scribe does not manage (not Ollama's, LM Studio's or TensorSharp's
    // port), so each is another AI service whose requests are negotiated by its readiness check alone.
    private static CleanupOptions Server(string model) => CleanupHarness.Custom("http://127.0.0.1:8000/v1", model);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_test_of_another_model_cannot_change_whether_the_serving_one_is_sent_plain_requests(
        bool candidateRefusesEverything)
    {
        var server = new SwitchableServer { Answers = Strict };
        await using var harness = new VocabularyCleanupHarness(source: null, server.Network);
        await harness.ConfigureAndWaitAsync(Server("model-a"));
        Assert.False(CarriesGenerationFields(server.Network.Sent.Last()));

        server.Answers = candidateRefusesEverything ? RefusesEverything : Strict;
        var tested = await harness.Service.TestAsync(Server("model-b")).WaitAsync(Bound);
        server.Answers = Strict;

        Assert.Equal(
            candidateRefusesEverything ? CleanupTestOutcome.Failed : CleanupTestOutcome.Connected, tested.Outcome);
        Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
        var result = await harness.Service.CleanAsync(Transcript, CancellationToken.None);

        Assert.NotEqual(CleanupOutcome.Failed, result.Outcome);
        var dictation = server.Network.Sent.Last();
        Assert.Equal(Transcript, dictation.TranscriptOf());
        Assert.False(CarriesGenerationFields(dictation));
    }

    [Fact]
    public async Task A_test_of_the_serving_model_starts_from_the_plain_requests_it_negotiated()
    {
        var server = new SwitchableServer { Answers = Strict };
        await using var harness = new VocabularyCleanupHarness(source: null, server.Network);
        await harness.ConfigureAndWaitAsync(Server("model-a"));
        var before = server.Network.Sent.Count;

        var tested = await harness.Service.TestAsync(Server("model-a")).WaitAsync(Bound);

        // As saving the same settings would, the test sends plain requests at once instead of meeting the refusal first.
        Assert.Equal(CleanupTestOutcome.Connected, tested.Outcome);
        Assert.False(CarriesGenerationFields(Assert.Single(server.Network.Sent.Skip(before))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_test_of_the_serving_model_never_makes_its_requests_plain(bool refusesEverything)
    {
        var server = new SwitchableServer { Answers = Lenient };
        await using var harness = new VocabularyCleanupHarness(source: null, server.Network);
        await harness.ConfigureAndWaitAsync(Server("model-a"));
        Assert.True(CarriesGenerationFields(server.Network.Sent.Last()));

        // The server refuses the fields while it is tested, and then either takes plain requests or refuses everything: the
        // test settles it in its own copy, which it never publishes, and the serving configuration keeps sending the
        // fields it negotiated.
        server.Answers = refusesEverything ? RefusesEverything : Strict;
        var tested = await harness.Service.TestAsync(Server("model-a")).WaitAsync(Bound);
        server.Answers = Lenient;

        Assert.Equal(refusesEverything ? CleanupTestOutcome.Failed : CleanupTestOutcome.Connected, tested.Outcome);
        var result = await harness.Service.CleanAsync(Transcript, CancellationToken.None);
        Assert.NotEqual(CleanupOutcome.Failed, result.Outcome);
        Assert.True(CarriesGenerationFields(server.Network.Sent.Last()));
    }

    // The server on the other end of the network, which a test switches between answers.
    private sealed class SwitchableServer
    {
        public SwitchableServer()
        {
            Network = new CanaryNetwork { Respond = (request, _) => Task.FromResult(Answers(request)) };
        }

        public CanaryNetwork Network { get; }

        public Func<SentRequest, HttpResponseMessage> Answers { get; set; } = Lenient;
    }

    // What vLLM does to the fields 0.5.2 added for servers on this PC: it takes reasoning_effort only as low, medium or high.
    private static HttpResponseMessage Strict(SentRequest request) => CarriesGenerationFields(request)
        ? ScriptedHttpHandler.Json(
            HttpStatusCode.BadRequest,
            """{"error":{"message":"reasoning_effort: Input should be 'low', 'medium' or 'high'","type":"BadRequestError"}}""")
        : CanaryNetwork.Echo(request);

    private static HttpResponseMessage Lenient(SentRequest request) => CanaryNetwork.Echo(request);

    private static HttpResponseMessage RefusesEverything(SentRequest request) => ScriptedHttpHandler.Json(
        HttpStatusCode.BadRequest,
        """{"error":{"message":"model not found","type":"invalid_request_error"}}""");

    // Whether a request carries what a plain request leaves out: reasoning_effort and the max_tokens copy.
    private static bool CarriesGenerationFields(SentRequest request)
    {
        using var body = JsonDocument.Parse(request.Body);
        return body.RootElement.TryGetProperty("reasoning_effort", out _) ||
            body.RootElement.TryGetProperty("max_tokens", out _);
    }
}
