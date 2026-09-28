using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using Scribe.App.Infrastructure;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    // --- Usage ----------------------------------------------------------------------------

    private void UsagePeriodBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            LoadUsage();
        }
    }

    private void UsageRefreshButton_Click(object sender, RoutedEventArgs e) => LoadUsage();
    private void UsageRetryButton_Click(object sender, RoutedEventArgs e) => LoadUsage();

    private void UpdateUsageMetricLayout()
    {
        var width = UsageMetricsCard.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var factor = TextScaleService.CurrentFactor;
        if (factor <= 1)
        {
            if (UsageMetricsGrid.Columns != 6)
            {
                UsageMetricsGrid.Columns = 6;
            }

            return;
        }

        UsageMetricsGrid.Columns = UsageMetricLayout.Columns(width, factor);
    }

    /// <summary>
    /// Recomputes the usage page for the selected period.
    /// </summary>
    /// <remarks>
    /// Period changes, refresh clicks and dictionary adds can arrive faster than a full history read
    /// and analysis completes. At most one computation runs; a request made meanwhile waits as the
    /// only pending one, replacing any older pending request, and the running one is cancelled at its
    /// next step because its answer is already stale. Only the newest request may publish, whether
    /// it succeeded or failed, and nothing publishes after the window closes.
    /// </remarks>
    private void LoadUsage()
    {
        if (_closed)
        {
            return;
        }

        var period = UsagePeriodBox.SelectedItem as UsagePeriodChoice ?? UsagePeriodChoice.All[1];
        var shownPeriod = _usageShownPeriod;
        UsageCoverageText.Visibility = Visibility.Visible;
        UsageCoverageText.Text = UsagePeriodState.Describe(shownPeriod is null ? null : ToUsagePeriod(shownPeriod), ToUsagePeriod(period), loadFailed: false).StatusText;
        UsageRetryButton.Visibility = Visibility.Collapsed;
        // Only when nothing has been shown yet: a request made while another loads, or a retry after a failure, finds the
        // sendable snapshot already cleared, but the numbers on screen are still the labeled old period's.
        if (_usageShownPeriod is null)
        {
            UsageDataPanel.Visibility = Visibility.Collapsed;
            UsageEmptyText.Visibility = Visibility.Collapsed;
        }

        // The shown snapshot no longer matches the request, so the insight button must not send it.
        _usageSnapshot = null;
        _usageLibraryScope = AiVocabularyScope.None;
        RefreshUsageInsightAvailability();

        if (_usageLoads.Submit(period) is { } work)
        {
            RunUsageLoads(work);
        }
    }

    private async void RunUsageLoads(CoalescedRequest<UsagePeriodChoice> first)
    {
        for (var work = first; work is not null; work = _usageLoads.Complete(work))
        {
            UsageReport.Result? result = null;
            Exception? failure = null;
            try
            {
                var request = work;
                var now = DateTimeOffset.UtcNow;
                result = await Task.Run(
                    () =>
                    {
                        // The library service is a vocabulary source, so the report takes its library entries and its
                        // shareable labels from one Current snapshot of that service and ignores these ids (contract
                        // 3.3.6). The ids, those of the committed vocabulary dictation runs on (the libraries the settings
                        // in use enable, never the window's unsaved switches), are what UsageReport reads a service that
                        // is not a source by, as release 0.4.4 read the libraries, sharing no library label.
                        var vocabulary = _libraryVocabulary.Current;
                        return UsageReport.Build(
                            _history,
                            _dictionary,
                            _libraries,
                            [.. vocabulary.AiScope.PermittedLibraryIds],
                            request.Value.Days,
                            now,
                            request.Cancellation,
                            _perfFlags);
                    },
                    request.Cancellation);
            }
            catch (OperationCanceledException) when (work.Cancellation.IsCancellationRequested)
            {
                // Superseded by a newer request, or the window closed. Nothing to show.
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (!_usageLoads.IsCurrent(work))
            {
                continue;
            }

            try
            {
                if (result is not null)
                {
                    ShowUsage(work.Value, result);
                }
                else if (failure is not null)
                {
                    ShowUsageFailure(failure);
                }
            }
            catch (Exception ex)
            {
                TryLog(ex, "Could not show usage insights.");
            }
        }
    }

    private void ShowUsage(UsagePeriodChoice period, UsageReport.Result result)
    {
        _usageSnapshot = result.Snapshot;
        _usageLibraryScope = result.LibraryScope;
        var snapshot = _usageSnapshot;

        UsageCoverageText.Text = UsagePeriodState.CoverageLine(period.Label, _usageSnapshot.Dictations, result.PeriodCapped, UsageReport.HistoryLimit) ?? string.Empty;
        UsageCoverageText.Visibility = UsageCoverageText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UsageDictationsText.Text = snapshot.Dictations.ToString("N0");
        UsageWordsText.Text = snapshot.Words.ToString("N0");
        UsageActiveDaysText.Text = snapshot.ActiveDays.ToString("N0");
        UsageSpeechText.Text = UsagePeriodState.FormatDuration(snapshot.Speech);
        UsageAverageText.Text = snapshot.AverageWords.ToString("0.#");
        UsageLongestText.Text = UsagePeriodState.FormatDuration(snapshot.LongestDictation);
        UsageAppsGrid.ItemsSource = snapshot.TopApps.Select(app => new UsageAppRow(app)).ToList();

        var weekly = snapshot.Granularity == UsageAnalyzer.TrendGranularity.Weekly;
        var trendRows = UsageTrendNormalizer.Normalize(snapshot.Trend)
            .Select(point => new UsageTrendRow(
                weekly ? $"Week of {point.Trend.Start:MMM d}" : point.Trend.Start.ToString("MMM d"),
                point.Trend.Dictations,
                point.Trend.Words,
                point.RelativeHeight))
            .ToList();
        UsageTrendChart.ItemsSource = trendRows;
        UsageTrendGrid.ItemsSource = trendRows.AsEnumerable().Reverse().ToList();
        var axis = UsagePeriodState.AxisLabels(snapshot.Trend, snapshot.Granularity);
        UsageTrendAxisStartText.Text = axis[0];
        UsageTrendAxisMiddleText.Text = axis[1];
        UsageTrendAxisEndText.Text = axis[2];

        var covered = snapshot.Terms.Where(term => term.Covered).ToList();
        var novel = snapshot.Terms.Where(term => !term.Covered).ToList();
        UsageKnownTerms.ItemsSource = covered.Count == 0
            ? ["None of your dictionary words came up in this period."]
            : covered.Select(FormatUsageTerm).ToList();
        UsageNovelEmptyHint.Visibility = novel.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UsageNovelTerms.Visibility = novel.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UsageNovelTerms.ItemsSource = novel
            .Select(term => new UsageTermRow(term.Text, term.Dictations))
            .ToList();

        UsageInsightResultText.Text = string.Empty;
        UsageInsightResultText.Visibility = Visibility.Collapsed;
        UsageDataPanel.Visibility = snapshot.Dictations == 0 ? Visibility.Collapsed : Visibility.Visible;
        UsageEmptyText.Visibility = snapshot.Dictations == 0 ? Visibility.Visible : Visibility.Collapsed;
        UsageRetryButton.Visibility = Visibility.Collapsed;
        _usageShownPeriod = period;
        RefreshUsageInsightAvailability();

        static string FormatUsageTerm(UsageAnalyzer.TermUsage term) =>
            $"{term.Text} ({term.Dictations:N0} dictation{(term.Dictations == 1 ? string.Empty : "s")})";
    }

    private void ShowUsageFailure(Exception failure)
    {
        _usageSnapshot = null;
        _usageLibraryScope = AiVocabularyScope.None;
        UsageCoverageText.Visibility = Visibility.Visible;
        UsageCoverageText.Text = UsagePeriodState.Describe(_usageShownPeriod is null ? null : ToUsagePeriod(_usageShownPeriod), ToUsagePeriod(UsagePeriodBox.SelectedItem as UsagePeriodChoice ?? UsagePeriodChoice.All[1]), loadFailed: true).StatusText;
        if (_usageShownPeriod is null)
        {
            UsageDataPanel.Visibility = Visibility.Collapsed;
            UsageEmptyText.Visibility = Visibility.Collapsed;
        }

        UsageRetryButton.Visibility = Visibility.Visible;
        UsageInsightButton.IsEnabled = false;
    }

    private async void UsageNovelTermAddButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button button || button.DataContext is not UsageTermRow term)
        {
            return;
        }

        button.IsEnabled = false;
        await Task.Yield();

        try
        {
            var entry = DictionaryEntry.New(term.Text.ToLowerInvariant(), term.Text);
            var persisted = PersistLearnedDictionaryEntries([entry]);
            if (persisted.Count == 0)
            {
                ShowInfo(
                    $"\"{term.Text}\" is already in the dictionary grid.",
                    Wpf.Ui.Controls.InfoBarSeverity.Informational);
                return;
            }

            // Only what is stored goes live. After a Save that failed, _settings still holds every edit it was given, a
            // picked AI cleanup provider among them, and applying it would send later dictations there with nothing
            // saved. The stored settings rebuild the post-processor and the glossary with the new entry; when none can be
            // used, only the vocabulary reloads. Either way the entry is said to be added once dictation can use it.
            var reapplied = StoredSettingsReapply.Reapply(_settingsRepository, _applySettings, _reloadVocabulary);
            var refresh = await reapplied.Vocabulary;
            if (_closed)
            {
                return;
            }

            if (refresh.Applied)
            {
                ShowInfo($"Added {term.Text} to your dictionary. This is already saved.");
            }
            else
            {
                ShowInfo(
                    Scribe.Core.Vocabulary.VocabularyNotice.SavedButNotApplied($"Added {term.Text} to your dictionary"),
                    Wpf.Ui.Controls.InfoBarSeverity.Warning);
            }

            LoadUsage();
        }
        catch (Exception ex)
        {
            button.IsEnabled = true;
            _log.LogWarning("Could not add a usage term to the dictionary ({Failure}).", FailureShape.Describe(ex));
            ShowInfo("Couldn't add that word to your dictionary. Try again.", Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private void RefreshUsageInsightAvailability()
    {
        if (UsageInsightButton is null)
        {
            return;
        }

        var state = UsageInsightAvailability.Describe(
            _committedSettings.EnableAiCleanup,
            _cleanup.Status == CleanupStatus.Ready,
            _committedSettings.AiCleanupProvider);
        // The disclosure names the service a request would go to, so it follows every refresh: a Save that changes the
        // provider, a tray change and the service becoming ready each come through here.
        UsageInsightText.Text = state.Description;
        UsageInsightCard.Visibility = state.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        UsageInsightDisabledText.Text = state.DisabledReason ?? string.Empty;
        UsageInsightDisabledText.Visibility = state.DisabledReason is null ? Visibility.Collapsed : Visibility.Visible;
        UsageInsightButton.IsEnabled = !_usageInsightRunning &&
            _usageSnapshot is { Dictations: > 0 } &&
            state.IsEnabled;
    }

    private async void UsageInsightButton_Click(object sender, RoutedEventArgs e)
    {
        if (_usageInsightRunning || _usageSnapshot is not { Dictations: > 0 } snapshot)
        {
            return;
        }

        // Taken with the snapshot, so the summary goes under the scope of the report it was built from.
        var libraryScope = _usageLibraryScope;

        if (_cleanup.Recipient is not { } recipient)
        {
            UsageInsightResultText.Text = UsageSummaryText.NotReady;
            UsageInsightResultText.Visibility = Visibility.Visible;
            return;
        }

        _usageInsightRunning = true;
        RefreshUsageInsightAvailability();
        UsageInsightResultText.Text = UsageSummaryText.Running;
        UsageInsightResultText.Visibility = Visibility.Visible;
        try
        {
            // Every request, the first attempt and each retry, goes only while the report's library scope is still
            // permitted and its libraries' content is unchanged.
            var completion = await _cleanup.CompleteAsync(
                UsageInsight.SystemPrompt,
                UsageInsight.BuildSummary(snapshot),
                recipient,
                libraryScope);
            if (!InsightStillApplies(snapshot))
            {
                return;
            }

            var resultText = completion.Outcome switch
            {
                ScopedCompletionOutcome.RecipientChanged when completion.NothingSent =>
                    UsageSummaryText.RecipientChangedNothingSent,

                // A later attempt was stopped after an earlier one had gone, so this must not say nothing was sent.
                ScopedCompletionOutcome.RecipientChanged or ScopedCompletionOutcome.NotReady =>
                    UsageSummaryText.RecipientChangedAfterSending,
                ScopedCompletionOutcome.LibraryScopeNarrowed =>
                    UsageSummaryText.LibraryScopeNarrowed,
                _ => UsageInsight.Parse(completion.Text) ?? UsageSummaryText.NoAnswer,
            };
            UsageInsightResultText.Text = resultText;
            UsageInsightResultText.Visibility = string.IsNullOrWhiteSpace(resultText) ? Visibility.Collapsed : Visibility.Visible;
            UsageInsightText.Text = UsageInsightAvailability.Describe(
                _committedSettings.EnableAiCleanup,
                _cleanup.Status == CleanupStatus.Ready,
                _committedSettings.AiCleanupProvider).Description;
        }
        catch (Exception ex)
        {
            if (!InsightStillApplies(snapshot))
            {
                return;
            }

            _log.LogWarning("Could not get a usage summary ({Failure}).", FailureShape.Describe(ex));
            UsageInsightResultText.Text = UsageSummaryText.Exception;
            UsageInsightResultText.Visibility = Visibility.Visible;
        }
        finally
        {
            _usageInsightRunning = false;
            if (!_closed)
            {
                RefreshUsageInsightAvailability();
            }
        }
    }

    // An insight describes the snapshot it was asked about. Once the page has moved to another
    // period or been refreshed, showing it would describe numbers that are no longer on screen.
    private bool InsightStillApplies(UsageAnalyzer.Snapshot asked)
    {
        if (_closed)
        {
            return false;
        }

        if (ReferenceEquals(_usageSnapshot, asked))
        {
            return true;
        }

        if (_usageSnapshot is null)
        {
            UsageInsightResultText.Text = UsageSummaryText.SnapshotChanged;
            UsageInsightResultText.Visibility = Visibility.Visible;
        }

        return false;
    }

    private sealed record UsagePeriodChoice(int? Days, string Label)
    {
        public override string ToString() => Label;

        public static IReadOnlyList<UsagePeriodChoice> All { get; } =
        [
            new(7, "Last 7 days"),
            new(30, "Last 30 days"),
            new(90, "Last 90 days"),
            new(null, "All kept history"),
        ];
    }

    private static UsagePeriod ToUsagePeriod(UsagePeriodChoice choice) => choice.Days switch
    {
        7 => UsagePeriod.Last7Days,
        30 => UsagePeriod.Last30Days,
        90 => UsagePeriod.Last90Days,
        null => UsagePeriod.AllKeptHistory,
        _ => UsagePeriod.Last30Days,
    };

    // Every DataGrid row type in this window compares by reference, never by value, which is why the records
    // below override Equals and the Core records are copied into the row classes after them. When a grid's
    // items are replaced, WPF reuses an old row's automation peer for any new item that merely Equals an old
    // one (ItemsControlAutomationPeer.GetChildrenCore, ItemAutomationPeer.ReuseForItem), but the reused peer
    // keeps the cell peers it made for the old item, and those hold that item only weakly
    // (DataGridItemAutomationPeer.GetOrCreateCellItemPeer, DataGridCellItemAutomationPeer). A reload builds
    // equal new rows, so once the old ones were collected every cell was announced as
    // "Item: , Column Display Index: 0", had no bounds, and focus inside the grid went unannounced.
    private sealed record UsageTrendRow(
        string Period,
        int Dictations,
        int Words,
        double RelativeHeight)
    {
        public string ToolTip =>
            $"{Period}: {Dictations:N0} dictation{(Dictations == 1 ? string.Empty : "s")}, " +
            $"{Words:N0} word{(Words == 1 ? string.Empty : "s")}";

        // UI Automation names a trend bar and a trend row after ToString(); a record's lists every field.
        public override string ToString() => ToolTip;

        public bool Equals(UsageTrendRow? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
    }

    /// <summary>One row of the Top apps grid, copied from Core's <see cref="UsageAnalyzer.AppUsage"/> record.</summary>
    private sealed class UsageAppRow(UsageAnalyzer.AppUsage app)
    {
        public string Name { get; } = app.Name;

        public int Dictations { get; } = app.Dictations;

        public int Words { get; } = app.Words;

        public override string ToString() => Name;
    }
}
