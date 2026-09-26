using Scribe.Core.Settings;
using Xunit;

namespace Scribe.Core.Tests;

public class SnippetBuilderTests
{
    private static SnippetBuilder.Row Row(long id, string? phrase, string? template, bool enabled = true) =>
        new(id, phrase, template, enabled);

    [Fact]
    public void Build_trims_phrase_and_maps_fields()
    {
        var result = SnippetBuilder.Build(new[] { Row(7, "  sig  ", "Best,\nChris", enabled: false) });

        Assert.False(result.HasDuplicate);
        var snippet = Assert.Single(result.Snippets);
        Assert.Equal(7, snippet.Id);
        Assert.Equal("sig", snippet.Phrase);
        Assert.Equal("Best,\nChris", snippet.Template); // template is not trimmed
        Assert.False(snippet.Enabled);
    }

    [Fact]
    public void Build_skips_rows_with_blank_phrase_or_template()
    {
        var result = SnippetBuilder.Build(new[]
        {
            Row(1, "  ", "template"),
            Row(2, "phrase", "   "),
            Row(3, null, "template"),
            Row(4, "phrase", null),
            Row(5, "kept", "value"),
        });

        Assert.Equal("kept", Assert.Single(result.Snippets).Phrase);
    }

    [Fact]
    public void Build_keeps_an_unchanged_stored_row_even_when_it_is_incomplete()
    {
        var result = SnippetBuilder.Build(new[]
        {
            new SnippetBuilder.Row(3, "legacy", string.Empty, Enabled: true, KeepAsStored: true),
            Row(0, string.Empty, string.Empty),
            Row(0, "new", "text"),
        });

        Assert.Equal(2, result.Snippets.Count);
        var legacy = Assert.Single(result.Snippets, snippet => snippet.Id == 3);
        Assert.Equal("legacy", legacy.Phrase);
        Assert.Equal(string.Empty, legacy.Template);
        Assert.Contains(result.Snippets, snippet => snippet.Phrase == "new");
    }

    [Fact]
    public void A_kept_row_is_written_exactly_as_stored_and_kept_rows_never_collide_with_each_other()
    {
        // Storage can hold "" and " " side by side (the unique index compares them as different); trimming them would
        // make a pair validation only warns about block every Save.
        var result = SnippetBuilder.Build(new[]
        {
            new SnippetBuilder.Row(1, string.Empty, string.Empty, Enabled: true, KeepAsStored: true),
            new SnippetBuilder.Row(2, " ", string.Empty, Enabled: true, KeepAsStored: true),
            new SnippetBuilder.Row(3, " legacy ", "x", Enabled: false, KeepAsStored: true),
        });

        Assert.False(result.HasDuplicate);
        Assert.Equal([string.Empty, " ", " legacy "], result.Snippets.Select(snippet => snippet.Phrase));
        Assert.False(result.Snippets[2].Enabled);
        Assert.Equal([0, 1, 2], result.IncludedRows);
    }

    [Fact]
    public void A_new_row_is_still_checked_against_kept_rows()
    {
        var result = SnippetBuilder.Build(new[]
        {
            Row(0, "Sig", "Best"),
            new SnippetBuilder.Row(4, "sig", "Regards", Enabled: true, KeepAsStored: true),
        });

        Assert.Equal(0, result.DuplicateIndex);
    }

    [Fact]
    public void Included_rows_name_exactly_the_rows_that_were_built()
    {
        var result = SnippetBuilder.Build(new[]
        {
            Row(0, string.Empty, string.Empty),
            Row(0, "sig", "Regards"),
            Row(0, "  ", "  "),
            new SnippetBuilder.Row(9, "legacy", string.Empty, Enabled: true, KeepAsStored: true),
        });

        Assert.Equal([1, 3], result.IncludedRows);
        Assert.Equal(2, result.Snippets.Count);
    }

    [Fact]
    public void A_whitespace_only_edit_leaves_a_stored_row_unchanged_as_validation_sees_it()
    {
        var edited = new StoredLegacyRow("legacy ", string.Empty);
        Assert.True(SettingsDraftValidator.IsUnchanged(edited.Row));
        Assert.False(SettingsDraftValidator.IsUnchanged(new StoredLegacyRow("legacy2", string.Empty).Row));
    }

    // A stored row as the window describes it to validation, loaded as "legacy" with an empty template.
    private readonly record struct StoredLegacyRow(string Phrase, string Template)
    {
        public SnippetDraftRow Row => new(
            RowKey: "saved:legacy",
            Origin: DraftRowOrigin.Saved,
            Touched: true,
            Phrase: Phrase,
            Template: Template,
            LoadedPhrase: "legacy",
            LoadedTemplate: string.Empty,
            Enabled: true,
            LoadedEnabled: true);
    }

    [Fact]
    public void Build_reports_first_duplicate_phrase_case_insensitively()
    {
        var result = SnippetBuilder.Build(new[]
        {
            Row(1, "hello", "a"),
            Row(2, "world", "b"),
            Row(3, "HELLO", "c"),
        });

        Assert.True(result.HasDuplicate);
        Assert.Equal(2, result.DuplicateIndex);
        Assert.Equal(3, result.Snippets.Count);
    }

    [Fact]
    public void Build_no_duplicate_returns_negative_one()
    {
        var result = SnippetBuilder.Build(new[] { Row(1, "a", "x"), Row(2, "b", "y") });

        Assert.Equal(-1, result.DuplicateIndex);
    }
}
