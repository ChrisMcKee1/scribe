using System.Net;
using System.Text;
using Azure.Core;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// The cleanup's numbers (PLAT-O-03 and its merged rows): one Debug line per attempt, numbers only, whatever the flags;
/// PerfFlags.CleanupPhaseTelemetry adds the send path's timing (response headers and the last body byte) through a
/// read-through content, and changes nothing on the wire. Driven through the production service, transport and hand-off
/// over the canary network; nothing leaves the process.
/// </summary>
public sealed class CleanupPhaseTelemetryTests
{
    private const string Transcript = "the canary transcript says hello to the phase timings";
    private const string Host = "vocabulary-canary.example.invalid";
    private const string ApiKey = "not-a-real-key";
    private const string Deployment = "cleanup-deployment";

    [Fact]
    public async Task Each_attempt_logs_its_numbers_and_never_the_text_host_key_or_deployment()
    {
        await using var harness = new VocabularyCleanupHarness(source: null);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());

        var result = await harness.Service.CleanAsync(Transcript, CancellationToken.None);

        Assert.NotEqual(CleanupOutcome.Failed, result.Outcome);
        var attempt = Assert.Single(AttemptLines(harness));
        Assert.Contains("1 request(s)", attempt.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("headers", attempt.Message, StringComparison.Ordinal); // send-path timing is flagged

        // An API key reaches no Azure CLI: every token phase says it never ran, rather than a measured 0 ms.
        Assert.Contains("Azure CLI gate 0 ms over 0 wait(s), 0 ended before admission", attempt.Message, StringComparison.Ordinal);
        Assert.Contains("Azure CLI token 0 ms over 0 call(s), 0 unfinished", attempt.Message, StringComparison.Ordinal);
        Assert.Contains("shared token wait 0 ms over 0 wait(s), 0 without the token", attempt.Message, StringComparison.Ordinal);
        foreach (var canary in new[] { Transcript, "canary transcript", Host, ApiKey, Deployment })
        {
            Assert.DoesNotContain(canary, attempt.AllText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Missing_usage_logs_as_unset_never_zero()
    {
        var network = new CanaryNetwork();
        network.Respond = (request, _) => Task.FromResult(
            request.IsProbe ? CanaryNetwork.Echo(request) : ResponsesWithoutUsage(request.TranscriptOf() ?? "ok"));
        await using var harness = new VocabularyCleanupHarness(source: null, network);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());

        await harness.Service.CleanAsync(Transcript, CancellationToken.None);

        var attempt = Assert.Single(AttemptLines(harness));
        Assert.Contains("tokens in unset, cached unset, out unset, reasoning unset", attempt.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_attempt_line_counts_every_http_attempt_across_the_sdk_s_retries()
    {
        var network = new CanaryNetwork();
        network.Respond = (request, _) => Task.FromResult(
            request.IsProbe ? CanaryNetwork.Echo(request) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await using var harness = new VocabularyCleanupHarness(source: null, network) { RetryPolicy = new ImmediateRetryPolicy(3) };
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());

        await harness.Service.CleanAsync(Transcript, CancellationToken.None);

        // Four 503s per attempt (the first try and three retries), and the log's count is the network's, whatever the
        // service then does with the failure.
        var sent = network.Sent.Count(request => !request.IsProbe);
        var counted = AttemptLines(harness)
            .Select(line => int.Parse(
                System.Text.RegularExpressions.Regex.Match(line.Message, @"^AI cleanup attempt: (\d+) request\(s\)").Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        Assert.NotEmpty(counted);
        Assert.All(counted, requests => Assert.Equal(4, requests));
        Assert.Equal(sent, counted.Sum());
    }

    [Fact]
    public async Task The_flag_adds_the_send_path_timing_and_changes_nothing_on_the_wire_or_in_the_answer()
    {
        var off = await RunAsync(PerfFlags.None);
        var on = await RunAsync(PerfFlags.Parse(PerfFlags.CleanupPhaseTelemetry));

        Assert.Equal(off.Body, on.Body); // the request, byte for byte
        Assert.Equal(off.Text, on.Text);
        Assert.Equal(off.Outcome, on.Outcome);
        Assert.DoesNotContain("headers", off.Line, StringComparison.Ordinal);
        Assert.Contains("over 1 response(s)", on.Line, StringComparison.Ordinal);
        Assert.Contains("last body bytes read at", on.Line, StringComparison.Ordinal);
        Assert.Contains("over 1 body(ies), 0 empty, 0 unfinished", on.Line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_timed_content_passes_every_byte_and_header_through_and_notes_the_end_once()
    {
        const string json = "{\"id\":\"resp_1\",\"text\":\"h\u00e9llo\"}";
        var inner = new StringContent(json, Encoding.UTF8, "application/json");
        var timings = new CleanupPhaseTimings(capturePhases: true, TimeSpan.Zero);
        using var timed = new CleanupPhaseTimings.TimedContent(inner, timings, System.Diagnostics.Stopwatch.GetTimestamp());

        Assert.Equal("application/json", timed.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", timed.Headers.ContentType?.CharSet);
        await using var stream = await timed.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal(json, await reader.ReadToEndAsync());
        var ended = timings.Read().BodyTicks;
        Assert.True(ended >= 0);
        Assert.Equal(0, await stream.ReadAsync(new byte[16])); // a second end of body is not counted again
        Assert.Equal(ended, timings.Read().BodyTicks);
    }

    [Fact]
    public async Task The_timed_content_disposes_the_original()
    {
        var inner = new StringContent("{}", Encoding.UTF8, "application/json");
        var timed = new CleanupPhaseTimings.TimedContent(inner, new CleanupPhaseTimings(true, TimeSpan.Zero), 0);

        timed.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => inner.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_cli_gate_wait_and_the_token_call_go_on_the_admission_of_the_flow_that_asked()
    {
        var credential = new SerializedAzureCliCredential(new InstantCredential());
        var timings = new CleanupPhaseTimings(capturePhases: false, TimeSpan.Zero);
        var admission = new CleanupAdmission(CleanupRequestKind.Dictation, AiVocabularyScope.None, null) { Timings = timings };

        using (admission.Enter())
        {
            await credential.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]), CancellationToken.None);
        }

        Assert.Equal(1, timings.Read().TokenCalls);

        // Outside any cleanup (Settings discovery), there is nothing to record and nothing fails.
        await credential.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]), CancellationToken.None);
        Assert.Equal(1, timings.Read().TokenCalls);
    }

    [Fact]
    public void Snapshots_subtract_to_one_attempts_share()
    {
        var timings = new CleanupPhaseTimings(capturePhases: true, TimeSpan.FromMilliseconds(2));
        timings.AddGateWait(TimeSpan.FromMilliseconds(10), admitted: true);
        timings.AddTokenCall(TimeSpan.FromMilliseconds(800), finished: true);
        var before = timings.Read();
        timings.AddGateWait(TimeSpan.FromMilliseconds(5), admitted: true);
        timings.AddTokenCall(TimeSpan.FromMilliseconds(700), finished: false);
        timings.AddGateWait(TimeSpan.FromMilliseconds(3), admitted: false);
        timings.AddSharedTokenWait(TimeSpan.FromMilliseconds(40), received: false);
        timings.AddHeaders(TimeSpan.FromMilliseconds(1500));
        timings.AddBody(TimeSpan.FromMilliseconds(1600));
        timings.AddEmptyBody();
        timings.AddUnfinishedBody();

        var share = timings.Read().Since(before);

        Assert.Equal(TimeSpan.FromMilliseconds(8).Ticks, share.GateTicks);
        Assert.Equal(2, share.GateWaits);
        Assert.Equal(1, share.GateUnadmitted);
        Assert.Equal(TimeSpan.FromMilliseconds(700).Ticks, share.TokenTicks);
        Assert.Equal(1, share.TokenCalls);
        Assert.Equal(1, share.TokenUnfinished);
        Assert.Equal(TimeSpan.FromMilliseconds(40).Ticks, share.SharedTicks);
        Assert.Equal(1, share.SharedWaits);
        Assert.Equal(1, share.SharedUnreceived);
        Assert.Equal(1, share.Responses);
        Assert.Equal(TimeSpan.FromMilliseconds(1600).Ticks, share.BodyTicks);
        Assert.Equal(1, share.Bodies);
        Assert.Equal(1, share.EmptyBodies);
        Assert.Equal(1, share.UnfinishedBodies);
        Assert.Equal(TimeSpan.FromMilliseconds(2), timings.Selection);
    }

    // The SDK's own retry policy with its waits taken out, so the retries run back to back.
    private sealed class ImmediateRetryPolicy(int maxRetries) : System.ClientModel.Primitives.ClientRetryPolicy(maxRetries)
    {
        protected override TimeSpan GetNextDelay(System.ClientModel.Primitives.PipelineMessage message, int tryCount) =>
            TimeSpan.FromTicks(1);

        protected override Task WaitAsync(TimeSpan time, CancellationToken cancellationToken) => Task.CompletedTask;

        protected override void Wait(TimeSpan time, CancellationToken cancellationToken)
        {
        }
    }

    private static IEnumerable<CleanupLogging.CapturedLogEntry> AttemptLines(VocabularyCleanupHarness harness) =>
        harness.Log.Entries.Where(entry => entry.Message.StartsWith("AI cleanup attempt:", StringComparison.Ordinal));

    private static async Task<(string Body, string Text, CleanupOutcome Outcome, string Line)> RunAsync(PerfFlags flags)
    {
        var network = new CanaryNetwork();
        network.Respond = async (request, cancellationToken) =>
        {
            // A little time on the wire, so the send-path numbers have something to measure.
            await Task.Delay(TimeSpan.FromMilliseconds(15), cancellationToken);
            return CanaryNetwork.Echo(request);
        };

        await using var harness = new VocabularyCleanupHarness(source: null, network, flags);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());
        var result = await harness.Service.CleanAsync(Transcript, CancellationToken.None);

        var dictation = Assert.Single(network.Sent, request => !request.IsProbe);
        var line = Assert.Single(AttemptLines(harness)).Message;
        return (dictation.Body, result.Text, result.Outcome, line);
    }

    private static HttpResponseMessage ResponsesWithoutUsage(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"id\":\"resp_nousage\",\"object\":\"response\",\"created_at\":1700000000,\"status\":\"completed\"," +
            "\"model\":\"cleanup-deployment\",\"output\":[{\"type\":\"message\",\"id\":\"msg_1\",\"status\":\"completed\"," +
            "\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":" +
            System.Text.Json.JsonSerializer.Serialize(text) + ",\"annotations\":[]}]}]," +
            "\"parallel_tool_calls\":false,\"tool_choice\":\"auto\",\"tools\":[]}",
            Encoding.UTF8,
            "application/json"),
    };

    private sealed class InstantCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }
}
