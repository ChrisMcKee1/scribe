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

        IReadOnlyList<HistoryEntry> entries;
        try
        {
            entries = await Task.Run(() => _history.GetRecent(HistoryRowFormat.RecentLimit));
        }
        catch (Exception ex)
        {
            if (_historyLoad.Fail(ticket))
            {
                TryLog(ex, "Could not load dictation history for Settings.");
                HistoryEmptyHint.Text = "Couldn't load your history.";
                HistoryRetryButton.Visibility = Visibility.Visible;
                HistoryStatusPanel.Visibility = Visibility.Visible;
            }

            return;
        }

        if (!_historyLoad.Publish(ticket, string.Empty))
        {
            return;
        }

        _historyRows.Clear();
        foreach (var entry in entries)
        {
            var row = HistoryRow.From(entry);

            // A rating still being written was read back before it landed; show the new value.
            _historyRows.Add(_ratingWrites.IsPending(row.Id)
                ? row with { Rating = _ratingWrites.Resolve(row.Id, row.Rating), RatingPending = true }
                : row);
        }

        var hasRows = _historyRows.Count > 0;
        HistoryEmptyHint.Text = _historyEmptyText;
        HistoryRetryButton.Visibility = Visibility.Collapsed;
        HistoryStatusPanel.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;
        HistorySearchBox.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        HistoryRangeText.Text = HistoryRowFormat.RangeLine(_historyRows.Count, HistoryRowFormat.RecentLimit) ?? string.Empty;
        HistoryRangeText.Visibility = HistoryRangeText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _historyView?.Refresh();
        UpdateHistorySearchStatus();
        UpdateHistorySelection();
    }

    private HistoryRow? SelectedHistory => HistoryGrid.SelectedItem as HistoryRow;

    private void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateHistorySelection();

    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _historyView?.Refresh();
        UpdateHistorySearchStatus();
        UpdateHistorySelection();
    }

    private void HistoryClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        HistorySearchBox.Clear();
        HistorySearchBox.Focus();
    }

    private void HistoryRetryButton_Click(object sender, RoutedEventArgs e)
    {
        HistoryRetryButton.Visibility = Visibility.Collapsed;
        HistoryEmptyHint.Text = "Loading history...";
        HistoryStatusPanel.Visibility = Visibility.Visible;
        LoadHistory();
    }

    private bool FilterHistoryRow(object item) =>
        item is not HistoryRow row ||
        TextFilter.Matches(HistorySearchBox?.Text, row.Text, row.App, row.When);

    private void UpdateHistorySearchStatus()
    {
        var filteredCount = _historyView?.Cast<object>().Count() ?? _historyRows.Count;
        var noMatches = _historyRows.Count > 0 && filteredCount == 0 && !string.IsNullOrWhiteSpace(HistorySearchBox.Text);
        HistoryNoMatchesPanel.Visibility = noMatches ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateHistorySelection()
    {
        var hasSelection = SelectedHistory is not null;
        var toolbar = HistoryRowFormat.Toolbar(_historyRows.Count > 0, hasSelection);
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

    private void RefreshHistoryEmptyTextFromCommitted()
    {
        _historyEmptyText = HistoryEmptyMessage();
        if (_historyRows.Count == 0 && HistoryRetryButton.Visibility != Visibility.Visible)
        {
            HistoryEmptyHint.Text = _historyEmptyText;
        }
    }

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

        _historyRows[index] = row with { Rating = next, RatingPending = true };

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

        var current = IndexOfHistoryRow(id);
        if (current >= 0)
        {
            _historyRows[current] = _historyRows[current] with { Rating = shown, RatingPending = false };
        }

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

            LoadHistory();
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
        if (_historyRows.Count == 0 ||
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

            LoadHistory();
            ShowInfo("Cleared dictation history.");
        }
        catch
        {
            if (_closed)
            {
                return;
            }

            ShowInfo("Couldn't clear history. Try again.", Wpf.Ui.Controls.InfoBarSeverity.Error);
            HistoryClearButton.IsEnabled = _historyRows.Count > 0;
        }
    }

    private sealed record HistoryRow(
        long Id, string When, string Text, string App, string Audio, string Decode, string Cleanup, string Details)
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
