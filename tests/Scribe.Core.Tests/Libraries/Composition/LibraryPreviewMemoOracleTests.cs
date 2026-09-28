using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Settings;
using Xunit.Abstractions;
using static Scribe.Core.Tests.Libraries.Composition.CompositionOracle;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// LB2 (2009a75, f09eab0, 2bffbd0, 5ac8535, 149dbe5): <see cref="LibraryComposition.Preview(LibraryDraft, LibraryCatalog, IReadOnlyList{DictionaryEntry}, GlossaryBudget)"/>
/// keeps the two latest previews of each draft and returns a kept one for the same committed catalog object, the same
/// budget and equal enabled dictionary entries in the same order. Over seeded sequences of drafts, every preview it returns
/// is read, member by member and in order, against a fresh composition of the same inputs (the overload that composes every
/// time, which keeps nothing): the rules, the library entries and the AI subset, the excluded ids, the libraries in use with
/// each one's entries, the badges, the glossary's entries and text, the Save prompt, the filters, and the status of every
/// spoken form in every library, winner content included.
/// </summary>
/// <remarks>
/// <para>
/// The sequences come from the real workspace, whose every edit hands out a new draft: terms added, edited, turned off and
/// deleted, packs switched on and off, AI permission given and withdrawn, undo and redo. Between edits the other inputs
/// change as the window's refreshes change them, and as the memo must tell apart: either page's budget; dictionary entries
/// changed, added, removed, turned on or off and reordered, or handed over as equal copies or padded with turned-off
/// entries (which a kept preview may serve); the committed catalog replaced by an equal but different object, or by one of
/// the same generation whose content differs; and two drafts alternating, the current one and one or two before it. Each
/// refresh composes as the window's two pages do, the Word packs page with the dictionary in the grid's order and the saved
/// budget, and often Your words too, with the dictionary sorted and the budget on screen, so both of a draft's kept previews
/// are asked for.
/// </para>
/// <para>
/// The three differences LB2 accepts (memopt HANDOFF section 8, items 1 and 2), none of which a reading can see. Equal
/// inputs return the same instance, so a dictionary winner's <see cref="TermStatus.WinningEntry"/> is the equal entry the
/// first call passed: compared here by value, while the instance itself is checked against a model of each draft's two
/// slots, which also checks that inputs no kept preview matches get a new one. Kept previews are retained with their draft,
/// two per draft, until it is collected. Everything a composition exposes is read-only through every interface, the fresh
/// one's included (<c>LibraryCompositionReadOnlyTests</c> pins that).
/// </para>
/// <para>
/// Seeds are fixed, each with a <see cref="Random"/> of its own, and a failure names its seed. Negative controls, made by
/// hand and undone, each failing at the first seed: with the budget left out of the memo's key (<c>Composes</c> not
/// comparing it), a preview kept for one budget is returned for another, and the fresh reading fails on a status's
/// glossary inclusion; with the committed catalog matched by generation instead of by reference, the catalog of the same
/// generation whose content differs is served the other content's preview, and the fresh reading fails on a rule; with
/// <c>Preview</c> composing every time, which no reading can see, the model's check fails.
/// </para>
/// </remarks>
public sealed class LibraryPreviewMemoOracleTests(ITestOutputHelper output)
{
    // Built-ins the embedded word packs do not have, so the workspace rebuilds each one's shipped rows from the catalog.
    private static readonly string[] BuiltIns = ["bi-one", "bi-two", "bi-three"];

    [Fact]
    public void Every_kept_preview_reads_as_a_fresh_composition_of_its_inputs_over_seeded_draft_sequences()
    {
        var counts = new Counts();
        ForEachSeed(2_009_000, 100, (seed, random) =>
        {
            var catalog = RandomCatalog(random, BuiltIns, mostlyOff: seed % 2 == 0, anyFileState: seed % 5 == 4, canonicalBuiltIns: true);
            LibraryCatalog[] catalogs = [catalog, Twin(catalog), Variant(random, catalog)];
            var workspace = new LibraryWorkspace(catalog, BuiltInLibraryOverlay.Instance, LibraryDecisions.DefaultAiPermission);
            var drafts = new List<LibraryDraft> { workspace.Draft };
            var memo = new MemoModel();
            var dictionary = RandomDictionary(random);
            var committed = catalog;
            var (saved, shown) = (new GlossaryBudget(80), new GlossaryBudget(80));
            for (var step = 0; step < 50; step++)
            {
                switch (random.Next(10))
                {
                    case < 4:
                        counts.Edits += Edit(random, workspace) ? 1 : 0;
                        if (!ReferenceEquals(drafts[^1], workspace.Draft))
                        {
                            drafts.Add(workspace.Draft);
                        }

                        break;
                    case 4 or 5:
                        dictionary = Change(random, dictionary, counts);
                        break;
                    case 6:
                        // The saved budget moves with the provider or the prompt style, the one on screen as it is edited.
                        if (random.Next(2) == 0)
                        {
                            saved = new GlossaryBudget(Pick(random, Budgets));
                        }
                        else
                        {
                            shown = new GlossaryBudget(Pick(random, Budgets));
                        }

                        counts.Budgets++;
                        break;
                    case 7:
                        committed = random.Next(4) == 0 ? Twin(catalog) : Pick(random, catalogs);
                        counts.Catalogs++;
                        break;
                    default:
                        // Nothing changes: the refresh asks again.
                        break;
                }

                // Mostly the current draft; otherwise one of the two before it, and then often the current one straight after.
                var draft = drafts.Count > 1 && random.Next(10) < 3 ? drafts[^Math.Min(drafts.Count, random.Next(2, 4))] : drafts[^1];
                var context = $"seed {seed} step {step}";
                Check(draft, committed, dictionary, saved, memo, counts, context + ", Word packs");
                if (random.Next(3) > 0)
                {
                    counts.YourWords++;
                    Check(draft, committed, Sorted(dictionary), shown, memo, counts, context + ", Your words");
                }

                if (!ReferenceEquals(draft, drafts[^1]) && random.Next(2) == 0)
                {
                    counts.Alternations++;
                    Check(drafts[^1], committed, dictionary, saved, memo, counts, context + ", current draft");
                }
            }
        });

        output.WriteLine(counts.ToString());
        Assert.True(counts.Edits >= 800, $"Only {counts.Edits} workspace edits applied.");
        Assert.True(counts.Composed >= 2_500, $"Only {counts.Composed} previews were composed.");
        Assert.True(counts.Reused >= 2_000, $"Only {counts.Reused} kept previews were returned.");
        Assert.True(counts.OlderSlot >= 1_000, $"Only {counts.OlderSlot} kept previews came from a draft's older slot.");
        Assert.True(counts.YourWords >= 1_600, $"Only {counts.YourWords} Your words refreshes.");
        Assert.True(counts.Alternations >= 350, $"Only {counts.Alternations} alternations between drafts.");
        Assert.True(counts.Budgets >= 250 && counts.Catalogs >= 250, $"{counts.Budgets} budget and {counts.Catalogs} catalog changes.");
        Assert.Equal(8, counts.DictionaryChanges.Count);
        Assert.All(counts.DictionaryChanges, pair => Assert.True(pair.Value >= 40, $"Only {pair.Value} dictionary changes of kind {pair.Key}."));
    }

    // One call as the window makes it: read against a fresh composition of the same inputs, then its instance against the
    // model of the draft's two slots.
    private static void Check(
        LibraryDraft draft,
        LibraryCatalog committed,
        List<DictionaryEntry> dictionary,
        GlossaryBudget budget,
        MemoModel memo,
        Counts counts,
        string context)
    {
        var preview = LibraryComposition.Preview(draft, committed, dictionary, budget);
        var fresh = LibraryComposition.Preview(draft, committed, dictionary, budget, LibraryDecisions.Precedence);
        Assert.NotSame(fresh, preview);
        var contents = draft.Libraries.Select(library => library.Content).Concat(committed.Libraries.Select(library => library.Content)).ToList();
        var (ids, keys) = (LibraryIds(contents), Keys(contents));
        AssertSameLines(
            Read(CompositionView.Of(fresh), ids, keys, dictionary, budget),
            Read(CompositionView.Of(preview), ids, keys, dictionary, budget),
            context);

        var inputs = new Inputs(draft, committed, [.. dictionary.Where(entry => entry is { Enabled: true })], budget);
        switch (memo.Record(inputs, preview, context))
        {
            case Slot.Recent:
                counts.Reused++;
                break;
            case Slot.Older:
                counts.Reused++;
                counts.OlderSlot++;
                break;
            default:
                counts.Composed++;
                break;
        }
    }

    // A workspace edit of a random pack: true when it changed the draft.
    private static bool Edit(Random random, LibraryWorkspace workspace)
    {
        var before = workspace.Draft;
        var libraries = workspace.Draft.Libraries.Where(library => !library.PendingDelete).ToList();
        if (libraries.Count == 0)
        {
            return false;
        }

        var id = Pick(random, libraries).Content.Id;
        var values = new TermValues(Pick(random, Forms), Pick(random, Written), random.Next(4) > 0);
        switch (random.Next(10))
        {
            case 0 or 1:
                workspace.SetEnabled(id, !workspace.Draft.LocalState.EnabledIds.Contains(id));
                break;
            case 2:
                workspace.SetAiPermission(id, random.Next(2) == 0);
                break;
            case 3:
                workspace.Undo();
                break;
            case 4:
                workspace.Redo();
                break;
            default:
                var rows = workspace.RowsOf(id);
                if (!workspace.CanEditContent(id))
                {
                    workspace.SetEnabled(id, true);
                }
                else if (rows.Count == 0 || random.Next(4) == 0)
                {
                    workspace.AddTerm(id, values);
                }
                else
                {
                    var row = Pick(random, rows);
                    switch (random.Next(3))
                    {
                        case 0:
                            workspace.EditTerm(id, row.RowId, values);
                            break;
                        case 1:
                            workspace.SetTermEnabled(id, row.RowId, !row.Row.Values.Enabled);
                            break;
                        default:
                            // A shipped built-in row is turned off, never deleted.
                            if (row.Row.Shipped is null)
                            {
                                workspace.DeleteTerm(id, row.RowId);
                            }
                            else
                            {
                                workspace.SetTermEnabled(id, row.RowId, !row.Row.Values.Enabled);
                            }

                            break;
                    }
                }

                break;
        }

        return !ReferenceEquals(before, workspace.Draft);
    }

    // The dictionary as the next refresh hands it over.
    private static List<DictionaryEntry> Change(Random random, List<DictionaryEntry> dictionary, Counts counts)
    {
        var entries = dictionary.Select(entry => entry with { }).ToList();
        var kind = random.Next(8);
        if ((kind is >= 1 and <= 4 && entries.Count == 0) || (kind == 2 && entries.Count < 2))
        {
            // Those need an entry, or two to swap: add one instead.
            kind = 0;
        }

        var at = entries.Count == 0 ? 0 : random.Next(entries.Count);
        switch (kind)
        {
            case 0:
                entries.Insert(random.Next(entries.Count + 1), RandomEntry(random, 100 + random.Next(100)));
                break;
            case 1:
                entries.RemoveAt(at);
                break;
            case 2:
                var other = (at + 1 + random.Next(entries.Count - 1)) % entries.Count;
                (entries[at], entries[other]) = (entries[other], entries[at]);
                break;
            case 3:
                entries[at] = random.Next(4) switch
                {
                    0 => entries[at] with { Pattern = Pick(random, Forms) },
                    1 => entries[at] with { Replacement = Pick(random, Written) },
                    2 => entries[at] with { WholeWord = !entries[at].WholeWord },
                    _ => entries[at] with { Id = entries[at].Id + 1_000 },
                };
                break;
            case 4:
                entries[at] = entries[at] with { Enabled = !entries[at].Enabled };
                break;
            case 5:
                // Turned-off entries the preview never reads, which a kept preview may serve.
                entries.Insert(random.Next(entries.Count + 1), RandomEntry(random, 300) with { Enabled = false });
                break;
            case 6:
                // Equal copies in another list: what the grid gives at every refresh.
                break;
            default:
                entries.RemoveAll(entry => !entry.Enabled);
                break;
        }

        counts.DictionaryChanges[kind] = counts.DictionaryChanges.GetValueOrDefault(kind) + 1;
        return entries;
    }

    // The dictionary as Your words hands it over: the same entries, sorted by spoken form and then by id.
    private static List<DictionaryEntry> Sorted(List<DictionaryEntry> dictionary) =>
        [.. dictionary.OrderBy(entry => entry.Pattern, StringComparer.Ordinal).ThenBy(entry => entry.Id)];

    // The same libraries and state in another catalog object of the same generation.
    private static LibraryCatalog Twin(LibraryCatalog catalog) => Catalog(catalog.Generation, catalog.LocalState, [.. catalog.Libraries]);

    // A catalog of the same generation whose first custom pack (else first pack) holds one more row and has its first row's
    // written form changed, with the same hash, so a preview that composes the file reads other content.
    private static LibraryCatalog Variant(Random random, LibraryCatalog catalog)
    {
        var libraries = catalog.Libraries.ToList();
        if (libraries.Count == 0)
        {
            return Twin(catalog);
        }

        var index = libraries.FindIndex(library => !library.Content.BuiltIn);
        index = index < 0 ? 0 : index;
        var library = libraries[index];
        var rows = library.Content.Rows.ToList();
        if (rows.Count > 0)
        {
            rows[0] = rows[0] with { Values = rows[0].Values with { Written = rows[0].Values.Written + " again" } };
        }

        rows.Add(library.Content.BuiltIn ? Shipped("variant row", "Variant") : Custom("variant row", Pick(random, Written)));
        libraries[index] = library with { Content = library.Content with { Rows = rows } };
        return Catalog(catalog.Generation, catalog.LocalState, [.. libraries]);
    }

    private enum Slot
    {
        None,
        Recent,
        Older,
    }

    // What the memo matches a call on: the draft and the committed catalog by reference, the budget, and the enabled
    // dictionary entries by value, in order.
    private sealed record Inputs(LibraryDraft Draft, LibraryCatalog Committed, DictionaryEntry[] Enabled, GlossaryBudget Budget)
    {
        public bool Match(Inputs other) =>
            ReferenceEquals(Draft, other.Draft) && ReferenceEquals(Committed, other.Committed) && Budget == other.Budget &&
            Enabled.SequenceEqual(other.Enabled);
    }

    // Each draft's two slots as the memo keeps them: the most recent inputs first, a match moved to the front, and new
    // inputs put in front of the one they replace, the third dropped.
    private sealed class MemoModel
    {
        private readonly Dictionary<LibraryDraft, List<(Inputs Inputs, LibraryComposition Preview)>> _slots =
            new(ReferenceEqualityComparer.Instance);

        private readonly HashSet<LibraryComposition> _returned = new(ReferenceEqualityComparer.Instance);

        // Checks the instance a call got against the slots, then records it: the slot it came from, or None for a new one.
        public Slot Record(Inputs inputs, LibraryComposition preview, string context)
        {
            if (!_slots.TryGetValue(inputs.Draft, out var slots))
            {
                _slots[inputs.Draft] = slots = [];
            }

            var hit = slots.FindIndex(slot => slot.Inputs.Match(inputs));
            if (hit >= 0)
            {
                // The first accepted difference: equal inputs, the same instance.
                Assert.True(ReferenceEquals(slots[hit].Preview, preview), $"{context}: equal inputs did not get the preview kept for them.");
                var slot = slots[hit];
                slots.RemoveAt(hit);
                slots.Insert(0, slot);
                return hit == 0 ? Slot.Recent : Slot.Older;
            }

            Assert.True(_returned.Add(preview), $"{context}: inputs no kept preview matches got a preview made for other inputs.");
            slots.Insert(0, (inputs, preview));
            if (slots.Count > 2)
            {
                slots.RemoveAt(2);
            }

            return Slot.None;
        }
    }

    private sealed class Counts
    {
        public int Edits { get; set; }

        public int Composed { get; set; }

        public int Reused { get; set; }

        public int OlderSlot { get; set; }

        public int YourWords { get; set; }

        public int Alternations { get; set; }

        public int Budgets { get; set; }

        public int Catalogs { get; set; }

        public SortedDictionary<int, int> DictionaryChanges { get; } = [];

        public override string ToString() =>
            $"{Edits} edits, {Composed} previews composed and {Reused} kept ones returned ({OlderSlot} from the older slot), " +
            $"{YourWords} Your words refreshes, {Alternations} alternations, {Budgets} budget and {Catalogs} catalog changes, " +
            $"dictionary changes by kind [{string.Join(", ", DictionaryChanges.Select(pair => $"{pair.Key}: {pair.Value}"))}].";
    }
}
