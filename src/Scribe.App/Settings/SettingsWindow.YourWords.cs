using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
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
    private bool _dictionarySuggestionRunning;
    private DictionaryRow? _dictionarySelectionPendingRestore;

    // CoalesceDictionaryStatus: one status refresh per burst of row notifications (typing in a cell raises two per
    // keystroke) and one per bulk edit (an import, suggestions, a cleanup) instead of one per row. Null with the flag off,
    // when every notification queues its own refresh and every row added or removed refreshes at once, as before.
    private StatusRefreshScheduler? _dictionaryStatus;

    private void InitializeDictionaryGrid()
    {
        if (_perfFlags.IsOn(PerfFlags.CoalesceDictionaryStatus))
        {
            _dictionaryStatus = new StatusRefreshScheduler(RefreshDictionaryStatus, work => Dispatcher.BeginInvoke(work));
        }

        _dictionaryView = CollectionViewSource.GetDefaultView(_rows);
        _dictionaryView.Filter = DictionaryFilter;
        DictionaryGrid.ItemsSource = _dictionaryView;
        DataGridCheckBoxClick.Attach(DictionaryGrid);
        DataGridTypingTab.Attach(DictionaryGrid, ReportDictionaryEditFailure);
        DataGridTextEdit.Attach(DictionaryGrid, ReportDictionaryEditFailure);
        _rows.CollectionChanged += DictionaryRows_CollectionChanged;
        DictionaryGrid.CellEditEnding += (_, _) => QueueDictionaryStatusRefresh();
        SetDictionaryEditable(false);
        RefreshDictionaryStatus();
    }

    // Until the rows arrive there is nothing to edit, and an edit on an empty grid would be saved as
    // if the user had deleted every entry.
    private void SetDictionaryEditable(bool editable)
    {
        DictionaryGrid.IsEnabled = editable;
        DictionaryAddButton.IsEnabled = editable;
        DictionaryEditButton.IsEnabled = editable && DictionaryGrid.SelectedItem is DictionaryRow;
        DictionarySuggestButton.IsEnabled = editable;
        DictionaryCleanupButton.IsEnabled = editable;
        DictionaryMoreButton.IsEnabled = editable || _dictionaryLoad.State == SettingsSectionState.Failed;
        DictionarySearchBox.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void LoadDictionaryAsync()
    {
        if (!_dictionaryLoad.TryBegin(DictionarySignature(), out var ticket))
        {
            return;
        }

        IReadOnlyList<DictionaryEntry> entries;
        try
        {
            entries = await Task.Run(() => _dictionary.GetAll());
        }
        catch (Exception ex)
        {
            if (_dictionaryLoad.Fail(ticket))
            {
                TryLog(ex, "Could not load the dictionary for Settings.");
                RefreshDictionaryStatus();
            }

            return;
        }

        if (!_dictionaryLoad.CanPublish(ticket))
        {
            return;
        }

        // Rows go in before the grid is bound and with the collection handler detached, as the old
        // synchronous load did, so a large dictionary does not queue one full status refresh per row.
        DictionaryGrid.ItemsSource = null;
        _rows.CollectionChanged -= DictionaryRows_CollectionChanged;
        try
        {
            foreach (var stale in _rows)
            {
                stale.PropertyChanged -= DictionaryRow_PropertyChanged;
            }

            _rows.Clear();
            foreach (var entry in entries)
            {
                var row = new DictionaryRow
                {
                    Id = entry.Id,
                    Pattern = entry.Pattern,
                    Replacement = entry.Replacement,
                    WholeWord = entry.WholeWord,
                    Enabled = entry.Enabled,
                    Origin = DraftRowOrigin.Saved,
                    LoadedPattern = entry.Pattern,
                    LoadedReplacement = entry.Replacement,
                    LoadedWholeWord = entry.WholeWord,
                    LoadedEnabled = entry.Enabled,
                };
                row.PropertyChanged += DictionaryRow_PropertyChanged;
                _rows.Add(row);
            }
        }
        finally
        {
            _rows.CollectionChanged += DictionaryRows_CollectionChanged;
            _dictionaryView = CollectionViewSource.GetDefaultView(_rows);
            _dictionaryView.Filter = DictionaryFilter;
            DictionaryGrid.ItemsSource = _dictionaryView;
        }

        _loadedDictionaryRows = LoadedDictionaryDraftRowsFromRows();
        _dictionaryLoad.Publish(ticket, DictionarySignature());
        SetDictionaryEditable(true);
        RefreshDictionaryStatus();
    }

    // Rows are watched individually as well as collectively: a checkbox click commits without
    // necessarily raising CellEditEnding, so relying on that alone left the Library badge stale.
    private void DictionaryRows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in e.OldItems?.OfType<DictionaryRow>() ?? [])
        {
            row.PropertyChanged -= DictionaryRow_PropertyChanged;
        }

        foreach (var row in e.NewItems?.OfType<DictionaryRow>() ?? [])
        {
            row.PropertyChanged += DictionaryRow_PropertyChanged;
        }

        RefreshDictionaryStatusForRowChange();
    }

    private void DictionaryRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Ignore everything UpdateDictionaryCoverage itself writes. The Coverage setter also raises
        // CoverageLabel, CoverageAppearance and CoverageVisibility, so filtering only Coverage left
        // three unfiltered notifications per row queuing a full recompute each: not an infinite loop
        // (the second pass is a no-op) but a Dispatcher flood proportional to dictionary size.
        if (e.PropertyName is null || e.PropertyName.StartsWith("Coverage", StringComparison.Ordinal))
        {
            return;
        }

        QueueDictionaryStatusRefresh();
    }

    // A row changed: refresh soon (CoalesceDictionaryStatus: once for the whole burst).
    private void QueueDictionaryStatusRefresh()
    {
        if (_dictionaryStatus is { } scheduler)
        {
            scheduler.Request();
            return;
        }

        Dispatcher.BeginInvoke(RefreshDictionaryStatus);
    }

    // A row was added, removed or replaced: refresh at once (CoalesceDictionaryStatus: at a bulk edit's end instead).
    private void RefreshDictionaryStatusForRowChange()
    {
        if (_dictionaryStatus is { } scheduler)
        {
            scheduler.RefreshNow();
            return;
        }

        RefreshDictionaryStatus();
    }

    // A bulk edit: with CoalesceDictionaryStatus, every refresh it asks for waits for its end, which refreshes once; null
    // (nothing held) with the flag off.
    private IDisposable? BatchDictionaryStatus() => _dictionaryStatus?.Batch();

    /// <summary>Recomputes the glossary hint, empty state and per-row word pack coverage badges together.</summary>
    private void RefreshDictionaryStatus()
    {
        UpdateDictionaryViewState();
        if (_perfFlags.IsOn(PerfFlags.ReuseStatusComposition))
        {
            var composition = new RefreshComposition();
            UpdateDictionaryGlossaryHint(ref composition);
            UpdateDictionaryCoverage(ref composition);
        }
        else
        {
            UpdateDictionaryGlossaryHint();
            UpdateDictionaryCoverage();
        }

        UpdateSelectedDictionaryCoverageStatus();
        UpdateDictionaryTabSummaries();
    }

    // ReuseStatusComposition: one refresh's word pack composition, made the first time a consumer asks. LibraryComposition
    // is immutable, and between the glossary hint and the badges nothing changes what it is composed from (the hint only
    // sets text and visibility), so the badges get exactly what composing again would give; a composition that failed
    // cleared the catalog and logged, after which composing again returned null without logging, which is what reuse gives.
    private struct RefreshComposition
    {
        private bool _made;
        private LibraryComposition? _composition;

        public LibraryComposition? Get(SettingsWindow window)
        {
            if (!_made)
            {
                _composition = window.CurrentLibraryComposition();
                _made = true;
            }

            return _composition;
        }
    }

    private bool DictionaryFilter(object item)
    {
        if (item is not DictionaryRow row)
        {
            return false;
        }

        // CachedRowSearchText: each row keeps its fields' normalized text, so a keystroke normalizes only the query.
        if (_perfFlags.IsOn(PerfFlags.CachedRowSearchText))
        {
            return row.MatchesSearch(DictionarySearchBox?.Text);
        }

        return TextFilter.Matches(DictionarySearchBox?.Text, row.Pattern, row.Replacement);
    }

    private void UpdateDictionaryViewState()
    {
        if (DictionaryGridCard is null || DictionaryStateCard is null)
        {
            return;
        }

        var loaded = _dictionaryLoad.IsLoaded;
        var failed = _dictionaryLoad.State == SettingsSectionState.Failed;
        var loading = _dictionaryLoad.State == SettingsSectionState.Loading || _dictionaryLoad.State == SettingsSectionState.Unloaded;
        var query = DictionarySearchBox?.Text ?? string.Empty;
        var visibleRows = _dictionaryView?.Cast<object>().Count() ?? _rows.Count;
        var showState = !loaded || _rows.Count == 0 || (!string.IsNullOrWhiteSpace(query) && visibleRows == 0);

        DictionaryGridCard.Visibility = loaded && !showState ? Visibility.Visible : Visibility.Collapsed;
        DictionaryStateCard.Visibility = showState ? Visibility.Visible : Visibility.Collapsed;
        DictionaryEmptyAddButton.Visibility = loaded && _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DictionaryEmptyLearnButton.Visibility = loaded && _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DictionaryRetryButton.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        DictionaryClearSearchButton.Visibility = loaded && !string.IsNullOrWhiteSpace(query) && visibleRows == 0 ? Visibility.Visible : Visibility.Collapsed;
        DictionaryStateText.Text = failed ? "Couldn't load your dictionary."
            : loading ? "Loading your dictionary..."
            : _rows.Count == 0 ? "No words yet. Add names and terms Scribe gets wrong."
            : "No words match your search.";
    }

    /// <summary>
    /// Tags each row with how it relates to the libraries that are switched on, so the user can see
    /// which entries are redundant and which are deliberate overrides without saving first.
    /// </summary>
    /// <remarks>Deliberately tolerant: a failure here costs a badge, never an edit.</remarks>
    private void UpdateDictionaryCoverage()
    {
        var composition = new RefreshComposition();
        UpdateDictionaryCoverage(ref composition);
    }

    private void UpdateDictionaryCoverage(ref RefreshComposition composition)
    {
        try
        {
            if (_rows.Count == 0)
            {
                return;
            }

            // Precedence, not the list's A to Z order: the badge must name the word pack dictation uses.
            var covering = composition.Get(this)?.Coverage()
                ?? DictionaryLibraryOverlapAnalyzer.Coverage(CurrentWordPackLibraries(), CollectEnabledLibraryIds());

            foreach (var row in _rows)
            {
                var pattern = (row.Pattern ?? string.Empty).Trim();
                if (pattern.Length == 0 || !covering.TryGetValue(pattern, out var hit))
                {
                    row.Coverage = DictionaryRowCoverage.None;
                    row.CoverageTooltip = string.Empty;
                    row.CoverageLibraryName = string.Empty;
                    row.CoverageLibraryReplacement = string.Empty;
                    continue;
                }

                var mine = (row.Replacement ?? string.Empty).Trim();
                var theirs = (hit.Entry.Replacement ?? string.Empty).Trim();
                var same = string.Equals(mine, theirs, StringComparison.Ordinal)
                           && row.WholeWord == hit.Entry.WholeWord;

                row.Coverage = same ? DictionaryRowCoverage.Duplicate : DictionaryRowCoverage.Override;
                row.CoverageLibraryName = hit.LibraryName;
                row.CoverageLibraryReplacement = theirs;
                row.CoverageTooltip = same
                    ? $"\"{hit.LibraryName}\" already writes this as \"{theirs}\", so this word changes nothing. You can keep it or delete it."
                    : $"\"{hit.LibraryName}\" writes this as \"{theirs}\". Your spelling wins. Turn yours off to use the word pack's.";
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not compute dictionary library coverage: {Failure}", FailureShape.DescribeWithStack(ex));
        }
    }

    private void DictionaryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DictionaryEditButton.IsEnabled = _dictionaryLoad.IsLoaded && DictionaryGrid.SelectedItem is DictionaryRow;
        if (DictionaryGrid.SelectedItem is DictionaryRow row)
        {
            _dictionarySelectionPendingRestore = row;
        }

        UpdateSelectedDictionaryCoverageStatus();
    }

    private void DictionarySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DictionaryGrid.SelectedItem is DictionaryRow selected)
        {
            _dictionarySelectionPendingRestore = selected;
        }

        _dictionaryView?.Refresh();
        RestoreDictionarySelectionIfVisible();
        UpdateDictionaryViewState();
    }

    private void RestoreDictionarySelectionIfVisible()
    {
        if (_dictionarySelectionPendingRestore is not { } row || !_rows.Contains(row) ||
            _dictionaryView?.Cast<object>().Contains(row) != true)
        {
            return;
        }

        DictionaryGrid.SelectedItem = row;
        DictionaryGrid.ScrollIntoView(row);
    }

    private void DictionaryClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        DictionarySearchBox.Text = string.Empty;
        DictionarySearchBox.Focus();
    }

    private void DictionaryRetryButton_Click(object sender, RoutedEventArgs e) =>
        LoadDictionaryAsync();

    private void DictionaryMoreButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var review = new MenuItem { Header = "Review overlaps with word packs..." };
        review.Click += ReviewDictionaryOverlaps_Click;
        menu.Items.Add(review);
        menu.Items.Add(new Separator());
        var import = new MenuItem { Header = "Import from a CSV file...", IsEnabled = _dictionaryLoad.IsLoaded };
        import.Click += DictionaryImportButton_Click;
        menu.Items.Add(import);
        var export = new MenuItem { Header = "Export to a CSV file...", IsEnabled = _dictionaryLoad.IsLoaded };
        export.Click += DictionaryExportButton_Click;
        menu.Items.Add(export);
        var template = new MenuItem { Header = "Get a CSV template..." };
        template.Click += DictionaryTemplateButton_Click;
        menu.Items.Add(template);
        menu.PlacementTarget = DictionaryMoreButton;
        menu.IsOpen = true;
    }

    private void DictionaryAddButton_Click(object sender, RoutedEventArgs e)
    {
        OpenDictionaryWordEditor(null, sender as UIElement);
    }

    private void DictionaryEditButton_Click(object sender, RoutedEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as DictionaryRow
            ?? DictionaryGrid.SelectedItem as DictionaryRow;
        if (row is not null)
        {
            OpenDictionaryWordEditor(row, sender as UIElement);
        }
    }

    private void OpenDictionaryWordEditor(DictionaryRow? edited, UIElement? invoker)
    {
        if (_saveInProgress)
        {
            ShowInfo("Wait for Settings to finish saving before opening the word editor.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        if (!_dictionaryLoad.IsLoaded)
        {
            ShowInfo("Wait for your dictionary to load before adding or editing a word.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        if (!DictionaryGrid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true) ||
            !DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true))
        {
            ShowInfo("Finish correcting the word you are editing first.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        var original = edited is null ? (DictionaryEntryBuilder.Row?)null : DictionaryEditorRow(edited);
        var result = DictionaryWordWindow.Show(this, original, (written, forms) =>
        {
            var index = edited is null ? (int?)null : _rows.IndexOf(edited);
            if (edited is not null && (index < 0 ||
                !string.Equals(edited.Pattern, original!.Value.Pattern, StringComparison.Ordinal) ||
                !string.Equals(edited.Replacement, original.Value.Replacement, StringComparison.Ordinal)))
            {
                return new DictionaryWordEditor.Result(null, [],
                    "This word changed while its editor was open. Cancel and open it again.");
            }

            var change = DictionaryWordEditor.Build(_rows.Select(DictionaryEditorRow).ToArray(), index, written, forms);
            if (change.Succeeded && change.AddedRows.Count > 0 && !ClearDictionarySearchForNewRow())
            {
                return new DictionaryWordEditor.Result(null, [],
                    "Cancel this editor and finish correcting the word in the list first.");
            }

            return change;
        });
        if (result is null)
        {
            if (invoker is { IsVisible: true })
            {
                invoker.Focus();
            }
            else if (edited is not null)
            {
                FocusDictionaryRow(edited);
            }

            return;
        }

        DictionaryRow? first = edited;
        using (BatchDictionaryStatus())
        {
            if (edited is not null && result.EditedRow is { } changed)
            {
                edited.Pattern = changed.Pattern ?? string.Empty;
                edited.Replacement = changed.Replacement ?? string.Empty;
            }

            foreach (var added in result.AddedRows)
            {
                var row = new DictionaryRow
                {
                    Pattern = added.Pattern ?? string.Empty,
                    Replacement = added.Replacement ?? string.Empty,
                    WholeWord = added.WholeWord,
                    Enabled = added.Enabled,
                };
                _rows.Add(row);
                first ??= row;
            }
        }

        if (first is not null)
        {
            FocusDictionaryRow(first);
        }
    }

    private static DictionaryEntryBuilder.Row DictionaryEditorRow(DictionaryRow row) =>
        new(row.Id, row.Pattern, row.Replacement, row.WholeWord, row.Enabled);

    private void FocusDictionaryRow(DictionaryRow row)
    {
        DictionaryGrid.SelectedItem = row;
        if (!DataGridTextEdit.Focus(DictionaryGrid, row, DictionarySpokenColumn))
        {
            _log.LogWarning("Could not restore focus to the dictionary row after closing its editor.");
            ShowInfo("The word is in the list, but focus couldn't return to it. Select it to continue.",
                Wpf.Ui.Controls.InfoBarSeverity.Warning);
        }
    }

    private void ReportDictionaryEditFailure()
    {
        _log.LogWarning("Could not focus the dictionary text editor.");
        ShowInfo("Couldn't start editing here. Select the word, then choose Edit.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
    }

    // Clears Find a word so a row being added can be seen. A row still being edited is committed first: refreshing the view
    // during an edit throws, which lost suggestions that arrived while a row was open. Returns false, clearing nothing, when
    // that edit won't commit (an invalid cell), so the caller leaves the rows alone.
    private bool ClearDictionarySearchForNewRow()
    {
        if (string.IsNullOrWhiteSpace(DictionarySearchBox.Text))
        {
            return true;
        }

        if (!DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true))
        {
            return false;
        }

        DictionarySearchBox.Text = string.Empty;
        _dictionaryView?.Refresh();
        return true;
    }

    /// <summary>Removes the row whose delete button was pressed.</summary>
    private void DictionaryDeleteRow_Click(object sender, RoutedEventArgs e)
    {
        // Committing first avoids removing a row the grid still believes it is editing.
        DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        if (sender is FrameworkElement { DataContext: DictionaryRow row })
        {
            _rows.Remove(row);
        }
    }

    // What the dictionary gives AI cleanup, counted by Core the way dictation builds it (GlossaryHint):
    // this page's rows as typed, the libraries of the committed selection, and the provider, prompt style and
    // switches on screen, so every control it reads refreshes it. Local find-and-replace is never capped. This is a
    // status line rather than an input limit, because blocking the 81st entry would break a feature that
    // still works.
    private void UpdateDictionaryGlossaryHint()
    {
        var composition = new RefreshComposition();
        UpdateDictionaryGlossaryHint(ref composition);
    }

    private void UpdateDictionaryGlossaryHint(ref RefreshComposition refreshComposition)
    {
        // Also reached from the AI page's handlers, which can run while InitializeComponent is still
        // creating the controls this reads.
        if (DictionaryGlossaryHint is null || AiPromptStyleCombo is null ||
            AiCleanupCheck is null || PostCheck is null)
        {
            return;
        }

        var entriesOn = _rows.Count(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Pattern));
        var entriesTotal = _rows.Count(r => !string.IsNullOrWhiteSpace(r.Pattern));
        DictionaryOnCountText.Text = $"{entriesOn:N0} of {entriesTotal:N0} words are on.";
        var aiOn = AiCleanupCheck.IsChecked == true;
        DictionaryAiVocabularyText.Visibility = aiOn ? Visibility.Visible : Visibility.Collapsed;
        DictionaryGlossaryDetails.Visibility = aiOn ? Visibility.Visible : Visibility.Collapsed;

        if (!_dictionaryLoad.IsLoaded)
        {
            DictionaryGlossaryHint.Text = _dictionaryLoad.State == SettingsSectionState.Failed
                ? "Couldn't load your dictionary."
                : "Loading your dictionary...";
            return;
        }

        // The entries the selected word packs compose to: precedence, not the list's A to Z order, decides which row
        // survives a shared spoken form. The hint counts them as given and never reorders them.
        var composition = refreshComposition.Get(this);
        if (_wordPackWorkspace is not null && _wordPackCatalog is null)
        {
            DictionaryGlossaryHint.Text = "Word pack vocabulary is unavailable right now.";
            return;
        }

        var localEntries = composition?.LibraryEntries
            ?? DictionaryLibraryComposer.ComposeLibraries(LibraryPrecedence.Enabled(CurrentWordPackLibraries(), CollectEnabledLibraryIds()));
        var aiEntries = composition?.AiLibraryEntries ?? localEntries;

        var hint = new GlossaryHint.Input(
            _rows.Select(r => new DictionaryEntryBuilder.Row(r.Id, r.Pattern, r.Replacement, r.WholeWord, r.Enabled)).ToList(),
            localEntries,
            AiCleanupOn: aiOn,
            PostProcessingOn: PostCheck.IsChecked == true,
            SelectedProvider,
            SelectedPromptStyle,
            aiEntries,
            SelectedCustomEndpoint,
            SendsWholeVocabulary);
        DictionaryGlossaryHint.Text = GlossaryHint.Describe(hint);

        // What a model's context needs to hold all of it, for each app's settings on the AI cleanup page.
        _wholeVocabularyTokens = GlossaryHint.WholeVocabularyTokens(hint);
        UpdateLocalModelTuning();
    }

    // The whole vocabulary switch of the app on this PC that runs the AI, as the page shows it.
    private bool SendsWholeVocabulary => SelectedProvider switch
    {
        Scribe.Core.Cleanup.CleanupProvider.FoundryLocal => FoundryWholeVocabularyCheck?.IsChecked == true,
        _ => SelectedLocalApp switch
        {
            Scribe.Core.Cleanup.LocalServerApp.Ollama => OllamaWholeVocabularyCheck?.IsChecked == true,
            Scribe.Core.Cleanup.LocalServerApp.LmStudio => LmStudioWholeVocabularyCheck?.IsChecked == true,
            _ => false,
        },
    };

    private LibraryComposition? CurrentLibraryComposition()
    {
        if (_wordPackWorkspace is null || _wordPackCatalog is null)
        {
            return null;
        }

        var personal = DictionaryEntryBuilder.Build(
                _rows.Select(r => new DictionaryEntryBuilder.Row(r.Id, r.Pattern, r.Replacement, r.WholeWord, r.Enabled)).ToList())
            .Entries
            .Where(entry => entry.Enabled)
            .OrderBy(entry => entry.Pattern, StringComparer.Ordinal)
            .ToList();

        try
        {
            return LibraryComposition.Preview(
                _wordPackWorkspace.Draft,
                _wordPackCatalog,
                personal,
                GlossaryBudget.For(SelectedPromptStyle, SelectedProvider, SelectedCustomEndpoint));
        }
        catch (Exception ex)
        {
            _wordPackCatalog = null;
            _log.LogWarning("Could not compose the word pack vocabulary preview: {Failure}", FailureShape.DescribeWithStack(ex));
            return null;
        }
    }

    private void UpdateSelectedDictionaryCoverageStatus()
    {
        if (DictionaryCoverageStatus is null)
        {
            return;
        }

        if (DictionaryGrid?.SelectedItem is not DictionaryRow row || row.Coverage == DictionaryRowCoverage.None)
        {
            DictionaryCoverageStatus.Visibility = Visibility.Collapsed;
            DictionaryCoverageStatus.Text = string.Empty;
            return;
        }

        DictionaryCoverageStatus.Text = row.Coverage == DictionaryRowCoverage.Duplicate
            ? $"\"{row.CoverageLibraryName}\" already writes this as \"{row.CoverageLibraryReplacement}\", so this word changes nothing. You can keep it or delete it."
            : $"\"{row.CoverageLibraryName}\" writes this as \"{row.CoverageLibraryReplacement}\". Your spelling wins. Turn yours off to use the word pack's.";
        DictionaryCoverageStatus.Visibility = Visibility.Visible;
    }

    private async void ReviewDictionaryOverlaps_Click(object? sender, RoutedEventArgs e)
    {
        DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        var dictionaryBefore = DictionarySignature();
        var wordPacksBefore = LibrarySignature();
        var covering = CurrentLibraryComposition()?.Coverage()
            ?? DictionaryLibraryOverlapAnalyzer.Coverage(CurrentWordPackLibraries(), CollectEnabledLibraryIds());
        var listed = _rows
            .Where(row => IsRedundantWithWordPack(row, covering))
            .Select(row => new ReviewedOverlap(row, row.Pattern, row.Replacement, row.WholeWord, row.Enabled))
            .ToList();
        if (listed.Count == 0)
        {
            ShowInfo("No words overlap with word packs.");
            return;
        }

        var list = string.Join("\n", listed.Take(8).Select(item => $"• {item.Pattern}"));
        if (listed.Count > 8)
        {
            list += $"\n• and {listed.Count - 8:N0} more";
        }

        if (!await ShowConfirmationAsync(ThemedConfirmation.Create(
                "Remove words a word pack already has?",
                list,
                $"Remove {listed.Count:N0}",
                cancelIsDefault: true,
                cancelText: "Keep them")))
        {
            return;
        }

        if (DictionarySignature() != dictionaryBefore || LibrarySignature() != wordPacksBefore)
        {
            ShowInfo("The word list changed. Review overlaps again.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        using (BatchDictionaryStatus())
        {
            foreach (var item in listed.Where(item => _rows.Contains(item.Row) && item.StillMatches()))
            {
                _rows.Remove(item.Row);
            }

            RefreshDictionaryStatusForRowChange();
        }
    }

    private static bool IsRedundantWithWordPack(DictionaryRow row, IReadOnlyDictionary<string, LibraryCoverage> covering)
    {
        var pattern = (row.Pattern ?? string.Empty).Trim();
        if (!row.Enabled || pattern.Length == 0 || !covering.TryGetValue(pattern, out var hit))
        {
            return false;
        }

        return string.Equals((row.Replacement ?? string.Empty).Trim(), (hit.Entry.Replacement ?? string.Empty).Trim(), StringComparison.Ordinal) &&
            row.WholeWord == hit.Entry.WholeWord;
    }

    private sealed record ReviewedOverlap(DictionaryRow Row, string Pattern, string Replacement, bool WholeWord, bool Enabled)
    {
        public bool StillMatches() =>
            string.Equals(Row.Pattern, Pattern, StringComparison.Ordinal) &&
            string.Equals(Row.Replacement, Replacement, StringComparison.Ordinal) &&
            Row.WholeWord == WholeWord &&
            Row.Enabled == Enabled;
    }

    // --- Dictionary suggestions from history -----------------------------------------------

    private async void DictionarySuggestButton_Click(object sender, RoutedEventArgs e)
    {
        DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        // The grid is the live source of truth for "already covered", so terms added but not yet
        // saved are excluded from new suggestions too.
        var current = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Pattern))
            .Select(r => new DictionaryEntry(r.Id, r.Pattern.Trim(), (r.Replacement ?? string.Empty).Trim()))
            .ToList();

        // Prefer the user's configured AI model: it can work out how a term is spoken versus how it is
        // written (acronyms, phonetic mishears, casing), not just spot repeated words. Fall back to the
        // offline pattern miner when no model is ready, so the button still helps with no AI configured.
        if (_cleanup.Recipient is { } recipient)
        {
            // The recipient is captured before the question and handed to CompleteAsync, which sends only
            // while cleanup still serves it, so the history goes where the dialog said or nowhere. Asked
            // unless both it and the saved provider run on this PC (AiRequestConsent says why both).
            if (AiRequestConsent.IsNeeded(recipient, _savedAiProvider) &&
                !await ConfirmRiskyAsync(
                    CleanupDisclosure.SuggestionConsentTitle,
                    CleanupDisclosure.SuggestionConsentFor(recipient.Provider),
                    "Send and continue"))
            {
                return;
            }

            await RunDictionarySuggestionAsync(() => SuggestWithAiAsync(current, recipient));
        }
        else
        {
            await RunDictionarySuggestionAsync(() => SuggestWithMinerAsync(current));
        }
    }

    // The history read now runs off the UI thread, so the button has to stay off for the whole run
    // or a second click would add every suggestion twice.
    private async Task RunDictionarySuggestionAsync(Func<Task> suggest)
    {
        if (_dictionarySuggestionRunning)
        {
            return;
        }

        _dictionarySuggestionRunning = true;
        var toolTip = DictionarySuggestButton.ToolTip;
        DictionarySuggestButton.ToolTip = null;
        DictionarySuggestButton.IsEnabled = false;
        DictionaryEmptyLearnButton.IsEnabled = false;
        DictionarySuggestBusy.Visibility = Visibility.Visible;
        try
        {
            await suggest();
        }
        finally
        {
            if (!_closed)
            {
                _dictionarySuggestionRunning = false;
                DictionarySuggestBusy.Visibility = Visibility.Collapsed;
                DictionarySuggestButton.IsEnabled = true;
                DictionaryEmptyLearnButton.IsEnabled = true;
                DictionarySuggestButton.ToolTip = toolTip;
            }
        }
    }

    private async Task SuggestWithAiAsync(IReadOnlyList<DictionaryEntry> current, CleanupRecipient recipient)
    {
        List<HistoryEntry> history;
        try
        {
            history = await Task.Run(() => _history.GetRecent(1000).ToList());
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _log.LogWarning("Could not read history for AI suggestions ({Failure}).", FailureShape.Describe(ex));
                ShowThemedMessage("Couldn't read history", UserFacingError.Describe("read your history", UserFacingErrorDestination.Database, ex).Message);
            }

            return;
        }

        // A dialog owned by a closed window throws, and this runs under an async void handler.
        if (_closed)
        {
            return;
        }

        var sample = AiDictionarySuggester.BuildHistorySample(history);
        if (string.IsNullOrWhiteSpace(sample))
        {
            ShowThemedMessage(
                "Nothing to suggest",
                "There are no recent dictations to learn from yet. Keep dictating and try again later.");
            return;
        }

        try
        {
            // The recipient the user agreed to, checked again where the request is built: a Save during the
            // history read above cannot turn this into a request to another provider.
            var completion = await _cleanup.CompleteAsync(AiDictionarySuggester.SystemPrompt, sample, recipient);
            if (_closed)
            {
                return;
            }

            if (completion.Outcome == CompletionOutcome.RecipientChanged)
            {
                ShowThemedMessage(
                    "Nothing was sent",
                    "Where AI cleanup runs changed after you agreed to send your dictations, so Scribe sent " +
                    "nothing. Choose Learn from history again to decide for the AI service in use now.");
                return;
            }

            var response = completion.Text;
            if (string.IsNullOrWhiteSpace(response))
            {
                // The model was unavailable or returned nothing: fall back to the deterministic miner.
                await SuggestWithMinerAsync(current, aiRanFirst: true);
                return;
            }

            var suggestions = AiDictionarySuggester.ParseSuggestions(response, current);
            if (suggestions.Count == 0)
            {
                await SuggestWithMinerAsync(current, aiRanFirst: true);
                return;
            }

            AddSuggestionRows(suggestions.Select(s => (s.Pattern, s.Replacement)));
            ShowInfo(
                $"Added {suggestions.Count} suggested {(suggestions.Count == 1 ? "word" : "words")} " +
                "your AI model inferred from recent dictations. Review them in the grid, delete any you " +
                "don't want, then save.");
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _log.LogWarning("Could not get AI suggestions ({Failure}).", FailureShape.Describe(ex));
                ShowThemedMessage("Couldn't get AI suggestions", UserFacingError.Describe("get AI suggestions", UserFacingErrorDestination.InternetService, ex).Message);
            }
        }
    }

    private async Task SuggestWithMinerAsync(IReadOnlyList<DictionaryEntry> current, bool aiRanFirst = false)
    {
        IReadOnlyList<DictionaryEntry> suggestions;
        try
        {
            suggestions = await Task.Run(() => DictionaryHistoryLearner.BuildEntries(_history.GetRecent(1000), current));
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _log.LogWarning("Could not scan history for dictionary cleanup ({Failure}).", FailureShape.Describe(ex));
                ShowThemedMessage("Couldn't scan history", UserFacingError.Describe("scan your history", UserFacingErrorDestination.Database, ex).Message);
            }

            return;
        }

        if (_closed)
        {
            return;
        }

        if (suggestions.Count == 0)
        {
            ShowThemedMessage(
                "Nothing to suggest",
                aiRanFirst
                    ? "Your AI model and the history scan didn't find any new words to add. Keep " +
                      "dictating and try again later."
                    : "No recurring technical words found in your recent dictations yet.\n\n" +
                      "Suggestions appear once a word shows up in three or more dictations, so keep " +
                      "dictating and try again later.");
            return;
        }

        AddSuggestionRows(suggestions.Select(s => (s.Pattern, s.Replacement)));
        ShowInfo(
            $"Added {suggestions.Count} suggested {(suggestions.Count == 1 ? "word" : "words")} " +
            "from your recent dictations. Review them in the grid, delete any you don't want, then save.");
    }

    private void AddSuggestionRows(IEnumerable<(string Pattern, string Replacement)> entries)
    {
        // Rows are added even when an open edit won't commit (the search then stays, and the new rows may be filtered out
        // until it's cleared): suggestions from a finished run are never dropped.
        ClearDictionarySearchForNewRow();
        DictionaryRow? first = null;
        using (BatchDictionaryStatus())
        {
            foreach (var (pattern, replacement) in entries)
            {
                var row = new DictionaryRow { Pattern = pattern, Replacement = replacement };
                _rows.Add(row);
                first ??= row;
            }
        }

        if (first is not null)
        {
            DictionaryGrid.SelectedItem = first;
            DictionaryGrid.ScrollIntoView(first);
        }
    }

    // --- Dictionary cleanup ---------------------------------------------------------------

    /// <summary>
    /// Scans dictation history for terms that have never earned their place and offers to retire
    /// them. Dead terms are not free: the enabled dictionary is rendered into the AI cleanup prompt
    /// on every dictation, capped at <see cref="CleanupPrompt.MaxGlossaryTermsLocal"/> terms for
    /// on-device models, so entries the user never says displace the ones they do.
    /// </summary>
    private async void DictionaryCleanupButton_Click(object sender, RoutedEventArgs e)
    {
        DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        LibraryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        // The grid is the live truth, as it is for "Learn from history". Judging the saved rows
        // instead would offer to delete an entry the user added thirty seconds ago.
        var current = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Pattern))
            .Select(r => new DictionaryEntry(
                r.Id,
                r.Pattern.Trim(),
                (r.Replacement ?? string.Empty).Trim(),
                r.WholeWord,
                r.Enabled))
            .ToList();

        // Word pack cleanup moves to the Word packs tab. This review offers only the user's own entries.

        // The scan is asynchronous and its findings name specific rows, so anything the user changes
        // while it runs invalidates the result. Applying a stale verdict could turn off a rule they
        // had just edited or re-enabled.
        var dictionaryBefore = DictionarySignature();
        var librariesBefore = LibrarySignature();

        DictionaryCleanupButton.IsEnabled = false;
        DictionaryCleanupBusy.Visibility = Visibility.Visible;
        DictionaryUsageReport report;
        try
        {
            // Off the UI thread: this reads up to a thousand transcripts and runs a regex per term
            // across the lot, which is quick but not instant on a large dictionary.
            var transcripts = await Task.Run(
                () => _history.GetRecent(1000).Select(h => h.Text).ToList());
            report = await Task.Run(
                () => DictionaryUsageAnalyzer.Analyze(transcripts, current, []));
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _log.LogWarning("Could not scan history for dictionary cleanup ({Failure}).", FailureShape.Describe(ex));
                ShowThemedMessage("Couldn't scan history", UserFacingError.Describe("scan your history", UserFacingErrorDestination.Database, ex).Message);
            }

            return;
        }
        finally
        {
            if (!_closed)
            {
                DictionaryCleanupButton.IsEnabled = true;
                DictionaryCleanupBusy.Visibility = Visibility.Collapsed;
            }
        }

        // Showing a dialog owned by a closed window throws, and this handler is async void, so the
        // exception would take the process down rather than surfacing anywhere useful.
        if (_closed)
        {
            return;
        }

        if (DictionarySignature() != dictionaryBefore || LibrarySignature() != librariesBefore)
        {
            ShowInfo(
                "Your dictionary changed while the scan was running, so the results are out of date. "
                + "Run the cleanup again.",
                Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        if (!report.HasEnoughEvidence || !report.HasFindings)
        {
            ShowThemedMessage("Clean up unused words", report.Summary);
            return;
        }

        var choice = DictionaryCleanupWindow.Show(this, report, _perfFlags);
        if (choice is null)
        {
            return;
        }

        await ApplyCleanupAsync(choice);
    }

    private async Task ApplyCleanupAsync(DictionaryCleanupChoice choice)
    {
        // Match back by spoken form rather than id: the grid can hold a row that has never been
        // saved (id 0), and two of those would be indistinguishable by id.
        var patterns = new HashSet<string>(
            choice.Entries.Select(en => en.Pattern.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var targets = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Pattern) && patterns.Contains(r.Pattern.Trim()))
            .ToList();

        // This cleanup no longer switches a word pack off or copies a word pack's terms into the dictionary.
        if (targets.Count == 0 && choice.Libraries.Count == 0)
        {
            return;
        }

        if (choice.Delete && targets.Count > 0 && !await ConfirmRiskyAsync(
                "Delete selected words?",
                $"{targets.Count} {(targets.Count == 1 ? "word" : "words")} will be removed from your "
                + "dictionary when you save. This can't be undone once saved. Turning them off instead "
                + "keeps them in the list so you can switch them back on later.",
                targets.Count == 1 ? "Delete word" : "Delete words"))
        {
            return;
        }

        using (BatchDictionaryStatus())
        {
            if (choice.Delete)
            {
                foreach (var row in targets)
                {
                    _rows.Remove(row);
                }
            }
            else
            {
                foreach (var row in targets)
                {
                    row.Enabled = false;
                }
            }

            RefreshDictionaryStatusForRowChange();
        }

        ShowInfo(choice.Delete
            ? $"{targets.Count} {(targets.Count == 1 ? "word" : "words")} removed. Review the change, then save to apply it."
            : $"{targets.Count} {(targets.Count == 1 ? "word" : "words")} turned off. Review the change, then save to apply it.");
    }

    // --- Dictionary CSV import / export ---------------------------------------------------

    // UTF-8 with BOM so Excel opens accented terms correctly instead of guessing the codepage.
    private static readonly UTF8Encoding CsvEncoding = new(encoderShouldEmitUTF8Identifier: true);

    private void DictionaryTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = "scribe-dictionary-template.csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, DictionaryCsv.Template, CsvEncoding);

            // Open it straight away so the user can start filling it in.
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not save the dictionary template ({Failure}).", FailureShape.Describe(ex));
            ShowThemedMessage("Couldn't save the template", UserFacingError.Describe("save the template", UserFacingErrorDestination.File, ex).Message);
        }
    }

    private void DictionaryExportButton_Click(object sender, RoutedEventArgs e)
    {
        DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        var dialog = new SaveFileDialog
        {
            FileName = "scribe-dictionary.csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var entries = _rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Pattern))
                .Select(r => new DictionaryEntry(
                    r.Id, r.Pattern.Trim(), (r.Replacement ?? string.Empty).Trim(), r.WholeWord, r.Enabled));
            File.WriteAllText(dialog.FileName, DictionaryCsv.Export(entries), CsvEncoding);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not export the dictionary ({Failure}).", FailureShape.Describe(ex));
            ShowThemedMessage("Couldn't export the dictionary", UserFacingError.Describe("export the dictionary", UserFacingErrorDestination.File, ex).Message);
        }
    }

    private void DictionaryImportButton_Click(object sender, RoutedEventArgs e)
    {
        DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        var dialog = new OpenFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        DictionaryCsvResult parsed;
        try
        {
            parsed = DictionaryCsv.Parse(File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not read the dictionary import file ({Failure}).", FailureShape.Describe(ex));
            ShowThemedMessage("Couldn't read that file", UserFacingError.Describe("read that file", UserFacingErrorDestination.File, ex).Message);
            return;
        }

        var (added, updated, unchanged) = MergeImportedEntries(parsed.Entries);
        var summary = DictionaryImportSummaryBuilder.Build(
            added,
            updated,
            unchanged,
            parsed.Errors.Select(error => error.ToString()).ToList());

        ShowInfo(
            summary.Body,
            parsed.Errors.Count > 0
                ? Wpf.Ui.Controls.InfoBarSeverity.Warning
                : Wpf.Ui.Controls.InfoBarSeverity.Success);
    }

    /// <summary>
    /// Merges imported entries into the grid (not the database; the save button owns persistence,
    /// so an import can still be cancelled). Matching is by spoken form, case-insensitive, mirroring
    /// the duplicate rule the save validation enforces.
    /// </summary>
    private (int Added, int Updated, int Unchanged) MergeImportedEntries(IReadOnlyList<DictionaryEntry> imported)
    {
        // The pure merge/counting lives in Core; here we apply its plan to the observable grid rows.
        var existing = _rows
            .Select((r, i) => new DictionaryImportMerger.ExistingRow(
                i, r.Id, r.Pattern, r.Replacement, r.WholeWord, r.Enabled))
            .ToList();

        var plan = DictionaryImportMerger.Merge(existing, imported);

        using var batch = BatchDictionaryStatus();
        foreach (var op in plan.Operations)
        {
            var entry = op.Entry;
            // Replace/append the row object (rather than mutate it) so the grid, which has no property
            // change notifications on DictionaryRow, refreshes the visible values.
            var row = new DictionaryRow
            {
                Id = entry.Id,
                Pattern = entry.Pattern,
                Replacement = entry.Replacement,
                WholeWord = entry.WholeWord,
                Enabled = entry.Enabled,
            };

            if (op.Kind == DictionaryImportMerger.OperationKind.Update)
            {
                _rows[op.Index] = row;
            }
            else
            {
                _rows.Add(row);
            }
        }

        return (plan.Added, plan.Updated, plan.Unchanged);
    }

    private sealed class RedundantDictionaryRowKeyComparer : IEqualityComparer<(string Pattern, string Replacement)>
    {
        public static readonly RedundantDictionaryRowKeyComparer Instance = new();

        public bool Equals((string Pattern, string Replacement) x, (string Pattern, string Replacement) y) =>
            string.Equals(x.Pattern, y.Pattern, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Replacement, y.Replacement, StringComparison.Ordinal);

        public int GetHashCode((string Pattern, string Replacement) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Pattern),
            StringComparer.Ordinal.GetHashCode(obj.Replacement));
    }

    /// <summary>How a dictionary row relates to the enabled libraries.</summary>
    public enum DictionaryRowCoverage
    {
        /// <summary>No enabled library covers this spoken form. The entry is doing its own work.</summary>
        None = 0,

        /// <summary>A library produces exactly this, so the entry is clutter.</summary>
        Duplicate,

        /// <summary>A library writes this spoken form differently; this entry wins.</summary>
        Override,
    }
}
