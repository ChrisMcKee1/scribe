using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Tests.Libraries.Storage;
using Scribe.Core.Tests.Vocabulary;
using Scribe.Core.Vocabulary;

namespace Scribe.Core.Tests.Libraries.Integration;

/// <summary>
/// Contract 9.4's W-V checks with the real source (the integration's step 3): stream W-V's publisher, dictation pass and
/// admission point over the library service itself (J's <see cref="DictionaryLibraryService"/> with the real parts),
/// where W-V's own tests use a source that applies the contract. A library the service holds back and restores at the same
/// generation is followed by dictation, which runs on the personal dictionary alone meanwhile; a dictation keeps the one
/// generation it was admitted with; every request is handed over only through the service's <c>TryHandOff</c>, so one
/// admitted under a scope a Save narrowed is held back without a failure, and a one-off completion goes only while both its
/// scope and its recipient hold; and every request the admitted pipeline sends to the Responses surface says store=false.
/// The cleanup service runs over W-V's canary network; nothing leaves the process.
/// </summary>
public sealed class LibraryVocabularyRealSourceTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const string Dictated = "please ask zeb ra quill about lan tern ridge";

    // Letters, spaces and a parenthesis only, which JSON escaping leaves as they are, so a request body can never hide them.
    private const string LibraryTerm = "Zebraquill (transcribed as";
    private const string DictionaryTerm = "Lanternridge (transcribed as";
    private const string Summary = "Dictations: 3\nWords: 40\nActive days: 2\nRecurring terms:\n- Zebraquill: 3 dictations";

    private readonly LibraryStorageFixture _fixture = new() { RealParts = true };

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task A_library_the_service_holds_back_and_restores_at_the_same_generation_is_followed_by_dictation()
    {
        // A library on and sent to AI cleanup since the first start, then a fresh process that cannot list the pending
        // manifests (J's A15): the service holds every library back at the stored generation.
        CommitTeam();
        _fixture.Restart();
        var files = new FaultingFileSystem
        {
            EnumerateFault = (directory, pattern) =>
                IsJournal(directory) && pattern.EndsWith(LibraryJournalNames.ManifestSuffix, StringComparison.OrdinalIgnoreCase)
                    ? FaultingFileSystem.SharingViolation()
                    : null,
        };
        var service = _fixture.Service(files);
        var (dictionary, processor) = PersonalDictionary();
        using var publisher = new VocabularyPublisher(service, dictionary, processor, NullLogger<VocabularyPublisher>.Instance, work => work());
        await publisher.StartAsync().WaitAsync(Bound);
        await using var harness = new VocabularyCleanupHarness(service);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom());
        var dictation = new DictationPostProcessor(processor);

        // Held back: no library rules and an empty scope. The dictation runs on the personal dictionary alone, and its
        // request, which carries no library term, is handed over by the service's gate with nothing failing.
        var heldBack = publisher.Current;
        Assert.Empty(heldBack.Libraries.Entries);
        Assert.Empty(heldBack.AiScope.PermittedLibraryIds);
        Assert.Equal(_fixture.StoredGeneration, heldBack.Libraries.Generation);
        var first = await DictateAsync(harness, dictation, heldBack);
        Assert.Equal(CleanupOutcome.Unchanged, first.Cleaned.Outcome);
        Assert.Null(first.Cleaned.FailureReason);
        Assert.False(first.Sent.Carries("Zebraquill"));
        Assert.True(first.Sent.Carries(DictionaryTerm));
        Assert.Equal("please ask zeb ra quill about Lanternridge", first.Written);

        // The listing works again, and the next load recovers: the service republishes the library at the same library
        // generation, and the publisher builds from it all the same; the next dictation has the library back.
        files.EnumerateFault = null;
        service.LoadCatalog();
        var restored = publisher.Current;
        Assert.True(restored.Number > heldBack.Number, "The restoration at the same generation was dropped.");
        Assert.Equal(heldBack.Libraries.Generation, restored.Libraries.Generation);
        Assert.Contains("team", restored.AiScope.PermittedLibraryIds);
        var second = await DictateAsync(harness, dictation, restored);
        Assert.Equal(CleanupOutcome.Unchanged, second.Cleaned.Outcome);
        Assert.True(second.Sent.Carries(LibraryTerm));
        Assert.True(second.Sent.Carries(DictionaryTerm));
        Assert.Equal("please ask Zebraquill about Lanternridge", second.Written);
    }

    [Fact]
    public async Task A_dictation_admitted_before_a_library_Save_keeps_its_generation_and_the_service_holds_its_request_back()
    {
        CommitTeam();
        var service = _fixture.Service();
        var (dictionary, processor) = PersonalDictionary();
        using var publisher = new VocabularyPublisher(service, dictionary, processor, NullLogger<VocabularyPublisher>.Instance, work => work());
        await publisher.StartAsync().WaitAsync(Bound);
        await using var harness = new VocabularyCleanupHarness(service);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom());
        var dictation = new DictationPostProcessor(processor);
        var admitted = publisher.Current;
        Assert.Contains("team", admitted.AiScope.PermittedLibraryIds);

        // A Save takes the library's AI permission away and keeps it on for this PC; its completion publishes, and the
        // publisher builds the next generation from what the service publishes.
        TakeAiPermission(service, "team");
        var after = publisher.Current;
        Assert.True(after.Number > admitted.Number, "The Save's publication never reached the publisher.");
        Assert.DoesNotContain("team", after.AiScope.PermittedLibraryIds);
        Assert.Contains(after.Libraries.Entries, entry => entry.Replacement == "Zebraquill");

        // The dictation admitted before the Save keeps its own generation: its request, under the scope the Save narrowed,
        // is held back by the service's gate without a failure and never reaches the network, and its dictionary pass still
        // writes with the rules it was admitted with.
        var sentBefore = harness.Network.Sent.Count;
        var straddling = await harness.Service.Admit(admitted.Cleanup).CleanAsync(Dictated).WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Skipped, straddling.Outcome);
        Assert.Null(straddling.FailureReason);
        Assert.Equal(Dictated, straddling.Text);
        Assert.Equal(sentBefore, harness.Network.Sent.Count);
        dictation.Use(admitted);
        Assert.Equal("please ask Zebraquill about Lanternridge", dictation.ProcessDetailed(straddling.Text, Dictated).Text);

        // A dictation admitted after it is handed over, with the dictionary's term and without the library's, and the
        // library still writes on this PC.
        var next = await DictateAsync(harness, dictation, after);
        Assert.Equal(CleanupOutcome.Unchanged, next.Cleaned.Outcome);
        Assert.False(next.Sent.Carries("Zebraquill"));
        Assert.True(next.Sent.Carries(DictionaryTerm));
        Assert.Equal("please ask Zebraquill about Lanternridge", next.Written);
    }

    [Fact]
    public async Task A_one_off_completion_goes_only_while_the_service_permits_its_scope_and_cleanup_serves_its_recipient()
    {
        CommitTeam();
        var service = _fixture.Service();
        await using var harness = new VocabularyCleanupHarness(service);
        harness.Network.Respond = (request, _) => Task.FromResult(CanaryNetwork.Chat("You dictate about the ridge."));
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom());
        var recipient = harness.Service.Recipient!;
        var reportScope = service.Current.AiScope;
        Assert.Contains("team", reportScope.PermittedLibraryIds);

        // Under the scope the service publishes, the insight goes.
        var completed = await harness.Service.CompleteAsync(UsageInsight.SystemPrompt, Summary, recipient, reportScope).WaitAsync(Bound);
        Assert.Equal(ScopedCompletionOutcome.Completed, completed.Outcome);
        Assert.Equal(1, completed.RequestsHandedOver);

        // Once a Save takes the library's permission away, the same report's scope is held back and nothing is sent.
        TakeAiPermission(service, "team");
        var sentBefore = harness.Network.Sent.Count;
        var narrowed = await harness.Service.CompleteAsync(UsageInsight.SystemPrompt, Summary, recipient, reportScope).WaitAsync(Bound);
        Assert.Equal(ScopedCompletionOutcome.LibraryScopeNarrowed, narrowed.Outcome);
        Assert.True(narrowed.NothingSent);
        Assert.Equal(sentBefore, harness.Network.Sent.Count);

        // A report built now goes again; but not to a recipient cleanup no longer serves.
        var current = await harness.Service.CompleteAsync(UsageInsight.SystemPrompt, Summary, recipient, service.Current.AiScope).WaitAsync(Bound);
        Assert.Equal(ScopedCompletionOutcome.Completed, current.Outcome);
        await harness.ConfigureAndWaitAsync(CleanupHarness.Custom("https://another-vocabulary-canary.example.invalid/v1"));
        sentBefore = harness.Network.Sent.Count;
        var moved = await harness.Service.CompleteAsync(UsageInsight.SystemPrompt, Summary, recipient, service.Current.AiScope).WaitAsync(Bound);
        Assert.Equal(ScopedCompletionOutcome.RecipientChanged, moved.Outcome);
        Assert.True(moved.NothingSent);
        Assert.Equal(sentBefore, harness.Network.Sent.Count);
    }

    [Fact]
    public async Task Every_request_the_admitted_pipeline_sends_to_the_responses_surface_through_the_real_service_says_store_false()
    {
        CommitTeam();
        var service = _fixture.Service();
        var (dictionary, processor) = PersonalDictionary();
        using var publisher = new VocabularyPublisher(service, dictionary, processor, NullLogger<VocabularyPublisher>.Instance, work => work());
        await publisher.StartAsync().WaitAsync(Bound);
        await using var harness = new VocabularyCleanupHarness(service);
        var retries = new GatedRetryPolicy(maxRetries: 1);
        harness.RetryPolicy = retries;
        var dictationAttempts = 0;
        harness.Network.Respond = (request, _) => Task.FromResult(
            request.TranscriptOf() == Dictated && Interlocked.Increment(ref dictationAttempts) == 1
                ? CanaryNetwork.Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"busy\"}}")
                : request.TranscriptOf() == Dictated || request.IsProbe
                    ? CanaryNetwork.Echo(request)
                    : CanaryNetwork.Responses("You dictate about the ridge."));
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Azure());

        // The readiness probe.
        var probe = Assert.Single(harness.Network.Sent);
        Assert.EndsWith("/openai/v1/responses", probe.Path, StringComparison.Ordinal);
        AssertStoreFalse(probe.Body);

        // A dictation admitted with the service's vocabulary: its first attempt, answered busy, and the client's own retry,
        // each handed over by the service's gate and each carrying the library's term.
        var waiting = retries.Waiting.Task;
        var cleaning = harness.Service.Admit(publisher.Current.Cleanup).CleanAsync(Dictated);
        (await waiting.WaitAsync(Bound)).SetResult();
        Assert.Equal(CleanupOutcome.Unchanged, (await cleaning.WaitAsync(Bound)).Outcome);
        var attempts = harness.Network.Sent.Where(request => request.TranscriptOf() == Dictated).ToList();
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, request =>
        {
            Assert.EndsWith("/openai/v1/responses", request.Path, StringComparison.Ordinal);
            Assert.True(request.Carries(LibraryTerm), "The admitted glossary never reached the request, so this proved nothing.");
            AssertStoreFalse(request.Body);
        });

        // A usage insight under the service's scope.
        var insight = await harness.Service
            .CompleteAsync(UsageInsight.SystemPrompt, Summary, harness.Service.Recipient!, service.Current.AiScope)
            .WaitAsync(Bound);
        Assert.Equal(ScopedCompletionOutcome.Completed, insight.Outcome);
        var completion = Assert.Single(harness.Network.Sent, request => request.Carries("Zebraquill") && request.TranscriptOf() != Dictated);
        Assert.EndsWith("/openai/v1/responses", completion.Path, StringComparison.Ordinal);
        AssertStoreFalse(completion.Body);
    }

    // A custom library on and sent to AI cleanup, as the first start records one that existed at the upgrade.
    private void CommitTeam()
    {
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("zeb ra quill", "Zebraquill")));
        _fixture.SaveEnabled("team");
        var catalog = _fixture.Service().LoadCatalog();
        Assert.Equal(1, catalog.Generation);
        Assert.Contains("team", catalog.LocalState.EnabledIds);
    }

    // The personal dictionary, over the fixture's database, and the post-processor dictation compiles it with.
    private (DictionaryRepository Dictionary, TextPostProcessor Processor) PersonalDictionary()
    {
        var dictionary = new DictionaryRepository(_fixture.Database);
        dictionary.AddRange([DictionaryEntry.New("lan tern ridge", "Lanternridge")]);
        return (dictionary, new TextPostProcessor(dictionary, NullLogger<TextPostProcessor>.Instance));
    }

    // A whole library Save through the shell's sequence (prepare, the settings commit, completion) that keeps the library on
    // and takes its AI permission away.
    private void TakeAiPermission(DictionaryLibraryService service, string id)
    {
        var catalog = service.LoadCatalog();
        var (_, outcome, failure) = Changes.Save(
            service, _fixture.Settings, Changes.Of(catalog, state: Changes.With(catalog.LocalState, ai: [(id, false)])));
        Assert.Null(failure);
        Assert.Equal(LibrarySaveStatus.Applied, outcome!.Status);
    }

    // One dictation as the controller runs it: admitted with a generation, cleaned under it, then its dictionary pass over
    // the same generation. Returns the request that reached the network and what would be typed.
    private static async Task<(CleanupResult Cleaned, SentRequest Sent, string Written)> DictateAsync(
        VocabularyCleanupHarness harness, DictationPostProcessor dictation, VocabularyGeneration admitted)
    {
        var sentBefore = harness.Network.Sent.Count;
        var cleaned = await harness.Service.Admit(admitted.Cleanup).CleanAsync(Dictated).WaitAsync(Bound);
        var sent = Assert.Single(harness.Network.Sent.Skip(sentBefore), request => !request.IsProbe);
        dictation.Use(admitted);
        return (cleaned, sent, dictation.ProcessDetailed(cleaned.Text, Dictated).Text);
    }

    private bool IsJournal(string directory) =>
        string.Equals(
            Path.GetFullPath(directory).TrimEnd('\\'), Path.GetFullPath(_fixture.Paths.LibraryJournalDir).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    private static void AssertStoreFalse(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.TryGetProperty("store", out var store), $"No \"store\" field was sent: {body}");
        Assert.Equal(JsonValueKind.False, store.ValueKind);
    }
}
