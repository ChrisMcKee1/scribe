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
