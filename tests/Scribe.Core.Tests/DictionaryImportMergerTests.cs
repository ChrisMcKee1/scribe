using Scribe.Core.Models;
using Scribe.Core.Settings;
using Xunit;

namespace Scribe.Core.Tests;

public class DictionaryImportMergerTests
{
    private static DictionaryImportMerger.ExistingRow Existing(
        int index, long id, string? pattern, string? replacement, bool wholeWord = true, bool enabled = true) =>
        new(index, id, pattern, replacement, wholeWord, enabled);

    [Fact]
    public void Merge_adds_new_pattern()
    {
        var plan = DictionaryImportMerger.Merge(
            Array.Empty<DictionaryImportMerger.ExistingRow>(),
            new[] { new DictionaryEntry(0, "azure", "Azure") });

        Assert.Equal((1, 0, 0), (plan.Added, plan.Updated, plan.Unchanged));
        var op = Assert.Single(plan.Operations);
        Assert.Equal(DictionaryImportMerger.OperationKind.Add, op.Kind);
        Assert.Equal("azure", op.Entry.Pattern);
    }

    [Fact]
    public void Merge_counts_identical_row_as_unchanged()
    {
        var existing = new[] { Existing(0, 3, "azure", "Azure") };

        var plan = DictionaryImportMerger.Merge(existing, new[] { new DictionaryEntry(0, "azure", "Azure") });

        Assert.Equal((0, 0, 1), (plan.Added, plan.Updated, plan.Unchanged));
        Assert.Empty(plan.Operations);
    }

    [Fact]
    public void Merge_updates_differing_row_preserving_id_and_pattern()
    {
        var existing = new[] { Existing(2, 42, "azure", "azure") };

        var plan = DictionaryImportMerger.Merge(
            existing, new[] { new DictionaryEntry(0, "AZURE", "Azure", WholeWord: false, Enabled: false) });

        Assert.Equal((0, 1, 0), (plan.Added, plan.Updated, plan.Unchanged));
        var op = Assert.Single(plan.Operations);
        Assert.Equal(DictionaryImportMerger.OperationKind.Update, op.Kind);
        Assert.Equal(2, op.Index);
        Assert.Equal(42, op.Entry.Id);              // existing Id preserved
        Assert.Equal("azure", op.Entry.Pattern);    // original spoken form preserved
        Assert.Equal("Azure", op.Entry.Replacement);
        Assert.False(op.Entry.WholeWord);
        Assert.False(op.Entry.Enabled);
    }

    [Fact]
    public void Merge_matches_case_insensitively_by_pattern()
    {
        var existing = new[] { Existing(0, 1, "azure", "old") };

        var plan = DictionaryImportMerger.Merge(existing, new[] { new DictionaryEntry(0, "  AZURE  ".Trim(), "new") });

        Assert.Equal(1, plan.Updated);
    }

    [Fact]
    public void Merge_ignores_whitespace_around_existing_pattern_when_matching()
    {
        var existing = new[] { Existing(0, 1, "  azure  ", "Azure") };

        var plan = DictionaryImportMerger.Merge(existing, new[] { new DictionaryEntry(0, "azure", "Azure") });

        Assert.Equal((0, 0, 1), (plan.Added, plan.Updated, plan.Unchanged));
    }

    [Fact]
    public void Merge_mixed_batch_reports_all_counts()
    {
        var existing = new[]
        {
            Existing(0, 1, "azure", "Azure"),      // will be unchanged
            Existing(1, 2, "cube", "cube"),        // will be updated
        };

        var plan = DictionaryImportMerger.Merge(existing, new[]
        {
            new DictionaryEntry(0, "azure", "Azure"),   // unchanged
            new DictionaryEntry(0, "cube", "Kubernetes"), // update
            new DictionaryEntry(0, "net", "NET"),        // add
        });

        Assert.Equal((1, 1, 1), (plan.Added, plan.Updated, plan.Unchanged));
        Assert.Equal(2, plan.Operations.Count); // one update, one add (unchanged emits nothing)
    }

    [Fact]
    public void Merge_later_duplicate_import_updates_the_just_added_row()
    {
        // Two imports share a spoken form not present in the existing set: first adds, second updates.
        var plan = DictionaryImportMerger.Merge(
            Array.Empty<DictionaryImportMerger.ExistingRow>(),
            new[]
            {
                new DictionaryEntry(0, "term", "First"),
                new DictionaryEntry(0, "term", "Second"),
            });

        Assert.Equal((1, 1, 0), (plan.Added, plan.Updated, plan.Unchanged));
        Assert.Collection(plan.Operations,
            op => Assert.Equal(DictionaryImportMerger.OperationKind.Add, op.Kind),
            op =>
            {
                Assert.Equal(DictionaryImportMerger.OperationKind.Update, op.Kind);
                Assert.Equal(0, op.Index); // the row the Add appended
                Assert.Equal("Second", op.Entry.Replacement);
            });
    }

    [Fact]
    public void Merge_first_writer_wins_for_duplicate_existing_patterns()
    {
        // Grid can hold two rows with the same spoken form; the first is the match target.
        var existing = new[]
        {
            Existing(0, 1, "dup", "one"),
            Existing(1, 2, "dup", "two"),
        };

        var plan = DictionaryImportMerger.Merge(existing, new[] { new DictionaryEntry(0, "dup", "three") });

        var op = Assert.Single(plan.Operations);
        Assert.Equal(0, op.Index);
        Assert.Equal(1, op.Entry.Id);
    }
}
