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

    private async void LoadHistory(int retry = 0)
    {
        HookHistoryLeave();
        if (!_historyLoad.TryBegin(null, out var ticket))
        {
            return;
        }

        if (retry > 0)
        {
            // Wait out the burst of deletions that made the last read stale, and read again only if History is still
            // the page on screen and nothing newer replaced this request.
            await Task.Delay(HistoryReadGeneration.RetryDelay(retry));
            if (!_historyLoad.CanPublish(ticket) || !IsHistoryPageShown())
            {
                return;
            }
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
                ShowHistoryLoadFailed();
            }

            return;
        }

        var requestStillCurrent = _historyLoad.CanPublish(ticket) && IsHistoryPageShown();
        var retryIfStale = _historyPagedRows.Count == 0 && _historyRows.Count == 0;
        var completion = _historyMutationGeneration.CompleteRead(generation, requestStillCurrent, retryIfStale, retry);
        if (completion == HistoryReadCompletion.Retry)
        {
            _historyLoad.Fail(ticket);
            LoadHistory(retry + 1);
            return;
        }

        if (completion == HistoryReadCompletion.GiveUp)
        {
            if (_historyLoad.Fail(ticket))
            {
                LogHistoryReadGaveUp("load", retry);
                ShowHistoryLoadFailed();
            }

            return;
        }

        if (completion == HistoryReadCompletion.Drop && requestStillCurrent)
        {
            // Still the current request, but a deletion or rating made it stale while rows were already shown. Those
            // rows were brought up to date as each change arrived, so they stand: finish this request as loaded
            // (never leave it Loading with nothing running) and carry on as a successful load would.
            if (_historyLoad.Publish(ticket, string.Empty))
            {
                ContinueWithShownHistory(SelectedHistory?.Id);
            }

            return;
        }

        if (completion == HistoryReadCompletion.Drop || !_historyLoad.Publish(ticket, string.Empty))
        {
            return;
        }

        var selectedId = SelectedHistory?.Id;
        Interlocked.Increment(ref _historyOlderTicket);
        _historyLoadedOlder = false;
        _historyOlderLoading = false;
        _historyOlderLoadFailed = false;
        _historyMayHaveOlder = entries.Count > HistoryRowFormat.RecentLimit;
        _historyPagedRows.Clear();
        foreach (var entry in entries.Take(HistoryRowFormat.RecentLimit))
        {
            _historyPagedRows.Add(RowFromEntry(entry));
        }

        ContinueWithShownHistory(selectedId);
    }

    private void ContinueWithShownHistory(long? selectedId)
    {
        if (IsHistorySearchActive())
        {
            StartHistorySearch(debounce: false);
        }
        else
        {
            ShowPagedHistoryRows(selectedId);
        }
    }

    // Leaving History ends its searches: one waiting to retry would otherwise resume on the next visit, beside the fresh
    // load that showing the page starts. Hooked on first use so the History code stays in this file. IsVisibleChanged
    // never fires offscreen (the render harness has no presentation source), where nothing navigates anyway.
    private void HookHistoryLeave()
    {
        if (_historyLeaveHooked)
        {
            return;
        }

        _historyLeaveHooked = true;
        SectionHistory.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                Interlocked.Increment(ref _historySearchTicket);
                _historySearchDelay?.Cancel();
            }
        };
    }

    private HistoryRow? SelectedHistory => HistoryGrid.SelectedItem as HistoryRow;

    // Visibility, not IsVisible: the offscreen render harness has no presentation source, where IsVisible is always false.
    private bool IsHistoryPageShown() => !_closed && SectionHistory.Visibility == Visibility.Visible;

    private void ShowHistoryLoadFailed()
    {
        HistoryEmptyHint.Text = HistoryRowFormat.LoadFailedText;
        HistoryInlineStatusText.Text = HistoryRowFormat.LoadFailedText;
        ApplyHistoryLoadState(loadFailed: true);
    }

    private void LogHistoryReadGaveUp(string read, int retries)
    {
        try
        {
            _log.LogInformation(
                "History {Read} stayed stale after {Retries} automatic retries while dictations were deleted or rated; showing Try again.",
                read,
                retries);
        }
        catch
        {
            // Diagnostics must never disrupt the settings window.
        }
    }

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

        // A failed search retries the search itself, with a fresh retry budget: going through the recent load could
        // drop that load as stale (rows are already shown) and never search again.
        if (IsHistorySearchActive() && _historyLoad.IsLoaded)
        {
            StartHistorySearch(debounce: false);
            return;
        }

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

        var requestStillCurrent = ticket == Interlocked.Read(ref _historyOlderTicket);
        if (requestStillCurrent)
        {
            _historyOlderLoading = false;
        }

        var completion = _historyMutationGeneration.CompleteRead(generation, requestStillCurrent, retryWhenStale: false);
        if (_closed || completion == HistoryReadCompletion.Drop || IsHistorySearchActive())
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

    private void StartHistorySearch(bool debounce, int retry = 0)
    {
        var query = HistorySearchBox.Text;
        _historySearchDelay?.Cancel();
        _historySearchDelay?.Dispose();
        var source = new CancellationTokenSource();
        _historySearchDelay = source;
        var ticket = Interlocked.Increment(ref _historySearchTicket);
        _ = RunHistorySearchAsync(query, ticket, debounce, retry, source.Token);
    }

    private bool IsCurrentHistorySearch(string query, long ticket, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        IsHistoryPageShown() &&
        ticket == Interlocked.Read(ref _historySearchTicket) &&
        string.Equals(query, HistorySearchBox.Text, StringComparison.Ordinal);

    private async Task RunHistorySearchAsync(string query, long ticket, bool debounce, int retry, CancellationToken cancellationToken)
    {
        // Captured after any wait, so a deletion during the debounce or a retry's wait doesn't make this read stale.
        var generation = 0L;
        try
        {
            if (debounce)
            {
                await Task.Delay(250, cancellationToken);
            }
            else if (retry > 0)
            {
                await Task.Delay(HistoryReadGeneration.RetryDelay(retry), cancellationToken);
            }

            if (!IsCurrentHistorySearch(query, ticket, cancellationToken))
            {
                return;
            }

            generation = _historyMutationGeneration.Capture();
            var entries = await Task.Run(() => _history.Search(query, HistoryRowFormat.RecentLimit), cancellationToken);
            var completion = _historyMutationGeneration.CompleteRead(
                generation, IsCurrentHistorySearch(query, ticket, cancellationToken), retryWhenStale: true, retry);
            if (completion == HistoryReadCompletion.Retry)
            {
                StartHistorySearch(debounce: false, retry + 1);
                return;
            }

            if (completion == HistoryReadCompletion.GiveUp)
            {
                LogHistoryReadGaveUp("search", retry);
                HistoryInlineStatusText.Text = HistoryRowFormat.LoadFailedText;
                ApplyHistoryLoadState(loadFailed: true);
                return;
            }

            if (completion == HistoryReadCompletion.Drop)
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
            var completion = _historyMutationGeneration.CompleteRead(
                generation, IsCurrentHistorySearch(query, ticket, CancellationToken.None), retryWhenStale: true, retry);
            if (completion == HistoryReadCompletion.Retry)
            {
                StartHistorySearch(debounce: false, retry + 1);
                return;
            }

            if (completion is HistoryReadCompletion.Drop)
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

    private void UpdateHistorySearchStatus() => ApplyHistoryLoadState(loadFailed: _historyShowsFailure);

    // Remembers whether the page shows a failure, so a change that only reconciles rows (a deletion, a rating) keeps the
    // failure and its Try again on screen; only a new read's outcome, Try again or a new search replaces it.
    private void ApplyHistoryLoadState(bool loadFailed, bool loading = false)
    {
        _historyShowsFailure = loadFailed;
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
        if (SectionDiagnostics.Visibility == Visibility.Visible)
        {
            LoadPerformanceStats();
        }
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

        ApplyHistoryLoadState(loadFailed: _historyShowsFailure);
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
