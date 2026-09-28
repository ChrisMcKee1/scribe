using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// Seeded inputs for the composition oracles (LB2's kept previews, LB3's status index) and a reading of everything a
/// composition answers, as lines in order, so two compositions of the same inputs are compared member by member.
/// </summary>
internal static class CompositionOracle
{
    /// <summary>
    /// Spoken forms from a small pool, so packs share them: some differ only in case or in the white space around them,
    /// which the term key folds, and one has a double space inside, which it keeps.
    /// </summary>
    public static readonly string[] Forms =
    [
        "git hub", "GIT HUB", " git hub ", "git  hub", "jay son", "kube", "Kube", "helm", "see sharp", "dot net",
        "kay eight ess", "pie torch", "type script", "um",
    ];

    /// <summary>
    /// Written forms, two of most spellings so rows agree and disagree, a removal (empty) and a template (two lines), which
    /// the glossary never carries.
    /// </summary>
    public static readonly string[] Written =
    [
        "GitHub", "Github", "JSON", "Json", "Kubernetes", "K8s", "Helm", "C#", ".NET", "dotnet", "PyTorch", "TypeScript", "",
        "Best,\nChris",
    ];

    /// <summary>Term budgets from none to the cloud's, the small ones cutting the glossary short.</summary>
    public static readonly int[] Budgets = [0, 1, 2, 3, 80, CleanupPrompt.MaxGlossaryTermsCloud];

    private static readonly string[] CustomIds = ["team", "alpha", "custom-github", "team-2"];

    public static T Pick<T>(Random random, IReadOnlyList<T> values) => values[random.Next(values.Count)];

    /// <summary>
    /// Runs <paramref name="body"/> once for each of <paramref name="count"/> seeds from <paramref name="first"/>, each with
    /// a <see cref="Random"/> of its own, so a failing seed reruns alone; any failure, an exception included, names its seed.
    /// </summary>
    public static void ForEachSeed(int first, int count, Action<int, Random> body)
    {
        for (var seed = first; seed < first + count; seed++)
        {
            try
            {
                body(seed, new Random(seed));
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Seed {seed} failed (rerun it alone with new Random({seed})): {exception.Message}", exception);
            }
        }
    }

    /// <summary>
    /// A catalog of up to five built-ins from <paramref name="builtInIds"/> and up to four custom packs over
    /// <see cref="Forms"/> and <see cref="Written"/>. A custom pack often holds a form twice, and a built-in does too through
    /// a renamed row; built-ins and custom packs share forms. Most packs are off when <paramref name="mostlyOff"/>; AI
    /// permission, accepted content, legacy markers, reviews, a remapped file name and (with <paramref name="anyFileState"/>)
    /// file states vary. With <paramref name="canonicalBuiltIns"/>, every built-in row is one the overlay gives (see
    /// <see cref="CanonicalBuiltInRow"/>), which the workspace's commands require; otherwise rows are built directly, some
    /// of them shapes the overlay never gives, which a composition reads all the same.
    /// </summary>
    public static LibraryCatalog RandomCatalog(
        Random random, IReadOnlyList<string> builtInIds, bool mostlyOff, bool anyFileState, bool canonicalBuiltIns = false)
    {
        var libraries = new List<CatalogLibrary>();
        var enabled = new List<string>();
        var ai = new List<(string Id, bool Permitted)>();
        var accepted = new List<(string Id, LibraryContentHash Hash)>();
        var markers = new List<(string Id, string Key)>();
        var onInTen = mostlyOff ? 3 : 9;

        foreach (var id in builtInIds.Where(_ => random.Next(4) > 0))
        {
            // A built-in keeps one row per shipped key, as its edits document does.
            var rows = new List<LibraryRow>();
            var keys = new HashSet<LibraryTermKey>();
            for (var count = random.Next(7); count > 0; count--)
            {
                var spoken = Pick(random, Forms);
                if (!keys.Add(Key(spoken)))
                {
                    continue;
                }

                var written = Pick(random, Written);
                var wholeWord = random.Next(4) > 0;
                rows.Add(canonicalBuiltIns ? CanonicalBuiltInRow(random, id, spoken, written, wholeWord) : random.Next(10) switch
                {
                    < 5 => Shipped(spoken, written, wholeWord),
                    5 or 6 => Edited(spoken, written, Pick(random, Forms), Pick(random, Written), wholeWord),
                    7 => Added(spoken, written),
                    8 => TurnedOff(spoken, written),
                    _ => Edited(spoken, written, spoken, Pick(random, Written)) with
                    {
                        Review = new TermReview(
                            new TermValues(spoken, Pick(random, Written)), new TermValues(spoken, Pick(random, Written)), TermFields.Written),
                    },
                });
            }

            libraries.Add(Committed(BuiltInLibrary(id, [.. rows]), state: FileState(random, anyFileState)));
            if (random.Next(10) < onInTen)
            {
                enabled.Add(id);
            }

            if (random.Next(5) == 0)
            {
                ai.Add((id, random.Next(2) == 0));
            }
        }

        var digit = '1';
        foreach (var id in CustomIds.Where(_ => random.Next(3) > 0))
        {
            var rows = new List<LibraryRow>();
            for (var count = random.Next(8); count > 0; count--)
            {
                rows.Add(Custom(Pick(random, Forms), Pick(random, Written), wholeWord: random.Next(4) > 0, enabled: random.Next(5) > 0));
            }

            var hash = Hash(digit++);
            var fileName = id == "custom-github" && random.Next(2) == 0 ? "github.csv" : null;
            libraries.Add(Committed(CustomLibrary(id, [.. rows]), hash, fileName, FileState(random, anyFileState)));
            if (random.Next(10) < onInTen)
            {
                enabled.Add(id);
            }

            if (random.Next(3) > 0)
            {
                ai.Add((id, random.Next(4) > 0));
            }

            switch (random.Next(10))
            {
                case < 7:
                    accepted.Add((id, hash));
                    break;
                case 7:
                    accepted.Add((id, Hash('f')));
                    break;
            }

            if (rows.Count > 0 && random.Next(3) == 0)
            {
                markers.Add((id, rows[random.Next(rows.Count)].Key.Value));
            }
        }

        // The catalog lists them in any order; precedence decides.
        Shuffle(random, libraries);
        return Catalog(State(enabled, ai, markers, accepted, lost: random.Next(12) == 0), [.. libraries]);
    }

    /// <summary>
    /// A built-in row the overlay gives for a shipped row of <paramref name="spoken"/>: shipped, edited or pinned by the
    /// overlay's own Edit (a rename included, so a built-in can hold a form twice), added, turned off, or edited against an
    /// older shipped value, which leaves a question the user owes (<see cref="LibraryRow.Review"/>).
    /// </summary>
    public static LibraryRow CanonicalBuiltInRow(Random random, string libraryId, string spoken, string written, bool wholeWord)
    {
        var overlay = BuiltInLibraryOverlay.Instance;
        var shipped = Shipped(spoken, written, wholeWord);
        switch (random.Next(10))
        {
            case < 5:
                return shipped;
            case 5 or 6:
                return overlay.Edit(shipped, new TermValues(Pick(random, Forms), Pick(random, Written), random.Next(4) > 0));
            case 7:
                return overlay.Add(new TermValues(spoken, written, wholeWord));
            case 8:
                return overlay.SetEnabled(shipped, false);
            default:
                var library = new DictionaryLibrary(
                    libraryId, "Name of " + libraryId, "General", null, BuiltIn: true, [shipped.Values.ToEntry()]);
                var edit = new BuiltInTermEdit(
                    shipped.Key,
                    BuiltInTermIntent.Edited,
                    shipped.Values with { Written = "Older " + written },
                    shipped.Values with { Written = Pick(random, Written) });
                return overlay.Apply(library, new BuiltInLibraryEdits(libraryId, [edit]))[0];
        }
    }

    /// <summary>Up to five personal entries over the same forms, some turned off.</summary>
    public static List<DictionaryEntry> RandomDictionary(Random random)
    {
        var entries = new List<DictionaryEntry>();
        for (var count = random.Next(6); count > 0; count--)
        {
            entries.Add(RandomEntry(random, entries.Count + 1));
        }

        return entries;
    }

    public static DictionaryEntry RandomEntry(Random random, long id) =>
        new(id, Pick(random, Forms), Pick(random, Written), WholeWord: random.Next(4) > 0, Enabled: random.Next(5) > 0);

    /// <summary>
    /// A draft of <paramref name="catalog"/> built directly: packs switched on and off, AI permission chosen, some packs
    /// pending deletion, and some whose content the Save writes, one of those with a row added.
    /// </summary>
    public static LibraryDraft RandomDraft(Random random, LibraryCatalog catalog)
    {
        var committed = catalog.LocalState;
        var enabled = catalog.Libraries
            .Select(library => library.Content.Id)
            .Where(id => committed.EnabledIds.Contains(id) != (random.Next(4) == 0))
            .ToList();
        var ai = catalog.Libraries.Where(_ => random.Next(2) == 0).Select(library => (library.Content.Id, random.Next(3) > 0)).ToList();
        var state = State(
            enabled,
            ai,
            committed.LegacyMarkers.Select(marker => (marker.LibraryId, marker.Key.Value)),
            committed.AcceptedContent.Select(pair => (pair.Key, pair.Value)),
            lost: committed.AiPermissionsLost);
        var libraries = catalog.Libraries.Select(library =>
        {
            var writes = random.Next(4) == 0;
            var content = writes && !library.Content.BuiltIn && random.Next(2) == 0
                ? library.Content with { Rows = [.. library.Content.Rows, Custom(Pick(random, Forms), Pick(random, Written))] }
                : library.Content;
            return new DraftLibrary(
                content, LibraryOrigin.Existing, library.State, PendingDelete: random.Next(10) == 0, Unsaved: writes,
                library.FileName, WritesContent: writes);
        });
        return Draft(random.Next(1, 100), state, [.. libraries]);
    }

    /// <summary>Every library id the contents name, and one no library has.</summary>
    public static List<string> LibraryIds(IEnumerable<LibraryContent> contents) =>
        [.. contents.Select(content => content.Id).Distinct(StringComparer.OrdinalIgnoreCase), "missing"];

    /// <summary>Every row's identity and competing key, and every form of the pool, each once.</summary>
    public static List<LibraryTermKey> Keys(IEnumerable<LibraryContent> contents) =>
        [.. contents
            .SelectMany(content => content.Rows)
            .SelectMany(row => new[] { row.Key, LibraryTiers.CompetingKey(row) })
            .Concat(Forms.Select(Key))
            .Where(key => !key.IsEmpty)
            .Distinct()];

    /// <summary>
    /// Everything <paramref name="view"/> answers, in order: its flags, rules, library entries and the AI subset, the
    /// excluded ids, the libraries in use with their entries, the badges, the glossary's entries and text for
    /// <paramref name="dictionary"/> and <paramref name="budget"/>, the Save prompt's overlaps, each library's filters, and
    /// the status of every key in every library, winner content included.
    /// </summary>
    public static string[] Read(
        CompositionView view,
        IReadOnlyList<string> libraryIds,
        IReadOnlyList<LibraryTermKey> keys,
        IReadOnlyList<DictionaryEntry> dictionary,
        GlossaryBudget budget)
    {
        var lines = new List<string> { $"preview={view.IsPreview} basis={view.Basis} legacy={view.AnyLegacyMarkerActive}" };
        lines.AddRange(view.Rules.Select(rule => $"rule {rule.LibraryId} {rule.Key.Value} {rule.Tier} {rule.LegacyMarkerActive} {Entry(rule.Entry)}"));
        lines.AddRange(view.LibraryEntries.Select(entry => "entry " + Entry(entry)));
        lines.AddRange(view.AiLibraryEntries.Select(entry => "ai " + Entry(entry)));
        lines.Add("excluded " + string.Join(",", view.AiExcludedLibraryIds));
        foreach (var library in view.EnabledLibraries)
        {
            lines.Add($"library {library.Id} {library.Name} {library.Category} {library.Description ?? "-"} {library.BuiltIn} {library.FileName ?? "-"}");
            lines.AddRange(library.Entries.Select(entry => "  " + Entry(entry)));
        }

        foreach (var (spoken, coverage) in view.Coverage())
        {
            lines.Add($"covers {spoken} {Entry(coverage.Entry)} {coverage.LibraryId} {coverage.LibraryName} {coverage.BuiltIn} {coverage.FileName ?? "-"}");
        }

        var vocabulary = CleanupPrompt.ComposeVocabulary(dictionary, view.AiLibraryEntries);
        lines.AddRange(vocabulary.Select(entry => "glossary entry " + Entry(entry)));
        lines.AddRange(CleanupPrompt.BuildGlossary(vocabulary, budget.MaxTerms).Split('\n').Select(line => "glossary " + line));
        lines.Add("overlaps " + string.Join(";", view.OverlapReport(dictionary).Overlaps));
        foreach (var id in libraryIds)
        {
            foreach (var filter in Enum.GetValues<TermFilter>())
            {
                lines.Add($"filter {id} {filter}: {string.Join(",", view.Filter(id, filter).Select(key => key.Value))}");
            }

            foreach (var key in keys)
            {
                var status = view.StatusOf(id, key);
                lines.Add(
                    $"status {id}/{key.Value}: {status.Marker} {status.Winner} {status.WinningLibraryId ?? "-"} " +
                    $"{Entry(status.WinningEntry)} same=[{string.Join(",", status.SameResultIn)}] " +
                    $"different=[{string.Join(",", status.DifferentResultIn)}] review={status.Review?.ToString() ?? "-"} " +
                    $"legacy={status.LegacyMarkerActive} glossary={status.Glossary}");
            }
        }

        return [.. lines];
    }

    /// <summary>Fails at the first line that differs, naming both, or when one reading is longer.</summary>
    public static void AssertSameLines(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string context)
    {
        for (var i = 0; i < Math.Min(expected.Count, actual.Count); i++)
        {
            if (!string.Equals(expected[i], actual[i], StringComparison.Ordinal))
            {
                Assert.Fail($"{context}: line {i + 1} differs.\nexpected: {expected[i]}\nactual:   {actual[i]}");
            }
        }

        Assert.True(expected.Count == actual.Count, $"{context}: {expected.Count} lines expected, {actual.Count} read.");
    }

    public static string Entry(DictionaryEntry? entry) =>
        entry is null
            ? "-"
            : $"{entry.Id}|{Escape(entry.Pattern)}|{Escape(entry.Replacement)}|{entry.WholeWord}|{entry.Enabled}";

    private static string Escape(string value) => value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static LibraryFileState FileState(Random random, bool anyFileState)
    {
        var states = Enum.GetValues<LibraryFileState>();
        return anyFileState && random.Next(6) == 0 ? states[random.Next(states.Length)] : LibraryFileState.Available;
    }

    private static void Shuffle<T>(Random random, List<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}

/// <summary>What a composition answers, taken from either implementation, so one reading serves both.</summary>
internal sealed record CompositionView(
    bool IsPreview,
    long Basis,
    bool AnyLegacyMarkerActive,
    IReadOnlyList<ComposedRule> Rules,
    IReadOnlyList<DictionaryEntry> LibraryEntries,
    IReadOnlyList<DictionaryEntry> AiLibraryEntries,
    IReadOnlySet<string> AiExcludedLibraryIds,
    IReadOnlyList<DictionaryLibrary> EnabledLibraries,
    Func<IReadOnlyDictionary<string, LibraryCoverage>> Coverage,
    Func<IReadOnlyList<DictionaryEntry>, DictionaryOverlapReport> OverlapReport,
    Func<string, TermFilter, IReadOnlyList<LibraryTermKey>> Filter,
    Func<string, LibraryTermKey, TermStatus> StatusOf)
{
    public static CompositionView Of(LibraryComposition composition) => new(
        composition.IsPreview, composition.Basis, composition.AnyLegacyMarkerActive, composition.Rules, composition.LibraryEntries,
        composition.AiLibraryEntries, composition.AiExcludedLibraryIds, composition.EnabledLibraries, composition.Coverage,
        composition.OverlapReport, composition.Filter, composition.StatusOf);

    public static CompositionView Of(LibraryCompositionAt10c9a0b composition) => new(
        composition.IsPreview, composition.Basis, composition.AnyLegacyMarkerActive, composition.Rules, composition.LibraryEntries,
        composition.AiLibraryEntries, composition.AiExcludedLibraryIds, composition.EnabledLibraries, composition.Coverage,
        composition.OverlapReport, composition.Filter, composition.StatusOf);
}
