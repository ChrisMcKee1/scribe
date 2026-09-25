using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Lifecycle;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.Tests.CleanupLogging;
using Scribe.Core.Vocabulary;
using static Scribe.Core.Tests.Vocabulary.TestVocabularies;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// The vocabulary generation every dictation is admitted with (plan 3.8, R6): built from one committed library snapshot
/// and one read of the personal dictionary, published complete in one step, and refreshed by coalescing requests. Each
/// case drives the real publisher and post-processor over scripted inputs, with the builds scheduled by the test.
/// </summary>
public sealed class VocabularyPublisherTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static readonly LibraryVocabulary TwoLibraries = Of(
        4,
        new Library("team", H1, true, Entry("kes trel", "Kestrel")),
        new Library("private", H2, false, Entry("quill moor", "Quillmoor")));

    [Fact]
    public async Task StartAsync_builds_the_first_generation_through_the_scheduler_from_one_snapshot_and_the_dictionary()
    {
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([Entry("harbour", "Harbour")]);
        var queued = new List<Action>();
        using var publisher = Publisher(source, dictionary, Processor(dictionary), queued.Add);

        // Nothing is read on the caller's thread: the first build is queued like any other, and the answer waits for it.
        var starting = publisher.StartAsync();
        Assert.False(starting.IsCompleted);
        Assert.Equal(0, source.CurrentReads);
        Assert.Same(VocabularyGeneration.Empty, publisher.Current);
        Assert.Single(queued)();

        var answer = await starting.WaitAsync(Bound);
        Assert.Equal(VocabularyRefreshOutcome.Applied, answer.Outcome);
        var first = answer.Generation;
        Assert.Same(first, publisher.Current);
        Assert.Equal(1, source.CurrentReads);
        Assert.Same(TwoLibraries, first.Libraries);
        Assert.Equal(["harbour"], first.Dictionary.Select(entry => entry.Pattern));

        // Local rules: the dictionary and every enabled library, the one kept from AI cleanup included.
        Assert.Equal(3, first.Rules.Count);
        var processor = Processor(dictionary);
        Assert.Equal("Harbour Kestrel Quillmoor", processor.ProcessDetailed("harbour kes trel quill moor", null, first.Rules).Text);

        // The glossary input: the dictionary composed with only the libraries AI cleanup may have, in the pipeline's order.
        Assert.Equal(
            CleanupPrompt.ComposeVocabulary(first.Dictionary, TwoLibraries.AiEntries),
            first.GlossaryEntries);
        Assert.Equal(["harbour", "kes trel"], first.GlossaryEntries.Select(entry => entry.Pattern));
        Assert.Same(TwoLibraries.AiScope, first.AiScope);
        Assert.Same(TwoLibraries.AiScope, first.Cleanup.Scope);
        Assert.DoesNotContain("Quillmoor", first.Cleanup.GlossaryFor(CleanupPrompt.MaxGlossaryTermsCloud), StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_production_the_first_generation_is_read_on_a_pool_thread_and_the_caller_is_never_held()
    {
        // The public constructor, as the app's container builds it. The first read of the library source can load a cold
        // catalog; holding that read shows StartAsync returned to its caller before it finished, and on another thread.
        // The caller waits here without awaiting, so its thread cannot be the one that reads; the hook never holds the
        // caller's own thread, so a build on it shows as a completed start instead of a wait.
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([Entry("harbour", "Harbour")]);
        using var readStarted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var caller = Environment.CurrentManagedThreadId;
        var reader = 0;
        source.ReadingCurrent = () =>
        {
            source.ReadingCurrent = null;
            reader = Environment.CurrentManagedThreadId;
            readStarted.Set();
            if (reader != caller)
            {
                release.Wait(Bound);
            }
        };
        using var publisher = new VocabularyPublisher(source, dictionary, Processor(dictionary), NullLogger<VocabularyPublisher>.Instance);

        var starting = publisher.StartAsync();
        Assert.True(readStarted.Wait(Bound), "The first generation's build never read the library source.");

        Assert.NotEqual(caller, reader);
        Assert.False(starting.IsCompleted);
        Assert.Same(VocabularyGeneration.Empty, publisher.Current);

        release.Set();
        var answer = await starting.WaitAsync(Bound);
        Assert.True(answer.Applied);
        Assert.Same(answer.Generation, publisher.Current);
        Assert.Same(TwoLibraries, answer.Generation.Libraries);
    }

    [Fact]
    public async Task Requests_that_arrive_while_a_build_waits_to_run_coalesce_into_one_newer_generation()
    {
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([]);
        var queued = new List<Action>();
        using var publisher = Publisher(source, dictionary, Processor(dictionary), queued.Add);
        var first = await StartQueuedAsync(publisher, queued);

        dictionary.Entries = [Entry("harbour", "Harbour")];
        var a = publisher.RefreshAsync();
        dictionary.Entries = [Entry("harbour", "Harbour"), Entry("lantern", "Lantern")];
        var b = publisher.RefreshAsync();
        var c = publisher.RefreshAsync();

        // One builder for all three, which has not run yet, so none of them is answered.
        var builder = Assert.Single(queued);
        Assert.False(a.IsCompleted || b.IsCompleted || c.IsCompleted);
        Assert.Same(first, publisher.Current);

        builder();

        // One build, reading its inputs after the newest request, answers all three with the same generation.
        var answers = new[] { await a.WaitAsync(Bound), await b.WaitAsync(Bound), await c.WaitAsync(Bound) };
        var built = answers[0].Generation;
        Assert.All(answers, answer => Assert.Equal(VocabularyRefreshOutcome.Applied, answer.Outcome));
        Assert.All(answers, answer => Assert.Same(built, answer.Generation));
        Assert.Same(built, publisher.Current);
        Assert.Equal(first.Number + 1, built.Number);
        Assert.Equal(["harbour", "lantern"], built.Dictionary.Select(entry => entry.Pattern));
        Assert.Equal(2, source.CurrentReads);

        // The builder finished with them: the next request schedules another.
        var d = publisher.RefreshAsync();
        Assert.Equal(2, queued.Count);
        queued[1]();
        Assert.Equal(built.Number + 1, (await d.WaitAsync(Bound)).Generation.Number);
    }

    [Fact]
    public async Task A_request_made_during_a_build_is_answered_by_the_next_build_never_by_one_that_read_its_inputs_before()
    {
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([]);
        using var publisher = Publisher(source, dictionary, Processor(dictionary), work => _ = Task.Run(work));
        await publisher.StartAsync().WaitAsync(Bound);

        // Hold the next build once it has read its inputs, before it publishes.
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        dictionary.Read = () =>
        {
            dictionary.Read = null;
            reading.TrySetResult();
            release.Wait(Bound);
        };

        var before = publisher.RefreshAsync();
        await reading.Task.WaitAsync(Bound);
        dictionary.Entries = [Entry("lantern", "Lantern")];
        var during = publisher.RefreshAsync();
        release.Set();

        var answeredBefore = (await before.WaitAsync(Bound)).Generation;
        var answeredDuring = await during.WaitAsync(Bound);
        Assert.Empty(answeredBefore.Dictionary);
        Assert.Equal(VocabularyRefreshOutcome.Applied, answeredDuring.Outcome);
        Assert.Equal(["lantern"], answeredDuring.Generation.Dictionary.Select(entry => entry.Pattern));
        Assert.True(answeredDuring.Generation.Number > answeredBefore.Number);
        Assert.Same(answeredDuring.Generation, publisher.Current);
    }

    [Fact]
    public async Task A_start_while_a_build_runs_starts_no_second_build_and_is_answered_by_the_build_after_it()
    {
        // One builder at a time, the first generation's included: a start that arrives while a build runs (a library
        // publication raised before the app started the publisher, say) is queued behind it, so no two builds overlap and
        // none can publish over a newer one.
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([Entry("harbour", "Harbour")]);
        var queued = new List<Action>();
        using var publisher = Publisher(source, dictionary, Processor(dictionary), queued.Add);

        var early = publisher.RefreshAsync();
        var builder = Assert.Single(queued);
        queued.Clear();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        dictionary.Read = () =>
        {
            dictionary.Read = null;
            reading.TrySetResult();
            release.Wait(Bound);
        };
        var running = Task.Run(builder);
        await reading.Task.WaitAsync(Bound);

        dictionary.Entries = [Entry("lantern", "Lantern")];
        var starting = publisher.StartAsync();
        Assert.Empty(queued);
        Assert.False(starting.IsCompleted);
        Assert.Equal(1, source.CurrentReads);
        release.Set();
        await running.WaitAsync(Bound);

        var earlyAnswer = await early.WaitAsync(Bound);
        var startAnswer = await starting.WaitAsync(Bound);
        Assert.Equal(["harbour"], earlyAnswer.Generation.Dictionary.Select(entry => entry.Pattern));
        Assert.Equal(VocabularyRefreshOutcome.Applied, startAnswer.Outcome);
        Assert.Equal(["lantern"], startAnswer.Generation.Dictionary.Select(entry => entry.Pattern));
        Assert.True(startAnswer.Generation.Number > earlyAnswer.Generation.Number);
        Assert.Same(startAnswer.Generation, publisher.Current);
        Assert.Equal(2, source.CurrentReads);
    }

    [Fact]
    public async Task A_change_is_acknowledged_only_once_its_generation_is_published_and_that_is_the_generation_the_next_admission_takes()
    {
        // The Settings Save's case, with the build held: a dictionary correction is stored, the application asks for a
        // generation, and the answer is what the Save awaits before it says the settings are saved.
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([Entry("harbour", "Harbour")]);
        var queued = new List<Action>();
        using var publisher = Publisher(source, dictionary, Processor(dictionary), queued.Add);
        var before = await StartQueuedAsync(publisher, queued);

        dictionary.Entries = [Entry("harbour", "Harbour"), Entry("quill more", "Quillmoor")];
        var acknowledgement = publisher.RefreshAsync();

        // Until the build runs nothing is acknowledged, and a dictation admitted now takes the generation before the change,
        // which is what an acknowledgement given at this point would have claimed was in effect.
        Assert.False(acknowledgement.IsCompleted);
        Assert.Same(before, Admit(publisher));

        Assert.Single(queued)();
        var answer = await acknowledgement.WaitAsync(Bound);

        // Acknowledged: the generation it names carries the correction, and it is exactly what the next admission takes.
        Assert.Equal(VocabularyRefreshOutcome.Applied, answer.Outcome);
        Assert.True(answer.Applied);
        Assert.Contains(answer.Generation.Dictionary, entry => entry.Replacement == "Quillmoor");
        Assert.Same(answer.Generation, Admit(publisher));
        Assert.Equal("Quillmoor", Processor(dictionary).ProcessDetailed("quill more", null, Admit(publisher).Rules).Text);
    }

    [Fact]
    public async Task A_build_that_cannot_read_the_dictionary_answers_not_applied_with_the_generation_dictation_keeps()
    {
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([Entry("harbour", "Harbour")]);
        var log = new CapturingLogger<VocabularyPublisher>();
        var queued = new List<Action>();
        using var publisher = new VocabularyPublisher(source, dictionary, Processor(dictionary), log, queued.Add);
        var before = await StartQueuedAsync(publisher, queued);

        // Saved but not applied: the build for the request could not read the dictionary, so the next dictation keeps the
        // generation from before the change, and the answer says so rather than that the change is in effect.
        dictionary.Failure = new InvalidOperationException("database is locked at C:\\Users\\canary\\scribe.db");
        var failed = publisher.RefreshAsync();
        Assert.Single(queued)();
        var answered = await failed.WaitAsync(Bound);

        Assert.Equal(VocabularyRefreshOutcome.NotApplied, answered.Outcome);
        Assert.False(answered.Applied);
        Assert.Same(before, answered.Generation);
        Assert.Same(before, publisher.Current);
        Assert.Same(before, Admit(publisher));
        Assert.Contains(log.Entries, entry => entry.Message.StartsWith("A vocabulary generation could not be built", StringComparison.Ordinal));
        Assert.DoesNotContain("canary", log.AllText, StringComparison.OrdinalIgnoreCase);

        // The next request, once the dictionary can be read, is applied.
        dictionary.Failure = null;
        var retried = publisher.RefreshAsync();
        queued[1]();
        Assert.Equal(VocabularyRefreshOutcome.Applied, (await retried.WaitAsync(Bound)).Outcome);
    }

    [Fact]
    public async Task A_request_the_publisher_can_no_longer_build_for_is_answered_stopped_never_applied()
    {
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([Entry("harbour", "Harbour")]);
        var queued = new List<Action>();
        var publisher = Publisher(source, dictionary, Processor(dictionary), queued.Add);
        var before = await StartQueuedAsync(publisher, queued);

        var waiting = publisher.RefreshAsync();
        publisher.Dispose();
        var answer = await waiting.WaitAsync(Bound);
        Assert.Equal(VocabularyRefreshOutcome.Stopped, answer.Outcome);
        Assert.Same(before, answer.Generation);
        Assert.Equal(VocabularyRefreshOutcome.Stopped, (await publisher.RefreshAsync().WaitAsync(Bound)).Outcome);
        Assert.Throws<ObjectDisposedException>(() => { _ = publisher.StartAsync(); });

        // A scheduler that refuses the build answers its requests too, as not applied.
        var refusing = Publisher(source, dictionary, Processor(dictionary), _ => throw new InvalidOperationException("no threads"));
        using (refusing)
        {
            Assert.Equal(VocabularyRefreshOutcome.NotApplied, (await refusing.StartAsync().WaitAsync(Bound)).Outcome);
            Assert.Same(VocabularyGeneration.Empty, refusing.Current);
        }
    }

    [Fact]
    public async Task A_commit_between_a_builds_two_reads_gives_one_transient_mix_and_the_next_generation_is_consistent()
    {
        // A Settings save that commits libraries and dictionary together, landing after a running build read the library
        // snapshot and before it read the dictionary: that build publishes the old libraries with the new dictionary. Each
        // commit asks for a generation after it lands (the library source's Changed, the save's application), so the build
        // after it reads both new, and that consistent generation is the one the save's acknowledgement names.
        var oldLibraries = Of(4, new Library("team", H1, true, Entry("kes trel", "Kestrel")));
        var newLibraries = Of(5, new Library("team", H2, true, Entry("kes trel", "Kestrelsaved")));
        var source = new TestVocabularySource(oldLibraries);
        var dictionary = new ScriptedDictionary([Entry("harbour", "Harbour")]);
        using var publisher = Publisher(source, dictionary, Processor(dictionary), work => _ = Task.Run(work));
        await publisher.StartAsync().WaitAsync(Bound);

        // Hold the next build after its library read, before its dictionary read.
        var between = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        dictionary.Reading = () =>
        {
            dictionary.Reading = null;
            between.TrySetResult();
            release.Wait(Bound);
        };
        var straddling = publisher.RefreshAsync();
        await between.Task.WaitAsync(Bound);

        // The commit lands between the two reads, and each source asks for a generation after it, as it does in the app.
        source.Publish(newLibraries);
        dictionary.Entries = [Entry("harbour", "Harbour"), Entry("lan tern", "Lantern")];
        var saved = publisher.RefreshAsync();
        release.Set();

        // The build that straddled the commit is a transient mix: the old libraries, the new dictionary. It is whole in
        // itself, and its glossary and the scope that judges it come from one snapshot, the old one, whose content the
        // published vocabulary no longer covers: AI cleanup stays bound to the content that snapshot permitted.
        var mixed = (await straddling.WaitAsync(Bound)).Generation;
        Assert.Same(oldLibraries, mixed.Libraries);
        Assert.Equal(["harbour", "lan tern"], mixed.Dictionary.Select(entry => entry.Pattern));
        Assert.Same(oldLibraries.AiScope, mixed.Cleanup.Scope);
        Assert.Contains("Kestrel (transcribed as", mixed.Cleanup.GlossaryFor(CleanupPrompt.MaxGlossaryTermsCloud), StringComparison.Ordinal);
        Assert.False(source.Current.AiScope.Covers(mixed.AiScope));
        Assert.False(source.TryHandOff(mixed.AiScope, () => Assert.Fail("A request of the mixed generation was handed over.")));

        // The mix never persists: the next generation, the one the save's acknowledgement names, reads both new.
        var consistent = await saved.WaitAsync(Bound);
        Assert.Equal(VocabularyRefreshOutcome.Applied, consistent.Outcome);
        Assert.True(consistent.Generation.Number > mixed.Number);
        Assert.Same(newLibraries, consistent.Generation.Libraries);
        Assert.Equal(["harbour", "lan tern"], consistent.Generation.Dictionary.Select(entry => entry.Pattern));
        Assert.Same(newLibraries.AiScope, consistent.Generation.Cleanup.Scope);
        Assert.Same(consistent.Generation, publisher.Current);
        Assert.Equal(
            "Kestrelsaved at the Harbour Lantern",
            Processor(dictionary).ProcessDetailed("kes trel at the harbour lan tern", null, consistent.Generation.Rules).Text);
    }

    [Fact]
    public async Task A_new_library_vocabulary_and_a_dictionary_reload_each_ask_for_a_new_generation()
    {
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([]);
        var processor = Processor(dictionary);
        var queued = new List<Action>();
        using var publisher = Publisher(source, dictionary, processor, queued.Add);
        await StartQueuedAsync(publisher, queued);

        var published = new List<VocabularyGeneration>();
        publisher.Published += published.Add;

        var next = Of(5, new Library("team", H1, true, Entry("kes trel", "KESTREL")));
        source.Publish(next);
        Assert.Single(queued)();
        Assert.Same(next, publisher.Current.Libraries);

        // A reload of the post-processor is the signal too, for whatever stores the dictionary and reloads it.
        dictionary.Entries = [Entry("harbour", "Harbour")];
        processor.Reload();
        Assert.Equal(2, queued.Count);
        queued[1]();
        Assert.Equal(["harbour"], publisher.Current.Dictionary.Select(entry => entry.Pattern));

        // Reloading the snippets alone is not a dictionary change.
        processor.ReloadSnippets();
        Assert.Equal(2, queued.Count);
        Assert.Equal(2, published.Count);
    }

    [Fact]
    public async Task A_dictionary_only_reload_keeps_the_library_vocabulary_it_had()
    {
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([]);
        using var publisher = Publisher(source, dictionary, Processor(dictionary), work => work());
        var before = (await publisher.StartAsync().WaitAsync(Bound)).Generation;

        dictionary.Entries = [Entry("harbour", "Harbour")];
        var after = (await publisher.RefreshAsync().WaitAsync(Bound)).Generation;

        Assert.Same(before.Libraries, after.Libraries);
        Assert.Same(before.AiScope, after.AiScope);
        Assert.Equal(before.Rules.Count + 1, after.Rules.Count);
        Assert.Equal("Harbour Kestrel Quillmoor", Processor(dictionary).ProcessDetailed("harbour kes trel quill moor", null, after.Rules).Text);
    }

    [Fact]
    public async Task A_library_vocabulary_that_cannot_be_read_counts_as_none_and_the_dictionary_still_applies()
    {
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([Entry("harbour", "Harbour")]);
        var log = new CapturingLogger<VocabularyPublisher>();
        using var publisher = new VocabularyPublisher(source, dictionary, Processor(dictionary), log, work => work());
        await publisher.StartAsync().WaitAsync(Bound);

        source.CurrentFailure = new IOException("C:\\Users\\canary\\libraries\\team.csv is locked");
        var answer = await publisher.RefreshAsync().WaitAsync(Bound);
        var generation = answer.Generation;

        // Fail closed for AI cleanup, the personal dictionary alone for local rules, as the post-processor falls back.
        Assert.Equal(VocabularyRefreshOutcome.Applied, answer.Outcome);
        Assert.Same(LibraryVocabulary.Empty, generation.Libraries);
        Assert.Same(AiVocabularyScope.None, generation.AiScope);
        Assert.Equal(["harbour"], generation.GlossaryEntries.Select(entry => entry.Pattern));
        Assert.Equal(1, generation.Rules.Count);
        Assert.DoesNotContain("canary", log.AllText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_publication_log_line_carries_counts_and_generations_only()
    {
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([Entry("harbour", "Harbour")]);
        var log = new CapturingLogger<VocabularyPublisher>();
        using var publisher = new VocabularyPublisher(source, dictionary, Processor(dictionary), log, work => work());
        await publisher.StartAsync().WaitAsync(Bound);
        await publisher.RefreshAsync().WaitAsync(Bound);

        var line = Assert.Single(log.Entries, entry => entry.Message.StartsWith("Vocabulary generation 2 published", StringComparison.Ordinal));
        Assert.Contains("1 dictionary entr(ies) and 2 library entr(ies) compiled into 3 rule(s)", line.Message, StringComparison.Ordinal);
        Assert.Contains("1 library entr(ies) from 1 librar(ies) may go to AI cleanup (library generation 4)", line.Message, StringComparison.Ordinal);
        foreach (var secret in new[] { "harbour", "Harbour", "kes trel", "Kestrel", "quill moor", "Quillmoor", "team", "private" })
        {
            Assert.DoesNotContain(secret, log.AllText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Disposing_stops_the_subscriptions_and_answers_waiting_requests_with_the_generation_in_use()
    {
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([]);
        var processor = Processor(dictionary);
        var queued = new List<Action>();
        var publisher = Publisher(source, dictionary, processor, queued.Add);
        var current = await StartQueuedAsync(publisher, queued);
        Assert.Equal(1, source.ChangedSubscribers);
        var waiting = publisher.RefreshAsync();

        publisher.Dispose();

        Assert.Equal(0, source.ChangedSubscribers);
        var stopped = await waiting.WaitAsync(Bound);
        Assert.Equal(VocabularyRefreshOutcome.Stopped, stopped.Outcome);
        Assert.Same(current, stopped.Generation);
        Assert.Same(current, (await publisher.RefreshAsync().WaitAsync(Bound)).Generation);
        source.Publish(Of(6));
        processor.Reload();
        Assert.Single(queued);
        queued[0]();
        Assert.Same(current, publisher.Current);
    }

    [Fact]
    public void The_glossary_and_the_glossary_hint_count_the_same_list_in_the_same_order()
    {
        // A library kept from AI cleanup: its term is applied on this PC and never reaches the glossary. The hint takes
        // the AI entries already composed, which is exactly the list the glossary is composed from (contract 9.4).
        var libraries = Of(
            7,
            new Library("team", H1, true, Entry("kes trel", "Kestrel"), Entry("har bour", "Harbour")),
            new Library("private", H2, false, Entry("quill moor", "Quillmoor")));
        var rows = new[]
        {
            new DictionaryEntryBuilder.Row(0, "lantern", "Lantern", true, true),
            new DictionaryEntryBuilder.Row(0, "kes trel", "KESTREL", true, true),
        };
        var dictionary = new ScriptedDictionary([.. DictionaryEntryBuilder.Build(rows).Entries.OrderBy(entry => entry.Pattern, SqliteBinaryCollation.Instance)]);
        var generation = new VocabularyGeneration(1, dictionary.GetEnabled(), libraries, Processor(dictionary).Compile(dictionary.GetEnabled(), libraries.Entries));

        foreach (var (provider, style) in new[] { (CleanupProvider.AzureFoundry, CleanupPromptStyle.Auto), (CleanupProvider.FoundryLocal, CleanupPromptStyle.Auto) })
        {
            var budget = CleanupPrompt.GlossaryTermBudget(style, provider);
            var sent = GlossaryLines(generation.Cleanup.GlossaryFor(budget));
            var hint = GlossaryHint.Describe(new GlossaryHint.Input(rows, generation.Libraries.AiEntries, true, true, provider, style));

            Assert.Equal(["- KESTREL (transcribed as \"kes trel\")", "- Lantern", "- Harbour (transcribed as \"har bour\")"], sent);
            Assert.Contains($"receives all {sent.Count} terms as vocabulary", hint, StringComparison.Ordinal);

            // The control: the hint handed every enabled library's entries would count the excluded library too.
            var wrong = GlossaryHint.Describe(new GlossaryHint.Input(rows, generation.Libraries.Entries, true, true, provider, style));
            Assert.Contains($"receives all {sent.Count + 1} terms as vocabulary", wrong, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_dictation_post_processor_applies_the_generation_it_was_given_and_nothing_else()
    {
        var dictionary = new ScriptedDictionary([Entry("harbour", "Harbour")]);
        var processor = Processor(dictionary);
        var first = new VocabularyGeneration(1, dictionary.GetEnabled(), TwoLibraries, processor.Compile(dictionary.GetEnabled(), TwoLibraries.Entries));
        var newer = new VocabularyGeneration(2, [Entry("harbour", "HARBOUR")], LibraryVocabulary.Empty, processor.Compile([Entry("harbour", "HARBOUR")], []));
        var dictation = new DictationPostProcessor(processor);

        // Before a dictation names its generation there is nothing it may apply.
        Assert.Throws<InvalidOperationException>(() => dictation.ProcessDetailed("harbour"));

        dictation.Use(first);
        processor.Reload();
        Assert.Equal("Harbour Kestrel", dictation.ProcessDetailed("harbour kes trel").Text);
        Assert.Same(first, dictation.Generation);

        dictation.Use(newer);
        Assert.Equal("HARBOUR kes trel", dictation.ProcessDetailed("harbour kes trel").Text);
    }

    [Fact]
    public void The_rules_a_generation_compiles_are_the_post_processors_own_for_the_same_entries()
    {
        var dictionary = new ScriptedDictionary([Entry("azure", "Azure"), Entry("kes trel", "KESTREL")]);
        var libraries = new StubLibraries(Entry("kes trel", "Kestrel"), Entry("a p i m", "APIM"));
        var standalone = new TextPostProcessor(dictionary, NullLogger<TextPostProcessor>.Instance, snippets: null, libraries: libraries);
        var rules = standalone.Compile(dictionary.GetEnabled(), libraries.GetEnabledLibraryEntries());

        foreach (var sentence in new[] { "deploy a p i m to azure", "kes trel and a p i m", "nothing here" })
        {
            Assert.Equal(standalone.ProcessDetailed(sentence, sentence), standalone.ProcessDetailed(sentence, sentence, rules), ResultComparer.Instance);
        }
    }

    [Fact]
    public void Reloading_raises_the_reload_signal_after_the_rules_are_rebuilt_and_reloading_snippets_does_not()
    {
        var dictionary = new ScriptedDictionary([]);
        var processor = Processor(dictionary);
        var reloads = new List<long>();
        processor.Reloaded += reload =>
        {
            // Raised outside the post-processor's lock, with the rules already rebuilt.
            Assert.Equal("Harbour", processor.Process("harbour"));
            reloads.Add(reload);
        };

        dictionary.Entries = [Entry("harbour", "Harbour")];
        processor.Reload();
        processor.Reload([]);
        processor.ReloadSnippets();

        Assert.Equal([1L, 2L], reloads);
    }

    [Fact]
    public void The_not_applied_notice_says_what_was_saved_and_that_dictation_keeps_its_previous_vocabulary()
    {
        var notice = VocabularyNotice.SavedButNotApplied("Settings saved");

        Assert.Equal(
            "Settings saved, but dictation couldn't load the change yet and keeps its previous vocabulary until the next " +
            "change or a restart.",
            notice);
        Assert.StartsWith("Added \"Quillmoor\" to your dictionary, but", VocabularyNotice.SavedButNotApplied("Added \"Quillmoor\" to your dictionary"), StringComparison.Ordinal);
        Assert.DoesNotContain("will now", notice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\u2014', notice);
        Assert.DoesNotContain('\u2013', notice);
        Assert.Throws<ArgumentException>(() => VocabularyNotice.SavedButNotApplied(" "));
    }

    private static VocabularyPublisher Publisher(
        ILibraryVocabularySource source, IDictionaryRepository dictionary, ITextPostProcessor processor, Action<Action> schedule) =>
        new(source, dictionary, processor, NullLogger<VocabularyPublisher>.Instance, schedule);

    // Starts a publisher whose builds the test runs: the first build is queued like any other, run here, and its generation
    // returned with the queue left empty.
    private static async Task<VocabularyGeneration> StartQueuedAsync(VocabularyPublisher publisher, List<Action> queued)
    {
        var starting = publisher.StartAsync();
        Assert.Single(queued)();
        queued.Clear();
        return (await starting.WaitAsync(Bound)).Generation;
    }

    // A recording admitted the way the dictation controller admits one: its capture context takes the publisher's current
    // generation inside the lifecycle's admission, and that is the generation its cleanup and its dictionary pass use.
    private static VocabularyGeneration Admit(VocabularyPublisher publisher)
    {
        var lifecycle = new DictationLifecycle<VocabularyGeneration>(() => { }, () => { });
        var activation = lifecycle.TryBeginRecording(() => publisher.Current);
        Assert.Equal(ActivationDecision.Started, activation.Decision);
        return activation.Capture!;
    }

    private static TextPostProcessor Processor(IDictionaryRepository dictionary) =>
        new(dictionary, NullLogger<TextPostProcessor>.Instance);

    private static List<string> GlossaryLines(string? glossary) =>
        glossary is null ? [] : [.. glossary.Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal))];

    /// <summary>A dictionary whose enabled entries the test sets, or makes unreadable.</summary>
    internal sealed class ScriptedDictionary(IReadOnlyList<DictionaryEntry> entries) : IDictionaryRepository
    {
        public IReadOnlyList<DictionaryEntry> Entries { get; set; } = entries;

        public Exception? Failure { get; set; }

        /// <summary>Runs as each read begins, before it takes the entries, on the reading thread; a test holds a build here.</summary>
        public Action? Reading { get; set; }

        /// <summary>Runs after each read has taken the entries, on the reading thread; a test holds a build here.</summary>
        public Action? Read { get; set; }

        public IReadOnlyList<DictionaryEntry> GetEnabled()
        {
            Reading?.Invoke();
            if (Failure is { } failure)
            {
                throw failure;
            }

            var entries = Entries;
            Read?.Invoke();
            return entries;
        }

        public IReadOnlyList<DictionaryEntry> GetAll() => GetEnabled();

        public DictionaryEntry Add(DictionaryEntry entry) => throw new NotSupportedException();

        public IReadOnlyList<DictionaryEntry> AddRange(IReadOnlyList<DictionaryEntry> entries) => throw new NotSupportedException();

        public void Update(DictionaryEntry entry) => throw new NotSupportedException();

        public void Delete(long id) => throw new NotSupportedException();

        public void SaveAll(IReadOnlyList<DictionaryEntry> entries) => throw new NotSupportedException();

        public int SeedIfEmpty(IEnumerable<DictionaryEntry> entries) => throw new NotSupportedException();

        public int DisableUnmodifiedEntries(IEnumerable<DictionaryEntry> entries) => throw new NotSupportedException();
    }

    private sealed class StubLibraries(params DictionaryEntry[] entries) : IDictionaryLibraryService
    {
        public IReadOnlyList<DictionaryLibrary> GetLibraries() => [];

        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries() => entries;

        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries(IReadOnlyCollection<string> enabledIds) => entries;

        public DictionaryLibrary Import(string csv, string? suggestedName) => throw new NotSupportedException();

        public void Remove(string id) => throw new NotSupportedException();
    }

    private sealed class ResultComparer : IEqualityComparer<TextPostProcessingResult>
    {
        public static ResultComparer Instance { get; } = new();

        public bool Equals(TextPostProcessingResult? x, TextPostProcessingResult? y) =>
            x is not null && y is not null && x.Text == y.Text && x.Replacements.SequenceEqual(y.Replacements);

        public int GetHashCode(TextPostProcessingResult obj) => obj.Text.GetHashCode(StringComparison.Ordinal);
    }
}
