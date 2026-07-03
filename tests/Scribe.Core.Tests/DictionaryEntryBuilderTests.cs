using Scribe.Core.Settings;
using Xunit;

namespace Scribe.Core.Tests;

public class DictionaryEntryBuilderTests
{
    private static DictionaryEntryBuilder.Row Row(
        long id, string? pattern, string? replacement = "", bool wholeWord = true, bool enabled = true) =>
        new(id, pattern, replacement, wholeWord, enabled);

    [Fact]
    public void Build_trims_and_maps_fields()
    {
        var result = DictionaryEntryBuilder.Build(new[]
        {
            Row(5, "  azure  ", "  Azure  ", wholeWord: false, enabled: false),
        });

        Assert.False(result.HasDuplicate);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(5, entry.Id);
        Assert.Equal("azure", entry.Pattern);
        Assert.Equal("Azure", entry.Replacement);
        Assert.False(entry.WholeWord);
        Assert.False(entry.Enabled);
    }

    [Fact]
    public void Build_skips_blank_pattern_rows()
    {
        var result = DictionaryEntryBuilder.Build(new[]
        {
            Row(1, null),
            Row(2, "   "),
            Row(3, "kept", "Kept"),
        });

        var entry = Assert.Single(result.Entries);
        Assert.Equal("kept", entry.Pattern);
        Assert.False(result.HasDuplicate);
    }

    [Fact]
    public void Build_null_replacement_becomes_empty()
    {
        var result = DictionaryEntryBuilder.Build(new[] { Row(1, "term", replacement: null) });

        Assert.Equal(string.Empty, Assert.Single(result.Entries).Replacement);
    }

    [Fact]
    public void Build_reports_first_duplicate_index_case_insensitively()
    {
        var result = DictionaryEntryBuilder.Build(new[]
        {
            Row(1, "azure"),
            Row(2, "other"),
            Row(3, "AZURE"),
            Row(4, "azure"),
        });

        Assert.True(result.HasDuplicate);
        Assert.Equal(2, result.DuplicateIndex); // the "AZURE" row, first repeat
        // Every non-blank row is still built even when a duplicate exists.
        Assert.Equal(4, result.Entries.Count);
    }

    [Fact]
    public void Build_duplicate_detection_ignores_surrounding_whitespace()
    {
        var result = DictionaryEntryBuilder.Build(new[]
        {
            Row(1, "azure"),
            Row(2, "  azure  "),
        });

        Assert.Equal(1, result.DuplicateIndex);
    }

    [Fact]
    public void Build_no_duplicate_returns_negative_one()
    {
        var result = DictionaryEntryBuilder.Build(new[] { Row(1, "a"), Row(2, "b") });

        Assert.Equal(-1, result.DuplicateIndex);
        Assert.False(result.HasDuplicate);
    }
}
