using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// ReuseStatusComposition's wiring: with the flag on one status refresh composes the word packs once for the glossary hint
/// and the badges; off (and for every other caller of the hint) each consumer composes for itself, as before.
/// </summary>
public sealed class ReuseStatusCompositionSourceTests
{
    private static string YourWords => Read("src", "Scribe.App", "Settings", "SettingsWindow.YourWords.cs");

    [Fact]
    public void One_refresh_shares_one_composition_only_with_the_flag_on()
    {
        var refresh = Slice(YourWords, "private void RefreshDictionaryStatus()", "private struct RefreshComposition");
        var on = Slice(refresh, "if (_perfFlags.IsOn(PerfFlags.ReuseStatusComposition))", "else");
        var off = Slice(refresh, "else", "UpdateSelectedDictionaryCoverageStatus();");

        Assert.Equal(
            "var composition = new RefreshComposition(); UpdateDictionaryGlossaryHint(ref composition); UpdateDictionaryCoverage(ref composition);",
            Collapse(on[(on.IndexOf('{', StringComparison.Ordinal) + 1)..on.LastIndexOf('}')]));
        Assert.Equal(
            "UpdateDictionaryGlossaryHint(); UpdateDictionaryCoverage();",
            Collapse(off[(off.IndexOf('{', StringComparison.Ordinal) + 1)..off.LastIndexOf('}')]));
    }

    [Theory]
    [InlineData("private void UpdateDictionaryCoverage()", "private void UpdateDictionaryCoverage(ref RefreshComposition composition)", "UpdateDictionaryCoverage(ref composition);")]
    [InlineData("private void UpdateDictionaryGlossaryHint()", "private void UpdateDictionaryGlossaryHint(ref RefreshComposition refreshComposition)", "UpdateDictionaryGlossaryHint(ref composition);")]
    public void The_parameterless_consumers_compose_for_themselves(string start, string end, string call)
    {
        var body = Slice(YourWords, start, end);

        Assert.Contains("var composition = new RefreshComposition();", body, StringComparison.Ordinal);
        Assert.Contains(call, body, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_consumer_takes_its_composition_from_the_holder_it_was_given()
    {
        var coverage = Slice(YourWords, "private void UpdateDictionaryCoverage(ref RefreshComposition composition)", "private void DictionaryGrid_SelectionChanged(");
        Assert.Contains("composition.Get(this)?.Coverage()", coverage, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentLibraryComposition()", coverage, StringComparison.Ordinal);

        var hint = Slice(YourWords, "private void UpdateDictionaryGlossaryHint(ref RefreshComposition refreshComposition)", "private LibraryComposition? CurrentLibraryComposition()");
        Assert.Contains("var composition = refreshComposition.Get(this);", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentLibraryComposition()", hint, StringComparison.Ordinal);

        var holder = Slice(YourWords, "private struct RefreshComposition", "private bool DictionaryFilter(");
        Assert.Single(Regex.Matches(holder, @"window\.CurrentLibraryComposition\(\)"));
    }

    private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts]));

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing start marker {start}.");
        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Missing end marker {end}.");
        return source[startIndex..endIndex];
    }

    private static string RepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Scribe.slnx")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
