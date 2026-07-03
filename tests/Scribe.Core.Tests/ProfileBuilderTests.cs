using Scribe.Core.Models;
using Scribe.Core.Settings;
using Xunit;

namespace Scribe.Core.Tests;

public class ProfileBuilderTests
{
    private static ProfileBuilder.Row Row(
        string? name, string? processes, string? style = null, NewlineInjectionMode? newline = null) =>
        new(name, processes, style, newline);

    [Fact]
    public void Build_splits_trims_and_maps()
    {
        var profiles = ProfileBuilder.Build(new[]
        {
            Row("  Email  ", " OUTLOOK , thunderbird ", "  formal  ", NewlineInjectionMode.KeepNewlines),
        });

        var profile = Assert.Single(profiles);
        Assert.Equal("Email", profile.Name);
        Assert.Equal(new[] { "OUTLOOK", "thunderbird" }, profile.ProcessNames);
        Assert.Equal("formal", profile.WritingStyle);
        Assert.Equal(NewlineInjectionMode.KeepNewlines, profile.NewlineHandling);
    }

    [Fact]
    public void Build_skips_rows_with_no_name_and_no_processes()
    {
        var profiles = ProfileBuilder.Build(new[]
        {
            Row("   ", "  "),
            Row(null, null),
            Row("Terminal", "wt"),
        });

        Assert.Single(profiles);
        Assert.Equal("Terminal", profiles[0].Name);
    }

    [Fact]
    public void Build_keeps_row_with_processes_but_no_name_as_unnamed()
    {
        var profiles = ProfileBuilder.Build(new[] { Row("  ", "slack") });

        var profile = Assert.Single(profiles);
        Assert.Equal("Unnamed profile", profile.Name);
        Assert.Equal(new[] { "slack" }, profile.ProcessNames);
    }

    [Fact]
    public void Build_blank_style_becomes_null()
    {
        var profiles = ProfileBuilder.Build(new[] { Row("A", "p", style: "   ") });

        Assert.Null(Assert.Single(profiles).WritingStyle);
    }

    [Fact]
    public void Build_drops_empty_process_tokens_from_commas()
    {
        var profiles = ProfileBuilder.Build(new[] { Row("A", "one,, ,two,") });

        Assert.Equal(new[] { "one", "two" }, Assert.Single(profiles).ProcessNames);
    }
}
