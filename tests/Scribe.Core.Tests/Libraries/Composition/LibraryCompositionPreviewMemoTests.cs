using System.Reflection;
using System.Runtime.CompilerServices;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Xunit.Abstractions;
using static Scribe.Core.Tests.Libraries.Composition.Lib;
using static Scribe.Core.Tests.Libraries.Composition.LibraryPreviewMemoPreconditionTests;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// The two most recent previews of each draft are kept with it (ledger LB2): the Settings window asks for a preview at
/// every term list refresh, and between two edits nothing it reads changes. A kept preview is returned only for the same
/// draft and committed catalog objects, the same budget and equal enabled dictionary entries in the same order, answers
/// every question as a fresh composition of those inputs does, and goes with its draft.
/// </summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class LibraryCompositionPreviewMemoTests(ITestOutputHelper output)
{
    [Fact]
    public void Equal_inputs_get_the_preview_already_made()
    {
        var (catalog, draft, dictionary, budget) = OverlappingInputs();
        var first = LibraryComposition.Preview(draft, catalog, dictionary, budget);

        Assert.Same(first, LibraryComposition.Preview(draft, catalog, Copy(dictionary), budget));
    }

    [Fact]
    public void Entries_a_preview_does_not_keep_do_not_stop_it_being_reused()
    {
        var (catalog, draft, dictionary, budget) = OverlappingInputs();
        var first = LibraryComposition.Preview(draft, catalog, dictionary, budget);
        IReadOnlyList<DictionaryEntry> padded =
        [
            new(20, "zed", "Zed", Enabled: false), .. Copy(dictionary).Where(entry => entry.Enabled), null!,
            new(21, "why", "Why", Enabled: false),
        ];

        Assert.Same(first, LibraryComposition.Preview(draft, catalog, padded, budget));
    }

    [Theory]
    [InlineData("budget")]
    [InlineData("order")]
    [InlineData("pattern")]
    [InlineData("replacement")]
    [InlineData("id")]
    [InlineData("whole word")]
    [InlineData("an entry turned on")]
    [InlineData("one more entry")]
    [InlineData("one entry fewer")]
    [InlineData("another catalog of the same generation")]
    [InlineData("another draft of the same revision")]
    public void A_change_to_any_input_a_preview_reads_composes_it_anew(string change)
    {
        var (catalog, draft, dictionary, budget) = OverlappingInputs();
        var first = LibraryComposition.Preview(draft, catalog, dictionary, budget);
        var entries = Copy(dictionary);
        var (otherCatalog, otherDraft, otherBudget) = (catalog, draft, budget);
        switch (change)
        {
            case "budget":
                otherBudget = new GlossaryBudget(1);
                break;
            case "order":
                (entries[0], entries[1]) = (entries[1], entries[0]);
                break;
            case "pattern":
                entries[0] = entries[0] with { Pattern = "helms" };
                break;
            case "replacement":
                entries[0] = entries[0] with { Replacement = "Helm!" };
                break;
            case "id":
                entries[0] = entries[0] with { Id = 11 };
                break;
            case "whole word":
                entries[0] = entries[0] with { WholeWord = false };
                break;
            case "an entry turned on":
                entries[2] = entries[2] with { Enabled = true };
                break;
            case "one more entry":
                entries.Add(new DictionaryEntry(4, "kube", "KUBE"));
                break;
            case "one entry fewer":
                entries.RemoveAt(1);
                break;
            case "another catalog of the same generation":
                otherCatalog = Catalog(catalog.Generation, catalog.LocalState, [.. catalog.Libraries]);
                break;
            case "another draft of the same revision":
                otherDraft = Draft(draft.Revision, draft.LocalState, [.. draft.Libraries]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, null);
        }

        var second = LibraryComposition.Preview(otherDraft, otherCatalog, entries, otherBudget);

        Assert.NotSame(first, second);
        Assert.Equal(Read(Fresh(otherDraft, otherCatalog, entries, otherBudget)), Read(second));
    }

    [Fact]
    public void Two_previews_are_kept_per_draft_and_the_one_used_least_recently_goes_first()
    {
        var (catalog, draft, dictionary, _) = OverlappingInputs();
        LibraryComposition Ask(int terms) => LibraryComposition.Preview(draft, catalog, dictionary, new GlossaryBudget(terms));

        var one = Ask(1);
        var two = Ask(2);
        Assert.Same(one, Ask(1));
        Assert.Same(two, Ask(2));
        Assert.Same(one, Ask(1));

        var three = Ask(3);

        Assert.Same(one, Ask(1));
        Assert.Same(three, Ask(3));
        Assert.NotSame(two, Ask(2));
    }

    [Fact]
    public void The_overload_that_takes_a_rule_composes_every_time_and_keeps_nothing()
    {
        var (catalog, draft, dictionary, budget) = OverlappingInputs();
        var composed = Fresh(draft, catalog, dictionary, budget);
        Assert.NotSame(composed, Fresh(draft, catalog, dictionary, budget));
        Assert.False(HasKeptPreviews(draft));

        var kept = LibraryComposition.Preview(draft, catalog, dictionary, budget);

        Assert.NotSame(composed, kept);
        foreach (var rule in Enum.GetValues<LibraryPrecedenceRule>())
        {
            Assert.NotSame(kept, LibraryComposition.Preview(draft, catalog, dictionary, budget, rule));
        }

        Assert.Same(kept, LibraryComposition.Preview(draft, catalog, dictionary, budget));
    }

    [Fact]
    public void A_call_that_throws_throws_as_it_always_did_and_keeps_nothing()
    {
        var (catalog, draft, dictionary, budget) = OverlappingInputs();
        var otherGeneration = Catalog(catalog.Generation + 1, catalog.LocalState, [.. catalog.Libraries]);
        const string Mismatch =
            "The committed catalog must be the one the draft was built from, the generation the draft names as its base.";

        foreach (var withRule in new[] { false, true })
        {
            LibraryComposition Ask(LibraryDraft d, LibraryCatalog c, IReadOnlyList<DictionaryEntry> e) =>
                withRule ? LibraryComposition.Preview(d, c, e, budget, LibraryDecisions.Precedence) : LibraryComposition.Preview(d, c, e, budget);

            Assert.Equal("draft", Assert.Throws<ArgumentNullException>(() => Ask(null!, null!, null!)).ParamName);
            Assert.Equal("committed", Assert.Throws<ArgumentNullException>(() => Ask(draft, null!, null!)).ParamName);
            Assert.Equal("dictionary", Assert.Throws<ArgumentNullException>(() => Ask(draft, catalog, null!)).ParamName);
            Assert.Equal("committed", Assert.Throws<ArgumentNullException>(() => Ask(draft, null!, dictionary)).ParamName);
            var mismatch = Assert.Throws<ArgumentException>(() => Ask(draft, otherGeneration, dictionary));
            Assert.Equal("committed", mismatch.ParamName);
            Assert.StartsWith(Mismatch, mismatch.Message, StringComparison.Ordinal);
            Assert.False(HasKeptPreviews(draft));
        }

        _ = LibraryComposition.Preview(draft, catalog, dictionary, budget);
        Assert.True(HasKeptPreviews(draft));
    }

    [Fact]
    public void A_caller_that_changes_its_list_afterwards_gets_a_preview_of_what_the_list_then_holds()
    {
        var (catalog, draft, dictionary, budget) = OverlappingInputs();
        var entries = Copy(dictionary);
        var first = LibraryComposition.Preview(draft, catalog, entries, budget);
        var firstRead = Read(first);

        entries.Add(new DictionaryEntry(4, "kube", "KUBE"));
        var added = LibraryComposition.Preview(draft, catalog, entries, budget);

        Assert.NotSame(first, added);
        Assert.Equal(Read(Fresh(draft, catalog, entries, budget)), Read(added));
        Assert.Equal(firstRead, Read(first));

        entries.RemoveAt(entries.Count - 1);
        Assert.Same(first, LibraryComposition.Preview(draft, catalog, entries, budget));
    }

    [Fact]
    public void A_kept_preview_answers_every_question_as_a_fresh_composition_does()
    {
        var (catalog, draft, dictionary, budget) = OverlappingInputs();
        var fresh = Read(Fresh(draft, catalog, dictionary, budget));
        var first = LibraryComposition.Preview(draft, catalog, dictionary, budget);
        Assert.Equal(fresh, Read(first));

        var kept = LibraryComposition.Preview(draft, catalog, Copy(dictionary), budget);

        Assert.Same(first, kept);
        Assert.Equal(fresh, Read(kept));
    }

    // The one thing a kept preview says differently: a status names the entry objects of the call that composed it.
    [Fact]
    public void A_dictionary_winner_of_a_kept_preview_is_the_equal_entry_the_first_call_passed()
    {
        var (catalog, draft, dictionary, budget) = OverlappingInputs();
        _ = LibraryComposition.Preview(draft, catalog, dictionary, budget);
        var copy = Copy(dictionary);

        var status = LibraryComposition.Preview(draft, catalog, copy, budget).StatusOf("extra", Key("helm"));

        Assert.Equal(TermWinner.Dictionary, status.Winner);
        Assert.Same(dictionary[0], status.WinningEntry);
        Assert.Equal(copy[0], status.WinningEntry);
    }

    [Fact]
    public void The_workspace_s_draft_is_composed_anew_at_each_revision_and_reused_in_between()
    {
        var catalog = Catalog(
            State(enabled: ["team"], ai: [("team", true)], accepted: [("team", H1)]),
            Committed(CustomLibrary("team", Custom("git hub", "GitHub"), Custom("kube", "Kubernetes")), H1));
        var workspace = new LibraryWorkspace(catalog, BuiltInLibraryOverlay.Instance, LibraryDecisions.DefaultAiPermission);
        var budget = new GlossaryBudget(Cleanup.CleanupPrompt.MaxGlossaryTermsCloud);
        LibraryComposition Ask() => LibraryComposition.Preview(workspace.Draft, catalog, [], budget);

        var first = Ask();
        Assert.Same(first, Ask());

        Assert.True(workspace.AddTerm("team", new TermValues("jay son", "JSON")).Applied);
        var edited = Ask();

        Assert.NotSame(first, edited);
        Assert.Contains(edited.Rules, rule => rule.Key == Key("jay son"));
        Assert.DoesNotContain(first.Rules, rule => rule.Key == Key("jay son"));
        Assert.Same(edited, Ask());

        workspace.DeleteTerm("team", workspace.RowsOf("team").Single(row => row.Row.Values.Spoken == "kube").RowId);
        var deleted = Ask();
        Assert.DoesNotContain(deleted.Rules, rule => rule.Key == Key("kube"));

        workspace.Undo();
        var undone = Ask();

        Assert.NotSame(deleted, undone);
        Assert.NotSame(edited, undone);
        Assert.Equal(
            edited.Rules.Select(rule => rule.Key.Value).Order(StringComparer.Ordinal),
            undone.Rules.Select(rule => rule.Key.Value).Order(StringComparer.Ordinal));
        Assert.Same(undone, Ask());
    }

    [Fact]
    public void Kept_previews_go_with_their_draft()
    {
        var (catalog, _, dictionary, budget) = OverlappingInputs();

        var preview = PreviewOfADraftNobodyHolds(catalog, dictionary, budget);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(preview.IsAlive);
    }

    [Fact]
    public void Threads_asking_for_one_preview_at_once_all_get_the_same_one()
    {
        const int Threads = 8;
        for (var round = 0; round < 20; round++)
        {
            var (catalog, draft, dictionary, budget) = OverlappingInputs();
            using var start = new Barrier(Threads);
            var results = new LibraryComposition?[Threads];
            var failures = new Exception?[Threads];
            var threads = Enumerable.Range(0, Threads).Select(index => new Thread(() =>
            {
                try
                {
                    var mine = Copy(dictionary);
                    start.SignalAndWait();
                    results[index] = LibraryComposition.Preview(draft, catalog, mine, budget);
                }
                catch (Exception ex)
                {
                    failures[index] = ex;
                }
            })).ToList();
            threads.ForEach(thread => thread.Start());
            threads.ForEach(thread => thread.Join());

            Assert.All(failures, Assert.Null);
            Assert.All(results, result => Assert.Same(results[0], result));
            Assert.Same(results[0], LibraryComposition.Preview(draft, catalog, dictionary, budget));
        }
    }

    [Fact]
    public void A_preview_asked_for_again_allocates_only_its_lookup()
    {
        var rows = Enumerable.Range(0, 1_000)
            .Select(i => Custom(FormattableString.Invariant($"term {i:D4}"), FormattableString.Invariant($"Term{i:D4}")))
            .ToArray();
        var team = CustomLibrary("team", rows);
        var state = State(enabled: ["team"], ai: [("team", true)], accepted: [("team", H1)]);
        var catalog = Catalog(state, Committed(team, H1));
        var draft = Draft(1, state, Draft(team));
        var dictionary = Enumerable.Range(0, 20)
            .Select(i => new DictionaryEntry(i + 1, FormattableString.Invariant($"personal {i}"), FormattableString.Invariant($"Personal{i}")))
            .ToList();
        var budget = new GlossaryBudget(80);
        var kept = LibraryComposition.Preview(draft, catalog, dictionary, budget);
        _ = kept.StatusOf("team", Key("term 0000"));
        var copy = Copy(dictionary);

        var again = Measure(() => LibraryComposition.Preview(draft, catalog, copy, budget));
        var composing = Measure(() => LibraryComposition.Preview(draft, catalog, copy, budget, LibraryDecisions.Precedence));
        output.WriteLine($"Preview of 1,000 rules asked for again: {again} bytes; composing it: {composing} bytes");

        Assert.Same(kept, LibraryComposition.Preview(draft, catalog, copy, budget));

        // Before previews were kept, every call composed: 202,912 bytes on x64 for these 1,000 rules and 20 entries. Asked
        // for again it is found by its draft and matched entry by entry: 40 bytes, the enumerator of the caller's list. The
        // bound sits halfway, so it fails if a call composes again.
        Assert.True(again <= 101_000, $"A preview asked for again allocated {again} bytes; the bound is 101,000.");
    }

    private static LibraryComposition Fresh(
        LibraryDraft draft, LibraryCatalog catalog, IReadOnlyList<DictionaryEntry> dictionary, GlossaryBudget budget) =>
        LibraryComposition.Preview(draft, catalog, dictionary, budget, LibraryDecisions.Precedence);

    // Equal values, other objects, another list: what the window's grid gives at every refresh.
    private static List<DictionaryEntry> Copy(IEnumerable<DictionaryEntry> entries) => [.. entries.Select(entry => entry with { })];

    private static bool HasKeptPreviews(LibraryDraft draft)
    {
        var table = typeof(LibraryComposition).GetField("PreviewMemos", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var arguments = new object?[] { draft, null };
        return (bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, arguments)!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PreviewOfADraftNobodyHolds(
        LibraryCatalog catalog, IReadOnlyList<DictionaryEntry> dictionary, GlossaryBudget budget)
    {
        var draft = Draft(7, catalog.LocalState, [.. catalog.Libraries.Select(library => Draft(library.Content))]);
        var preview = LibraryComposition.Preview(draft, catalog, dictionary, budget);
        _ = preview.StatusOf("team", Key("kube"));
        Assert.Same(preview, LibraryComposition.Preview(draft, catalog, dictionary, budget));
        return new WeakReference(preview);
    }

    // The smallest of three measured runs after a warm-up: the first calls JIT the path and load its types, and the
    // smallest run is the one no runtime bookkeeping landed in.
    private static long Measure(Func<object> compose)
    {
        for (var i = 0; i < 3; i++)
        {
            GC.KeepAlive(compose());
        }

        var smallest = long.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = compose();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(result);
            smallest = Math.Min(smallest, bytes);
        }

        return smallest;
    }
}
