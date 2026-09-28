using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// DeferSettingsPageData and BoundedDiagnosticsReads' wiring in the Settings window: with the flags off the constructor
/// starts the three reads and the failures read 10,000 rows, as before; on, History and Diagnostics read when their page
/// shows and the failures read only the page shown.
/// </summary>
public sealed class SettingsPageDataSourceTests
{
    private static string Window => Read("src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs");

    [Fact]
    public void The_constructor_starts_the_three_reads_only_with_the_flag_off()
    {
        var constructor = Slice(Window, "public SettingsWindow(", "RefreshAiStatus();");
        var reads = Slice(constructor, "if (!_perfFlags.IsOn(PerfFlags.DeferSettingsPageData))", "openStages?.Mark(\"reads\");");

        Assert.Equal("LoadHistory(); LoadFailures(); LoadPerformanceStats(); }", Collapse(reads[(reads.IndexOf('{', StringComparison.Ordinal) + 1)..]));
        Assert.Single(Regex.Matches(constructor, @"(?<![\w.])LoadFailures\(\);"));
        Assert.Single(Regex.Matches(constructor, @"(?<![\w.])LoadPerformanceStats\(\);"));
    }

    [Fact]
    public void With_the_flag_on_the_Diagnostics_page_reads_once_when_it_first_shows_and_History_as_before()
    {
        var showPage = Slice(Window, "internal void ShowPage(SettingsPage page, string? focusName = null)", "private void ShowSection(Grid section)");

        Assert.Contains("if (page == SettingsPage.History)\r\n        {\r\n            LoadHistory();", showPage.ReplaceLineEndings("\r\n"), StringComparison.Ordinal);
        var diagnostics = Slice(showPage, "else if (page == SettingsPage.Diagnostics && !_diagnosticsDataRequested && _perfFlags.IsOn(PerfFlags.DeferSettingsPageData))", "else if (page == SettingsPage.Usage)");
        Assert.Equal("{ _diagnosticsDataRequested = true; LoadFailures(); LoadPerformanceStats(); }", Collapse(diagnostics[diagnostics.IndexOf('{', StringComparison.Ordinal)..]));

        // Before any read the page says it is loading (InitializeReadOnlySections), so a late read shows nothing untrue.
        var sections = Slice(Window, "SetFailuresListState(\"Loading...\", showGrid: false);", "private void SetFailuresListState(");
        Assert.Contains("ShowPerformanceStats(null, statsFailed: false, DiagnosticsSpeedReadState.Reading);", sections, StringComparison.Ordinal);
    }

    [Fact]
    public void The_failures_read_the_page_they_show_only_with_the_flag_on_and_the_count_comes_from_the_same_read()
    {
        var load = Slice(Window, "private async void LoadFailures()", "private async void ClearFailuresButton_Click(");

        Assert.Contains("if (_perfFlags.IsOn(PerfFlags.BoundedDiagnosticsReads))", load, StringComparison.Ordinal);
        Assert.Contains("var page = await Task.Run(() => _failureLog.GetRecentPage(20, 10_000));", load, StringComparison.Ordinal);
        Assert.Contains("failures = await Task.Run(() => _failureLog.GetRecent(10_000));", load, StringComparison.Ordinal);
        Assert.Contains("failureCount = failures.Count;", load, StringComparison.Ordinal);
        Assert.Contains("failureCount = page.Count;", load, StringComparison.Ordinal);
        Assert.Contains("foreach (var failure in failures.Take(20))", load, StringComparison.Ordinal);
        Assert.Contains("$\"Showing the 20 most recent of {failureCount:N0} failures.\"", load, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"(?<![\w.])failures\.Count >"), load);
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
