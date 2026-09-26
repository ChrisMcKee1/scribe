using System.ClientModel.Primitives;
using System.Net;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;
using Scribe.Core.Vocabulary;
using static Scribe.Core.Tests.Vocabulary.TestVocabularies;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// The admission point on every outbound cleanup request (contract 2.10): each attempt carries the vocabulary of the
/// generation its dictation was admitted with and is handed over only while the published scope still covers that
/// generation's scope. Each case puts a revocation between two steps with gates (never a sleep) and reads, from a canary
/// network under the production transport, what was actually sent before and after it. Nothing leaves the process.
/// </summary>
public sealed class VocabularyHandOffTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // Letters and spaces only, so JSON escaping in a request body can never hide them.
    private const string LibraryCanary = "Zebraquill";
    private const string LibrarySpoken = "zeb ra quill";
    private const string LibraryId = "kestrelmoor-private";
    private const string Dictated = "please ask about the harbour lanterns today";

    private static readonly LibraryVocabulary Permitted = Of(1, new Library(LibraryId, H1, true, Entry(LibrarySpoken, LibraryCanary)));
    private static readonly LibraryVocabulary Revoked = Of(2, new Library(LibraryId, H1, false, Entry(LibrarySpoken, LibraryCanary)));

    private static VocabularyGeneration GenerationOf(LibraryVocabulary libraries) =>
        new(libraries.Generation, [], libraries, CompiledDictionaryRules.Empty);

    private static IEnumerable<bool> HandOffsOf(TestVocabularySource source, AiVocabularyScope scope) =>
        source.HandOffs.Where(handOff => ReferenceEquals(handOff.Admitted, scope)).Select(handOff => handOff.HandedOver);

    [Fact]
    public async Task A_revocation_between_two_chunks_holds_the_second_back_and_the_first_went_under_its_admitted_scope()
    {
        var source = new TestVocabularySource(Permitted);
        await using var harness = new VocabularyCleanupHarness(source);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom(CleanupPromptStyle.Local));
        var admitted = GenerationOf(Permitted);

        // The Local prompt style cleans in chunks of 2,400 characters, so this is at least two requests.
        var text = string.Join(' ', Enumerable.Repeat("Please ask about the harbour lanterns today.", 70));
        Assert.True(TextCleanupService.PrepareChunks(text, VocabularyCleanupHarness.Custom(CleanupPromptStyle.Local)).Count >= 2);

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanups = 0;
        harness.Network.Respond = async (request, ct) =>
        {
            if (!request.IsProbe && Interlocked.Increment(ref cleanups) == 1)
            {
                await held.Task.WaitAsync(ct);
            }

            return CanaryNetwork.Echo(request);
        };

        var firstChunk = harness.Network.Next(request => !request.IsProbe);
        var cleaning = harness.Service.Admit(admitted.Cleanup).CleanAsync(text);
        var first = await firstChunk.WaitAsync(Bound);
        source.Publish(Revoked);
        held.SetResult();
        var result = await cleaning.WaitAsync(Bound);

        // The first chunk was handed over before the revocation, with the admitted vocabulary and under its scope; the
        // second was judged after it, held back, and never reached the network.
        Assert.True(first.Carries(LibraryCanary));
        Assert.Single(harness.Network.Sent, request => !request.IsProbe);
        Assert.Equal([true, false], HandOffsOf(source, admitted.AiScope));

        // Local rules finish the held-back segment: its text stays as dictated, and a permission change is no failure.
        Assert.Equal(text, result.Text);
        Assert.Equal(CleanupOutcome.Unchanged, result.Outcome);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task A_revocation_before_the_clients_own_retry_holds_the_retry_back()
    {
        var source = new TestVocabularySource(Permitted);
        await using var harness = new VocabularyCleanupHarness(source);
        var retries = new GatedRetryPolicy(maxRetries: 3);
        harness.RetryPolicy = retries;
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom());
        var admitted = GenerationOf(Permitted);

        var cleanups = 0;
        harness.Network.Respond = (request, _) => Task.FromResult(
            !request.IsProbe && Interlocked.Increment(ref cleanups) == 1
                ? CanaryNetwork.Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"busy\"}}")
                : CanaryNetwork.Echo(request));

        var waiting = retries.Waiting.Task;
        var cleaning = harness.Service.Admit(admitted.Cleanup).CleanAsync(Dictated);
        var release = await waiting.WaitAsync(Bound);
        source.Publish(Revoked);
        release.SetResult();
        var result = await cleaning.WaitAsync(Bound);

        var sent = Assert.Single(harness.Network.Sent, request => !request.IsProbe);
        Assert.True(sent.Carries(LibraryCanary));
        Assert.Equal([true, false], HandOffsOf(source, admitted.AiScope));
        Assert.Equal(CleanupOutcome.Skipped, result.Outcome);
        Assert.Null(result.SkipReason);
        Assert.Null(result.FailureReason);
        Assert.Equal(Dictated, result.Text);
    }

    [Fact]
    public async Task The_chat_completions_fallback_is_handed_over_request_by_request_and_its_probe_carries_no_vocabulary()
    {
        // MAI-Thinking-1's answer to a Responses call: a 400, which buys the Chat Completions surface.
        var source = new TestVocabularySource(Permitted);
        await using var harness = new VocabularyCleanupHarness(source);
        harness.Network.Respond = (request, _) => Task.FromResult(request.Path.EndsWith("/responses", StringComparison.Ordinal)
            ? CanaryNetwork.Json(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"The requested operation is unsupported.\"}}")
            : CanaryNetwork.Echo(request));
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());
        var admitted = GenerationOf(Permitted);

        // Before the revocation the fallback surface carries the admitted vocabulary, under its scope.
        Assert.Equal(CleanupOutcome.Unchanged, (await harness.Service.Admit(admitted.Cleanup).CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        var before = harness.Network.Sent[^1];
        Assert.EndsWith("/openai/v1/chat/completions", before.Path, StringComparison.Ordinal);
        Assert.True(before.Carries(LibraryCanary));

        // After it, a request still admitted with the old vocabulary is held back on that surface too.
        source.Publish(Revoked);
        var sent = harness.Network.Sent.Count;
        var held = await harness.Service.Admit(admitted.Cleanup).CleanAsync(Dictated).WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Skipped, held.Outcome);
        Assert.Equal(sent, harness.Network.Sent.Count);
        Assert.Equal([true, false], HandOffsOf(source, admitted.AiScope));

        // A reconfiguration after the revocation runs the fallback again: both probes go, and neither carries a term.
        harness.Service.Configure(VocabularyCleanupHarness.Azure() with { AzureDeployment = "other-deployment" });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var probes = harness.Network.Sent.Skip(sent).ToList();
        Assert.Collection(
            probes,
            responses => Assert.EndsWith("/openai/v1/responses", responses.Path, StringComparison.Ordinal),
            chat => Assert.EndsWith("/openai/v1/chat/completions", chat.Path, StringComparison.Ordinal));
        Assert.All(probes, probe => Assert.False(probe.Carries(LibraryCanary)));

        // A dictation admitted with the vocabulary published now goes, without the revoked library's term.
        await harness.Service.Admit(GenerationOf(Revoked).Cleanup).CleanAsync(Dictated).WaitAsync(Bound);
        Assert.EndsWith("/openai/v1/chat/completions", harness.Network.Sent[^1].Path, StringComparison.Ordinal);
        Assert.False(harness.Network.Sent[^1].Carries(LibraryCanary));
    }

    [Fact]
    public async Task A_probe_queued_behind_a_model_load_goes_through_the_admission_point_carrying_nothing_after_a_revocation()
    {
        var source = new TestVocabularySource(Permitted);
        await using var harness = new VocabularyCleanupHarness(source);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom());
        var admitted = GenerationOf(Permitted);
        await harness.Service.Admit(admitted.Cleanup).CleanAsync(Dictated).WaitAsync(Bound);
        Assert.True(harness.Network.Sent[^1].Carries(LibraryCanary));

        // Switching to Foundry Local queues its readiness probe behind the model load, which the test holds.
        harness.Qwen.LoadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.Configure(CleanupHarness.FoundryOn());
        await harness.Qwen.LoadStarted.Task.WaitAsync(Bound);
        source.Publish(Revoked);
        var probe = harness.Network.Next(request => request.IsProbe);
        harness.Qwen.LoadGate.SetResult();
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // The probe went through the admission point with no library scope, and carried none of the vocabulary.
        Assert.False((await probe.WaitAsync(Bound)).Carries(LibraryCanary));
        Assert.Contains(source.HandOffs, handOff => ReferenceEquals(handOff.Admitted, AiVocabularyScope.None) && handOff.HandedOver);

        // The old admission is judged at the new provider's transport as well.
        var sent = harness.Network.Sent.Count;
        Assert.Equal(CleanupOutcome.Skipped, (await harness.Service.Admit(admitted.Cleanup).CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Equal(sent, harness.Network.Sent.Count);
        Assert.Equal([true, false], HandOffsOf(source, admitted.AiScope));
    }

    [Fact]
    public async Task A_revocation_between_two_usage_insight_attempts_holds_the_second_back()
    {
        var source = new TestVocabularySource(Permitted);
        await using var harness = new VocabularyCleanupHarness(source);
        var retries = new GatedRetryPolicy(maxRetries: 3);
        harness.RetryPolicy = retries;
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom());
        var recipient = harness.Service.Recipient!;

        // The report's scope, as a usage report would carry it, and a summary whose label is the library's term.
        var scope = Permitted.AiScope;
        var summary = "Dictations: 3\nWords: 40\nActive days: 2\nRecurring terms:\n- " + LibraryCanary + ": 3 dictations";
        var attempts = 0;
        harness.Network.Respond = (request, _) => Task.FromResult(
            request.IsProbe ? CanaryNetwork.Echo(request)
            : Interlocked.Increment(ref attempts) == 1 ? CanaryNetwork.Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"busy\"}}")
            : CanaryNetwork.Chat("You dictate about harbour lanterns."));

        var waiting = retries.Waiting.Task;
        var completing = harness.Service.CompleteAsync(UsageInsightPrompt, summary, recipient, scope);
        var release = await waiting.WaitAsync(Bound);
        source.Publish(Revoked);
        release.SetResult();
        var result = await completing.WaitAsync(Bound);

        Assert.Equal(ScopedCompletionOutcome.LibraryScopeNarrowed, result.Outcome);
        Assert.Null(result.Text);
        Assert.Equal(1, result.RequestsHandedOver);
        Assert.False(result.NothingSent);
        Assert.True(Assert.Single(harness.Network.Sent, request => !request.IsProbe).Carries(LibraryCanary));
        Assert.Equal([true, false], HandOffsOf(source, scope));

        // The report it was built from is stale from now on: asked again, nothing at all is sent.
        var again = await harness.Service.CompleteAsync(UsageInsightPrompt, summary, recipient, scope).WaitAsync(Bound);
        Assert.Equal(ScopedCompletionOutcome.LibraryScopeNarrowed, again.Outcome);
        Assert.True(again.NothingSent);
        Assert.Single(harness.Network.Sent, request => !request.IsProbe);

        // A report built on what is published now goes; the other overload's recipient check still decides first.
        var current = await harness.Service.CompleteAsync(UsageInsightPrompt, "Dictations: 3", recipient, Revoked.AiScope).WaitAsync(Bound);
        Assert.Equal(ScopedCompletionOutcome.Completed, current.Outcome);
        harness.Service.Configure(VocabularyCleanupHarness.Custom() with { CustomModel = "another-model" });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(
            ScopedCompletionOutcome.RecipientChanged,
            (await harness.Service.CompleteAsync(UsageInsightPrompt, "Dictations: 3", recipient, Revoked.AiScope).WaitAsync(Bound)).Outcome);
    }

    [Fact]
    public async Task A_copilot_session_is_created_and_sent_to_only_through_the_admission_point()
    {
        var source = new TestVocabularySource(Permitted);
        await using var runtime = new FakeCopilotRuntime();
        await using var harness = new VocabularyCleanupHarness(source);
        harness.Service.CopilotRuntimeUrlForTesting = runtime.Url;
        await harness.ConfigureAndWaitAsync(Copilot());
        var admitted = GenerationOf(Permitted);

        // The probe created its session and sent to it through the gate, with no library scope and no vocabulary.
        Assert.Equal(2, source.HandOffs.Count(handOff => ReferenceEquals(handOff.Admitted, AiVocabularyScope.None) && handOff.HandedOver));
        Assert.DoesNotContain(runtime.Requests, request => request.Carries(LibraryCanary));

        // A dictation's session carries the glossary in its system message; its send carries the transcript.
        var cleaned = await harness.Service.Admit(admitted.Cleanup).CleanAsync(Dictated).WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Unchanged, cleaned.Outcome);
        Assert.Contains(LibraryCanary, runtime.Creates[^1].SystemMessage, StringComparison.Ordinal);
        Assert.Equal(Dictated, FakeCopilotRuntime.TranscriptOf(runtime.Sends[^1].Prompt!));
        Assert.Equal([true, true], HandOffsOf(source, admitted.AiScope));

        // After a revocation nothing of that admission reaches the runtime: no session, no send.
        source.Publish(Revoked);
        var calls = runtime.Requests.Count;
        var held = await harness.Service.Admit(admitted.Cleanup).CleanAsync(Dictated).WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Skipped, held.Outcome);
        Assert.Equal(calls, runtime.Requests.Count);
        Assert.Equal([true, true, false], HandOffsOf(source, admitted.AiScope));
    }

    [Fact]
    public async Task A_revocation_after_the_copilot_session_is_created_holds_its_send_back_and_the_session_is_destroyed()
    {
        var source = new TestVocabularySource(Permitted);
        await using var runtime = new FakeCopilotRuntime();
        await using var harness = new VocabularyCleanupHarness(source);
        harness.Service.CopilotRuntimeUrlForTesting = runtime.Url;
        await harness.ConfigureAndWaitAsync(Copilot());
        var admitted = GenerationOf(Permitted);

        // The runtime holds its answer to the dictation's session creation, which carries the glossary.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.BeforeAnswer = (request, ct) =>
            request.Method == "session.create" && request.Carries(LibraryCanary) ? held.Task.WaitAsync(ct) : Task.CompletedTask;
        var created = runtime.Next(request => request.Method == "session.create" && request.Carries(LibraryCanary));
        var sends = runtime.Sends.Count;

        var cleaning = harness.Service.Admit(admitted.Cleanup).CleanAsync(Dictated);
        var creation = await created.WaitAsync(Bound);
        var destroyed = runtime.Next(request => request.Method == "session.destroy" && request.SessionId == creation.SessionId);
        source.Publish(Revoked);
        held.SetResult();
        var result = await cleaning.WaitAsync(Bound);

        // Nothing asked the runtime to run the model with that glossary after the revocation: the send was judged after
        // it and held back, and the session that had received the glossary was destroyed.
        Assert.DoesNotContain(runtime.Sends, send => send.SessionId == creation.SessionId);
        Assert.Equal(sends, runtime.Sends.Count);
        Assert.Equal(creation.SessionId, (await destroyed.WaitAsync(Bound)).SessionId);

        // The creation went before the revocation, under the scope it was admitted with.
        Assert.Equal([true, false], HandOffsOf(source, admitted.AiScope));

        // Local rules finish the dictation: its text as dictated, and a permission change is no failure.
        Assert.Equal(CleanupOutcome.Skipped, result.Outcome);
        Assert.Equal(Dictated, result.Text);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task A_revocation_after_a_copilot_usage_insight_session_is_created_never_sends_the_summary()
    {
        var source = new TestVocabularySource(Permitted);
        await using var runtime = new FakeCopilotRuntime();
        await using var harness = new VocabularyCleanupHarness(source);
        harness.Service.CopilotRuntimeUrlForTesting = runtime.Url;
        await harness.ConfigureAndWaitAsync(Copilot());
        var recipient = harness.Service.Recipient!;

        // The report's scope and a summary whose label is the library's term. The insight's own system prompt carries
        // no library term, so here the vocabulary travels in the send, which is what the admission point must hold back.
        var scope = Permitted.AiScope;
        var summary = "Dictations: 3\nWords: 40\nActive days: 2\nRecurring terms:\n- " + LibraryCanary + ": 3 dictations";
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.BeforeAnswer = (request, ct) =>
            request.Method == "session.create" && request.SystemMessage == UsageInsightPrompt ? held.Task.WaitAsync(ct) : Task.CompletedTask;
        var created = runtime.Next(request => request.Method == "session.create" && request.SystemMessage == UsageInsightPrompt);

        var completing = harness.Service.CompleteAsync(UsageInsightPrompt, summary, recipient, scope);
        var creation = await created.WaitAsync(Bound);
        source.Publish(Revoked);
        held.SetResult();
        var result = await completing.WaitAsync(Bound);

        Assert.DoesNotContain(runtime.Requests, request => request.Carries(LibraryCanary));
        Assert.DoesNotContain(runtime.Sends, send => send.SessionId == creation.SessionId);
        Assert.Equal(ScopedCompletionOutcome.LibraryScopeNarrowed, result.Outcome);
        Assert.Null(result.Text);
        Assert.Equal([true, false], HandOffsOf(source, scope));
    }

    internal static CleanupOptions Copilot() =>
        new(true, CleanupProvider.GitHubCopilot, CleanupModelCatalog.DefaultAlias, null, null, CopilotModel: "cleanup-model");

    [Fact]
    public async Task A_request_content_was_admitted_for_is_held_back_once_the_content_changed_even_if_the_new_content_is_permitted()
    {
        var source = new TestVocabularySource(Permitted);
        await using var harness = new VocabularyCleanupHarness(source);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom());
        var admittedAtH1 = GenerationOf(Permitted);

        // The same library, permitted again, for other content (a file replaced outside Scribe, or a Save of the library).
        var atH2 = Of(3, new Library(LibraryId, H2, true, Entry(LibrarySpoken, LibraryCanary)));
        source.Publish(atH2);

        var sent = harness.Network.Sent.Count;
        Assert.Equal(CleanupOutcome.Skipped, (await harness.Service.Admit(admittedAtH1.Cleanup).CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Equal(sent, harness.Network.Sent.Count);

        // Admitted for the new content, it goes.
        await harness.Service.Admit(GenerationOf(atH2).Cleanup).CleanAsync(Dictated).WaitAsync(Bound);
        Assert.True(harness.Network.Sent[^1].Carries(LibraryCanary));
    }

    [Fact]
    public async Task A_held_back_request_is_not_retried_by_the_clients_default_policy_and_is_logged_by_shape()
    {
        var source = new TestVocabularySource(Revoked);
        await using var harness = new VocabularyCleanupHarness(source);
        harness.RetryPolicy = ClientRetryPolicy.Default;
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom());
        var admitted = GenerationOf(Permitted);

        // The default policy retries a transport failure three times with a backoff of seconds; a refusal is not one.
        var result = await harness.Service.Admit(admitted.Cleanup).CleanAsync(Dictated).WaitAsync(Bound);

        Assert.Equal([false], HandOffsOf(source, admitted.AiScope));
        Assert.DoesNotContain(harness.Network.Sent, request => !request.IsProbe);
        Assert.Equal(CleanupOutcome.Skipped, result.Outcome);
        Assert.Null(result.SkipReason);
        Assert.Null(result.FailureReason);

        // The log carries counts and a generation: never the library, never the term, never what was dictated.
        var line = Assert.Single(harness.Log.Entries, entry => entry.Message.StartsWith("AI cleanup held back", StringComparison.Ordinal));
        Assert.Contains("1 of 1 segment(s)", line.Message, StringComparison.Ordinal);
        foreach (var secret in new[] { LibraryCanary, LibrarySpoken, LibraryId, Dictated })
        {
            Assert.DoesNotContain(secret, harness.Log.AllText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task With_an_admission_point_a_glossary_given_in_the_options_never_reaches_the_wire()
    {
        // A glossary in the options would go with every request under no scope at all, so the service drops it.
        var source = new TestVocabularySource(Permitted);
        await using var harness = new VocabularyCleanupHarness(source);
        var glossary = CleanupPrompt.BuildGlossary([Entry(LibrarySpoken, LibraryCanary)]);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom() with { Glossary = glossary });

        await harness.Service.CleanAsync(Dictated).WaitAsync(Bound);
        Assert.NotEmpty(harness.Network.Sent);
        Assert.All(harness.Network.Sent, request => Assert.False(request.Carries(LibraryCanary)));

        // The control: the same term, admitted with its scope, is carried.
        await harness.Service.Admit(GenerationOf(Permitted).Cleanup).CleanAsync(Dictated).WaitAsync(Bound);
        Assert.True(harness.Network.Sent[^1].Carries(LibraryCanary));
    }

    [Fact]
    public async Task A_request_reaching_either_gate_without_an_admission_is_never_sent()
    {
        var network = new CanaryNetwork();
        using var client = new HttpClient(new VocabularyHandOffHandler(network));
        var sent = await Record.ExceptionAsync(() => client.GetAsync("https://vocabulary-canary.example.invalid/"));
        var refused = VocabularyHandOffRefusedException.Find(sent);
        Assert.NotNull(refused);
        Assert.False(refused.Admitted);
        Assert.Empty(network.Sent);

        // The Copilot agent refuses before it creates a session: nothing at all reaches the runtime.
        await using var runtime = new FakeCopilotRuntime();
        await using var copilot = new GitHub.Copilot.CopilotClient(new GitHub.Copilot.CopilotClientOptions
        {
            Connection = GitHub.Copilot.RuntimeConnection.ForUri(runtime.Url),
        });
        await copilot.StartAsync();
        var calls = runtime.Requests.Count;
        var gated = GitHubCopilotAgentFactory.Create(copilot, "instructions " + LibraryCanary, null, "ScribeCleanup");
        var run = await Record.ExceptionAsync(() => gated.RunAsync(Dictated));
        Assert.False(VocabularyHandOffRefusedException.Find(run)?.Admitted ?? true);
        Assert.Equal(calls, runtime.Requests.Count);

        // A streamed run could not be ordered against a revocation at all, so it is refused whatever the admission.
        using (new CleanupAdmission(CleanupRequestKind.Dictation, AiVocabularyScope.None, null).Enter())
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                await foreach (var update in gated.RunStreamingAsync(Dictated))
                {
                    Assert.Fail($"A streamed run produced {update}.");
                }
            });
        }

        Assert.Equal(calls, runtime.Requests.Count);
    }

    private const string UsageInsightPrompt = Scribe.Core.Diagnostics.UsageInsight.SystemPrompt;
}
