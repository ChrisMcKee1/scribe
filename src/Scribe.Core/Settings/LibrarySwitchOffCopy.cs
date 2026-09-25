using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Settings;

/// <summary>
/// Which libraries the dictionary cleanup switches off, and which still-used library terms it copies into the user's
/// dictionary first. A library goes off as a whole, so its working rules have to move into the dictionary, and the move
/// must not change what dictation writes; a library for which that cannot be shown is kept on instead.
/// </summary>
/// <remarks>
/// <para>
/// Which libraries are on before and after the switch comes from the Libraries list's rows the way Save stores them, as
/// ids, and the library service applies every loaded library with a saved id. So a built-in and a hand-placed file that
/// share an id go on and off together: unticking one while the other's row stays ticked switches nothing off in
/// dictation, and unticking the last row with the id switches both off, the one whose row was already unticked included.
/// Every library that goes off keeps what the review kept of it, or every enabled row when the review has no verdict on it.
/// </para>
/// <para>
/// A library that would go off is switched off only when none of its enabled rows, used or not, overlaps a rule that stays
/// in effect (an enabled dictionary row, or an enabled row of a library that stays on) or a rule copied from another
/// library; when no two of its own copies fold alike; and when no row of it that goes (every enabled row that is not
/// copied, a blocked copy included) meets a row copied from it or from another library going off, or a row of a library
/// kept on, or reaches one of those through rules that stay in effect. Two spoken forms overlap when one, folded by
/// <see cref="SpokenFormFold"/>, equals or contains the other; they meet when they overlap or a nonempty proper suffix of
/// one is a proper prefix of the other, either way round, whole-word flags ignored.
/// </para>
/// <para>
/// The fold is broader than every comparison dictation makes, and every spoken form is an escaped literal that matches
/// exactly its own length of text, so spoken forms that fold differently never match the same text and are never one key
/// to the composer. Then a copy writes exactly what its rule wrote: rule order only decides between rules that match the
/// same text, which a copy shares with no rule that stays in effect and with no other copy, and its key is one that no
/// rule that stays on has, so it hides nothing and nothing hides it. Only forms that fold alike tie that way; containment
/// is refused across libraries as well, to stay well clear of that edge. Copies of one library may meet one another,
/// since where they start and how long they are decide between them before their order does.
/// </para>
/// <para>
/// A row that goes, meanwhile, could decide texts a copy or a row kept on takes once it is gone, because the matcher takes
/// the match that starts first, then the longer one: "kilo" beside "k" gives "kilogram" with both and "Kilo" with "k"
/// alone, and "ab" beside "bc" gives "Yc" for "abc" with both and "aX" with "bc" alone. So it must not meet any of them.
/// Nor may it reach one through rules that stay in effect: with "bc" staying on beside "ab" and "cd", "abcd" is "YZ",
/// and without "ab" the freed "bc" pushes "cd" out and writes "aXd". Such a push can pass along any number of rules, so
/// rules that stay in effect and meet one another form groups (see Linked), and no row that goes may meet a group that a
/// copy or a row of a library kept on meets. A row that goes may still run into a dictionary row or a row of a library
/// that stays on without being asked about, and that rule may then apply where the row that goes used to. The promise is
/// that copies and libraries kept on write exactly what they wrote, and that a text a dropped row applied to loses it,
/// which a rule that stays in effect may then take. With the default libraries on, the rules that stay in effect form one
/// group that every shipped spoken form meets, so in practice a library with a row that goes is switched off only when
/// nothing is copied and nothing is kept on.
/// </para>
/// <para>
/// Otherwise the library is kept on, whole, with nothing copied from it, together with every library that shares its id;
/// its rows then stay in effect as well, so the decision is repeated until no more libraries are kept on. Keeping a
/// library on only ever adds rules that stay in effect, so the repeat settles, and on the same libraries in any order.
/// </para>
/// <para>
/// A library that is switched off has each kept row copied that dictation compiles today (no enabled dictionary row has
/// its spoken form, and it is the first enabled row for it in precedence order, <see cref="LibraryPrecedence"/>), unless a
/// dictionary row that is switched off already has the spoken form: a duplicate would block Save, so that term is
/// reported instead. A kept row that dictation does not compile is never applied (another row beats it), so losing it
/// changes nothing. A row whose copy Save would store differently (spaces around its spoken or written form, which no
/// library file can hold because the loader trims both) cannot be copied as it is, and keeps its library on.
/// </para>
/// <para>
/// The window used to copy first-wins in the review's order against the dictionary alone, a flaw present since 0.4.3 that
/// copied a losing rule over a winner that stayed on. Later versions here compared the composer's keys, then ran the
/// matcher over each term's own spoken form; both missed rules that meet the same text in other ways (inside a word,
/// or in text already in its written form).
/// </para>
/// </remarks>
public static class LibrarySwitchOffCopy
{
    /// <summary>One row of the dictionary grid as the cleanup leaves it, with the fields dictation reads.</summary>
    public readonly record struct Row(string? Pattern, string? Replacement, bool WholeWord, bool Enabled);

    /// <summary>One row of the Libraries list: the library it stands for, and whether its box is ticked.</summary>
    public readonly record struct LibraryRow(string Id, bool BuiltIn, bool Enabled);

    /// <summary>A library the cleanup was asked to switch off and keeps on instead.</summary>
    /// <param name="OverlappingTerms">
    /// How many enabled rows keep it on, across this library and every library that shares its id and would have gone off
    /// with it: rows that overlap a rule that stays in effect, a copy, or one another, and rows that would go and meet a
    /// copy or a row kept on, directly or through rules that stay in effect.
    /// </param>
    public sealed record KeptOnLibrary(string Id, bool BuiltIn, string Name, int OverlappingTerms);

    /// <param name="Copies">Entries to add to the dictionary, switched on, in precedence order of their libraries.</param>
    /// <param name="Collided">
    /// Rules dictation applies today, of libraries that are switched off, that could not be copied because a dictionary
    /// row that is switched off already has the spoken form (a duplicate would block Save), so the user has to be told.
    /// </param>
    /// <param name="KeptOn">
    /// The libraries asked for that stay on, in precedence order: their rows must stay ticked, and nothing of them is copied.
    /// </param>
    public sealed record Result(IReadOnlyList<DictionaryEntry> Copies, int Collided, IReadOnlyList<KeptOnLibrary> KeptOn)
    {
        /// <summary>Whether the cleanup keeps this library on, so its row must stay ticked.</summary>
        public bool KeepsOn(string id, bool builtIn) =>
            KeptOn.Any(library => library.BuiltIn == builtIn && string.Equals(library.Id, id, StringComparison.OrdinalIgnoreCase));
    }

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

        // The dictionary as Save stores it: both forms trimmed, blank rows skipped. Any row with a spoken form blocks a copy
        // of it, since Save refuses a duplicate whether or not the row is on.
        var dictionary = Stored(rows.Select(row => (row.Pattern, row.Replacement, row.WholeWord, row.Enabled)));
        var blocking = new HashSet<string>(dictionary.Select(entry => entry.Pattern), StringComparer.OrdinalIgnoreCase);
        var dictionaryOn = dictionary.Where(entry => entry.Enabled).ToList();
        var writtenByDictionary = new HashSet<string>(dictionaryOn.Select(entry => entry.Pattern), StringComparer.OrdinalIgnoreCase);

        var (enabled, staying, goingOff, selected) = Switch(libraries, libraryRows, switchingOff, verdicts);
        if (goingOff.Count == 0)
        {
            return new Result([], 0, []);
        }

        var folds = new Dictionary<string, string>(StringComparer.Ordinal);
        string Folded(string spoken)
        {
            if (!folds.TryGetValue(spoken, out var folded))
            {
                folds[spoken] = folded = SpokenFormFold.Fold(spoken);
            }

            return folded;
        }

        // Which row the composer keeps for each spoken form today.
        var current = DictionaryLibraryOverlapAnalyzer.Coverage(enabled, enabled.Select(library => library.Id));
        var considered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sides = goingOff.Select(g => Side.For(g.Library, g.Usage, current, writtenByDictionary, blocking, considered, Folded)).ToList();

        // What stays in effect whatever is decided here.
        var inEffect = dictionaryOn.Select(entry => entry.Pattern)
            .Concat(staying.SelectMany(SpokenForms))
            .Select(Folded)
            .ToHashSet(StringComparer.Ordinal);

        // Where each row would reach through rules that stay in effect: see Linked.
        var links = Linked([.. inEffect], sides);

        var count = sides.Count;
        var againstInEffect = sides.Select(side => Overlapping(side.Folds, inEffect, runsInto: _ => false)).ToArray();
        var againstRows = new HashSet<int>[count, count];
        var againstCopies = new HashSet<int>[count, count];
        for (var i = 0; i < count; i++)
        {
            for (var j = 0; j < count; j++)
            {
                if (i != j)
                {
                    // A row that is not copied goes, so it must not meet what remains in the text either way round.
                    var side = sides[i];
                    againstRows[i, j] = Overlapping(side.Folds, sides[j].RowFolds, runsInto: row => !side.Copied.Contains(row));
                    againstCopies[i, j] = Overlapping(side.Folds, sides[j].CopyFolds, runsInto: row => !side.Copied.Contains(row));
                }
            }
        }

        var keptOn = new bool[count];
        HashSet<int> Overlaps(int i)
        {
            var found = new HashSet<int>(againstInEffect[i]);
            found.UnionWith(sides[i].OwnProblems);
            for (var j = 0; j < count; j++)
            {
                if (j != i)
                {
                    // A library kept on stays in effect with every row; one that goes off leaves only its copies.
                    found.UnionWith(keptOn[j] ? againstRows[i, j] : againstCopies[i, j]);
                }
            }

            // Nor may a row that goes reach one of those rows, or a copy of its own library's, through rules that stay in
            // effect.
            var guarded = new HashSet<int>();
            for (var j = 0; j < count; j++)
            {
                for (var row = 0; row < sides[j].Folds.Length; row++)
                {
                    if ((j != i && keptOn[j]) || sides[j].Copied.Contains(row))
                    {
                        guarded.UnionWith(links[j][row]);
                    }
                }
            }

            for (var row = 0; row < sides[i].Folds.Length; row++)
            {
                if (!sides[i].Copied.Contains(row) && links[i][row].Overlaps(guarded))
                {
                    found.Add(row);
                }
            }

            return found;
        }

        var groups = Enumerable.Range(0, count)
            .GroupBy(i => sides[i].Library.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.ToArray())
            .ToList();
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var group in groups.Where(group => !keptOn[group[0]] && group.Any(i => Overlaps(i).Count > 0)))
            {
                foreach (var i in group)
                {
                    keptOn[i] = true;
                }

                changed = true;
            }
        }

        var copies = new List<DictionaryEntry>();
        var collided = 0;
        for (var i = 0; i < count; i++)
        {
            if (keptOn[i])
            {
                continue;
            }

            foreach (var candidate in sides[i].Candidates)
            {
                if (candidate.Blocked)
                {
                    collided++;
                }
                else
                {
                    copies.Add(candidate.Copy);
                }
            }
        }

        // A row asked for stays ticked when its id's libraries are kept on, since the saved id is what keeps them on.
        var kept = new List<KeptOnLibrary>();
        foreach (var usage in selected)
        {
            var group = groups.FirstOrDefault(g => string.Equals(sides[g[0]].Library.Id, usage.Id, StringComparison.OrdinalIgnoreCase));
            if (group is not null && keptOn[group[0]])
            {
                kept.Add(new KeptOnLibrary(usage.Id, usage.BuiltIn, usage.Name, group.Sum(i => Overlaps(i).Count)));
            }
        }

        return new Result(copies, collided, kept);
    }

    /// <summary>
    /// The notice for the libraries the cleanup kept on, naming them in plain words. It never names a term.
    /// </summary>
    public static string DescribeKeptOn(IReadOnlyList<KeptOnLibrary> keptOn)
    {
        ArgumentNullException.ThrowIfNull(keptOn);
        if (keptOn.Count == 0)
        {
            return string.Empty;
        }

        var names = keptOn.Select(library => $"\"{library.Name}\"").ToList();
        var list = names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
        return names.Count == 1
            ? $"Kept on: {list}. Some of its terms overlap other terms dictation applies, so switching it off could change "
                + "what dictation writes."
            : $"Kept on: {list}. Some of their terms overlap other terms dictation applies, so switching them off could "
                + "change what dictation writes.";
    }

    private static bool IsFor(LibraryUsage usage, DictionaryLibrary library) =>
        usage.BuiltIn == library.BuiltIn && string.Equals(usage.Id, library.Id, StringComparison.OrdinalIgnoreCase);

    private static bool IsFor(LibraryUsage usage, LibraryRow row) =>
        usage.BuiltIn == row.BuiltIn && string.Equals(usage.Id, row.Id, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The libraries dictation applies before and after the switch, every library that goes off with what the review kept
    /// of it (in precedence order), and the libraries asked for (once each, in precedence order). Dictation applies
    /// libraries by saved id, and Save saves the id of every ticked row, so a built-in and a hand-placed file that share an
    /// id go on and off together whatever their own boxes say: both apply while either row is ticked, and both go off with
    /// the last one. Unticking one while the other stays ticked changes nothing.
    /// </summary>
    private static (IReadOnlyList<DictionaryLibrary> Enabled, IReadOnlyList<DictionaryLibrary> Staying,
        IReadOnlyList<(DictionaryLibrary Library, LibraryUsage Usage)> GoingOff, IReadOnlyList<LibraryUsage> Selected) Switch(
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
            .Select(library => (library, known.FirstOrDefault(usage => IsFor(usage, library))
                ?? new LibraryUsage(library.Id, library.Name, [.. library.EnabledEntries], UnusedCount: 0, library.BuiltIn)))
            .ToList();

        // Only a library whose row is ticked can be switched off, or kept on.
        var asked = LibraryPrecedence.Order(
            selected
                .Where(usage => ticked.Any(row => IsFor(usage, row)))
                .GroupBy(usage => usage.Id, StringComparer.OrdinalIgnoreCase)
                .SelectMany(sameId => sameId.DistinctBy(usage => usage.BuiltIn)),
            usage => usage.Id,
            usage => usage.BuiltIn);
        return (enabled, staying, goingOff, asked);
    }

    private static IEnumerable<string> SpokenForms(DictionaryLibrary library) =>
        library.EnabledEntries.Where(entry => !string.IsNullOrWhiteSpace(entry.Pattern)).Select(entry => entry.Pattern.Trim());

    // The positions in `folds` whose spoken form meets any of `others`; `runsInto` says for which positions running into
    // one counts as well as containing or sitting inside it.
    private static HashSet<int> Overlapping(IReadOnlyList<string> folds, IReadOnlyCollection<string> others, Func<int, bool> runsInto)
    {
        var found = new HashSet<int>();
        for (var i = 0; i < folds.Count; i++)
        {
            var alsoRunningInto = runsInto(i);
            foreach (var other in others)
            {
                if (Meet(folds[i], other, alsoRunningInto))
                {
                    found.Add(i);
                    break;
                }
            }
        }

        return found;
    }

    // Whether two folded spoken forms can meet in one text: one equals or contains the other, or, with `runningInto`, a
    // nonempty proper suffix of one is a proper prefix of the other, either way round, so a text can hold both sharing the
    // stretch between them. Whole-word flags are ignored, which only ever finds more.
    private static bool Meet(string a, string b, bool runningInto)
    {
        if (a.Length >= b.Length ? a.Contains(b, StringComparison.Ordinal) : b.Contains(a, StringComparison.Ordinal))
        {
            return true;
        }

        if (!runningInto)
        {
            return false;
        }

        for (var shared = 1; shared < Math.Min(a.Length, b.Length); shared++)
        {
            if (a.AsSpan(a.Length - shared).SequenceEqual(b.AsSpan(0, shared)) ||
                b.AsSpan(b.Length - shared).SequenceEqual(a.AsSpan(0, shared)))
            {
                return true;
            }
        }

        return false;
    }

    // For each row of each library that would go off, the groups of rules that stay in effect it meets. Rules that stay in
    // effect and meet fall into one group, and so do rules linked through others: the matcher takes the match that starts
    // first, so a row that goes can free a rule it met to apply, which then pushes out the next rule it meets, and so on
    // along the text ("ab" gone beside "bc" and "cd" turns "abcd" from "YZ" into "aXd"). A row that goes and a copy or a
    // row kept on that meet one group can therefore reach each other. Whole-word flags are ignored, which only finds more.
    private static HashSet<int>[][] Linked(string[] inEffect, IReadOnlyList<Side> sides)
    {
        var group = Enumerable.Range(0, inEffect.Length).ToArray();
        int Root(int k)
        {
            while (group[k] != k)
            {
                group[k] = group[group[k]];
                k = group[k];
            }

            return k;
        }

        for (var a = 0; a < inEffect.Length; a++)
        {
            for (var b = a + 1; b < inEffect.Length; b++)
            {
                if (Root(a) != Root(b) && Meet(inEffect[a], inEffect[b], runningInto: true))
                {
                    group[Root(a)] = Root(b);
                }
            }
        }

        // One group is enough to know a row meets it, so the rest of that group's rules are skipped.
        return [.. sides.Select(side => side.Folds.Select(fold =>
        {
            var met = new HashSet<int>();
            for (var k = 0; k < inEffect.Length; k++)
            {
                var root = Root(k);
                if (!met.Contains(root) && Meet(fold, inEffect[k], runningInto: true))
                {
                    met.Add(root);
                }
            }

            return met;
        }).ToArray())];
    }

    // The same rule wherever the text is: the spoken form exactly as it is compiled, and the written form and word-boundary
    // rule as they are applied, all compared ordinally.
    private static bool Identical(DictionaryEntry a, DictionaryEntry b) =>
        string.Equals(a.Pattern, b.Pattern, StringComparison.Ordinal) &&
        string.Equals(a.Replacement, b.Replacement, StringComparison.Ordinal) &&
        a.WholeWord == b.WholeWord;

    // What Save stores for these rows: DictionaryEntryBuilder trims both forms and skips blank rows.
    private static IReadOnlyList<DictionaryEntry> Stored(IEnumerable<(string? Pattern, string? Replacement, bool WholeWord, bool Enabled)> rows) =>
        DictionaryEntryBuilder.Build([.. rows.Select(row => new DictionaryEntryBuilder.Row(0, row.Pattern, row.Replacement, row.WholeWord, row.Enabled))])
            .Entries;

    /// <param name="Row">The position of the row it copies among the library's rows with a spoken form.</param>
    /// <param name="Copy">The entry handed to the window.</param>
    /// <param name="Blocked">A dictionary row that is switched off has the spoken form, so the copy cannot be saved.</param>
    /// <param name="Exact">Save stores the copy as exactly the rule it copies.</param>
    private sealed record Candidate(int Row, DictionaryEntry Copy, bool Blocked, bool Exact);

    /// <summary>A library that would go off: its enabled rows, folded, and the copies it would leave.</summary>
    private sealed class Side
    {
        public required DictionaryLibrary Library { get; init; }

        /// <summary>Every enabled row with a spoken form, folded, by position.</summary>
        public required string[] Folds { get; init; }

        public required HashSet<string> RowFolds { get; init; }

        public required IReadOnlyList<Candidate> Candidates { get; init; }

        /// <summary>The copies it would make, folded.</summary>
        public required HashSet<string> CopyFolds { get; init; }

        /// <summary>The positions of the rows it would copy; every other row goes with the library.</summary>
        public required HashSet<int> Copied { get; init; }

        /// <summary>Rows whose copies the matcher could read as the same text as another copy of this library's, that
        /// Save would store differently, or that go and meet a row of this library's that is copied.</summary>
        public required HashSet<int> OwnProblems { get; init; }

        public static Side For(
            DictionaryLibrary library,
            LibraryUsage usage,
            IReadOnlyDictionary<string, LibraryCoverage> current,
            HashSet<string> writtenByDictionary,
            HashSet<string> blocking,
            HashSet<string> considered,
            Func<string, string> folded)
        {
            var rows = library.EnabledEntries.Where(entry => !string.IsNullOrWhiteSpace(entry.Pattern)).ToList();
            var folds = rows.Select(entry => folded(entry.Pattern.Trim())).ToArray();

            var candidates = new List<Candidate>();
            foreach (var term in usage.KeepTerms)
            {
                var pattern = term?.Pattern?.Trim();
                if (term is null || string.IsNullOrEmpty(pattern) || writtenByDictionary.Contains(pattern))
                {
                    // Nothing to keep, or the dictionary writes this spoken form today and still will.
                    continue;
                }

                if (!current.TryGetValue(pattern, out var winner) || winner.BuiltIn != library.BuiltIn ||
                    !string.Equals(winner.LibraryId, library.Id, StringComparison.OrdinalIgnoreCase) ||
                    !winner.Entry.Equals(term) || !considered.Add(pattern))
                {
                    // Another library's row, or another row of this one, is the rule dictation compiles for it.
                    continue;
                }

                var row = rows.FindIndex(entry => ReferenceEquals(entry, winner.Entry));
                if (row < 0)
                {
                    // The winner is not a row of this library, so it is not this library's rule.
                    continue;
                }

                var copy = new DictionaryEntry(0, pattern, term.Replacement, term.WholeWord, Enabled: true);
                var saved = Stored([(copy.Pattern, copy.Replacement, copy.WholeWord, copy.Enabled)])[0];
                candidates.Add(new Candidate(row, copy, Blocked: blocking.Contains(pattern), Exact: Identical(saved, winner.Entry)));
            }

            var copying = candidates.Where(candidate => !candidate.Blocked).ToList();
            var copied = copying.Select(candidate => candidate.Row).ToHashSet();
            var ownProblems = new HashSet<int>(copying.Where(candidate => !candidate.Exact).Select(candidate => candidate.Row));
            foreach (var tied in copying.GroupBy(candidate => folds[candidate.Row], StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                ownProblems.UnionWith(tied.Select(candidate => candidate.Row));
            }

            // A row that goes, blocked copies included, must not meet a row that is copied: the matcher takes the match
            // that starts first, then the longer one, so the row that goes could have decided texts the copy takes once
            // it is gone ("kilo" beside "k", "ab" beside "bc"). Rows dictation does not compile are held to it too, which
            // is more than the matcher needs. Two copies that meet are fine: both stay, and where they start and how long
            // they are still decide between them.
            for (var row = 0; row < folds.Length; row++)
            {
                if (!copied.Contains(row) && copied.Any(copy => Meet(folds[row], folds[copy], runningInto: true)))
                {
                    ownProblems.Add(row);
                }
            }

            return new Side
            {
                Library = library,
                Folds = folds,
                RowFolds = folds.ToHashSet(StringComparer.Ordinal),
                Candidates = candidates,
                CopyFolds = copying.Select(candidate => folds[candidate.Row]).ToHashSet(StringComparer.Ordinal),
                Copied = copied,
                OwnProblems = ownProblems,
            };
        }
    }
}
