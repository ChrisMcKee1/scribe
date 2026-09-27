using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>Builders for the catalogs, drafts, rows and states the composition tests compose over.</summary>
internal static class Lib
{
    public static readonly LibraryContentHash H1 = Hash('1');
    public static readonly LibraryContentHash H2 = Hash('2');
    public static readonly LibraryContentHash H3 = Hash('3');
    public static readonly LibraryContentHash H4 = Hash('4');

    public static LibraryContentHash Hash(char digit) => new(new string(digit, 64));

    public static LibraryTermKey Key(string spoken) => LibraryTermKey.From(spoken);

    public static LibraryRow Custom(string spoken, string written, bool wholeWord = true, bool enabled = true) =>
        LibraryRow.Custom(new TermValues(spoken, written, wholeWord, enabled));

    public static LibraryRow Shipped(string spoken, string written, bool wholeWord = true)
    {
        var values = new TermValues(spoken, written, wholeWord);
        return new LibraryRow(Key(spoken), values, TermOrigin.Shipped, Shipped: values);
    }

    /// <summary>A built-in row the user changed: it keeps the shipped row's identity and competes as what it now says.</summary>
    public static LibraryRow Edited(string shippedSpoken, string shippedWritten, string spoken, string written, bool wholeWord = true)
    {
        var shipped = new TermValues(shippedSpoken, shippedWritten);
        var values = new TermValues(spoken, written, wholeWord);
        return new LibraryRow(
            Key(shippedSpoken), values, TermOrigin.Edited, shipped,
            new BuiltInTermEdit(Key(shippedSpoken), BuiltInTermIntent.Edited, shipped, values));
    }

    public static LibraryRow Added(string spoken, string written)
    {
        var values = new TermValues(spoken, written);
        return new LibraryRow(Key(spoken), values, TermOrigin.Added, Edit: new BuiltInTermEdit(Key(spoken), BuiltInTermIntent.Added, null, values));
    }

    public static LibraryRow TurnedOff(string spoken, string written)
    {
        var shipped = new TermValues(spoken, written);
        var values = shipped with { Enabled = false };
        return new LibraryRow(Key(spoken), values, TermOrigin.Off, shipped, new BuiltInTermEdit(Key(spoken), BuiltInTermIntent.Off, shipped, null));
    }

    public static LibraryContent CustomLibrary(string id, params LibraryRow[] rows) =>
        new(id, BuiltIn: false, Name: "Name of " + id, Category: "Custom", Description: null, rows);

    public static LibraryContent BuiltInLibrary(string id, params LibraryRow[] rows) =>
        new(id, BuiltIn: true, Name: "Name of " + id, Category: "General", Description: null, rows);

    /// <summary>A committed library; a custom one takes <c>id.csv</c> unless a file name is given.</summary>
    public static CatalogLibrary Committed(
        LibraryContent content, LibraryContentHash? hash = null, string? fileName = null, LibraryFileState state = LibraryFileState.Available) =>
        new(content, state, content.BuiltIn ? null : fileName ?? content.Id + ".csv", hash);

    public static LibraryCatalog Catalog(LibraryLocalState state, params CatalogLibrary[] libraries) =>
        new(generation: 5, libraries, state, [], [], filesAwaitingRelease: 0);

    public static LibraryCatalog Catalog(long generation, LibraryLocalState state, params CatalogLibrary[] libraries) =>
        new(generation, libraries, state, [], [], filesAwaitingRelease: 0);

    public static DraftLibrary Draft(
        LibraryContent content, LibraryOrigin origin = LibraryOrigin.Existing, bool pendingDelete = false, string? fileName = null,
        LibraryFileState state = LibraryFileState.Available) =>
        new(content, origin, state, pendingDelete, Unsaved: false, content.BuiltIn ? null : fileName ?? content.Id + ".csv");

    public static LibraryDraft Draft(long revision, LibraryLocalState state, params DraftLibrary[] libraries) =>
        new(revision, baseGeneration: 5, libraries, state, []);

    public static LibraryLocalState State(
        IEnumerable<string>? enabled = null,
        IEnumerable<(string Id, bool Permitted)>? ai = null,
        IEnumerable<(string Id, string Key)>? markers = null,
        IEnumerable<(string Id, LibraryContentHash Hash)>? accepted = null,
        bool lost = false,
        LocalStateHealth health = LocalStateHealth.Ok,
        IEnumerable<string>? legacy = null,
        IEnumerable<string>? notice = null) =>
        LibraryLocalState.Create(
            enabled,
            legacy,
            ai?.Select(pair => new KeyValuePair<string, bool>(pair.Id, pair.Permitted)),
            markers?.Select(pair => new LegacyMarker(pair.Id, Key(pair.Key))),
            notice,
            health,
            accepted?.Select(pair => new KeyValuePair<string, LibraryContentHash>(pair.Id, pair.Hash)),
            lost);

    public static LibraryIdentity BuiltInIdentity(string id) => new(id, BuiltIn: true, FileName: null);

    public static LibraryIdentity CustomIdentity(string id, string? fileName = null) => new(id, BuiltIn: false, fileName ?? id + ".csv");

    public static LibraryIdentity IdentityOf(CatalogLibrary library) =>
        new(library.Content.Id, library.Content.BuiltIn, library.FileName);

    public static LibraryComposition Compose(LibraryCatalog catalog, params DictionaryEntry[] dictionary) =>
        LibraryComposition.Committed(catalog, dictionary, new GlossaryBudget(Cleanup.CleanupPrompt.MaxGlossaryTermsCloud));

    /// <summary>The written form the composition applies for a spoken form, or null when no library supplies it.</summary>
    public static string? Winner(LibraryComposition composition, string spoken) =>
        composition.Rules.FirstOrDefault(rule => rule.Key == Key(spoken))?.Entry.Replacement;

    public static string? WinnerLibrary(LibraryComposition composition, string spoken) =>
        composition.Rules.FirstOrDefault(rule => rule.Key == Key(spoken))?.LibraryId;
}

/// <summary>
/// A library service that is also a vocabulary source, the way the library service will be after integration: the
/// published scope is what <see cref="TryHandOff"/> compares with, under a gate, and the old seam returns a canary nobody
/// should read once a vocabulary source is there.
/// </summary>
internal sealed class FakeVocabularySource : ILibraryVocabularySource, IDictionaryLibraryService
{
    private readonly object _gate = new();
    private AiVocabularyScope _published;

    public FakeVocabularySource(LibraryVocabulary current)
    {
        Current = current;
        _published = current.AiScope;
    }

    public LibraryVocabulary Current { get; private set; }

    public int HandOffs { get; private set; }

    /// <summary>What the old seam would return: if a report ever reads it, the canary shows.</summary>
    public IReadOnlyList<DictionaryEntry> SeamEntries { get; set; } = [DictionaryEntry.New("seam canary", "SeamCanary")];

    public event Action<long>? Changed
    {
        add { }
        remove { }
    }

    /// <summary>Publishes a new committed vocabulary and its scope together, as a completed Save or an adoption does.</summary>
    public void Publish(LibraryVocabulary vocabulary)
    {
        lock (_gate)
        {
            Current = vocabulary;
            _published = vocabulary.AiScope;
        }
    }

    /// <summary>Publishes a narrowed scope on its own, as a prepared Save does before its files are in place.</summary>
    public void PublishScope(AiVocabularyScope scope)
    {
        lock (_gate)
        {
            _published = scope;
        }
    }

    public bool TryHandOff(AiVocabularyScope admitted, Action handOff)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        ArgumentNullException.ThrowIfNull(handOff);
        lock (_gate)
        {
            // What the library service does: the composer decides whether permission narrowed since admission.
            if (LibraryComposer.Instance.HasNarrowed(admitted, _published))
            {
                return false;
            }

            HandOffs++;
            handOff();
            return true;
        }
    }

    public IReadOnlyList<DictionaryLibrary> GetLibraries() => [];

    public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries() => SeamEntries;

    public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries(IReadOnlyCollection<string> enabledIds) => SeamEntries;

    public DictionaryLibrary Import(string csv, string? suggestedName) => throw new NotSupportedException();

    public void Remove(string id) => throw new NotSupportedException();
}

/// <summary>What dictation writes, from the real post-processor, for a stored dictionary and a list of library rules.</summary>
internal static class Dictation
{
    public static string[] Write(
        IEnumerable<DictionaryEntry> dictionary, IReadOnlyList<DictionaryEntry> libraryRules, IEnumerable<string> inputs)
    {
        using var database = Persistence.ScribeDatabase.CreateInMemory();
        var repository = new Persistence.DictionaryRepository(database);
        var saved = Settings.DictionaryEntryBuilder.Build(
            [.. dictionary.Select(e => new Settings.DictionaryEntryBuilder.Row(0, e.Pattern, e.Replacement, e.WholeWord, e.Enabled))]).Entries;
        if (saved.Count > 0)
        {
            repository.AddRange(saved);
        }

        var processor = new TextPostProcessor(
            repository, Microsoft.Extensions.Logging.Abstractions.NullLogger<TextPostProcessor>.Instance, snippets: null,
            libraries: new Rules(libraryRules));
        return [.. inputs.Select(processor.Process)];
    }

    private sealed class Rules(IReadOnlyList<DictionaryEntry> rules) : IDictionaryLibraryService
    {
        public IReadOnlyList<DictionaryLibrary> GetLibraries() => [];

        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries() => rules;

        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries(IReadOnlyCollection<string> enabledIds) => rules;

        public DictionaryLibrary Import(string csv, string? suggestedName) => throw new NotSupportedException();

        public void Remove(string id) => throw new NotSupportedException();
    }
}
