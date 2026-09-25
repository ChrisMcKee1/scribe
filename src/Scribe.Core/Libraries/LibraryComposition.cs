using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Libraries;

/// <summary>
/// The one composition of the libraries (plan 3.2, 3.6): which row supplies each spoken form under Decision 1's tiers and
/// legacy markers, the subset AI cleanup may carry, and everything the Libraries and Dictionary pages say about rows,
/// badges, the Save prompt and glossary inclusion. Committed compositions feed dictation (through
/// <see cref="ILibraryComposer.ComposeVocabulary"/>); previews feed the Settings window, and name the draft revision they
/// were computed for so a stale one can be discarded. A preview is composed against the committed catalog the draft was
/// built from, so its AI permission is what the draft's Save would commit (round 2, part 3).
/// </summary>
/// <remarks>
/// <para>
/// Under <see cref="LibraryDecisions.Precedence"/>: disabled rows, and libraries that are off, paused or pending deletion,
/// drop out first; then authored rows come before shipped rows; inside a tier, built-ins in
/// <see cref="LibraryPrecedence.BuiltInOrder"/>, then custom libraries by physical file name (review finding A16), and
/// rows in saved order; one rule per <see cref="LibraryTermKey"/>, 0.4.3's key (A10). A legacy marker (L, K) is active
/// while some enabled, usable built-in has an enabled row whose spoken form folds alike (<see cref="PostProcessing.SpokenFormFold"/>,
/// broader than every comparison dictation makes) and which L's row would not apply exactly alike: a different written
/// form or word boundaries, or a spoken form the matcher reads differently (round 2, review finding A3). An active marked
/// row competes after every shipped row, which keeps what dictation writes at the upgrade exactly as 0.4.3 wrote it
/// (decision 3), for rows with one key and for rows the matcher takes for one text under different keys. Where an
/// unmarked authored row applies exactly as the old shipped winner did, it now supplies it, which changes no output.
/// </para>
/// <para>
/// Immutable and safe to share. Built in one pass over the rows; the statuses' indexes and the glossary inclusion are
/// computed on first use, once.
/// </para>
/// </remarks>
public sealed class LibraryComposition
{
    private static readonly TermStatus UnknownRow = new(
        TermMarker.None, TermWinner.None, null, null, [], [], null, false, GlossaryInclusion.NotApplied);

    private readonly Source[] _sources;
    private readonly Dictionary<string, Source> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<DictionaryEntry> _dictionary;
    private readonly Dictionary<LibraryTermKey, DictionaryEntry> _dictionaryByKey = [];
    private readonly Dictionary<LibraryTermKey, ComposedRule> _ruleByKey = [];
    private readonly Dictionary<DictionaryEntry, Source> _sourceOfRule = new(ReferenceEqualityComparer.Instance);
    private readonly GlossaryBudget _budget;
    private readonly Lazy<Dictionary<DictionaryEntry, GlossaryInclusion>> _glossary;
    private readonly Lazy<Dictionary<LibraryTermKey, List<(Source Source, int Row)>>> _rowsByKey;
    private readonly Lazy<IReadOnlyList<DictionaryLibrary>> _enabledLibraries;

    private LibraryComposition(
        bool isPreview,
        long basis,
        IEnumerable<Source> sources,
        IReadOnlyList<DictionaryEntry> dictionary,
        GlossaryBudget budget,
        LibraryPrecedenceRule rule)
    {
        IsPreview = isPreview;
        Basis = basis;
        _budget = budget;

        // Precedence decides, never the order the libraries arrive in: the list on screen is alphabetical.
        _sources = [.. sources.OrderBy(source => source, Comparer<Source>.Create((a, b) =>
            LibraryPrecedence.Compare(a.Id, a.BuiltIn, a.FileName, b.Id, b.BuiltIn, b.FileName)))];
        foreach (var source in _sources)
        {
            _byId.TryAdd(source.Id, source);
        }

        _dictionary = [.. dictionary.Where(entry => entry is { Enabled: true })];
        foreach (var entry in _dictionary)
        {
            var key = LibraryTermKey.From(entry.Pattern);
            if (!key.IsEmpty)
            {
                _dictionaryByKey.TryAdd(key, entry);
            }
        }

        if (rule == LibraryPrecedenceRule.AuthoredFirstWithLegacyMarkers)
        {
            MarkActiveLegacyRows();
        }

        var rules = new List<ComposedRule>();
        for (var tier = 0; tier < 3; tier++)
        {
            foreach (var source in _sources.Where(source => source.Participates))
            {
                for (var row = 0; row < source.Keys.Length; row++)
                {
                    var key = source.Keys[row];
                    if (!source.Content.Rows[row].Values.Enabled || key.IsEmpty || TierOf(rule, source, row) != tier ||
                        _ruleByKey.ContainsKey(key))
                    {
                        continue;
                    }

                    var composed = new ComposedRule(
                        source.Entries[row],
                        source.Id,
                        key,
                        LibraryTiers.IsAuthored(source.Content.Rows[row]) ? RuleTier.Authored : RuleTier.Shipped,
                        source.MarkerActive[row]);
                    _ruleByKey.Add(key, composed);
                    _sourceOfRule.Add(composed.Entry, source);
                    rules.Add(composed);
                }
            }
        }

        Rules = rules.AsReadOnly();
        LibraryEntries = rules.Select(composed => composed.Entry).ToList().AsReadOnly();
        AiLibraryEntries = rules
            .Where(composed => _sourceOfRule[composed.Entry].AiPermitted)
            .Select(composed => composed.Entry)
            .ToList()
            .AsReadOnly();
        AnyLegacyMarkerActive = _sources.Any(source => source.Participates && source.MarkerActive.Any(active => active));
        AiExcludedLibraryIds = _sources
            .Where(source => source.Participates && !source.AiPermitted)
            .Select(source => source.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _glossary = new(ComputeGlossary);
        _rowsByKey = new(IndexRowsByKey);
        _enabledLibraries = new(() => _sources
            .Where(source => source.Participates)
            .Select(source => new DictionaryLibrary(
                source.Id, source.Content.Name, source.Content.Category, source.Content.Description, source.BuiltIn,
                Array.AsReadOnly(source.Entries))
            { FileName = source.BuiltIn ? null : source.FileName })
            .ToList()
            .AsReadOnly());
    }

    /// <summary>Whether this is a draft's preview (the result after Save) rather than the committed result.</summary>
    public bool IsPreview { get; }

    /// <summary>The committed generation, or for a preview the draft revision it was computed for.</summary>
    public long Basis { get; }

    /// <summary>One rule per spoken form, in composition order: the order dictation and the glossary walk them.</summary>
    public IReadOnlyList<ComposedRule> Rules { get; }

    /// <summary><see cref="Rules"/> as the entries dictation applies.</summary>
    public IReadOnlyList<DictionaryEntry> LibraryEntries { get; }

    /// <summary>
    /// The entries of <see cref="LibraryEntries"/> whose library may be sent to AI cleanup, in the same order: a filter of
    /// the same winners, never a second composition, so the glossary never teaches a spelling local replacement would not
    /// write.
    /// </summary>
    public IReadOnlyList<DictionaryEntry> AiLibraryEntries { get; }

    /// <summary>Whether any legacy marker is active in a library in use: the one upgrade notice shows only then.</summary>
    public bool AnyLegacyMarkerActive { get; }

    /// <summary>
    /// The libraries in use (enabled, usable, not pending deletion) that may not be sent to AI cleanup, whose terms the
    /// dictionary cleanup must not copy into the always-sent dictionary.
    /// </summary>
    public IReadOnlySet<string> AiExcludedLibraryIds { get; }

    /// <summary>
    /// The libraries in use, in precedence order, as the usage review and the dictionary cleanup read them: every row's
    /// values as an entry (turned-off rows with <see cref="DictionaryEntry.Enabled"/> false), the same entry objects the
    /// rules hold, and a custom library's physical file name.
    /// </summary>
    public IReadOnlyList<DictionaryLibrary> EnabledLibraries => _enabledLibraries.Value;

    /// <summary>The committed composition of <paramref name="catalog"/>, with the personal dictionary merged on top.</summary>
    /// <param name="catalog">The committed libraries and local state.</param>
    /// <param name="dictionary">The personal dictionary in the order dictation reads it; only enabled entries count.</param>
    /// <param name="budget">The glossary budget dictation uses.</param>
    /// <remarks>
    /// A custom library whose content does not match <see cref="LibraryLocalState.AcceptedContent"/> composes with the
    /// defaults of a discovered one until an adoption records it (review finding A4): it is not permitted for AI cleanup,
    /// and its legacy markers are the ones the upgrade would give it rather than the stored ones.
    /// </remarks>
    public static LibraryComposition Committed(
        LibraryCatalog catalog, IReadOnlyList<DictionaryEntry> dictionary, GlossaryBudget budget) =>
        Committed(catalog, dictionary, budget, LibraryDecisions.Precedence);

    /// <summary>
    /// The preview of <paramref name="draft"/> where its committed catalog is not at hand; the Libraries page and the
    /// dictionary cleanup use <see cref="Preview(LibraryDraft, LibraryCatalog, IReadOnlyList{DictionaryEntry}, GlossaryBudget)"/>.
    /// </summary>
    /// <param name="draft">The draft at one revision.</param>
    /// <param name="dictionary">The personal dictionary in the order dictation reads it; only enabled entries count.</param>
    /// <param name="budget">The glossary budget dictation uses.</param>
    /// <remarks>
    /// Without the committed catalog the preview cannot tell which files the Save leaves as they are, nor what they hold,
    /// so AI permission fails closed: a library the draft brings in (created, imported, duplicated, restored, or kept from
    /// a retired built-in or from a change made outside Scribe), which its Save always writes, is judged by the draft's
    /// choices, and every library already committed is taken as not permitted (round 2, part 3: the review of D).
    /// </remarks>
    public static LibraryComposition Preview(
        LibraryDraft draft, IReadOnlyList<DictionaryEntry> dictionary, GlossaryBudget budget) =>
        Preview(draft, committed: null, dictionary, budget, LibraryDecisions.Precedence);

    /// <summary>The preview of <paramref name="draft"/>: the result its Save would give.</summary>
    /// <param name="draft">The draft at one revision.</param>
    /// <param name="committed">
    /// The committed catalog the draft was built from (its <see cref="LibraryCatalog.Generation"/> is the draft's
    /// <see cref="LibraryDraft.BaseGeneration"/>): what each library's file holds now.
    /// </param>
    /// <param name="dictionary">The personal dictionary in the order dictation reads it; only enabled entries count.</param>
    /// <param name="budget">The glossary budget dictation uses.</param>
    /// <remarks>
    /// AI permission is what the Save would commit (round 2, part 3: the review of D). A library whose content the Save
    /// writes, one the committed catalog does not hold or whose metadata or rows differ from the committed library's, is
    /// judged by the draft's choices alone, since that Save records the hash of every file it writes. Every other library
    /// keeps its committed file, which composes exactly as the committed composition composes it (review finding A4):
    /// judged against the draft's accepted content, and while that is not the file's content, not permitted and with the
    /// legacy markers the upgrade would give it, however its enabled state or AI box changed (those changes mark it
    /// unsaved, and write nothing).
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="committed"/> is not the catalog the draft was built from.</exception>
    public static LibraryComposition Preview(
        LibraryDraft draft, LibraryCatalog committed, IReadOnlyList<DictionaryEntry> dictionary, GlossaryBudget budget)
    {
        ArgumentNullException.ThrowIfNull(committed);
        return Preview(draft, committed, dictionary, budget, LibraryDecisions.Precedence);
    }

    // Each decision-1 alternative behind the one policy point, so a veto is a change to LibraryDecisions alone.
    internal static LibraryComposition Committed(
        LibraryCatalog catalog, IReadOnlyList<DictionaryEntry> dictionary, GlossaryBudget budget, LibraryPrecedenceRule rule)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(dictionary);
        var state = catalog.LocalState;
        var markers = MarkersByLibrary(state);
        Dictionary<string, List<TermValues>>? shipped = null;
        var sources = new List<Source>(catalog.Libraries.Count);
        foreach (var library in catalog.Libraries)
        {
            var content = library.Content;
            IReadOnlySet<LibraryTermKey> marked;
            if (!content.BuiltIn && !AiVocabularyPolicy.ContentIsAccepted(state, content.Id, false, library.ContentHash))
            {
                shipped ??= LibraryTiers.ShippedValues(catalog.Libraries.Where(l => l.Content.BuiltIn).Select(l => l.Content));
                marked = LibraryTiers.UpgradeMarkers(content, shipped).Select(marker => marker.Key).ToHashSet();
            }
            else
            {
                marked = markers.GetValueOrDefault(content.Id) ?? EmptyKeys;
            }

            sources.Add(new Source(
                content,
                library.FileName,
                participates: state.EnabledIds.Contains(content.Id) && LibraryTiers.IsUsable(library.State),
                aiPermitted: AiVocabularyPolicy.IsPermitted(state, content.Id, content.BuiltIn, library.ContentHash),
                marked));
        }

        return new LibraryComposition(isPreview: false, catalog.Generation, sources, dictionary, budget, rule);
    }

    internal static LibraryComposition Preview(
        LibraryDraft draft,
        LibraryCatalog? committed,
        IReadOnlyList<DictionaryEntry> dictionary,
        GlossaryBudget budget,
        LibraryPrecedenceRule rule)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(dictionary);
        if (committed is not null && committed.Generation != draft.BaseGeneration)
        {
            throw new ArgumentException(
                "The committed catalog must be the one the draft was built from, the generation the draft names as its base.",
                nameof(committed));
        }

        var state = draft.LocalState;
        var markers = MarkersByLibrary(state);
        Dictionary<string, List<TermValues>>? shipped = null;
        var sources = new List<Source>(draft.Libraries.Count);
        foreach (var library in draft.Libraries)
        {
            var content = library.Content;
            IReadOnlySet<LibraryTermKey> marked = markers.GetValueOrDefault(content.Id) ?? EmptyKeys;
            bool aiPermitted;
            if (committed is null)
            {
                // Nothing committed can be judged without the catalog; a library the draft brings in is always written.
                aiPermitted = IsBroughtIn(library.Origin) && AiVocabularyPolicy.IsPermittedByChoice(state, content.Id, content.BuiltIn);
            }
            else if (FileTheSaveKeeps(committed, content) is { } kept)
            {
                // The Save leaves this file as it is, so it composes as the committed composition composes it (A4): judged
                // against the accepted content, and with the upgrade's markers when that content is not the accepted one.
                aiPermitted = AiVocabularyPolicy.IsPermitted(state, content.Id, content.BuiltIn, kept.ContentHash);
                if (!content.BuiltIn && !AiVocabularyPolicy.ContentIsAccepted(state, content.Id, false, kept.ContentHash))
                {
                    shipped ??= LibraryTiers.ShippedValues(draft.Libraries.Where(l => l.Content.BuiltIn).Select(l => l.Content));
                    marked = LibraryTiers.UpgradeMarkers(content, shipped).Select(marker => marker.Key).ToHashSet();
                }
            }
            else
            {
                // The Save writes this content and records its hash in the same commit, so the draft's choices apply.
                aiPermitted = AiVocabularyPolicy.IsPermittedByChoice(state, content.Id, content.BuiltIn);
            }

            sources.Add(new Source(
                content,
                library.FileName,
                participates: state.EnabledIds.Contains(content.Id) && LibraryTiers.IsUsable(library.State) && !library.PendingDelete,
                aiPermitted,
                marked));
        }

        return new LibraryComposition(isPreview: true, draft.Revision, sources, dictionary, budget, rule);
    }

    // A library the draft brings in: its Save always writes the file (a restore puts the entry's bytes back).
    private static bool IsBroughtIn(LibraryOrigin origin) =>
        origin is LibraryOrigin.Created or LibraryOrigin.Imported or LibraryOrigin.Duplicated or LibraryOrigin.Restored or
            LibraryOrigin.RetiredBuiltIn or LibraryOrigin.ChangedOutside;

    // The committed file the draft's Save leaves in place for this content: the committed library of the same id and kind
    // with the draft's metadata and rows. Null when the Save writes the content, including every library the committed
    // catalog does not hold.
    private static CatalogLibrary? FileTheSaveKeeps(LibraryCatalog committed, LibraryContent content) =>
        committed.Find(content.Id) is { } file && file.Content.BuiltIn == content.BuiltIn && SameContent(file.Content, content)
            ? file
            : null;

    // Whether the Save leaves a library's file as it is: the same metadata and rows. Rows compare by value (record
    // equality), the list element by element.
    private static bool SameContent(LibraryContent committed, LibraryContent draft) =>
        ReferenceEquals(committed, draft) ||
        (string.Equals(committed.Name, draft.Name, StringComparison.Ordinal) &&
            string.Equals(committed.Category, draft.Category, StringComparison.Ordinal) &&
            string.Equals(committed.Description, draft.Description, StringComparison.Ordinal) &&
            string.Equals(committed.BasedOn, draft.BasedOn, StringComparison.Ordinal) &&
            committed.Rows.SequenceEqual(draft.Rows));

    /// <summary>
    /// What the page says about the row of <paramref name="libraryId"/> whose identity is <paramref name="key"/>
    /// (<see cref="LibraryRow.Key"/>, which for a renamed built-in row is its shipped spoken form); the row competes as
    /// the spoken form it writes. An unknown library or row has no winner and no marker.
    /// </summary>
    public TermStatus StatusOf(string libraryId, LibraryTermKey key)
    {
        ArgumentNullException.ThrowIfNull(libraryId);
        if (!_byId.TryGetValue(libraryId, out var source) || source.RowOf(key) is not (>= 0 and var row))
        {
            return UnknownRow;
        }

        var libraryRow = source.Content.Rows[row];
        var values = libraryRow.Values;
        var competing = source.Keys[row];
        DictionaryEntry? personal = null;
        ComposedRule? rule = null;
        if (!competing.IsEmpty)
        {
            _dictionaryByKey.TryGetValue(competing, out personal);
            _ruleByKey.TryGetValue(competing, out rule);
        }

        var thisRow = rule is not null && ReferenceEquals(rule.Entry, source.Entries[row]);
        TermWinner winner;
        string? winningId = null;
        DictionaryEntry? winning = null;
        if (personal is not null)
        {
            (winner, winning) = (TermWinner.Dictionary, personal);
        }
        else if (rule is not null)
        {
            (winner, winningId, winning) = (thisRow ? TermWinner.ThisRow : TermWinner.OtherLibrary, rule.LibraryId, rule.Entry);
        }
        else
        {
            winner = TermWinner.None;
        }

        var same = new List<string>();
        var different = new List<string>();
        if (!competing.IsEmpty && _rowsByKey.Value.TryGetValue(competing, out var holders))
        {
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { source.Id };
            foreach (var (other, otherRow) in holders)
            {
                if (listed.Add(other.Id))
                {
                    (LibraryTiers.SameResult(other.Content.Rows[otherRow].Values, values) ? same : different).Add(other.Id);
                }
            }
        }

        var glossary =
            winner != TermWinner.ThisRow ? GlossaryInclusion.NotApplied
            : !source.AiPermitted ? GlossaryInclusion.NotPermitted
            : _glossary.Value.GetValueOrDefault(rule!.Entry, GlossaryInclusion.NotEligible);

        var inEffect = source.Participates && values.Enabled && !competing.IsEmpty;
        var marker =
            libraryRow.Review is not null ? TermMarker.Update
            : source.MarkerActive[row] ? TermMarker.Check
            : inEffect && winning is not null && winner != TermWinner.ThisRow && !LibraryTiers.SameResult(winning, values)
                ? TermMarker.NotUsed
            : !values.Enabled && winner != TermWinner.None ? TermMarker.OffHere
            : string.IsNullOrWhiteSpace(values.Written) ? TermMarker.Removes
            : source.BuiltIn && LibraryTiers.IsAuthored(libraryRow) ? TermMarker.Changed
            : TermMarker.None;

        return new TermStatus(
            marker, winner, winningId, winning, same.AsReadOnly(), different.AsReadOnly(), libraryRow.Review,
            source.MarkerActive[row], glossary);
    }

    /// <summary>
    /// Every spoken form the libraries in use cover (trimmed, case-insensitive), with the rule that applies and the library
    /// that supplies it: the Dictionary page's badges, from the same winners dictation applies.
    /// </summary>
    public IReadOnlyDictionary<string, LibraryCoverage> Coverage()
    {
        var covering = new Dictionary<string, LibraryCoverage>(StringComparer.OrdinalIgnoreCase);
        foreach (var composed in Rules)
        {
            var source = _sourceOfRule[composed.Entry];
            covering.TryAdd(composed.Key.Value, new LibraryCoverage(composed.Entry, source.Id, source.Content.Name, source.BuiltIn)
            {
                FileName = LibraryTiers.PrecedenceFileName(source.Id, source.BuiltIn, source.FileName),
            });
        }

        return covering;
    }

    /// <summary>
    /// The Save prompt's report for <paramref name="personal"/> against the rules in use, each overlap named after the
    /// library that supplies the rule (a row turned off in an earlier library no longer takes the name).
    /// </summary>
    public DictionaryOverlapReport OverlapReport(IReadOnlyList<DictionaryEntry> personal)
    {
        ArgumentNullException.ThrowIfNull(personal);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var composed in Rules)
        {
            names.TryAdd(composed.Key.Value, _sourceOfRule[composed.Entry].Content.Name);
        }

        return DictionaryLibraryOverlapAnalyzer.Analyze(personal, LibraryEntries, names);
    }

    /// <summary>
    /// The identities (<see cref="LibraryRow.Key"/>) of the rows of <paramref name="libraryId"/> the Show filter keeps, in
    /// saved order, each once; empty for an unknown library.
    /// </summary>
    public IReadOnlyList<LibraryTermKey> Filter(string libraryId, TermFilter filter)
    {
        ArgumentNullException.ThrowIfNull(libraryId);
        if (!_byId.TryGetValue(libraryId, out var source))
        {
            return [];
        }

        var keys = new List<LibraryTermKey>();
        var seen = new HashSet<LibraryTermKey>();
        for (var row = 0; row < source.Content.Rows.Count; row++)
        {
            var libraryRow = source.Content.Rows[row];
            var kept = filter switch
            {
                TermFilter.ChangedByYou => LibraryTiers.IsAuthored(libraryRow),
                TermFilter.TurnedOff => !libraryRow.Values.Enabled,
                TermFilter.NeedsAttention => libraryRow.Review is not null || source.MarkerActive[row],
                _ => true,
            };
            if (kept && seen.Add(libraryRow.Key))
            {
                keys.Add(libraryRow.Key);
            }
        }

        return keys.AsReadOnly();
    }

    // The library that supplies a rule of this composition; internal for the vocabulary's origins.
    internal string? LibraryOf(DictionaryEntry ruleEntry) =>
        _sourceOfRule.TryGetValue(ruleEntry, out var source) ? source.Id : null;

    // Decision 1's tiers, as the pass that picks the first row per spoken form visits them.
    private static int TierOf(LibraryPrecedenceRule rule, Source source, int row)
    {
        var authored = LibraryTiers.IsAuthored(source.Content.Rows[row]);
        return rule switch
        {
            LibraryPrecedenceRule.AuthoredFirstEverywhere => authored ? 0 : 1,
            LibraryPrecedenceRule.ShippedFirst => authored ? 1 : 0,
            _ => !authored ? 1 : source.MarkerActive[row] ? 2 : 0,
        };
    }

    private static readonly IReadOnlySet<LibraryTermKey> EmptyKeys = new HashSet<LibraryTermKey>();

    private static Dictionary<string, IReadOnlySet<LibraryTermKey>> MarkersByLibrary(LibraryLocalState state) =>
        state.LegacyMarkers
            .GroupBy(marker => marker.LibraryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlySet<LibraryTermKey>)group.Select(marker => marker.Key).ToHashSet(),
                StringComparer.OrdinalIgnoreCase);

    // A marked row's marker is active while an enabled, usable built-in has an enabled row whose spoken form folds alike
    // and which the marked row would not apply exactly alike (LibraryTiers.AppliesAlike): the rule the marker was made by,
    // so the marked row competes after the shipped rows the matcher can take for its text, whatever their keys (round 2,
    // review finding A3). Rows of libraries not in use never compete, so their markers stay inactive.
    private void MarkActiveLegacyRows()
    {
        var marked = _sources.Where(source => source.Participates && source.MarkedKeys.Count > 0).ToList();
        if (marked.Count == 0)
        {
            return;
        }

        var builtInRows = new Dictionary<string, List<(Source Source, int Row)>>(StringComparer.Ordinal);
        foreach (var source in _sources.Where(source => source.Participates && source.BuiltIn))
        {
            for (var row = 0; row < source.Keys.Length; row++)
            {
                if (source.Content.Rows[row].Values.Enabled && !source.Keys[row].IsEmpty)
                {
                    var fold = LibraryTiers.MarkerFold(source.Content.Rows[row].Values.Spoken);
                    if (!builtInRows.TryGetValue(fold, out var list))
                    {
                        builtInRows[fold] = list = [];
                    }

                    list.Add((source, row));
                }
            }
        }

        foreach (var source in marked)
        {
            for (var row = 0; row < source.Keys.Length; row++)
            {
                var values = source.Content.Rows[row].Values;
                source.MarkerActive[row] =
                    values.Enabled &&
                    source.MarkedKeys.Contains(source.Keys[row]) &&
                    builtInRows.TryGetValue(LibraryTiers.MarkerFold(values.Spoken), out var competing) &&
                    competing.Any(other =>
                        !(ReferenceEquals(other.Source, source) && other.Row == row) &&
                        !LibraryTiers.AppliesAlike(other.Source.Content.Rows[other.Row].Values, values));
            }
        }
    }

    // Enabled rows of the libraries in use by spoken form, in precedence and saved order, for the statuses' comparisons.
    private Dictionary<LibraryTermKey, List<(Source Source, int Row)>> IndexRowsByKey()
    {
        var rows = new Dictionary<LibraryTermKey, List<(Source Source, int Row)>>();
        foreach (var source in _sources.Where(source => source.Participates))
        {
            for (var row = 0; row < source.Keys.Length; row++)
            {
                if (source.Content.Rows[row].Values.Enabled && !source.Keys[row].IsEmpty)
                {
                    if (!rows.TryGetValue(source.Keys[row], out var list))
                    {
                        rows[source.Keys[row]] = list = [];
                    }

                    list.Add((source, row));
                }
            }
        }

        return rows;
    }

    // Whether each permitted rule's line is in the glossary this budget renders: counted over exactly the vocabulary
    // dictation builds (CleanupPrompt.ComposeVocabulary of the dictionary and AiLibraryEntries), with each entry's line
    // taken from the real renderer and the renderer's selection repeated around it: lines in order, each key once, the
    // term budget, and a stop before the first line that would pass the character budget.
    private Dictionary<DictionaryEntry, GlossaryInclusion> ComputeGlossary()
    {
        var inclusion = new Dictionary<DictionaryEntry, GlossaryInclusion>(ReferenceEqualityComparer.Instance);
        var vocabulary = CleanupPrompt.ComposeVocabulary(_dictionary, AiLibraryEntries);
        var lines = GlossaryLines(vocabulary);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long chars = 0;
        var count = 0;
        var stopped = _budget.MaxTerms <= 0;
        for (var i = 0; i < vocabulary.Count; i++)
        {
            var line = lines[i];
            GlossaryInclusion result;
            if (line is null || !seen.Add(GlossaryKey(line)))
            {
                result = GlossaryInclusion.NotEligible;
            }
            else if (stopped || chars + line.Length + 1 > CleanupPrompt.MaxGlossaryChars)
            {
                stopped = true;
                result = GlossaryInclusion.OverBudget;
            }
            else
            {
                chars += line.Length + 1;
                result = GlossaryInclusion.Included;
                stopped = ++count >= _budget.MaxTerms;
            }

            if (_sourceOfRule.ContainsKey(vocabulary[i]))
            {
                inclusion[vocabulary[i]] = result;
            }
        }

        return inclusion;
    }

    // The line the renderer gives each entry on its own, or null when it gives none, from the renderer itself, a hundred
    // entries a call. A line is at most 222 characters, so a hundred of them stay inside the character budget, and the
    // renderer emits them in entry order, each entry at most one line: a chunk that renders as many lines as it has entries
    // gives each entry its own. One that renders fewer (an entry whose written form is only quotes, or two entries with
    // one key) is rendered again entry by entry.
    internal static string?[] GlossaryLines(IReadOnlyList<DictionaryEntry> entries)
    {
        const int chunkSize = 100;
        var lines = new string?[entries.Count];
        var chunk = new List<int>(chunkSize);
        for (var i = 0; i <= entries.Count; i++)
        {
            if (i < entries.Count)
            {
                // The renderer's own first test; everything it lets through goes into a chunk.
                var entry = entries[i];
                if (entry is null || !entry.Enabled || !CleanupPrompt.IsVocabularyReplacement(entry.Replacement))
                {
                    continue;
                }

                chunk.Add(i);
                if (chunk.Count < chunkSize)
                {
                    continue;
                }
            }

            if (chunk.Count == 0)
            {
                continue;
            }

            var rendered = RenderedLines(CleanupPrompt.BuildGlossary([.. chunk.Select(index => entries[index])], chunk.Count));
            for (var k = 0; k < chunk.Count; k++)
            {
                lines[chunk[k]] = rendered.Length == chunk.Count ? rendered[k] : GlossaryLine(entries[chunk[k]]);
            }

            chunk.Clear();
        }

        return lines;
    }

    // The line the glossary renders for an entry on its own, from the renderer itself, or null when it renders none.
    internal static string? GlossaryLine(DictionaryEntry entry)
    {
        if (!entry.Enabled || !CleanupPrompt.IsVocabularyReplacement(entry.Replacement))
        {
            return null;
        }

        var rendered = RenderedLines(CleanupPrompt.BuildGlossary([entry], 1));
        return rendered.Length == 0 ? null : rendered[0];
    }

    // A rendered glossary is one header line and then one line per term.
    private static string[] RenderedLines(string glossary) =>
        glossary.Length == 0 ? [] : glossary[(glossary.IndexOf('\n') + 1)..].Split('\n');

    // The key the glossary de-duplicates lines by: the written form, and the spoken form when the line shows one. The
    // renderer drops double quotes from both, so the first " (transcribed as " always separates them.
    internal static string GlossaryKey(string line)
    {
        const string separator = " (transcribed as \"";
        var body = line.StartsWith("- ", StringComparison.Ordinal) ? line[2..] : line;
        var at = body.IndexOf(separator, StringComparison.Ordinal);
        return at >= 0 && body.EndsWith("\")", StringComparison.Ordinal)
            ? body[..at] + "|" + body[(at + separator.Length)..^2]
            : body;
    }

    /// <summary>One library as the composition sees it.</summary>
    private sealed class Source
    {
        private Dictionary<LibraryTermKey, int>? _rowsByIdentity;

        public Source(
            LibraryContent content, string? fileName, bool participates, bool aiPermitted, IReadOnlySet<LibraryTermKey> markedKeys)
        {
            Content = content;
            FileName = fileName;
            Participates = participates;
            AiPermitted = aiPermitted;
            MarkedKeys = markedKeys;
            Entries = new DictionaryEntry[content.Rows.Count];
            Keys = new LibraryTermKey[content.Rows.Count];
            MarkerActive = new bool[content.Rows.Count];
            for (var row = 0; row < content.Rows.Count; row++)
            {
                Entries[row] = content.Rows[row].Values.ToEntry();
                Keys[row] = LibraryTiers.CompetingKey(content.Rows[row]);
            }
        }

        public LibraryContent Content { get; }

        public string Id => Content.Id;

        public bool BuiltIn => Content.BuiltIn;

        /// <summary>A custom library's physical file name as the container gave it (null means <c>Id + ".csv"</c>).</summary>
        public string? FileName { get; }

        /// <summary>Enabled, usable, and (in a draft) not pending deletion.</summary>
        public bool Participates { get; }

        public bool AiPermitted { get; }

        public IReadOnlySet<LibraryTermKey> MarkedKeys { get; }

        /// <summary>Each row's values as an entry, one object per row, which the rules hold by reference.</summary>
        public DictionaryEntry[] Entries { get; }

        /// <summary>Each row's competing spoken form.</summary>
        public LibraryTermKey[] Keys { get; }

        /// <summary>Each row's legacy marker is active.</summary>
        public bool[] MarkerActive { get; }

        /// <summary>The first row whose identity is <paramref name="key"/>, else the first competing as it, else -1.</summary>
        public int RowOf(LibraryTermKey key)
        {
            var rows = Volatile.Read(ref _rowsByIdentity);
            if (rows is null)
            {
                rows = [];
                for (var row = 0; row < Content.Rows.Count; row++)
                {
                    rows.TryAdd(Content.Rows[row].Key, row);
                }

                rows = Interlocked.CompareExchange(ref _rowsByIdentity, rows, null) ?? rows;
            }

            return rows.TryGetValue(key, out var found) ? found : Array.IndexOf(Keys, key);
        }
    }
}
