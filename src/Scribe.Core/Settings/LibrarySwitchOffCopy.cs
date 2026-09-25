using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Settings;

/// <summary>
/// Which still-used library terms the dictionary cleanup copies into the user's dictionary before it switches their
/// libraries off. A library is switched off as a whole, so a working rule of its has to move into the dictionary first,
/// and the copy must keep what dictation writes.
/// </summary>
/// <remarks>
/// <para>
/// Only a kept term whose row is a rule dictation compiles today is considered: no enabled dictionary row has its spoken
/// form, and it is the first enabled row for its spoken form in precedence order (<see cref="LibraryPrecedence"/>),
/// whether that library stays on or is switched off too. No other kept row is ever applied, so losing it changes nothing.
/// The rows come from <see cref="DictionaryLibraryOverlapAnalyzer.Coverage"/>, never from the order the review lists the
/// libraries in (most unused terms first).
/// </para>
/// <para>
/// Which libraries are on before and after the switch comes from the Libraries list's rows the way Save stores them, as
/// ids, and the library service applies every loaded library with a saved id. So a built-in and a hand-placed file that
/// share an id go on and off together: unticking one while the other's row stays ticked switches nothing off in
/// dictation, and unticking the last row with the id switches both off, the one whose row was already unticked included.
/// Every library that goes off keeps what the review kept of it.
/// </para>
/// <para>
/// Whether such a term is copied is decided by what dictation writes, never by comparing entries. The composer keeps one
/// row per spoken form compared trimmed and OrdinalIgnoreCase, but the matcher is an invariant case-insensitive regular
/// expression that folds letters differently (it does not treat the Greek final sigma as sigma, and it treats the Kelvin
/// sign as k), and rule order breaks ties between rules that match the same text. So the real dictionary pass
/// (<see cref="TextPostProcessor.ApplyDictionaryPass"/>) is run over the term's spoken form and its case variants with the
/// rules as they are today and as they would be after the switch, with and without the copy.
/// </para>
/// <para>
/// A term is copied unless leaving it out keeps every one of those results and, in addition, either a library that stays
/// on then supplies an identical rule (the same spoken form, written form and word-boundary rule compared ordinally, so
/// the same compiled matcher wherever the text is) or the copy itself would change a result. A copy can: it goes ahead of
/// every library rule, and the dictionary is read sorted by spoken form rather than in precedence order, so it can beat a
/// rule the matcher applies to the same text that wins today. Decisions interact for the same reason, so the terms are
/// decided in precedence order starting from no copies, and the pass is repeated until no decision changes. Each term is
/// judged on its own spoken form and case variants: the matcher compares a text one character at a time through case
/// classes, so a copy that would change another term's text in the same class changes this term's text the same way.
/// </para>
/// <para>
/// Until this was moved here, the window copied first-wins in the review's order against the dictionary alone, a flaw
/// present since 0.4.3: switching off only the library that loses a spoken form copied its losing rule over the winner
/// that stays on. The first version here then took a rule that stays on for the same rule whenever the composer gave it
/// the same key. A winner the scan found no trace of is not kept, so nothing is copied for its spoken form; that is the
/// cleanup dropping an unused rule, as intended.
/// </para>
/// </remarks>
public static class LibrarySwitchOffCopy
{
    // In every case found so far the first pass settles every decision and the second only confirms it. The repeat is there
    // because a later decision can in principle change what an earlier one relied on; the bound only guards against two
    // decisions that keep undoing each other.
    private const int MaxPasses = 8;

    /// <summary>One row of the dictionary grid as the cleanup leaves it, with the fields dictation reads.</summary>
    public readonly record struct Row(string? Pattern, string? Replacement, bool WholeWord, bool Enabled);

    /// <summary>One row of the Libraries list: the library it stands for, and whether its box is ticked.</summary>
    public readonly record struct LibraryRow(string Id, bool BuiltIn, bool Enabled);

    /// <param name="Copies">Entries to add to the dictionary, switched on, in precedence order of their libraries.</param>
    /// <param name="Collided">
    /// Rules dictation applies today that could not be copied because a dictionary row that is switched off already has
    /// the spoken form (a duplicate would block Save), and whose loss is not provably harmless, so the user has to be told
    /// the result may change.
    /// </param>
    public sealed record Result(IReadOnlyList<DictionaryEntry> Copies, int Collided);

    /// <param name="rows">The dictionary rows after the cleanup has deleted or switched off its own entries.</param>
    /// <param name="libraries">Every loaded library, in any order.</param>
    /// <param name="libraryRows">The Libraries list's rows as they are before the switch, ticked or not.</param>
    /// <param name="switchingOff">The libraries whose rows the cleanup unticks, with the terms the review kept, in any order.</param>
    /// <param name="verdicts">
    /// What the review kept of each library it found unused terms in. A library that goes off only because it shares an id
    /// with one whose row is unticked keeps what its verdict kept, or, with no verdict, every enabled row, since the review
    /// then found nothing of it unused.
    /// </param>
    public static Result Plan(
        IEnumerable<Row> rows,
        IEnumerable<DictionaryLibrary> libraries,
        IEnumerable<LibraryRow> libraryRows,
        IEnumerable<LibraryUsage> switchingOff,
        IEnumerable<LibraryUsage>? verdicts = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(libraryRows);
        ArgumentNullException.ThrowIfNull(switchingOff);

        // The dictionary as Save stores it: both forms trimmed, blank rows skipped.
        var dictionary = Stored(rows.Select(row => (row.Pattern, row.Replacement, row.WholeWord, row.Enabled)));

        // Case-insensitive and trimmed, as the composer and the duplicate check on Save compare spoken forms.
        var existing = new HashSet<string>(dictionary.Select(entry => entry.Pattern), StringComparer.OrdinalIgnoreCase);
        var dictionaryOn = dictionary.Where(entry => entry.Enabled).ToList();
        var writtenByDictionary = new HashSet<string>(dictionaryOn.Select(entry => entry.Pattern), StringComparer.OrdinalIgnoreCase);

        var (enabled, staying, usages) = Switch(libraries, libraryRows, switchingOff, verdicts);

        // Which row the composer keeps for each spoken form, today and with only the libraries that stay on.
        var current = DictionaryLibraryOverlapAnalyzer.Coverage(enabled, enabled.Select(library => library.Id));
        var afterSwitch = DictionaryLibraryOverlapAnalyzer.Coverage(staying, staying.Select(library => library.Id));

        var candidates = new List<Candidate>();
        var considered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var usage in LibraryPrecedence.Order(usages, usage => usage.Id, usage => usage.BuiltIn))
        {
            foreach (var term in usage.KeepTerms)
            {
                var pattern = term?.Pattern?.Trim();
                if (term is null || string.IsNullOrEmpty(pattern) || writtenByDictionary.Contains(pattern))
                {
                    // Nothing to keep, or the dictionary writes this spoken form today and still will.
                    continue;
                }

                if (!current.TryGetValue(pattern, out var winner) || !IsFor(usage, winner) || !winner.Entry.Equals(term) ||
                    !considered.Add(pattern))
                {
                    // Another library's row, or another row of this one, is the rule dictation compiles for it.
                    continue;
                }

                var copy = new DictionaryEntry(0, pattern, term.Replacement, term.WholeWord, Enabled: true);
                candidates.Add(new Candidate(
                    copy,
                    Stored([(copy.Pattern, copy.Replacement, copy.WholeWord, copy.Enabled)])[0],
                    Identical: afterSwitch.TryGetValue(pattern, out var next) && Identical(next.Entry, term),
                    Blocked: existing.Contains(pattern)));
            }
        }

        if (candidates.Count == 0)
        {
            return new Result([], 0);
        }

        var dictation = new Dictation(dictionaryOn, enabled, staying, [.. candidates.Select(c => c.Saved)]);
        var probes = candidates.Select(c => CaseVariants(c.Copy.Pattern)).ToList();
        var today = probes.Select(variants => variants.Select(dictation.Today).ToList()).ToList();

        // Nothing is copied to begin with, so the first pass meets the terms in precedence order, the order they compete in
        // today, and a term that wins today is copied before any spelling it beats. Starting from every copy can stall: when
        // copies sort in the reverse of that order, removing any one of them still leaves a wrong one in front.
        var copied = new bool[candidates.Count];

        bool Keeps(int index, bool withCopy) =>
            probes[index].Select((probe, k) => (probe, k))
                .All(p => dictation.After(p.probe, j => j == index ? withCopy : copied[j]) == today[index][p.k]);

        bool LeaveOut(int index) => Keeps(index, withCopy: false) && (candidates[index].Identical || !Keeps(index, withCopy: true));

        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var changed = false;
            for (var i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Blocked)
                {
                    continue;
                }

                var copy = !LeaveOut(i);
                changed |= copy != copied[i];
                copied[i] = copy;
            }

            if (!changed)
            {
                break;
            }
        }

        var collided = candidates.Where((c, i) => c.Blocked && !LeaveOut(i)).Count();
        return new Result([.. candidates.Where((_, i) => copied[i]).Select(c => c.Copy)], collided);
    }

    private static bool IsFor(LibraryUsage usage, DictionaryLibrary library) =>
        usage.BuiltIn == library.BuiltIn && string.Equals(usage.Id, library.Id, StringComparison.OrdinalIgnoreCase);

    private static bool IsFor(LibraryUsage usage, LibraryRow row) =>
        usage.BuiltIn == row.BuiltIn && string.Equals(usage.Id, row.Id, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The libraries dictation applies before and after the switch, and every library that goes off with what the review
    /// kept of it. Dictation applies libraries by saved id, and Save saves the id of every ticked row, so a built-in and a
    /// hand-placed file that share an id go on and off together whatever their own boxes say: both apply while either row
    /// is ticked, and both go off with the last one. Unticking one while the other stays ticked changes nothing.
    /// </summary>
    private static (IReadOnlyList<DictionaryLibrary> Enabled, IReadOnlyList<DictionaryLibrary> Staying, List<LibraryUsage> GoingOff) Switch(
        IEnumerable<DictionaryLibrary> libraries,
        IEnumerable<LibraryRow> libraryRows,
        IEnumerable<LibraryUsage> switchingOff,
        IEnumerable<LibraryUsage>? verdicts)
    {
        var loaded = libraries.Where(library => library is not null).ToList();
        var selected = switchingOff.Where(usage => usage is not null).ToList();
        var ticked = libraryRows.Where(row => row.Enabled).ToList();
        var stillTicked = ticked.Where(row => !selected.Any(usage => IsFor(usage, row))).ToList();

        // LibraryPrecedence.Enabled matches ids as the saved set is read, which is what the library service applies.
        var enabled = LibraryPrecedence.Enabled(loaded, ticked.Select(row => row.Id));
        var staying = LibraryPrecedence.Enabled(loaded, stillTicked.Select(row => row.Id));

        var known = selected.Concat(verdicts?.Where(usage => usage is not null) ?? []).ToList();
        var goingOff = enabled
            .Where(library => !staying.Contains(library))
            .Select(library => known.FirstOrDefault(usage => IsFor(usage, library))
                ?? new LibraryUsage(library.Id, library.Name, [.. library.EnabledEntries], UnusedCount: 0, library.BuiltIn))
            .ToList();
        return (enabled, staying, goingOff);
    }

    private static bool IsFor(LibraryUsage usage, LibraryCoverage coverage) =>
        usage.BuiltIn == coverage.BuiltIn && string.Equals(usage.Id, coverage.LibraryId, StringComparison.OrdinalIgnoreCase);

    // The same compiled matcher and the same result wherever the text is: the spoken form exactly as it is compiled, and the
    // written form and word-boundary rule as they are applied, all compared ordinally.
    private static bool Identical(DictionaryEntry a, DictionaryEntry b) =>
        string.Equals(a.Pattern, b.Pattern, StringComparison.Ordinal) &&
        string.Equals(a.Replacement, b.Replacement, StringComparison.Ordinal) &&
        a.WholeWord == b.WholeWord;

    // The spoken form as it is, and in invariant lower and upper case, the forms dictation most often meets it in.
    private static List<string> CaseVariants(string spoken) =>
        [.. new[] { spoken, spoken.ToLowerInvariant(), spoken.ToUpperInvariant() }.Distinct(StringComparer.Ordinal)];

    // What Save stores for these rows: DictionaryEntryBuilder trims both forms and skips blank rows.
    private static IReadOnlyList<DictionaryEntry> Stored(IEnumerable<(string? Pattern, string? Replacement, bool WholeWord, bool Enabled)> rows) =>
        DictionaryEntryBuilder.Build([.. rows.Select(row => new DictionaryEntryBuilder.Row(0, row.Pattern, row.Replacement, row.WholeWord, row.Enabled))])
            .Entries;

    /// <param name="Copy">The entry handed to the window.</param>
    /// <param name="Saved">The same entry as Save will store it, which is what dictation will read.</param>
    /// <param name="Identical">A library that stays on supplies an identical rule for the spoken form after the switch.</param>
    /// <param name="Blocked">A dictionary row that is switched off has the spoken form, so the copy cannot be saved.</param>
    private sealed record Candidate(DictionaryEntry Copy, DictionaryEntry Saved, bool Identical, bool Blocked);

    /// <summary>
    /// What dictation writes today and after the switch, through the real composer and matcher: the enabled dictionary
    /// rows in the order the repository reads them back, then the composed libraries, the first rule per spoken form
    /// winning, as <see cref="TextPostProcessor"/> builds its rules. Only the rules that match a text take part in writing
    /// it, in the list's order, which gives the same result as the whole list; so each text is matched against every rule
    /// once, and every list tried after that is put together from the few that match.
    /// </summary>
    private sealed class Dictation
    {
        private readonly Dictionary<DictionaryEntry, TextPostProcessor.CompiledRule?> _compiled = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(string Pattern, bool WholeWord), int> _regexIds = [];
        private readonly List<(string Pattern, TextPostProcessor.CompiledRule? Rule)> _regexes = [];
        private readonly Dictionary<string, Matching> _byText = new(StringComparer.Ordinal);
        private readonly (DictionaryEntry Entry, int Regex)[] _today;
        private readonly (DictionaryEntry Entry, int Regex)[] _dictionary;
        private readonly (DictionaryEntry Entry, int Regex)[] _copies;
        private readonly (DictionaryEntry Entry, int Regex, int Copy)[] _staying;

        /// <param name="copies">Every copy that might be saved, as Save stores it; a list of copies is a set of indexes here.</param>
        public Dictation(
            IReadOnlyList<DictionaryEntry> dictionaryOn,
            IReadOnlyList<DictionaryLibrary> enabled,
            IReadOnlyList<DictionaryLibrary> staying,
            IReadOnlyList<DictionaryEntry> copies)
        {
            var dictionary = DictionaryLibraryComposer.Merge(dictionaryOn.OrderBy(entry => entry.Pattern, DictionaryRepository.PatternOrder), []);
            _dictionary = [.. dictionary.Select(entry => (entry, RegexOf(entry)))];
            _today = [.. DictionaryLibraryComposer.Merge(dictionary, DictionaryLibraryComposer.ComposeLibraries(enabled)).Select(entry => (entry, RegexOf(entry)))];
            _copies = [.. copies.Select(entry => (entry, RegexOf(entry)))];

            // After the switch a rule of a library that stays on is shadowed by a dictionary row with its spoken form, and by
            // a copy with it when that copy is saved, as Merge would shadow it.
            var shadowedByDictionary = new HashSet<string>(dictionary.Select(entry => entry.Pattern.Trim()), StringComparer.OrdinalIgnoreCase);
            var copyBySpokenForm = copies.Select((copy, index) => (copy.Pattern, index)).ToDictionary(p => p.Pattern, p => p.index, StringComparer.OrdinalIgnoreCase);
            _staying = [.. DictionaryLibraryComposer.ComposeLibraries(staying)
                .Where(entry => !shadowedByDictionary.Contains(entry.Pattern.Trim()))
                .Select(entry => (entry, RegexOf(entry), copyBySpokenForm.TryGetValue(entry.Pattern.Trim(), out var index) ? index : -1))];
        }

        /// <summary>What dictation writes for <paramref name="text"/> today.</summary>
        public string Today(string text)
        {
            var matching = For(text);
            return Write(matching.Text, matching.Today);
        }

        /// <summary>What dictation writes for <paramref name="text"/> after the switch, with the copies <paramref name="saved"/> picks.</summary>
        public string After(string text, Func<int, bool> saved)
        {
            var matching = For(text);
            var dictionary = matching.Dictionary
                .Concat(matching.Copies.Where(saved).Select(index => _copies[index].Entry))
                .OrderBy(entry => entry.Pattern, DictionaryRepository.PatternOrder);
            var libraries = matching.Staying.Where(s => s.Copy < 0 || !saved(s.Copy)).Select(s => s.Entry);
            return Write(matching.Text, dictionary.Concat(libraries));
        }

        private Matching For(string text)
        {
            var normalized = TextPostProcessor.NormalizeDictated(text);
            if (!_byText.TryGetValue(normalized, out var matching))
            {
                // Each distinct regex once. A spoken form is matched literally, one character for one (the matcher has no
                // multi-character case folding), so one longer than the text cannot match and is not run.
                var matches = new bool[_regexes.Count];
                for (var id = 0; id < matches.Length; id++)
                {
                    var (pattern, rule) = _regexes[id];
                    matches[id] = rule is not null && pattern.Length <= normalized.Length && rule.Matches(normalized);
                }

                matching = new Matching(
                    normalized,
                    [.. _today.Where(e => matches[e.Regex]).Select(e => e.Entry)],
                    [.. _dictionary.Where(e => matches[e.Regex]).Select(e => e.Entry)],
                    [.. Enumerable.Range(0, _copies.Length).Where(index => matches[_copies[index].Regex])],
                    [.. _staying.Where(s => matches[s.Regex]).Select(s => (s.Entry, s.Copy))]);
                _byText[normalized] = matching;
            }

            return matching;
        }

        // The regex dictation compiles for an entry depends only on its spoken form and word-boundary rule, so a copy and
        // the library row it came from share one, and so does every repeat of a row across libraries.
        private int RegexOf(DictionaryEntry entry)
        {
            var key = (entry.Pattern ?? string.Empty, entry.WholeWord);
            if (!_regexIds.TryGetValue(key, out var id))
            {
                id = _regexes.Count;
                _regexIds[key] = id;
                _regexes.Add((key.Item1, Compile(entry)));
            }

            return id;
        }

        private string Write(string normalized, IEnumerable<DictionaryEntry> entries) =>
            TextPostProcessor.ApplyDictionaryPass(normalized, entries.Select(Compile).OfType<TextPostProcessor.CompiledRule>());

        private TextPostProcessor.CompiledRule? Compile(DictionaryEntry entry)
        {
            if (!_compiled.TryGetValue(entry, out var rule))
            {
                rule = TextPostProcessor.TryCompile(entry, out _);
                _compiled[entry] = rule;
            }

            return rule;
        }

        /// <summary>The entries of each list that match one normalized text, in their list's order.</summary>
        private sealed record Matching(
            string Text,
            IReadOnlyList<DictionaryEntry> Today,
            IReadOnlyList<DictionaryEntry> Dictionary,
            IReadOnlyList<int> Copies,
            IReadOnlyList<(DictionaryEntry Entry, int Copy)> Staying);
    }
}
