using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Scribe.App.Infrastructure;
using Scribe.Core.Diagnostics;
using Scribe.Core.Feedback;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    // --- History --------------------------------------------------------------------------

    private async void LoadHistory()
    {
        if (!_historyLoad.TryBegin(null, out var ticket))
        {
            return;
        }

        var generation = _historyMutationGeneration.Capture();
        IReadOnlyList<HistoryEntry> entries;
        try
        {
            entries = await Task.Run(() => _history.GetRecent(HistoryRowFormat.RecentLimit + 1));
        }
        catch (Exception ex)
        {
            if (_historyLoad.Fail(ticket))
            {
                TryLog(ex, "Could not load dictation history for Settings.");
                HistoryEmptyHint.Text = HistoryRowFormat.LoadFailedText;
                HistoryInlineStatusText.Text = HistoryRowFormat.LoadFailedText;
                ApplyHistoryLoadState(loadFailed: true);
            }

            return;
        }

        if (!_historyLoad.Publish(ticket, string.Empty) || !_historyMutationGeneration.IsCurrent(generation))
        {
            return;
        }

        var selectedId = SelectedHistory?.Id;
        _historyLoadedOlder = false;
        _historyOlderLoading = false;
        _historyOlderLoadFailed = false;
        _historyMayHaveOlder = entries.Count > HistoryRowFormat.RecentLimit;
        _historyPagedRows.Clear();
        foreach (var entry in entries.Take(HistoryRowFormat.RecentLimit))
        {
            _historyPagedRows.Add(RowFromEntry(entry));
        }

        if (IsHistorySearchActive())
        {
            StartHistorySearch(debounce: false);
        }
        else
        {
            ShowPagedHistoryRows(selectedId);
        }
    }

    private HistoryRow? SelectedHistory => HistoryGrid.SelectedItem as HistoryRow;

    private void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateHistorySelection();

    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsHistorySearchActive())
        {
            StartHistorySearch(debounce: true);
            return;
        }

        _historySearchDelay?.Cancel();
        ShowPagedHistoryRows(SelectedHistory?.Id);
    }

    private void HistoryClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        HistorySearchBox.Clear();
        HistorySearchBox.Focus();
    }

    private void HistoryRetryButton_Click(object sender, RoutedEventArgs e)
    {
        HistoryEmptyHint.Text = HistoryRowFormat.LoadingText;
        HistoryInlineStatusText.Text = HistoryRowFormat.LoadingText;
        ApplyHistoryLoadState(loadFailed: false, loading: true);
        LoadHistory();
    }

    private async void HistoryLoadOlderButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsHistorySearchActive() || _historyOlderLoading || (!_historyMayHaveOlder && !_historyOlderLoadFailed))
        {
            return;
        }

        if (_historyPagedRows.LastOrDefault() is not { } boundary)
        {
            return;
        }

        _historyOlderLoading = true;
        _historyOlderLoadFailed = false;
        var ticket = Interlocked.Increment(ref _historyOlderTicket);
        var generation = _historyMutationGeneration.Capture();
        UpdateHistoryRangeLine();

        IReadOnlyList<HistoryEntry> entries;
        try
        {
            entries = await Task.Run(() => _history.GetOlder(boundary.TimestampUtc, boundary.Id, HistoryRowFormat.RecentLimit + 1));
        }
        catch (Exception ex)
        {
            if (ticket == Interlocked.Read(ref _historyOlderTicket))
            {
                _historyOlderLoading = false;
                _historyOlderLoadFailed = _historyMutationGeneration.IsCurrent(generation);
            }

            TryLog(ex, "Could not load older dictation history for Settings.");
            UpdateHistoryRangeLine();
            return;
        }

        if (ticket == Interlocked.Read(ref _historyOlderTicket))
        {
            _historyOlderLoading = false;
        }

        if (_closed || !_historyMutationGeneration.IsCurrent(generation) || IsHistorySearchActive())
        {
            UpdateHistoryRangeLine();
            return;
        }

        _historyOlderLoadFailed = false;
        _historyLoadedOlder = true;
        _historyMayHaveOlder = entries.Count > HistoryRowFormat.RecentLimit;
        foreach (var entry in entries.Take(HistoryRowFormat.RecentLimit))
        {
            _historyPagedRows.Add(RowFromEntry(entry));
        }

        ShowPagedHistoryRows(SelectedHistory?.Id);
    }

    private bool FilterHistoryRow(object item) => true;

    private bool IsHistorySearchActive() => !string.IsNullOrWhiteSpace(HistorySearchBox?.Text);

    private void StartHistorySearch(bool debounce)
    {
        var query = HistorySearchBox.Text;
        _historySearchDelay?.Cancel();
        _historySearchDelay?.Dispose();
        var source = new CancellationTokenSource();
        _historySearchDelay = source;
        var ticket = Interlocked.Increment(ref _historySearchTicket);
        var generation = _historyMutationGeneration.Capture();
        _ = RunHistorySearchAsync(query, ticket, generation, debounce, source.Token);
    }

    private async Task RunHistorySearchAsync(string query, long ticket, long generation, bool debounce, CancellationToken cancellationToken)
    {
        try
        {
            if (debounce)
            {
                await Task.Delay(250, cancellationToken);
            }

            var entries = await Task.Run(() => _history.Search(query, HistoryRowFormat.RecentLimit), cancellationToken);
            if (cancellationToken.IsCancellationRequested ||
                _closed ||
                ticket != Interlocked.Read(ref _historySearchTicket) ||
                !_historyMutationGeneration.IsCurrent(generation) ||
                !string.Equals(query, HistorySearchBox.Text, StringComparison.Ordinal))
            {
                return;
            }

            var selectedId = SelectedHistory?.Id;
            SetHistoryRows(entries.Select(RowFromEntry), selectedId);
            ApplyHistoryLoadState(loadFailed: false);
            UpdateHistoryRangeLine();
            UpdateHistorySelection();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_closed || ticket != Interlocked.Read(ref _historySearchTicket) || !_historyMutationGeneration.IsCurrent(generation))
            {
                return;
            }

            TryLog(ex, "Could not search dictation history for Settings.");
            HistoryInlineStatusText.Text = HistoryRowFormat.LoadFailedText;
            ApplyHistoryLoadState(loadFailed: true);
        }
    }

    private void ShowPagedHistoryRows(long? selectedId)
    {
        SetHistoryRows(_historyPagedRows, selectedId);
        HistoryEmptyHint.Text = _historyEmptyText;
        HistoryRetryButton.Visibility = Visibility.Collapsed;
        ApplyHistoryLoadState(loadFailed: false);
        UpdateHistoryRangeLine();
        UpdateHistorySelection();
    }

    private void SetHistoryRows(IEnumerable<HistoryRow> rows, long? selectedId)
    {
        _historyRows.Clear();
        foreach (var row in rows)
        {
            _historyRows.Add(row);
        }

        if (selectedId is { } reselect && IndexOfHistoryRow(reselect) is >= 0 and var reselectAt)
        {
            HistoryGrid.SelectedItem = _historyRows[reselectAt];
        }

        _historyView?.Refresh();
    }

    private HistoryRow RowFromEntry(HistoryEntry entry)
    {
        var row = HistoryRow.From(entry);
        return _ratingWrites.IsPending(row.Id)
            ? row with { Rating = _ratingWrites.Resolve(row.Id, row.Rating), RatingPending = true }
            : row;
    }

    private void UpdateHistoryRangeLine()
    {
        if (IsHistorySearchActive())
        {
            var searchLine = HistoryRowFormat.SearchLine(_historyRows.Count, HistoryRowFormat.RecentLimit);
            HistoryRangeText.Text = searchLine.Text;
            HistoryLoadOlderButton.Visibility = Visibility.Collapsed;
            HistoryRangePanel.Visibility = searchLine.ShowClearSearch ? Visibility.Collapsed : Visibility.Visible;
            HistoryNoMatchesText.Text = searchLine.Text;
            HistoryClearSearchButton.Content = HistoryRowFormat.ClearSearch;
            return;
        }

        var pageLine = HistoryRowFormat.PageLine(
            _historyPagedRows.Count,
            HistoryRowFormat.RecentLimit,
            _historyLoadedOlder,
            _historyMayHaveOlder,
            _historyOlderLoadFailed);
        HistoryRangeText.Text = _historyOlderLoading ? HistoryRowFormat.LoadingText : pageLine.Text;
        HistoryLoadOlderButton.Content = pageLine.LoadOlderButtonText;
        HistoryLoadOlderButton.IsEnabled = !_historyOlderLoading;
        HistoryLoadOlderButton.Visibility = pageLine.ShowLoadOlder && !_historyOlderLoading ? Visibility.Visible : Visibility.Collapsed;
        HistoryRangePanel.Visibility = HistoryRangeText.Text.Length == 0 && HistoryLoadOlderButton.Visibility != Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UpdateHistorySearchStatus() => ApplyHistoryLoadState(loadFailed: false);

    private void ApplyHistoryLoadState(bool loadFailed, bool loading = false)
    {
        var searchActive = IsHistorySearchActive();
        var hasPagedRows = _historyPagedRows.Count > 0;
        var hasShownRows = _historyRows.Count > 0;
        var noMatches = searchActive && !hasShownRows;
        var showCentered = !hasPagedRows && !hasShownRows && !noMatches;
        var showInline = (loadFailed || loading) && (hasPagedRows || hasShownRows || noMatches);
        HistoryGrid.Visibility = hasShownRows ? Visibility.Visible : Visibility.Collapsed;
        HistoryToolbarGrid.Visibility = hasPagedRows || hasShownRows || noMatches ? Visibility.Visible : Visibility.Collapsed;
        HistorySearchBox.Visibility = hasPagedRows || hasShownRows || noMatches ? Visibility.Visible : Visibility.Collapsed;
        HistoryStatusPanel.Visibility = showCentered ? Visibility.Visible : Visibility.Collapsed;
        HistoryRetryButton.Visibility = loadFailed && showCentered ? Visibility.Visible : Visibility.Collapsed;
        HistoryInlineStatusPanel.Visibility = showInline ? Visibility.Visible : Visibility.Collapsed;
        HistoryInlineRetryButton.Visibility = loadFailed ? Visibility.Visible : Visibility.Collapsed;
        HistoryNoMatchesPanel.Visibility = noMatches && !loadFailed && !loading ? Visibility.Visible : Visibility.Collapsed;
        UpdateHistoryRangeLine();
    }

    private void UpdateHistorySelection()
    {
        var hasSelection = SelectedHistory is not null;
        var toolbar = HistoryRowFormat.Toolbar(_historyPagedRows.Count > 0 || _historyRows.Count > 0, hasSelection);
        HistoryCopyButton.IsEnabled = toolbar.CanCopy;
        HistoryDeleteButton.IsEnabled = toolbar.CanDelete;
        HistoryClearButton.IsEnabled = toolbar.CanDeleteAll;
        HistoryDetailsCard.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        if (SelectedHistory is not { } row)
        {
            return;
        }

        HistoryDetailsText.Text = row.Text;
        HistoryDetailsTimingText.Text = row.Details;
        HistoryUsefulButton.Tag = row.Id;
        HistoryNotUsefulButton.Tag = row.Id;
        HistoryReportButton.Tag = row.Id;
        HistoryUsefulButton.IsEnabled = row.CanRate;
        HistoryNotUsefulButton.IsEnabled = row.CanRate;
        HistoryReportButton.Visibility = row.CanReport ? Visibility.Visible : Visibility.Collapsed;
        UpdateHistoryRatingButtons(row);
    }

    private void UpdateHistoryRatingButtons(HistoryRow row)
    {
        HistoryUsefulIcon.Filled = row.Rating == AiRating.Useful;
        HistoryNotUsefulIcon.Filled = row.Rating == AiRating.NotUseful;
        HistoryUsefulButton.Appearance = row.Rating == AiRating.Useful
            ? Wpf.Ui.Controls.ControlAppearance.Primary
            : Wpf.Ui.Controls.ControlAppearance.Secondary;
        HistoryNotUsefulButton.Appearance = row.Rating == AiRating.NotUseful
            ? Wpf.Ui.Controls.ControlAppearance.Primary
            : Wpf.Ui.Controls.ControlAppearance.Secondary;
        AutomationProperties.SetName(HistoryUsefulButton, row.ThumbUpName);
        AutomationProperties.SetName(HistoryNotUsefulButton, row.ThumbDownName);
    }

    private string HistoryEmptyMessage()
    {
        var binding = _committedSettings.Hotkey;
        return HistoryRowFormat.EmptyState(HotkeyText.Verb(binding.Mode), HotkeyCapture.Describe(binding));
    }

    // Everything on History and Usage that describes the saved and running settings rather than the draft.
    private void OnCommittedSettingsChanged()
    {
        RefreshHistoryEmptyTextFromCommitted();
        RefreshUsageInsightAvailability();
    }

    private void RefreshHistoryEmptyTextFromCommitted()
    {
        _historyEmptyText = HistoryEmptyMessage();
        if (_historyRows.Count == 0 && HistoryRetryButton.Visibility != Visibility.Visible)
        {
            HistoryEmptyHint.Text = _historyEmptyText;
        }
    }

    private void RefreshHistorySettingsSummary()
    {
        if (HistorySettingsSummaryText is null)
        {
            return;
        }

        var days = SelectedDurationValue(HistoryRetentionCombo, HistoryRetentionCustomBox, _settings.HistoryRetentionDays);
        HistorySettingsSummaryText.Text = HistorySettingsSummary.Describe(days, StoreAudioCheck.IsChecked == true);
    }

    private void HistorySettings_Changed(object sender, RoutedEventArgs e) => RefreshHistorySettingsSummary();

    // A custom duration reaches what Save reads (the box's Value) when it commits, so the summary follows the same value.
    private void HistoryRetentionCustomBox_ValueChanged(object sender, Wpf.Ui.Controls.NumberBoxValueChangedEventArgs e) =>
        RefreshHistorySettingsSummary();

    private void HistoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => CopyHistoryText();

    private void HistoryThumbUp_Click(object sender, RoutedEventArgs e) =>
        RateHistoryRow(sender, AiRating.Useful);

    private void HistoryThumbDown_Click(object sender, RoutedEventArgs e) =>
        RateHistoryRow(sender, AiRating.NotUseful);

    /// <summary>
    /// Records an opinion about one AI result, or clears it when the same thumb is pressed twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stays local. This is a quality signal about a rewrite, never a rating of Scribe: Store policy
    /// requires an in-app rating OF THE APP to route to the Store's own mechanism regardless of
    /// sentiment, and treats sending positive sentiment to the Store while keeping negative
    /// sentiment private as a fraudulent practice. The Store rating action lives in About, is
    /// unconditional, and is deliberately not wired to these buttons.
    /// </para>
    /// <para>
    /// The write runs off the UI thread and the row shows the new rating at once. The row's thumbs
    /// stay off until the write finishes, the row is found again by id afterwards because the list
    /// may have been reloaded, and a failed write puts the old rating back.
    /// </para>
    /// </remarks>
    private async void RateHistoryRow(object sender, AiRating rating)
    {
        if (sender is not FrameworkElement { Tag: long id })
        {
            return;
        }

        var index = IndexOfHistoryRow(id);
        if (index < 0)
        {
            return;
        }

        // Pressing the same thumb again clears it, so a misclick is undoable without a second
        // control explaining itself.
        var row = _historyRows[index];
        var next = row.Rating == rating ? AiRating.Unrated : rating;
        if (!_ratingWrites.TryBegin(id, row.Rating, next))
        {
            return;
        }

        ReplaceHistoryRow(index, row with { Rating = next, RatingPending = true });
        UpdateHistoryCachedRow(id, cached => cached with { Rating = next, RatingPending = true });

        var saved = false;
        try
        {
            await Task.Run(() => _history.SetAiRating(id, next));
            saved = true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not save the rating for history entry {Id} ({Failure}).", id, FailureShape.Describe(ex));
        }

        var shown = _ratingWrites.Complete(id, saved);
        if (_closed)
        {
            return;
        }

        _historyMutationGeneration.CompleteRatingWrite(saved);
        UpdateHistoryRowById(id, current => current with { Rating = shown, RatingPending = false });

        UpdateHistorySelection();

        // A refresh still running may have read this row before the write landed. It would publish
        // the old rating now that nothing is pending, so it is restarted to read the new one.
        if (saved && _historyLoad.Invalidate())
        {
            LoadHistory();
        }

        if (!saved)
        {
            ShowInfo("Couldn't save that rating. Try again.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
        }
    }

    /// <summary>
    /// Opens a report for one AI result. Composes it, shows the user exactly what it contains, and
    /// leaves the sending to them.
    /// </summary>
    private void HistoryReport_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: long id })
        {
            return;
        }

        var index = IndexOfHistoryRow(id);
        if (index < 0)
        {
            return;
        }

        ShowAiReportDialog(_historyRows[index].Text, useCurrentAttribution: false);
    }

    // Rows are records compared by reference, so replacing one clears the list's selection when it was the selected row.
    // Put it back on the replacement then, and only then: a row the user selected meanwhile keeps its selection.
    private void ReplaceHistoryRow(int index, HistoryRow replacement)
    {
        var wasSelected = ReferenceEquals(HistoryGrid.SelectedItem, _historyRows[index]);
        _historyRows[index] = replacement;
        var pageIndex = _historyPagedRows.FindIndex(row => row.Id == replacement.Id);
        if (pageIndex >= 0)
        {
            _historyPagedRows[pageIndex] = replacement;
        }

        if (wasSelected && HistoryGrid.SelectedItem is null)
        {
            HistoryGrid.SelectedItem = replacement;
        }
    }

    private void UpdateHistoryRowById(long id, Func<HistoryRow, HistoryRow> update)
    {
        if (IndexOfHistoryRow(id) is >= 0 and var shownIndex)
        {
            ReplaceHistoryRow(shownIndex, update(_historyRows[shownIndex]));
        }
        else
        {
            UpdateHistoryCachedRow(id, update);
        }
    }

    private void UpdateHistoryCachedRow(long id, Func<HistoryRow, HistoryRow> update)
    {
        var pageIndex = _historyPagedRows.FindIndex(row => row.Id == id);
        if (pageIndex >= 0)
        {
            _historyPagedRows[pageIndex] = update(_historyPagedRows[pageIndex]);
        }
    }

    private void OnHistoryDeleted(HistoryDeletion deletion)
    {
        _ = Dispatcher.BeginInvoke(() => ApplyHistoryDeletion(deletion));
    }

    private void ApplyHistoryDeletion(HistoryDeletion deletion)
    {
        if (_closed)
        {
            return;
        }

        _historyMutationGeneration.AdvanceForDeletion();
        var selectedId = SelectedHistory?.Id;
        _historyPagedRows.RemoveAll(row => HistoryDeletionCoversRow(deletion, row));
        for (var i = _historyRows.Count - 1; i >= 0; i--)
        {
            if (HistoryDeletionCoversRow(deletion, _historyRows[i]))
            {
                _historyRows.RemoveAt(i);
            }
        }

        if (deletion.Kind == HistoryDeletionKind.Clear)
        {
            _historyLoadedOlder = false;
            _historyMayHaveOlder = false;
            _historyOlderLoadFailed = false;
        }

        if (selectedId is { } reselect && IndexOfHistoryRow(reselect) is >= 0 and var reselectAt)
        {
            HistoryGrid.SelectedItem = _historyRows[reselectAt];
        }

        ApplyHistoryLoadState(loadFailed: false);
        UpdateHistorySelection();
    }

    private static bool HistoryDeletionCoversRow(HistoryDeletion deletion, HistoryRow row) =>
        deletion.Kind switch
        {
            HistoryDeletionKind.Entry when deletion.Entry is { } entry =>
                entry.Id == row.Id,
            HistoryDeletionKind.Clear => true,
            HistoryDeletionKind.OlderThan when deletion.CutoffUtc is { } cutoff => row.TimestampUtc < cutoff,
            _ => false,
        };

    private int IndexOfHistoryRow(long id)
    {
        for (var i = 0; i < _historyRows.Count; i++)
        {
            if (_historyRows[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private void HistoryCopyButton_Click(object sender, RoutedEventArgs e) => CopyHistoryText();

    private void CopyHistoryText()
    {
        if (SelectedHistory is not { } row)
        {
            return;
        }

        try
        {
            var copied = ScribeClipboard.SetText(row.Text);
            ShowInfo(
                copied ? "Copied the selected dictation." : "Couldn't copy the dictation. Try again.",
                copied ? Wpf.Ui.Controls.InfoBarSeverity.Success : Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        catch
        {
            ShowInfo("Couldn't copy the dictation. Try again.", Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private async void HistoryDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHistory is not { } row)
        {
            return;
        }

        if (!await ConfirmRiskyAsync("Delete this dictation?", "This can't be undone.", "Delete dictation"))
        {
            return;
        }

        HistoryDeleteButton.IsEnabled = false;
        try
        {
            await Task.Run(() => _history.Delete(row.Id));
            if (_closed)
            {
                return;
            }

            ApplyHistoryDeletion(new HistoryDeletion(HistoryDeletionKind.Entry, new HistoryEntry(row.Id, row.TimestampUtc, row.Text, 0, 0)));
            ShowInfo("Deleted the selected history entry.");
        }
        catch
        {
            if (_closed)
            {
                return;
            }

            ShowInfo("Couldn't delete the history entry. Try again.", Wpf.Ui.Controls.InfoBarSeverity.Error);
            UpdateHistorySelection();
        }
    }

    private async void HistoryClearButton_Click(object sender, RoutedEventArgs e)
    {
        if ((_historyPagedRows.Count == 0 && _historyRows.Count == 0) ||
            !await ConfirmRiskyAsync(
                "Delete all history?",
                "This deletes every saved dictation and recording now, including ones not shown here. Your dictionary, snippets and settings are kept. This can't be undone.",
                "Delete all history"))
        {
            return;
        }

        HistoryClearButton.IsEnabled = false;
        try
        {
            await Task.Run(_history.Clear);
            if (_closed)
            {
                return;
            }

            ApplyHistoryDeletion(new HistoryDeletion(HistoryDeletionKind.Clear));
            ShowInfo("Cleared dictation history.");
        }
        catch
        {
            if (_closed)
            {
                return;
            }

            ShowInfo("Couldn't clear history. Try again.", Wpf.Ui.Controls.InfoBarSeverity.Error);
            HistoryClearButton.IsEnabled = _historyPagedRows.Count > 0 || _historyRows.Count > 0;
        }
    }

    private sealed record HistoryRow(
        long Id, DateTimeOffset TimestampUtc, string When, string Text, string App, string Audio, string Decode, string Cleanup, string Details)
    {
        /// <summary>
        /// Whether AI cleanup actually ran for this dictation. The thumbs and the report only apply
        /// to generative output: a dictation that was merely transcribed, or that had the user's own
        /// dictionary applied, is not AI-generated content and reporting it as such would be noise.
        /// </summary>
        public bool HasCleanupTime { get; init; }

        /// <summary>What the user said about the result, if anything.</summary>
        public AiRating Rating { get; init; } = AiRating.Unrated;

        /// <summary>A rating write for this row is still running; its thumbs stay off until it lands.</summary>
        public bool RatingPending { get; init; }

        public bool CanRate => !RatingPending;

        /// <summary>Filled glyph when chosen, outline when not. Segoe MDL2 Assets.</summary>
        public string ThumbUpGlyph => Rating == AiRating.Useful ? "" : "";

        public string ThumbDownGlyph => Rating == AiRating.NotUseful ? "" : "";

        // The thumbs draw only a glyph, so these name them for UI Automation: each says what its ToolTip says,
        // and adds whether it is the rating given, which the filled glyph shows.
        public string ThumbUpName =>
            Rating == AiRating.Useful ? "Useful, selected" : "Useful";

        public string ThumbDownName =>
            Rating == AiRating.NotUseful ? "Not useful, selected" : "Not useful";

        public bool CanReport => HasCleanupTime;

        // UI Automation names the row after ToString(), and so the Result cell too, which holds buttons rather
        // than text. A record's ToString lists every field.
        public override string ToString() => $"{When}, {Text}";

        // Compared by reference, not by value: see the note above UsageTrendRow.
        public bool Equals(HistoryRow? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

        public static HistoryRow From(HistoryEntry entry) => new(
            entry.Id,
            entry.TimestampUtc,
            entry.TimestampUtc.ToLocalTime().ToString("MMM d, h:mm tt"),
            entry.Text,
            string.IsNullOrWhiteSpace(entry.TargetApp) ? HistoryRowFormat.NotApplicable : AppDisplayName.For(entry.TargetApp!),
            HistoryRowFormat.Audio(entry.AudioMilliseconds),
            HistoryRowFormat.Latency(entry.DecodeMilliseconds),
            HistoryRowFormat.CleanupTime(entry.CleanupMilliseconds),
            HistoryRowFormat.Details(entry.AudioMilliseconds, entry.DecodeMilliseconds, entry.CleanupMilliseconds))
        {
            HasCleanupTime = entry.CleanupMilliseconds is >= 0,
            Rating = entry.AiRating,
        };
    }

}
