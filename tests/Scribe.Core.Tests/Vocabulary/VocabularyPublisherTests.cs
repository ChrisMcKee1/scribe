using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
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
    public void Start_publishes_the_first_generation_on_the_calling_thread_from_one_snapshot_and_the_dictionary()
    {
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([Entry("harbour", "Harbour")]);
        var queued = new List<Action>();
        using var publisher = Publisher(source, dictionary, Processor(dictionary), queued.Add);

        Assert.Same(VocabularyGeneration.Empty, publisher.Current);
        var first = publisher.Start();

        // Built inline, before Start returned: nothing was queued, and the generation is the one published.
        Assert.Empty(queued);
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
    public async Task Requests_that_arrive_while_a_build_waits_to_run_coalesce_into_one_newer_generation()
    {
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([]);
        var queued = new List<Action>();
        using var publisher = Publisher(source, dictionary, Processor(dictionary), queued.Add);
        var first = publisher.Start();

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
        var built = await a.WaitAsync(Bound);
        Assert.Same(built, await b.WaitAsync(Bound));
        Assert.Same(built, await c.WaitAsync(Bound));
        Assert.Same(built, publisher.Current);
        Assert.Equal(first.Number + 1, built.Number);
        Assert.Equal(["harbour", "lantern"], built.Dictionary.Select(entry => entry.Pattern));
        Assert.Equal(2, source.CurrentReads);

        // The builder finished with them: the next request schedules another.
        var d = publisher.RefreshAsync();
        Assert.Equal(2, queued.Count);
        queued[1]();
        Assert.Equal(built.Number + 1, (await d.WaitAsync(Bound)).Number);
    }

    [Fact]
    public async Task A_request_made_during_a_build_is_answered_by_the_next_build_never_by_one_that_read_its_inputs_before()
    {
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([]);
        using var publisher = Publisher(source, dictionary, Processor(dictionary), work => _ = Task.Run(work));
        publisher.Start();

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

        var answeredBefore = await before.WaitAsync(Bound);
        var answeredDuring = await during.WaitAsync(Bound);
        Assert.Empty(answeredBefore.Dictionary);
        Assert.Equal(["lantern"], answeredDuring.Dictionary.Select(entry => entry.Pattern));
        Assert.True(answeredDuring.Number > answeredBefore.Number);
        Assert.Same(answeredDuring, publisher.Current);
    }

    [Fact]
    public async Task A_build_that_read_older_inputs_never_publishes_over_a_newer_generation()
    {
        // Two builds overlap: Start builds on the caller's thread while a refresh builds on the pool. Start reads its
        // inputs first and is held there; the refresh reads newer inputs and publishes; then Start finishes. Its ticket
        // is older than the published generation's, so it must not replace it.
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([Entry("harbour", "Harbour")]);
        using var publisher = Publisher(source, dictionary, Processor(dictionary), work => _ = Task.Run(work));

        var startRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseStart = new ManualResetEventSlim();
        dictionary.Read = () =>
        {
            dictionary.Read = null;
            startRead.TrySetResult();
            releaseStart.Wait(Bound);
        };

        var starting = Task.Run(publisher.Start);
        await startRead.Task.WaitAsync(Bound);

        dictionary.Entries = [Entry("lantern", "Lantern")];
        var refreshed = await publisher.RefreshAsync().WaitAsync(Bound);
        Assert.Equal(["lantern"], refreshed.Dictionary.Select(entry => entry.Pattern));
        Assert.Same(refreshed, publisher.Current);

        releaseStart.Set();
        await starting.WaitAsync(Bound);

        Assert.Same(refreshed, publisher.Current);
        Assert.Equal(["lantern"], publisher.Current.Dictionary.Select(entry => entry.Pattern));
    }

    [Fact]
    public void A_new_library_vocabulary_and_a_dictionary_reload_each_ask_for_a_new_generation()
    {
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([]);
        var processor = Processor(dictionary);
        var queued = new List<Action>();
        using var publisher = Publisher(source, dictionary, processor, queued.Add);
        publisher.Start();

        var published = new List<VocabularyGeneration>();
        publisher.Published += published.Add;

        var next = Of(5, new Library("team", H1, true, Entry("kes trel", "KESTREL")));
        source.Publish(next);
        Assert.Single(queued)();
        Assert.Same(next, publisher.Current.Libraries);

        // Quick add and learning from history store an entry and reload the post-processor, which is the signal.
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
        var before = publisher.Start();

        dictionary.Entries = [Entry("harbour", "Harbour")];
        var after = await publisher.RefreshAsync().WaitAsync(Bound);

        Assert.Same(before.Libraries, after.Libraries);
        Assert.Same(before.AiScope, after.AiScope);
        Assert.Equal(before.Rules.Count + 1, after.Rules.Count);
        Assert.Equal("Harbour Kestrel Quillmoor", Processor(dictionary).ProcessDetailed("harbour kes trel quill moor", null, after.Rules).Text);
    }

    [Fact]
    public async Task A_build_that_cannot_read_the_dictionary_keeps_the_previous_generation_and_answers_its_request()
    {
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([Entry("harbour", "Harbour")]);
        var log = new CapturingLogger<VocabularyPublisher>();
        using var publisher = new VocabularyPublisher(source, dictionary, Processor(dictionary), log, work => work());
        var before = publisher.Start();

        dictionary.Failure = new InvalidOperationException("database is locked at C:\\Users\\canary\\scribe.db");
        var answered = await publisher.RefreshAsync().WaitAsync(Bound);

        Assert.Same(before, answered);
        Assert.Same(before, publisher.Current);
        Assert.Contains(log.Entries, entry => entry.Message.StartsWith("A vocabulary generation could not be built", StringComparison.Ordinal));
        Assert.DoesNotContain("canary", log.AllText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_library_vocabulary_that_cannot_be_read_counts_as_none_and_the_dictionary_still_applies()
    {
        var source = new TestVocabularySource(TwoLibraries);
        var dictionary = new ScriptedDictionary([Entry("harbour", "Harbour")]);
        var log = new CapturingLogger<VocabularyPublisher>();
        using var publisher = new VocabularyPublisher(source, dictionary, Processor(dictionary), log, work => work());
        publisher.Start();

        source.CurrentFailure = new IOException("C:\\Users\\canary\\libraries\\team.csv is locked");
        var generation = await publisher.RefreshAsync().WaitAsync(Bound);

        // Fail closed for AI cleanup, the personal dictionary alone for local rules, as the post-processor falls back.
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
        publisher.Start();
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
        var current = publisher.Start();
        Assert.Equal(1, source.ChangedSubscribers);
        var waiting = publisher.RefreshAsync();

        publisher.Dispose();

        Assert.Equal(0, source.ChangedSubscribers);
        Assert.Same(current, await waiting.WaitAsync(Bound));
        Assert.Same(current, await publisher.RefreshAsync().WaitAsync(Bound));
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

    private static VocabularyPublisher Publisher(
        ILibraryVocabularySource source, IDictionaryRepository dictionary, ITextPostProcessor processor, Action<Action> schedule) =>
        new(source, dictionary, processor, NullLogger<VocabularyPublisher>.Instance, schedule);

    private static TextPostProcessor Processor(IDictionaryRepository dictionary) =>
        new(dictionary, NullLogger<TextPostProcessor>.Instance);

    private static List<string> GlossaryLines(string? glossary) =>
        glossary is null ? [] : [.. glossary.Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal))];

    /// <summary>A dictionary whose enabled entries the test sets, or makes unreadable.</summary>
    internal sealed class ScriptedDictionary(IReadOnlyList<DictionaryEntry> entries) : IDictionaryRepository
    {
        public IReadOnlyList<DictionaryEntry> Entries { get; set; } = entries;

        public Exception? Failure { get; set; }

        /// <summary>Runs after each read has taken the entries, on the reading thread; a test holds a build here.</summary>
        public Action? Read { get; set; }

        public IReadOnlyList<DictionaryEntry> GetEnabled()
        {
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
