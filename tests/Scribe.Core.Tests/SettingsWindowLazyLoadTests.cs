namespace Scribe.Core.Tests;

public sealed class SettingsWindowLazyLoadTests
{
    [Fact]
    public void Settings_constructor_does_not_start_hidden_history_or_diagnostics_reads()
    {
        var source = File.ReadAllText(FindRepoFile("src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs"));
        var constructorTail = Slice(
            source,
            "LoadSnippetsAsync();",
            "_cleanup.StatusChanged += OnCleanupStatusChanged;");

        Assert.DoesNotContain("LoadHistory();", constructorTail, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadFailures();", constructorTail, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadPerformanceStats();", constructorTail, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigating_to_history_or_diagnostics_starts_the_page_reads()
    {
        var source = File.ReadAllText(FindRepoFile("src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs"));
        var showPage = Slice(source, "internal void ShowPage(SettingsPage page, string? focusName = null)", "if (!string.IsNullOrWhiteSpace(focusName)");

        Assert.Contains("if (page == SettingsPage.History)", showPage, StringComparison.Ordinal);
        Assert.Contains("LoadHistory();", showPage, StringComparison.Ordinal);
        Assert.Contains("else if (page == SettingsPage.Diagnostics)", showPage, StringComparison.Ordinal);
        Assert.Contains("LoadFailures();", showPage, StringComparison.Ordinal);
        Assert.Contains("LoadPerformanceStats();", showPage, StringComparison.Ordinal);
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing start marker {start}.");
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Missing end marker {end}.");
        return source[startIndex..endIndex];
    }

    private static string FindRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find repository file.", Path.Combine(parts));
    }
}
