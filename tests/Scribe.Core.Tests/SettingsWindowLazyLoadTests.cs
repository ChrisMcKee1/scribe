namespace Scribe.Core.Tests;

/// <summary>
/// The Settings window's deferred page reads, and the app shell probe that measures them. When the History and Diagnostics
/// pages read is DeferSettingsPageData's to decide (off: the constructor reads all three, as 0.5.0 did; on: each page reads
/// when it shows), and <see cref="SettingsPageDataSourceTests"/> pins the constructor and ShowPage; this class pins the
/// committed-settings refresh and the probe.
/// </summary>
public sealed class SettingsWindowLazyLoadTests
{
    [Fact]
    public void Committed_settings_change_refreshes_speed_figures_as_before_unless_the_deferred_page_has_not_asked()
    {
        var source = File.ReadAllText(FindRepoFile("src", "Scribe.App", "Settings", "SettingsWindow.History.cs"));
        var handler = Slice(source, "private void OnCommittedSettingsChanged()", "private void RefreshHistoryEmptyTextFromCommitted()");

        Assert.Contains("RefreshHistoryEmptyTextFromCommitted();", handler, StringComparison.Ordinal);
        Assert.Contains("RefreshUsageInsightAvailability();", handler, StringComparison.Ordinal);
        Assert.Contains(
            "if (!_perfFlags.IsOn(PerfFlags.DeferSettingsPageData) || _diagnosticsDataRequested)",
            handler,
            StringComparison.Ordinal);
        Assert.Contains("LoadPerformanceStats();", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("SectionDiagnostics.Visibility", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void App_shell_probe_uses_owned_child_root_and_refuses_real_data_roots()
    {
        var source = File.ReadAllText(FindRepoFile("tools", "Scribe.Benchmarks", "AppShellProbe.cs"));

        Assert.Contains("CreateOwnedDataRoot(dataRootBase)", source, StringComparison.Ordinal);
        Assert.Contains("private static string CreateOwnedDataRoot", source, StringComparison.Ordinal);
        Assert.Contains("OwnershipMarker", source, StringComparison.Ordinal);
        Assert.Contains("RefuseSensitiveDataRoot(rootBase);", source, StringComparison.Ordinal);
        Assert.Contains("Path.Combine(localAppData, \"ScribeData\")", source, StringComparison.Ordinal);
        Assert.Contains("Path.Combine(userProfile, \".Scribe\")", source, StringComparison.Ordinal);
        Assert.Contains("IsPackageLocalCache(root, localAppData)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete(dataRoot", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete(rootBase", source, StringComparison.Ordinal);
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
