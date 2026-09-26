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

    private void InitializeDictionaryGrid()
    {
        _dictionaryView = CollectionViewSource.GetDefaultView(_rows);
        _dictionaryView.Filter = DictionaryFilter;
        DictionaryGrid.ItemsSource = _dictionaryView;
        DataGridCheckBoxClick.Attach(DictionaryGrid);
        DataGridTypingTab.Attach(DictionaryGrid);
        _rows.CollectionChanged += DictionaryRows_CollectionChanged;
        DictionaryGrid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(RefreshDictionaryStatus);
        SetDictionaryEditable(false);
        RefreshDictionaryStatus();
    }

    // Until the rows arrive there is nothing to edit, and an edit on an empty grid would be saved as
    // if the user had deleted every entry.
    private void SetDictionaryEditable(bool editable)
    {
        DictionaryGrid.IsEnabled = editable;
        DictionaryAddButton.IsEnabled = editable;
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

        RefreshDictionaryStatus();
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

        Dispatcher.BeginInvoke(RefreshDictionaryStatus);
    }

    /// <summary>Recomputes the glossary hint, empty state and per-row word pack coverage badges together.</summary>
    private void RefreshDictionaryStatus()
    {
        UpdateDictionaryViewState();
        UpdateDictionaryGlossaryHint();
        UpdateDictionaryCoverage();
        UpdateSelectedDictionaryCoverageStatus();
    }

    private bool DictionaryFilter(object item)
    {
        if (item is not DictionaryRow row)
        {
            return false;
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
        try
        {
            if (_rows.Count == 0)
            {
                return;
            }

            // Precedence, not the list's A to Z order: the badge must name the word pack dictation uses.
            var covering = CurrentLibraryComposition()?.Coverage()
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

    /// <summary>
    /// Adds a blank row and puts the cursor in it. The grid's own placeholder row was the only way
    /// to add an entry, which is invisible unless you already know it exists.
    /// </summary>
    private void DictionaryAddButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ClearDictionarySearchForNewRow())
        {
            return;
        }

        var row = new DictionaryRow();
        _rows.Add(row);

        DictionaryGrid.ScrollIntoView(row);

        // The row container is generated lazily, and BeginEdit silently does nothing when it does
        // not exist yet. Forcing layout first is what makes the new row actually land in edit mode
        // rather than appearing blank and unfocused.
        DictionaryGrid.UpdateLayout();

        DictionaryGrid.SelectedItem = row;
        DictionaryGrid.CurrentCell = new DataGridCellInfo(row, DictionaryGrid.Columns.Count > 1 ? DictionaryGrid.Columns[1] : DictionaryGrid.Columns[0]);
        DictionaryGrid.BeginEdit();
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
        var composition = CurrentLibraryComposition();
        if (_wordPackWorkspace is not null && _wordPackCatalog is null)
        {
            DictionaryGlossaryHint.Text = "Word pack vocabulary is unavailable right now.";
            return;
        }

        var localEntries = composition?.LibraryEntries
            ?? DictionaryLibraryComposer.ComposeLibraries(LibraryPrecedence.Enabled(CurrentWordPackLibraries(), CollectEnabledLibraryIds()));
        var aiEntries = composition?.AiLibraryEntries ?? localEntries;

        DictionaryGlossaryHint.Text = GlossaryHint.Describe(new GlossaryHint.Input(
            _rows.Select(r => new DictionaryEntryBuilder.Row(r.Id, r.Pattern, r.Replacement, r.WholeWord, r.Enabled)).ToList(),
            localEntries,
            AiCleanupOn: aiOn,
            PostProcessingOn: PostCheck.IsChecked == true,
            SelectedProvider,
            SelectedPromptStyle,
            aiEntries));
    }

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
                GlossaryBudget.For(SelectedPromptStyle, SelectedProvider));
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

        foreach (var item in listed.Where(item => _rows.Contains(item.Row) && item.StillMatches()))
        {
            _rows.Remove(item.Row);
        }

        RefreshDictionaryStatus();
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
                DictionarySuggestButton.ToolTip =
                    "Learn vocabulary from recent dictations. A configured remote AI provider receives " +
                    "a bounded text sample only after you confirm; otherwise Scribe scans locally.";
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
                ShowThemedMessage("Scribe", $"Could not read your history:\n{ex.Message}");
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
                    "Your AI cleanup provider changed after you agreed to send your dictations, so Scribe sent " +
                    "nothing. Press the button again to decide about the provider now in use.");
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
                ShowThemedMessage("Scribe", $"Could not get AI suggestions:\n{ex.Message}");
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
                ShowThemedMessage("Scribe", $"Could not scan your history:\n{ex.Message}");
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
        foreach (var (pattern, replacement) in entries)
        {
            var row = new DictionaryRow { Pattern = pattern, Replacement = replacement };
            _rows.Add(row);
            first ??= row;
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
                ShowThemedMessage("Scribe", $"Could not scan your history:\n{ex.Message}");
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

        var choice = DictionaryCleanupWindow.Show(this, report);
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
                $"{targets.Count} {(targets.Count == 1 ? "entry" : "entries")} will be removed from your "
                + "dictionary when you save. This cannot be undone once saved. Turning them off instead "
                + "keeps them in the list so you can switch them back on later.",
                "Delete"))
        {
            return;
        }

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

        RefreshDictionaryStatus();
        ShowInfo(choice.Delete
            ? $"{targets.Count} {(targets.Count == 1 ? "entry" : "entries")} removed. Review the change, then save to apply it."
            : $"{targets.Count} {(targets.Count == 1 ? "entry" : "entries")} turned off. Review the change, then save to apply it.");
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
            ShowThemedMessage("Scribe", $"Could not save the template:\n{ex.Message}");
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
            ShowThemedMessage("Scribe", $"Could not export the dictionary:\n{ex.Message}");
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
            ShowThemedMessage("Scribe", $"Could not read that file:\n{ex.Message}");
            return;
        }

        var (added, updated, unchanged) = MergeImportedEntries(parsed.Entries);
        var summary = new StringBuilder();
        summary.Append($"Imported {added} new {(added == 1 ? "word" : "words")}");
        if (updated > 0)
        {
            summary.Append($", updated {updated}");
        }

        if (unchanged > 0)
        {
            summary.Append($", {unchanged} already up to date");
        }

        summary.Append('.');
        if (added + updated > 0)
        {
            summary.Append(" The changes apply when you save.");
        }

        if (parsed.Errors.Count > 0)
        {
            summary.Append("\n\nSome rows couldn't be read:\n")
                   .Append(string.Join('\n', parsed.Errors.Take(8)));
            if (parsed.Errors.Count > 8)
            {
                summary.Append($"\n…and {parsed.Errors.Count - 8} more.");
            }
        }

        ShowInfo(
            summary.ToString(),
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
