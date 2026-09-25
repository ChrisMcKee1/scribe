using System.Net;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Libraries;
using static Scribe.Core.Tests.Vocabulary.TestVocabularies;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// A one-off completion (the usage insight, the dictionary suggester) goes only to the recipient its caller captured, and
/// only while cleanup is ready, at every attempt it makes (contracts 3.3.6 and 9.4): release 0.4.4's recipient rule and the
/// library scope, both judged at each hand-off. The recipient used to be checked once, when the completion took its agent,
/// so an attempt the client's retry policy held, or the Copilot session's send after its creation, still left through the
/// old client after the user switched provider or turned AI cleanup off. Each case puts the change between two attempts
/// with gates, never a sleep, and reads what reached the canary network or the loopback Copilot runtime after it.
/// </summary>
public sealed class CompletionRecipientHandOffTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // Letters only, so JSON escaping in a request body can never hide it.
    private const string Canary = "Lanternquay";
    private const string LibraryId = "harbourline-team";
    private const string Summary = "Dictations: 3\nWords: 40\nActive days: 2\nRecurring terms:\n- " + Canary + ": 3 dictations";

    // A dictation whose request finds the on-device model evicted.
    private const string Evicted = "please ask about the harbour lanterns today";

    private static readonly LibraryVocabulary Permitted = Of(1, new Library(LibraryId, H1, true, Entry("lan tern quay", Canary)));

    public static TheoryData<string> Changes() => ["provider", "off"];

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task A_change_between_two_http_attempts_of_a_usage_insight_sends_no_later_attempt(string change)
    {
        var source = new TestVocabularySource(Permitted);
        await using var harness = new VocabularyCleanupHarness(source);
        var retries = new GatedRetryPolicy(maxRetries: 3);
        harness.RetryPolicy = retries;
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom());
        var recipient = harness.Service.Recipient!;
        AnswerTheFirstCompletionBusy(harness.Network);

        var waiting = retries.Waiting.Task;
        var completing = harness.Service.CompleteAsync(UsageInsight.SystemPrompt, Summary, recipient, Permitted.AiScope);
        var release = await waiting.WaitAsync(Bound);
        await ChangeAsync(harness, change);
        release.SetResult();
        var result = await completing.WaitAsync(Bound);

        // The first attempt went before the change; the client's own retry, due after it, never left.
        Assert.Single(harness.Network.Sent, request => request.Carries(Canary));
        Assert.Equal(1, result.RequestsHandedOver);
        Assert.False(result.NothingSent);
        Assert.Null(result.Text);
        Assert.Equal(
            change == "provider" ? ScopedCompletionOutcome.RecipientChanged : ScopedCompletionOutcome.NotReady,
            result.Outcome);
    }

    [Fact]
    public async Task The_suggesters_completion_stops_at_its_next_attempt_after_a_provider_change_and_never_says_nothing_was_sent()
    {
        // Release 0.4.4's overload, which the dictionary suggester uses: it sends history, and no library vocabulary.
        var source = new TestVocabularySource(LibraryVocabulary.Empty);
        await using var harness = new VocabularyCleanupHarness(source);
        var retries = new GatedRetryPolicy(maxRetries: 3);
        harness.RetryPolicy = retries;
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom());
        var recipient = harness.Service.Recipient!;
        AnswerTheFirstCompletionBusy(harness.Network);

        var waiting = retries.Waiting.Task;
        var completing = harness.Service.CompleteAsync("Suggest dictionary entries.", "recent dictations: " + Canary, recipient);
        var release = await waiting.WaitAsync(Bound);
        await ChangeAsync(harness, "provider");
        release.SetResult();
        var result = await completing.WaitAsync(Bound);

        // One attempt left before the change, so the result may not claim that nothing was sent.
        Assert.Single(harness.Network.Sent, request => request.Carries(Canary));
        Assert.Equal(CompletionOutcome.Failed, result.Outcome);
        Assert.False(result.NothingSent);
        Assert.Null(result.Text);
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task A_change_between_a_copilot_insight_sessions_creation_and_its_send_sends_nothing_more(string change)
    {
        var source = new TestVocabularySource(Permitted);
        await using var runtime = new FakeCopilotRuntime();
        await using var harness = new VocabularyCleanupHarness(source);
        harness.Service.CopilotRuntimeUrlForTesting = runtime.Url;
        await harness.ConfigureAndWaitAsync(VocabularyHandOffTests.Copilot());
        var recipient = harness.Service.Recipient!;

        // The runtime holds its answer to the insight session's creation; the summary, with the canary, travels in the send.
        var creationHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.BeforeAnswer = (request, ct) => IsInsightCreation(request) ? creationHeld.Task.WaitAsync(ct) : Task.CompletedTask;
        var created = runtime.Next(IsInsightCreation);
        var completing = harness.Service.CompleteAsync(UsageInsight.SystemPrompt, Summary, recipient, Permitted.AiScope);
        var creation = await created.WaitAsync(Bound);
        var destroyed = runtime.Next(request => request.Method == "session.destroy" && request.SessionId == creation.SessionId);

        if (change == "provider")
        {
            // Another Copilot model is another recipient. Its client's handshake is held, as the CLI's takes about twenty
            // seconds, so the insight's client is still connected when its send is due and only the hand-off can stop it.
            var handshakeHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.BeforeAnswer = (request, ct) => request.Method == "connect" ? handshakeHeld.Task.WaitAsync(ct) : Task.CompletedTask;
            var handshake = runtime.Next(request => request.Method == "connect");
            harness.Service.Configure(VocabularyHandOffTests.Copilot() with { CopilotModel = "another-model" });
            await handshake.WaitAsync(Bound);
        }
        else
        {
            harness.Service.Configure(VocabularyHandOffTests.Copilot() with { Enabled = false });
            await harness.WaitForStatusAsync(CleanupStatus.Disabled);
        }

        creationHeld.SetResult();
        var result = await completing.WaitAsync(Bound);

        // The creation went before the change; the send, due after it, never did, and the session was destroyed.
        Assert.DoesNotContain(runtime.Sends, send => send.SessionId == creation.SessionId);
        Assert.DoesNotContain(runtime.Requests, request => request.Carries(Canary));
        Assert.Equal(creation.SessionId, (await destroyed.WaitAsync(Bound)).SessionId);
        Assert.Equal(1, result.RequestsHandedOver);
        Assert.False(result.NothingSent);
        Assert.Null(result.Text);

        // Either way the service is not serving the insight's recipient at the send: it is setting up the other model, or
        // it is off.
        Assert.Equal(ScopedCompletionOutcome.NotReady, result.Outcome);
    }

    [Fact]
    public async Task A_later_attempt_while_cleanup_is_unavailable_for_the_same_recipient_is_not_sent()
    {
        // Readiness, not only the recipient: the configuration stays the same, but a dictation found the on-device model
        // evicted and could not load it back, so cleanup is Unavailable with its client still in place. The completion's
        // retry, due then, is not sent to a recipient that is not ready.
        var source = new TestVocabularySource(Permitted);
        await using var harness = new VocabularyCleanupHarness(source);
        var retries = new GatedRetryPolicy(maxRetries: 3);
        harness.RetryPolicy = retries;
        await harness.ConfigureAndWaitAsync(CleanupHarness.FoundryOn());
        var recipient = harness.Service.Recipient!;
        var completionAttempts = 0;
        harness.Network.Respond = (request, _) => Task.FromResult(
            request.Carries(Canary) && Interlocked.Increment(ref completionAttempts) == 1
                ? CanaryNetwork.Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"busy\"}}")
                : request.Carries(Canary)
                    ? CanaryNetwork.Chat("You dictate about the harbour.")
                    : request.TranscriptOf() == Evicted
                        ? CanaryNetwork.Json(
                            HttpStatusCode.BadRequest,
                            "{\"error\":{\"message\":\"Model '" + CleanupHarness.FoundryVariant + "' is not loaded. Please load the model before getting a ChatClient.\"}}")
                        : CanaryNetwork.Echo(request));

        var waiting = retries.Waiting.Task;
        var completing = harness.Service.CompleteAsync(UsageInsight.SystemPrompt, Summary, recipient, Permitted.AiScope);
        var release = await waiting.WaitAsync(Bound);

        harness.Qwen.LoadFailure = new InvalidOperationException("The model file is damaged.");
        await harness.Service.CleanAsync(Evicted).WaitAsync(Bound);
        Assert.Equal(CleanupStatus.Unavailable, harness.Service.Status);

        release.SetResult();
        var result = await completing.WaitAsync(Bound);

        Assert.Single(harness.Network.Sent, request => request.Carries(Canary));
        Assert.Equal(ScopedCompletionOutcome.NotReady, result.Outcome);
        Assert.Equal(1, result.RequestsHandedOver);
    }

    [Fact]
    public async Task A_prompt_only_change_between_two_attempts_keeps_the_recipient_and_the_retry_goes()
    {
        // The control: a dictionary term or the writing style changes only what the prompt says, so the request still goes
        // to the same place and the completion is not stopped for it.
        var source = new TestVocabularySource(Permitted);
        await using var harness = new VocabularyCleanupHarness(source);
        var retries = new GatedRetryPolicy(maxRetries: 3);
        harness.RetryPolicy = retries;
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom());
        var recipient = harness.Service.Recipient!;
        AnswerTheFirstCompletionBusy(harness.Network);

        var waiting = retries.Waiting.Task;
        var completing = harness.Service.CompleteAsync(UsageInsight.SystemPrompt, Summary, recipient, Permitted.AiScope);
        var release = await waiting.WaitAsync(Bound);
        harness.Service.Configure(VocabularyCleanupHarness.Custom() with { WritingStyle = "Short sentences." });
        Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
        release.SetResult();
        var result = await completing.WaitAsync(Bound);

        Assert.Equal(ScopedCompletionOutcome.Completed, result.Outcome);
        Assert.Equal(2, harness.Network.Sent.Count(request => request.Carries(Canary)));
        Assert.Equal(2, result.RequestsHandedOver);
    }

    // The completion's first attempt is answered busy, so the client's retry policy holds a second; anything else is echoed.
    private static void AnswerTheFirstCompletionBusy(CanaryNetwork network)
    {
        var attempts = 0;
        network.Respond = (request, _) => Task.FromResult(
            request.Carries(Canary) && Interlocked.Increment(ref attempts) == 1
                ? CanaryNetwork.Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"busy\"}}")
                : request.Carries(Canary)
                    ? CanaryNetwork.Chat("You dictate about the harbour.")
                    : CanaryNetwork.Echo(request));
    }

    // The user switches provider (another endpoint, serving before the retry is due) or turns AI cleanup off.
    private static async Task ChangeAsync(VocabularyCleanupHarness harness, string change)
    {
        if (change == "provider")
        {
            await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom() with
            {
                CustomEndpoint = "https://another-canary.example.invalid/v1",
                CustomModel = "another-model",
            });
        }
        else
        {
            harness.Service.Configure(VocabularyCleanupHarness.Custom() with { Enabled = false });
            await harness.WaitForStatusAsync(CleanupStatus.Disabled);
        }
    }

    private static bool IsInsightCreation(CopilotRuntimeRequest request) =>
        request.Method == "session.create" && request.SystemMessage == UsageInsight.SystemPrompt;
}
