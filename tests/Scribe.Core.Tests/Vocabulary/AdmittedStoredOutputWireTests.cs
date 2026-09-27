using System.Net;
using System.Text.Json;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;
using Scribe.Core.Vocabulary;
using static Scribe.Core.Tests.Vocabulary.TestVocabularies;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// Cloud cleanup stores nothing, read from the wire of the complete admitted pipeline: the cleanup service the app builds,
/// with the library vocabulary's admission point and its hand-off handler in front of the transport, sending to a canary
/// network. The factory-level wire tests (<see cref="CleanupStoredOutputWireTests"/>) and the package contract
/// (<see cref="StoredOutputWireContractTests"/>) stay as they are; these companions show the control still reaches every
/// request once each attempt goes through the hand-off: the readiness probe, an admitted dictation's chunk and the
/// client's own retry of it, and a usage insight, on the Responses surface and on the Chat Completions fallback.
/// </summary>
public sealed class AdmittedStoredOutputWireTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // Letters only, so JSON escaping in a request body can never hide them.
    private const string Canary = "Quaystorm";
    private const string Dictated = "please ask kay storm about the harbour lanterns today";
    private const string Summary = "Dictations: 3\nWords: 40\nActive days: 2\nRecurring terms:\n- " + Canary + ": 3 dictations";

    private static readonly LibraryVocabulary Permitted = Of(1, new Library("harbourline-team", H1, true, Entry("kay storm", Canary)));

    [Fact]
    public async Task On_the_responses_surface_every_request_the_admitted_pipeline_sends_says_store_false()
    {
        var source = new TestVocabularySource(Permitted);
        await using var harness = new VocabularyCleanupHarness(source);
        var retries = new GatedRetryPolicy(maxRetries: 1);
        harness.RetryPolicy = retries;
        var dictationAttempts = 0;
        harness.Network.Respond = (request, _) => Task.FromResult(
            request.TranscriptOf() == Dictated && Interlocked.Increment(ref dictationAttempts) == 1
                ? CanaryNetwork.Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"busy\"}}")
                : request.Carries(Canary) && request.TranscriptOf() != Dictated
                    ? CanaryNetwork.Responses("You dictate about the harbour.")
                    : CanaryNetwork.Echo(request));
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());

        // The probe.
        var probe = Assert.Single(harness.Network.Sent);
        Assert.EndsWith("/openai/v1/responses", probe.Path, StringComparison.Ordinal);
        AssertStoreFalse(probe.Body);

        // An admitted dictation: its first attempt, answered busy, and the client's own retry of it, both handed over.
        var waiting = retries.Waiting.Task;
        var cleaning = harness.Service.Admit(GenerationOf(Permitted).Cleanup).CleanAsync(Dictated);
        (await waiting.WaitAsync(Bound)).SetResult();
        Assert.Equal(CleanupOutcome.Unchanged, (await cleaning.WaitAsync(Bound)).Outcome);
        var dictation = harness.Network.Sent.Where(request => request.TranscriptOf() == Dictated).ToList();
        Assert.Equal(2, dictation.Count);
        Assert.All(dictation, request =>
        {
            Assert.EndsWith("/openai/v1/responses", request.Path, StringComparison.Ordinal);
            Assert.True(request.Carries(Canary), "The admitted glossary never reached the request, so this proved nothing.");
            AssertStoreFalse(request.Body);
        });

        // A usage insight under its report's scope.
        var insight = await harness.Service
            .CompleteAsync(UsageInsight.SystemPrompt, Summary, harness.Service.Recipient!, Permitted.AiScope)
            .WaitAsync(Bound);
        Assert.Equal(ScopedCompletionOutcome.Completed, insight.Outcome);
        var completion = Assert.Single(harness.Network.Sent, request => request.Carries(Canary) && request.TranscriptOf() != Dictated);
        Assert.EndsWith("/openai/v1/responses", completion.Path, StringComparison.Ordinal);
        AssertStoreFalse(completion.Body);

        // Everything went through the admission point: the probe with no scope, the rest under the scope admitted.
        Assert.All(source.HandOffs, handOff => Assert.True(handOff.HandedOver));
        Assert.Equal(harness.Network.Sent.Count, source.HandOffs.Count);
    }

    [Fact]
    public async Task On_the_chat_completions_fallback_no_request_the_admitted_pipeline_sends_carries_store()
    {
        // A deployment that rejects the Responses API (MAI-Thinking-1's 400), which buys the Chat Completions surface.
        var source = new TestVocabularySource(Permitted);
        await using var harness = new VocabularyCleanupHarness(source);
        var retries = new GatedRetryPolicy(maxRetries: 1);
        harness.RetryPolicy = retries;
        var dictationAttempts = 0;
        harness.Network.Respond = (request, _) => Task.FromResult(
            request.Path.EndsWith("/responses", StringComparison.Ordinal)
                ? CanaryNetwork.Json(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"The requested operation is unsupported.\"}}")
                : request.TranscriptOf() == Dictated && Interlocked.Increment(ref dictationAttempts) == 1
                    ? CanaryNetwork.Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"busy\"}}")
                    : request.Carries(Canary) && request.TranscriptOf() != Dictated
                        ? CanaryNetwork.Chat("You dictate about the harbour.")
                        : CanaryNetwork.Echo(request));
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());

        // The Responses probe the deployment refused said store=false; the fallback's own probe carries no store field.
        Assert.Collection(
            harness.Network.Sent,
            responses =>
            {
                Assert.EndsWith("/openai/v1/responses", responses.Path, StringComparison.Ordinal);
                AssertStoreFalse(responses.Body);
            },
            chat =>
            {
                Assert.EndsWith("/openai/v1/chat/completions", chat.Path, StringComparison.Ordinal);
                AssertNoStore(chat.Body);
            });

        // An admitted dictation on the fallback, its first attempt answered busy and the client's retry of it.
        var waiting = retries.Waiting.Task;
        var cleaning = harness.Service.Admit(GenerationOf(Permitted).Cleanup).CleanAsync(Dictated);
        (await waiting.WaitAsync(Bound)).SetResult();
        Assert.Equal(CleanupOutcome.Unchanged, (await cleaning.WaitAsync(Bound)).Outcome);
        var dictation = harness.Network.Sent.Where(request => request.TranscriptOf() == Dictated).ToList();
        Assert.Equal(2, dictation.Count);
        Assert.All(dictation, request =>
        {
            Assert.EndsWith("/openai/v1/chat/completions", request.Path, StringComparison.Ordinal);
            Assert.True(request.Carries(Canary), "The admitted glossary never reached the request, so this proved nothing.");
            AssertNoStore(request.Body);
        });

        // A usage insight on the fallback.
        var insight = await harness.Service
            .CompleteAsync(UsageInsight.SystemPrompt, Summary, harness.Service.Recipient!, Permitted.AiScope)
            .WaitAsync(Bound);
        Assert.Equal(ScopedCompletionOutcome.Completed, insight.Outcome);
        var completion = Assert.Single(harness.Network.Sent, request => request.Carries(Canary) && request.TranscriptOf() != Dictated);
        Assert.EndsWith("/openai/v1/chat/completions", completion.Path, StringComparison.Ordinal);
        AssertNoStore(completion.Body);

        Assert.All(source.HandOffs, handOff => Assert.True(handOff.HandedOver));
        Assert.Equal(harness.Network.Sent.Count, source.HandOffs.Count);
    }

    private static VocabularyGeneration GenerationOf(LibraryVocabulary libraries) =>
        new(libraries.Generation, [], libraries, CompiledDictionaryRules.Empty);

    private static void AssertStoreFalse(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.TryGetProperty("store", out var store), $"No \"store\" field was sent: {body}");
        Assert.Equal(JsonValueKind.False, store.ValueKind);
    }

    // Chat Completions never sets store: Azure stores a chat completion only when it is true, and some deployments reject
    // a parameter they do not know (see CleanupStoredOutputWireTests).
    private static void AssertNoStore(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.False(document.RootElement.TryGetProperty("store", out _), $"Chat Completions is not meant to carry a \"store\" field: {body}");
    }
}
