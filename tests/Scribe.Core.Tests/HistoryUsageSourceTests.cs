using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// The History and Usage pages' window rules the review of a1e3867 found broken, pinned by source because the window has
/// no tests of its own. Each names the scenario that went wrong.
/// </summary>
public sealed class HistoryUsageSourceTests
{
    [Fact]
    public void The_summary_disclosure_follows_every_availability_refresh_and_every_committed_change()
    {
        // Load Usage with Foundry Local saved, pick Microsoft Foundry, save: the card went on naming Foundry Local.
        var usage = Read("SettingsWindow.Usage.cs");
        var refresh = Body(usage, "private void RefreshUsageInsightAvailability()");
        Assert.Contains("UsageInsightText.Text = state.Description;", refresh, StringComparison.Ordinal);
        Assert.Contains("_committedSettings.AiCleanupProvider", refresh, StringComparison.Ordinal);

        // Every later write of the saved and running settings (a Save, an adopted tray change) refreshes what History and
        // Usage say about them. The constructor's first snapshot comes before either page has loaded.
        var window = string.Concat(Directory.EnumerateFiles(SettingsFolder(), "SettingsWindow*.cs").Select(File.ReadAllText));
        var writes = Regex.Matches(window, @"_committedSettings = [^;]+;\s*(?<next>[^;]+;)")
            .Where(write => !write.Groups["next"].Value.Contains("_savedAiProvider = _settings.AiCleanupProvider", StringComparison.Ordinal))
            .ToList();
        Assert.True(writes.Count >= 2, $"Only {writes.Count} later writes of the committed settings were found.");
        Assert.All(writes, write => Assert.Equal("OnCommittedSettingsChanged();", write.Groups["next"].Value.Trim()));

        var history = Read("SettingsWindow.History.cs");
        var changed = Body(history, "private void OnCommittedSettingsChanged()");
        Assert.Contains("RefreshUsageInsightAvailability();", changed, StringComparison.Ordinal);
        Assert.Contains("RefreshHistoryEmptyTextFromCommitted();", changed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_usage_request_hides_the_numbers_only_when_none_were_ever_shown()
    {
        // Last 30 days shown, then Last 7 days and Last 90 days quickly: the second request hid the old numbers.
        var load = Body(Read("SettingsWindow.Usage.cs"), "private void LoadUsage()");
        Assert.Contains("if (_usageShownPeriod is null)", load, StringComparison.Ordinal);
        Assert.DoesNotContain("if (_usageSnapshot is null)", load, StringComparison.Ordinal);
    }

    [Fact]
    public void Rating_or_reloading_keeps_the_selected_dictation_selected()
    {
        // Selecting a dictation and pressing Useful collapsed its details: replacing the record cleared the selection.
        var history = Read("SettingsWindow.History.cs");
        var rate = Body(history, "private async void RateHistoryRow(");
        Assert.DoesNotMatch(new Regex(@"_historyRows\[\w+\]\s*=(?!=)"), rate);
        Assert.Contains("UpdateHistoryRowById(id", rate, StringComparison.Ordinal);

        var replace = Body(history, "private void ReplaceHistoryRow(");
        Assert.Contains("ReferenceEquals(HistoryGrid.SelectedItem, _historyRows[index])", replace, StringComparison.Ordinal);
        Assert.Contains("HistoryGrid.SelectedItem = replacement;", replace, StringComparison.Ordinal);

        var load = Body(history, "private async void LoadHistory()");
        Assert.True(
            load.IndexOf("var selectedId = SelectedHistory?.Id;", StringComparison.Ordinal) <
            load.IndexOf("ShowPagedHistoryRows(selectedId);", StringComparison.Ordinal),
            "LoadHistory must read the selection before it replaces the rows.");
        var setRows = Body(history, "private void SetHistoryRows(");
        Assert.Contains("HistoryGrid.SelectedItem = _historyRows[reselectAt];", setRows, StringComparison.Ordinal);
    }

    [Fact]
    public void A_custom_retention_refreshes_the_history_settings_summary()
    {
        // Custom, 7 days, collapse the expander: the summary still said 90 days while Save stored 7.
        var xaml = File.ReadAllText(Path.Combine(SettingsFolder(), "SettingsWindow.xaml"));
        Assert.Matches(new Regex(@"x:Name=""HistoryRetentionCustomBox""[^>]*ValueChanged=""HistoryRetentionCustomBox_ValueChanged"""), xaml);
        Assert.Matches(
            new Regex(@"private void HistoryRetentionCustomBox_ValueChanged\([^)]*\)\s*=>\s*RefreshHistorySettingsSummary\(\);"),
            Read("SettingsWindow.History.cs"));
    }

    [Fact]
    public void Try_again_goes_through_the_load_state_and_never_covers_the_rows()
    {
        var retry = Body(Read("SettingsWindow.History.cs"), "private void HistoryRetryButton_Click(");
        Assert.Contains("ApplyHistoryLoadState(loadFailed: false, loading: true);", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("HistoryStatusPanel.Visibility", retry, StringComparison.Ordinal);
    }

    [Fact]
    public void History_deletion_matching_uses_the_persisted_row_id()
    {
        var covers = Body(Read("SettingsWindow.History.cs"), "private static bool HistoryDeletionCoversRow(");
        Assert.Contains("entry.Id == row.Id", covers, StringComparison.Ordinal);
        Assert.DoesNotContain("entry.Text", covers, StringComparison.Ordinal);
    }

    [Fact]
    public void Older_page_completion_clears_loading_before_search_or_stale_return()
    {
        var loadOlder = Body(Read("SettingsWindow.History.cs"), "private async void HistoryLoadOlderButton_Click(");
        Assert.Contains(
            "if (ticket == Interlocked.Read(ref _historyOlderTicket))\r\n        {\r\n            _historyOlderLoading = false;\r\n        }\r\n\r\n        if (_closed || !_historyMutationGeneration.IsCurrent(generation) || IsHistorySearchActive())",
            loadOlder,
            StringComparison.Ordinal);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(SettingsFolder(), file));

    private static string SettingsFolder() => Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings");

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} was not found.");
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            depth += source[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[open..(i + 1)];
            }
        }

        throw new InvalidOperationException($"{signature} has no end.");
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
