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

        var load = Body(history, "private async void LoadHistory(int retry = 0)");
        var readSelection = load.IndexOf("var selectedId = SelectedHistory?.Id;", StringComparison.Ordinal);
        Assert.True(
            readSelection >= 0 && readSelection < load.IndexOf("ContinueWithShownHistory(selectedId);", StringComparison.Ordinal),
            "LoadHistory must read the selection before it replaces the rows.");
        Assert.Contains("ShowPagedHistoryRows(selectedId);", Body(history, "private void ContinueWithShownHistory("), StringComparison.Ordinal);
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
            "if (requestStillCurrent)\r\n        {\r\n            _historyOlderLoading = false;\r\n        }\r\n\r\n        var completion = _historyMutationGeneration.CompleteRead(generation, requestStillCurrent, retryWhenStale: false);",
            loadOlder,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Stale_current_search_and_needed_first_load_are_retried()
    {
        var history = Read("SettingsWindow.History.cs");
        var search = Body(history, "private async Task RunHistorySearchAsync(");
        Assert.Contains("generation, IsCurrentHistorySearch(query, ticket, cancellationToken), retryWhenStale: true, retry);", search, StringComparison.Ordinal);
        Assert.Contains("StartHistorySearch(debounce: false, retry + 1);", search, StringComparison.Ordinal);

        var load = Body(history, "private async void LoadHistory(int retry = 0)");
        Assert.True(
            load.IndexOf("CompleteRead(generation, requestStillCurrent, retryIfStale, retry)", StringComparison.Ordinal) <
            load.IndexOf("_historyLoad.Publish(ticket, string.Empty)", StringComparison.Ordinal),
            "The generation must be checked before a recent load is marked published.");
        Assert.Contains("LoadHistory(retry + 1);", load, StringComparison.Ordinal);
    }

    [Fact]
    public void Automatic_retries_are_bounded_and_stop_once_History_is_not_shown()
    {
        // Deletions arriving during every read retried forever, and a read made stale after the user left History
        // started another read on a page nobody saw.
        var history = Read("SettingsWindow.History.cs");
        var load = Body(history, "private async void LoadHistory(int retry = 0)");
        Assert.Contains("completion == HistoryReadCompletion.GiveUp", load, StringComparison.Ordinal);
        Assert.Contains("var requestStillCurrent = _historyLoad.CanPublish(ticket) && IsHistoryPageShown();", load, StringComparison.Ordinal);
        Assert.Contains("if (!_historyLoad.CanPublish(ticket) || !IsHistoryPageShown())", load, StringComparison.Ordinal);

        var search = Body(history, "private async Task RunHistorySearchAsync(");
        Assert.Contains("completion == HistoryReadCompletion.GiveUp", search, StringComparison.Ordinal);
        Assert.True(
            search.IndexOf("if (!IsCurrentHistorySearch(query, ticket, cancellationToken))", StringComparison.Ordinal) <
            search.IndexOf("_history.Search(query", StringComparison.Ordinal),
            "A search that waited (debounce or retry) must check it is still current before it reads.");
        Assert.Matches(new Regex(@"private bool IsCurrentHistorySearch\([^)]*\) =>[^;]*IsHistoryPageShown\(\) &&"), history);
    }

    [Fact]
    public void A_shown_failure_survives_changes_that_only_reconcile_rows()
    {
        // A search that gave up showed its failure and Try again; the next deletion hid both and left the old matches
        // under the new query.
        var history = Read("SettingsWindow.History.cs");
        Assert.Contains("ApplyHistoryLoadState(loadFailed: _historyShowsFailure);", Body(history, "private void ApplyHistoryDeletion("), StringComparison.Ordinal);
        Assert.Contains("private void UpdateHistorySearchStatus() => ApplyHistoryLoadState(loadFailed: _historyShowsFailure);", history, StringComparison.Ordinal);
        Assert.Contains("_historyShowsFailure = loadFailed;", Body(history, "private void ApplyHistoryLoadState("), StringComparison.Ordinal);
    }

    [Fact]
    public void Try_again_after_a_failed_search_searches_again()
    {
        var retry = Body(Read("SettingsWindow.History.cs"), "private void HistoryRetryButton_Click(");
        Assert.True(
            retry.IndexOf("if (IsHistorySearchActive() && _historyLoad.IsLoaded)", StringComparison.Ordinal) <
            retry.IndexOf("StartHistorySearch(debounce: false);", StringComparison.Ordinal),
            "A failed search must retry the search itself, not only the recent load.");
    }

    [Fact]
    public void A_current_load_made_stale_while_rows_are_shown_finishes_as_loaded()
    {
        // Dropping it left the section Loading with nothing running, and no search started.
        var load = Body(Read("SettingsWindow.History.cs"), "private async void LoadHistory(int retry = 0)");
        var drop = load.IndexOf("completion == HistoryReadCompletion.Drop && requestStillCurrent", StringComparison.Ordinal);
        Assert.True(drop >= 0, "The current stale load must be handled.");
        Assert.True(load.IndexOf("ContinueWithShownHistory(SelectedHistory?.Id);", drop, StringComparison.Ordinal) > drop);
    }

    [Fact]
    public void Leaving_History_ends_its_searches()
    {
        var history = Read("SettingsWindow.History.cs");
        Assert.Contains("HookHistoryLeave();", Body(history, "private async void LoadHistory(int retry = 0)"), StringComparison.Ordinal);
        var hook = Body(history, "private void HookHistoryLeave(");
        Assert.Contains("Interlocked.Increment(ref _historySearchTicket);", hook, StringComparison.Ordinal);
        Assert.Contains("_historySearchDelay?.Cancel();", hook, StringComparison.Ordinal);
    }

    [Fact]
    public void Replacing_the_page_cache_invalidates_older_page_tickets()
    {
        var history = Read("SettingsWindow.History.cs");
        var load = Body(history, "private async void LoadHistory(int retry = 0)");
        Assert.Contains("Interlocked.Increment(ref _historyOlderTicket);", load, StringComparison.Ordinal);

        var loadOlder = Body(history, "private async void HistoryLoadOlderButton_Click(");
        Assert.Contains("var requestStillCurrent = ticket == Interlocked.Read(ref _historyOlderTicket);", loadOlder, StringComparison.Ordinal);
        Assert.Contains("completion == HistoryReadCompletion.Drop", loadOlder, StringComparison.Ordinal);
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
