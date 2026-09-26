using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Scribe.App.Infrastructure;
using Scribe.Core.Cleanup;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private const int WmDpiChanged = 0x02E0;

    // WPF-UI's close button clears the notice's IsOpen and raises no event, so the notice's actions watch IsOpen.
    private static readonly DependencyPropertyDescriptor WordPackNoticeOpen =
        DependencyPropertyDescriptor.FromProperty(Wpf.Ui.Controls.InfoBar.IsOpenProperty, typeof(Wpf.Ui.Controls.InfoBar));

    private readonly ObservableCollection<LibraryRow> _libraryRows = new();
    private LibraryWorkspace? _wordPackWorkspace;
    private LibraryCatalog? _wordPackCatalog;
    private bool _updatingLibraryRows;
    private bool _updatingLibraryTerms;
    private LibraryOrdering? _libraryOrdering;
    private readonly LibrarySearch _librarySearch = LibrarySearch.ForCurrentCulture();
    private readonly LibraryTermSort _libraryTermSort = LibraryTermSort.ForCurrentCulture();
    private readonly ObservableCollection<LibraryTermRow> _libraryTermRows = new();
    private DispatcherTimer? _librarySearchTimer;
    private LibrarySearchResult? _librarySearchResult;
    private LibraryTermSortOrder _libraryTermSortOrder = LibraryTermSortOrder.SavedOrder;
    private string? _selectedLibraryId;
    private LibraryLayout? _wordPackLayout;
    private bool _wordPackStackedCardOpen;
    private bool _updatingWordPackHeader;
    private bool _updatingWordDetails;
    private long? _wordDetailsRowId;
    private string? _renamingLibraryId;
    private string? _noticeLibraryId;
    private string? _activeLoadNoticeKey;
    private readonly WordPackSaveProtocol _wordPackSaveProtocol;
    private void TryRunWordPackAccelerator(KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (DictionaryTabs.SelectedItem == WordPacksTab &&
            Keyboard.Modifiers == ModifierKeys.Alt &&
            (e.Key == Key.Left || e.SystemKey == Key.Left) &&
            _wordPackLayout?.SideBySide == false)
        {
            e.Handled = true;
            ShowWordPackListPage();
            return;
        }

        var accelerator =
            Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F ? SettingsAccelerator.Find :
            Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N ? SettingsAccelerator.AddTerm :
            Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N ? SettingsAccelerator.NewLibrary :
            (SettingsAccelerator?)null;
        if (accelerator is null)
        {
            return;
        }

        // Find and Add work on both Dictionary tabs, each in its own list; a new word pack only on the Word packs tab.
        var onDictionary = SectionDictionary.Visibility == Visibility.Visible;
        var onWordPacks = onDictionary && DictionaryTabs.SelectedItem == WordPacksTab;
        if (!SettingsCloseGuard.CanRunAccelerator(accelerator.Value, new AcceleratorState(_capturing, _imeComposing, onDictionary)) ||
            (accelerator.Value == SettingsAccelerator.NewLibrary && !onWordPacks))
        {
            return;
        }

        e.Handled = true;
        switch (accelerator.Value)
        {
            case SettingsAccelerator.Find when onWordPacks:
                LibrarySearchBox.Focus();
                LibrarySearchBox.SelectAll();
                break;
            case SettingsAccelerator.Find:
                DictionarySearchBox.Focus();
                DictionarySearchBox.SelectAll();
                break;
            case SettingsAccelerator.AddTerm when onWordPacks:
                _ = AddWordPackTermAsync();
                break;
            case SettingsAccelerator.AddTerm:
                DictionaryAddButton_Click(this, new RoutedEventArgs());
                break;
            case SettingsAccelerator.NewLibrary:
                LibraryNewButton_Click(this, new RoutedEventArgs());
                break;
        }
    }
    // --- Word packs -----------------------------------------------------------------------

    private void InitializeLibraryGrid()
    {
        WordPackNoticeOpen.AddValueChanged(WordPackNoticeBar, WordPackNoticeBar_IsOpenChanged);
        Closed += (_, _) => WordPackNoticeOpen.RemoveValueChanged(WordPackNoticeBar, WordPackNoticeBar_IsOpenChanged);
        LibraryGrid.ItemsSource = _libraryRows;
        DataGridCheckBoxClick.Attach(LibraryGrid);
        DataGridTypingTab.Attach(LibraryTermsGrid);

        // The Word packs tab stages switches in the workspace. Save commits the workspace's change set with the
        // settings document, then marks exactly that change set saved after the library journal publishes it.
        _libraryRows.CollectionChanged += LibraryRows_CollectionChanged;

        _libraryDetailEmptyText = LibraryDetailEmpty.Text;
        LibraryDetailEmpty.Text = "Loading word packs...";
        LibraryTermsGrid.ItemsSource = _libraryTermRows;
        DataGridCheckBoxClick.Attach(LibraryTermsGrid);
        SetLibrariesEditable(false);
        UpdateLibraryDetail(null);
    }

    private void LibraryRows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in e.OldItems?.OfType<LibraryRow>() ?? [])
        {
            row.PropertyChanged -= LibraryRow_PropertyChanged;
        }

        foreach (var row in e.NewItems?.OfType<LibraryRow>() ?? [])
        {
            row.PropertyChanged += LibraryRow_PropertyChanged;
        }
    }

    private void LibraryRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updatingLibraryRows || sender is not LibraryRow row)
        {
            return;
        }

        if (e.PropertyName == nameof(LibraryRow.Enabled))
        {
            _wordPackWorkspace?.SetEnabled(row.Id, row.Enabled);
            Dispatcher.BeginInvoke(RefreshDictionaryStatus);
        }
        else if (e.PropertyName == nameof(LibraryRow.AiCleanup))
        {
            _wordPackWorkspace?.SetAiPermission(row.Id, row.AiCleanup);
            Dispatcher.BeginInvoke(RefreshDictionaryStatus);
        }

        if (string.Equals(row.Id, _selectedLibraryId, StringComparison.OrdinalIgnoreCase))
        {
            SyncWordPackHeader(row.Id);
        }
    }

    private void LibraryUseCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingWordPackHeader || _selectedLibraryId is null || _wordPackWorkspace is null)
        {
            return;
        }

        var enabled = LibraryUseCheck.IsChecked == true;
        _wordPackWorkspace.SetEnabled(_selectedLibraryId, enabled);
        if (LibraryGrid.SelectedItem is LibraryRow row)
        {
            _updatingLibraryRows = true;
            row.Enabled = enabled;
            _updatingLibraryRows = false;
        }

        RefreshWordPackList(_selectedLibraryId);
        RefreshDictionaryStatus();
        UpdateLibraryDetail(LibraryGrid.SelectedItem as LibraryRow);
    }

    private void LibraryAiCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingWordPackHeader || _selectedLibraryId is null || _wordPackWorkspace is null)
        {
            return;
        }

        var permitted = LibraryAiCheck.IsChecked == true;
        var result = _wordPackWorkspace.SetAiPermission(_selectedLibraryId, permitted);
        if (!result.Applied && result.Issue is { } issue)
        {
            ShowInfo(LibraryEditor.Message(issue), Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        LibraryAiHelpText.Text = WordPackUiText.AiHelp(permitted, _savedAiProvider == CleanupProvider.FoundryLocal);
        RefreshWordPackList(_selectedLibraryId);
    }

    // The enabled set is saved from these rows, so an unloaded list must not be editable: saving it
    // would switch every library off.
    private void SetLibrariesEditable(bool editable)
    {
        LibraryGrid.IsEnabled = editable;
        LibraryImportButton.IsEnabled = editable;
        LibraryExportButton.IsEnabled = editable;
        LibraryNewButton.IsEnabled = editable;
        LibraryMoreButton.IsEnabled = editable;
        LibraryDetailMoreButton.IsEnabled = editable;
        LibraryAddWordButton.IsEnabled = editable;
        LibrarySortButton.IsEnabled = editable;
    }

    private async void LoadLibrariesAsync()
    {
        if (!_libraryLoad.TryBegin(LibrarySignature(), out var ticket))
        {
            return;
        }

        LibraryCatalog catalog;
        try
        {
            catalog = await Task.Run(_libraryStore.LoadCatalog);
        }
        catch (Exception ex)
        {
            if (_libraryLoad.Fail(ticket))
            {
                TryLog(ex, "Could not load dictionary libraries for Settings.");
                LibraryDetailEmpty.Text =
                    "Couldn't load word packs.";
            }

            return;
        }

        if (!_libraryLoad.CanPublish(ticket))
        {
            return;
        }

        _wordPackCatalog = catalog;
        _wordPackWorkspace = new LibraryWorkspace(catalog, BuiltInLibraryOverlay.Instance, LibraryDecisions.DefaultAiPermission);

        // Clear raises a reset that names no removed rows, so their handlers are dropped here.
        foreach (var stale in _libraryRows)
        {
            stale.PropertyChanged -= LibraryRow_PropertyChanged;
        }

        // One A to Z list of built-in and custom libraries, each shown as the committed selection has it. The ordering is
        // captured now, so a library imported later is placed by the same rules as the rows already shown.
        _libraryOrdering = LibraryOrdering.ForCurrentCulture();
        _updatingLibraryRows = true;
        _libraryRows.Clear();
        foreach (var library in _libraryOrdering.Sort(catalog.Libraries.Select(l => l.Content), l => l.Name, l => l.Id))
        {
            _libraryRows.Add(NewLibraryRow(library));
        }
        _updatingLibraryRows = false;

        _libraryLoad.Publish(ticket, LibrarySignature());
        LibraryDetailEmpty.Text = _libraryDetailEmptyText;
        SetLibrariesEditable(true);

        // Preview the first library so the detail panel is never blank when the page opens.
        if (_libraryRows.Count > 0)
        {
            LibraryGrid.SelectedIndex = 0;
        }
        else
        {
            UpdateLibraryDetail(null);
        }

        // Coverage badges and the glossary count depend on which libraries are on.
        RefreshDictionaryStatus();
        ShowCurrentWordPackLoadNotice();
    }

    private void LibraryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingLibraryRows)
        {
            return;
        }

        if (!FinishPendingLibraryRename())
        {
            return;
        }

        UpdateLibraryDetail(LibraryGrid.SelectedItem as LibraryRow);
        if (_wordPackLayout?.SideBySide == false && _wordPackStackedCardOpen)
        {
            ShowWordPackCardPage();
        }
    }

    private void LibrarySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _librarySearchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _librarySearchTimer.Stop();
        _librarySearchTimer.Tick -= LibrarySearchTimer_Tick;
        _librarySearchTimer.Tick += LibrarySearchTimer_Tick;
        _librarySearchTimer.Start();
    }

    private void LibrarySearchTimer_Tick(object? sender, EventArgs e)
    {
        _librarySearchTimer?.Stop();
        ApplyLibrarySearch();
    }

    private void ApplyLibrarySearch()
    {
        if (_wordPackWorkspace is null)
        {
            return;
        }

        var sources = _wordPackWorkspace.Draft.Libraries
            .Where(library => !library.PendingDelete)
            .Select(library => new LibrarySearchSource(library.Content.Id, _wordPackWorkspace.RowsOf(library.Content.Id)))
            .ToList();
        _librarySearchResult = _librarySearch.Search(sources, LibrarySearchBox.Text);
        foreach (var row in _libraryRows)
        {
            ApplySearchState(row);
        }

        if (_selectedLibraryId is not null)
        {
            RefreshTermRows(_selectedLibraryId);
        }

        if (_librarySearchResult.IsActive)
        {
            var packs = _librarySearchResult.Libraries.Count(library => library.Count > 0);
            AnnounceFrom(LibrarySearchBox, $"{_librarySearchResult.TotalMatches:N0} matches in {packs:N0} word packs");
        }
    }

    private void ApplySearchState(LibraryRow row)
    {
        var active = _librarySearchResult?.IsActive == true;
        var count = active ? _librarySearchResult!.CountIn(row.Id) : 0;
        row.MatchCount = active ? count : null;
        row.TextBrush = active && count == 0
            ? TryFindResource("TextFillColorTertiaryBrush") as Brush
            : TryFindResource("TextFillColorPrimaryBrush") as Brush;
    }

    private void LibraryNewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wordPackWorkspace is null)
        {
            return;
        }

        var id = _wordPackWorkspace.CreateLibrary();
        RefreshWordPackList(id);
        RefreshDictionaryStatus();
        if (_wordPackLayout?.SideBySide == false)
        {
            ShowWordPackCardPage();
        }
    }

    private async void LibraryAddWordButton_Click(object sender, RoutedEventArgs e) => await AddWordPackTermAsync();

    private void LibraryEmptyActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLibraryId is null)
        {
            LibraryNewButton_Click(sender, e);
            return;
        }

        _ = AddWordPackTermAsync(_librarySearchResult?.IsActive == true ? _librarySearchResult.Query : null);
    }

    private void WordPacksBackButton_Click(object sender, RoutedEventArgs e) => ShowWordPackListPage();

    private void LibraryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_wordPackLayout?.SideBySide == false && LibraryGrid.SelectedItem is LibraryRow)
        {
            ShowWordPackCardPage();
        }
    }

    private void LibraryGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _wordPackLayout?.SideBySide == false && LibraryGrid.SelectedItem is LibraryRow)
        {
            e.Handled = true;
            ShowWordPackCardPage();
        }
    }

    private void LibraryGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _wordPackLayout?.SideBySide == false && LibraryGrid.SelectedItem is LibraryRow)
        {
            e.Handled = true;
            ShowWordPackCardPage();
        }
    }

    private void LibraryMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wordPackWorkspace is null)
        {
            return;
        }

        var menu = new ContextMenu();
        var deleted = new MenuItem { Header = $"Recently deleted ({_wordPackWorkspace.Draft.RecentlyDeleted.Count})" };
        deleted.Click += (_, _) => ShowRecentlyDeletedDialog();
        menu.Items.Add(deleted);

        menu.PlacementTarget = LibraryMoreButton;
        menu.IsOpen = true;
    }

    private void WordPacksIntroInfoButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = WordPacksIntroInfoButton };
        menu.Items.Add(new TextBlock
        {
            Text = "Ready-made lists of words, like product names. Turn on the ones you use. Your own words always win.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
            Margin = new Thickness(12, 8, 12, 8),
        });
        menu.IsOpen = true;
    }

    private void LibraryDetailMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wordPackWorkspace is null || LibraryGrid.SelectedItem is not LibraryRow row)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = LibraryDetailMoreButton };
        AddMenuItem(menu, "Duplicate", () =>
        {
            var id = _wordPackWorkspace.Duplicate(row.Id);
            RefreshWordPackList(id);
        });
        if (!row.BuiltIn)
        {
            AddMenuItem(menu, "Rename", () => BeginLibraryRename(row.Id));
        }

        if (row.BuiltIn && row.Unsaved)
        {
            AddMenuItem(menu, "Restore all built-in versions...", async () => await RestoreAllBuiltInValuesAsync(row));
        }

        if (row.Unsaved)
        {
            AddMenuItem(menu, "Discard unsaved changes to this word pack", () =>
            {
                _wordPackWorkspace.DiscardLibrary(row.Id);
                RefreshWordPackList(row.Id);
                UpdateLibraryDetail(LibraryGrid.SelectedItem as LibraryRow);
            });
        }

        if (!row.BuiltIn)
        {
            AddMenuItem(menu, "Delete word pack...", () => LibraryRemoveButton_Click(sender, e));
        }

        menu.IsOpen = true;
    }

    private static void AddMenuItem(ContextMenu menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void LibraryTermDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLibraryId is null || _wordPackWorkspace is null ||
            sender is not FrameworkElement { DataContext: LibraryTermRow row })
        {
            return;
        }

        if (!_wordPackWorkspace.CanEditContent(_selectedLibraryId))
        {
            ShowInfo("This word pack can't be edited right now.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        try
        {
            _wordPackWorkspace.DeleteTerm(_selectedLibraryId, row.RowId);
            RefreshTermRows(_selectedLibraryId);
            RefreshWordPackList(_selectedLibraryId);
            RefreshDictionaryStatus();
        }
        catch (InvalidOperationException)
        {
            ShowInfo("This word can be turned off or restored, but not deleted.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
        }
    }

    private void LibraryTermMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLibraryId is null || _wordPackWorkspace is null ||
            sender is not FrameworkElement { DataContext: LibraryTermRow row })
        {
            return;
        }

        OpenTermMenu(row, (UIElement)sender);
    }

    private void OpenTermMenu(LibraryTermRow row, UIElement target)
    {
        if (_selectedLibraryId is null || _wordPackWorkspace is null)
        {
            return;
        }

        var draft = _wordPackWorkspace.RowsOf(_selectedLibraryId).FirstOrDefault(item => item.RowId == row.RowId);
        if (draft is null)
        {
            return;
        }

        var commands = LibraryEditor.AvailableCommands(draft.Row, editingText: false);
        var menu = new ContextMenu { PlacementTarget = target };
        AddMenuItem(menu, "Word details", () => OpenWordDetails(row));
        AddTermCommand(menu, commands, row, TermCommands.TurnOff, "Turn off", () => SetWordPackTermEnabled(row, false));
        AddTermCommand(menu, commands, row, TermCommands.TurnOn, "Turn on", () => SetWordPackTermEnabled(row, true));
        AddTermCommand(menu, commands, row, TermCommands.Delete, "Delete word", () => DeleteWordPackTerm(row));
        AddTermCommand(menu, commands, row, TermCommands.RestoreBuiltIn, "Restore the built-in version", () => RestoreBuiltInTerm(row));
        AddTermCommand(menu, commands, row, TermCommands.ShowOtherSources, "Show other word packs with this word", () => ShowOtherWordPacks(row));
        AddTermCommand(menu, commands, row, TermCommands.Copy, "Copy", () => ScribeClipboard.SetText($"{row.Pattern},{row.Replacement}"));
        AddTermCommand(menu, commands, row, TermCommands.CopyToDictionary, "Copy to my dictionary", () => CopyWordPackTermToDictionary(row));
        menu.IsOpen = true;
    }

    private static void AddTermCommand(ContextMenu menu, TermCommands commands, LibraryTermRow row, TermCommands command, string header, Action action)
    {
        if (!commands.HasFlag(command))
        {
            return;
        }

        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void SetWordPackTermEnabled(LibraryTermRow row, bool enabled)
    {
        if (_selectedLibraryId is null || _wordPackWorkspace is null)
        {
            return;
        }

        _wordPackWorkspace.SetTermEnabled(_selectedLibraryId, row.RowId, enabled);
        row.SetValues(row.Pattern, row.Replacement, row.WholeWord, enabled);
        RefreshTermRows(_selectedLibraryId);
        RefreshWordPackList(_selectedLibraryId);
        RefreshDictionaryStatus();
    }

    private void DeleteWordPackTerm(LibraryTermRow row)
    {
        if (_selectedLibraryId is null || _wordPackWorkspace is null)
        {
            return;
        }

        _wordPackWorkspace.DeleteTerm(_selectedLibraryId, row.RowId);
        RefreshTermRows(_selectedLibraryId);
        RefreshWordPackList(_selectedLibraryId);
        RefreshDictionaryStatus();
        AnnounceFrom(LibraryTermsGrid, $"Deleted \"{row.Pattern}\".");
    }

    private void RestoreBuiltInTerm(LibraryTermRow row)
    {
        if (_selectedLibraryId is null || _wordPackWorkspace is null)
        {
            return;
        }

        _wordPackWorkspace.RestoreBuiltInValues(_selectedLibraryId, row.RowId);
        RefreshTermRows(_selectedLibraryId);
        RefreshWordPackList(_selectedLibraryId);
    }

    private void CopyWordPackTermToDictionary(LibraryTermRow row)
    {
        _rows.Add(new DictionaryRow
        {
            Pattern = row.Pattern,
            Replacement = row.Replacement,
            WholeWord = row.WholeWord,
            Enabled = true,
        });
        ShowInfo("Copied to your dictionary. Save to apply it.");
    }

    private void ShowOtherWordPacks(LibraryTermRow row)
    {
        if (_wordPackWorkspace is null || row.Status is null)
        {
            return;
        }

        var ids = row.Status.SameResultIn.Concat(row.Status.DifferentResultIn).ToList();
        if (row.Status.WinningLibraryId is { } winner)
        {
            ids.Insert(0, winner);
        }

        var menu = new ContextMenu { PlacementTarget = LibraryTermsGrid };
        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_wordPackWorkspace.Draft.Find(id) is not { } pack)
            {
                continue;
            }

            AddMenuItem(menu, pack.Content.Name, () =>
            {
                LibraryGrid.SelectedItem = _libraryRows.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                if (_wordPackLayout?.SideBySide == false)
                {
                    ShowWordPackCardPage();
                }
            });
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No other word packs", IsEnabled = false });
        }

        menu.IsOpen = true;
    }

    private void OpenWordDetails(LibraryTermRow row)
    {
        _wordDetailsRowId = row.RowId;
        WordDetailsPanel.Visibility = Visibility.Visible;
        RefreshWordDetails();
        ApplyWordPackLayout();
    }

    private void RefreshWordDetails()
    {
        if (_wordDetailsRowId is null || _selectedLibraryId is null || _wordPackWorkspace is null)
        {
            WordDetailsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var row = _libraryTermRows.FirstOrDefault(item => item.RowId == _wordDetailsRowId.Value);
        if (row is null)
        {
            WordDetailsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _updatingWordDetails = true;
        WordDetailsWordText.Text = string.IsNullOrWhiteSpace(row.Pattern) ? "New word" : row.Pattern;
        WordDetailsSpokenBox.Text = row.Pattern;
        WordDetailsWrittenBox.Text = row.Replacement;
        WordDetailsWholeWordCheck.IsChecked = row.WholeWord;
        WordDetailsSourceText.Text = SourceLineFor(row);
        WordDetailsStatusText.Text = row.Status is null ? string.Empty : DescribeTermStatus(row.Status);
        WordDetailsGlossaryText.Text = row.Status is null ? string.Empty : WordPackUiText.GlossaryLine(row.Status.Glossary);
        WordDetailsHintsText.Text = HintLine(row.Hints);
        WordDetailsDictionaryButton.Visibility = FindDictionaryRow(row.Pattern) is null ? Visibility.Collapsed : Visibility.Visible;
        _updatingWordDetails = false;
    }

    private static string SourceLineFor(LibraryTermRow row) =>
        row.Status?.Marker == TermMarker.Changed ? "Changed from the built-in version." : "Comes from this word pack.";

    private static string HintLine(TermHints hints)
    {
        if (hints == TermHints.None)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (hints.HasFlag(TermHints.OrdinaryWord))
        {
            parts.Add("This is a common word.");
        }

        if (hints.HasFlag(TermHints.WholeWordOff))
        {
            parts.Add("Whole words only is off.");
        }

        if (hints.HasFlag(TermHints.ForcesLowercase))
        {
            parts.Add("This may force lowercase.");
        }

        if (hints.HasFlag(TermHints.LongForGlossary))
        {
            parts.Add("Not included in AI vocabulary because it is over 100 characters.");
        }

        if (hints.HasFlag(TermHints.MultiLine))
        {
            parts.Add("Multi-line words are better as snippets.");
        }

        if (hints.HasFlag(TermHints.IrregularSpacing))
        {
            parts.Add("Spacing will be normalized when you save.");
        }

        return string.Join(" ", parts);
    }

    private void WordDetailsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingWordDetails || _wordDetailsRowId is null || _selectedLibraryId is null || _wordPackWorkspace is null)
        {
            return;
        }

        var row = _libraryTermRows.FirstOrDefault(item => item.RowId == _wordDetailsRowId.Value);
        if (row is null)
        {
            return;
        }

        ApplyWordDetailsEdit(row, WordDetailsSpokenBox.Text, WordDetailsWrittenBox.Text, row.WholeWord);
    }

    private void WordDetailsWholeWordCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingWordDetails || _wordDetailsRowId is null)
        {
            return;
        }

        if (_libraryTermRows.FirstOrDefault(item => item.RowId == _wordDetailsRowId.Value) is { } row)
        {
            ApplyWordDetailsEdit(row, row.Pattern, row.Replacement, WordDetailsWholeWordCheck.IsChecked == true);
        }
    }

    private void ApplyWordDetailsEdit(LibraryTermRow row, string spoken, string written, bool wholeWord)
    {
        if (_selectedLibraryId is null || _wordPackWorkspace is null)
        {
            return;
        }

        var result = _wordPackWorkspace.EditTerm(
            _selectedLibraryId,
            row.RowId,
            new TermValues(spoken, written, wholeWord, row.Enabled),
            removalIntent: false);
        if (!result.Applied && result.Issue is { } issue)
        {
            var message = LibraryEditor.Message(issue, spoken);
            ShowInfo(message, Wpf.Ui.Controls.InfoBarSeverity.Warning);
            AnnounceFrom(WordDetailsPanel, message);
            return;
        }

        _updatingLibraryTerms = true;
        row.SetValues(spoken, written, wholeWord, row.Enabled);
        _updatingLibraryTerms = false;
        UpdateSelectedLibraryDirtyState();
        RefreshDictionaryStatus();
        WordDetailsDictionaryButton.Visibility = FindDictionaryRow(spoken) is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void WordDetailsBackButton_Click(object sender, RoutedEventArgs e)
    {
        WordDetailsPanel.Visibility = Visibility.Collapsed;
        _wordDetailsRowId = null;
        ApplyWordPackLayout();
        LibraryTermsGrid.Focus();
    }

    private void WordDetailsDictionaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wordDetailsRowId is null)
        {
            return;
        }

        var row = _libraryTermRows.FirstOrDefault(item => item.RowId == _wordDetailsRowId.Value);
        if (row is null || FindDictionaryRow(row.Pattern) is not { } dictionaryRow)
        {
            return;
        }

        DictionaryTabs.SelectedItem = YourWordsTab;
        DictionaryGrid.SelectedItem = dictionaryRow;
        DictionaryGrid.ScrollIntoView(dictionaryRow);
        DictionaryGrid.Focus();
    }

    private DictionaryRow? FindDictionaryRow(string spoken)
    {
        if (!_dictionaryLoad.IsLoaded || string.IsNullOrWhiteSpace(spoken))
        {
            return null;
        }

        return _rows.FirstOrDefault(row =>
            row.Enabled &&
            LibraryTermKey.AreSame(row.Pattern, spoken));
    }

    private async Task AddWordPackTermAsync(string? spoken = null)
    {
        if (_selectedLibraryId is null || _wordPackWorkspace is null)
        {
            return;
        }

        if (!_wordPackWorkspace.CanEditContent(_selectedLibraryId))
        {
            ShowInfo("This word pack can't be edited right now.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(spoken) &&
            _wordPackWorkspace.Draft.Find(_selectedLibraryId)?.Content.BuiltIn == true)
        {
            spoken = await AskForWordPackSpokenFormAsync();
            if (string.IsNullOrWhiteSpace(spoken))
            {
                return;
            }
        }

        var beforeRows = _wordPackWorkspace.RowsOf(_selectedLibraryId).Select(row => row.RowId).ToHashSet();
        var result = _wordPackWorkspace.AddTerm(_selectedLibraryId, new TermValues(spoken ?? string.Empty, string.Empty), removalIntent: false);
        if (!result.Applied)
        {
            ShowInfo(result.Issue is null ? "Couldn't add a word." : LibraryEditor.Message(result.Issue), Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        if (_librarySearchResult?.IsActive == true)
        {
            ApplyLibrarySearch();
        }

        RefreshTermRows(_selectedLibraryId);
        UpdateSelectedLibraryDirtyState();
        var addedId = _wordPackWorkspace.RowsOf(_selectedLibraryId).FirstOrDefault(row => !beforeRows.Contains(row.RowId))?.RowId;
        var row = addedId is null
            ? _libraryTermRows.LastOrDefault()
            : _libraryTermRows.FirstOrDefault(candidate => candidate.RowId == addedId.Value);
        if (row is not null)
        {
            LibraryTermsGrid.SelectedItem = row;
            LibraryTermsGrid.ScrollIntoView(row);
            if (!string.IsNullOrEmpty(spoken))
            {
                OpenWordDetails(row);
                WordDetailsWrittenBox.Focus();
            }
        }
    }

    private Task<string?> AskForWordPackSpokenFormAsync()
    {
        var box = new TextBox { MinWidth = 280, Margin = new Thickness(0, 8, 0, 0) };
        var dialog = new Wpf.Ui.Controls.FluentWindow
        {
            Title = "Add word",
            Owner = this,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var add = new Button { Content = "Next", IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        add.Click += (_, _) => dialog.DialogResult = true;
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = "Scribe hears", Style = (Style)FindResource("CardTitle") },
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { cancel, add },
                },
            },
        };

        box.Focus();
        return Task.FromResult(dialog.ShowDialog() == true ? box.Text : null);
    }

    private void ShowRecentlyDeletedDialog()
    {
        if (_wordPackWorkspace is null)
        {
            return;
        }

        var list = new ListBox
        {
            MinWidth = 420,
            MinHeight = 180,
            ItemsSource = _wordPackWorkspace.Draft.RecentlyDeleted.Select(entry =>
                new RecentlyDeletedRow(entry)).ToList(),
            DisplayMemberPath = nameof(RecentlyDeletedRow.Display),
        };
        var restore = new Button { Content = "Restore word pack", MinWidth = 140, Margin = new Thickness(0, 0, 8, 0) };
        var purge = new Button { Content = "Delete permanently", MinWidth = 140, Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 90 };
        var dialog = new Wpf.Ui.Controls.FluentWindow
        {
            Title = $"Recently deleted ({_wordPackWorkspace.Draft.RecentlyDeleted.Count})",
            Owner = this,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        restore.Click += async (_, _) =>
        {
            if (list.SelectedItem is RecentlyDeletedRow row)
            {
                var content = await Task.Run(() => _libraryStore.ReadRecentlyDeleted(row.Entry));
                if (content is not null)
                {
                    var id = _wordPackWorkspace.RestoreDeleted(content);
                    RefreshWordPackList(id);
                    dialog.DialogResult = true;
                }
            }
        };
        purge.Click += async (_, _) =>
        {
            if (list.SelectedItem is RecentlyDeletedRow row &&
                await ConfirmRiskyAsync(
                    "Delete word pack permanently",
                    $"Delete {row.Entry.Name} permanently? This can't be undone.",
                    "Delete permanently"))
            {
                _wordPackWorkspace.DeletePermanently(row.Entry);
                dialog.DialogResult = true;
            }
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                list,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { restore, purge, close },
                },
            },
        };
        dialog.ShowDialog();
        RefreshWordPackList();
        RefreshDictionaryStatus();
    }

    private void BeginLibraryRename(string libraryId)
    {
        var library = _wordPackWorkspace?.Draft.Find(libraryId);
        if (library is null)
        {
            return;
        }

        _renamingLibraryId = libraryId;
        LibraryDetailRenameBox.Text = library.Content.Name;
        LibraryDetailName.Visibility = Visibility.Collapsed;
        LibraryDetailRenameBox.Visibility = Visibility.Visible;
        LibraryRenameValidation.Visibility = Visibility.Collapsed;
        LibraryDetailRenameBox.Focus();
        LibraryDetailRenameBox.SelectAll();
    }

    private bool CommitLibraryRename()
    {
        if (_renamingLibraryId is null || _wordPackWorkspace is null)
        {
            return true;
        }

        var result = _wordPackWorkspace.Rename(_renamingLibraryId, LibraryDetailRenameBox.Text);
        if (!result.Applied && result.Issue is { } issue)
        {
            LibraryRenameValidation.Text = LibraryEditor.Message(issue);
            LibraryRenameValidation.Visibility = Visibility.Visible;
            AnnounceFrom(LibraryDetailRenameBox, LibraryRenameValidation.Text);
            Dispatcher.BeginInvoke(() =>
            {
                LibraryDetailRenameBox.Focus();
                LibraryDetailRenameBox.SelectAll();
            });
            return false;
        }

        var renamed = _renamingLibraryId;
        _renamingLibraryId = null;
        LibraryDetailRenameBox.Visibility = Visibility.Collapsed;
        LibraryDetailName.Visibility = Visibility.Visible;
        RefreshWordPackList(renamed);
        RefreshDictionaryStatus();
        return true;
    }

    private void CancelLibraryRename()
    {
        _renamingLibraryId = null;
        LibraryDetailRenameBox.Visibility = Visibility.Collapsed;
        LibraryRenameValidation.Visibility = Visibility.Collapsed;
        LibraryDetailName.Visibility = Visibility.Visible;
    }

    private void LibraryDetailRenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitLibraryRename();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelLibraryRename();
        }
    }

    private void LibraryDetailRenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => FinishPendingLibraryRename();

    private bool FinishPendingLibraryRename()
    {
        if (_renamingLibraryId is null)
        {
            return true;
        }

        if (CommitLibraryRename())
        {
            return true;
        }

        _updatingLibraryRows = true;
        LibraryGrid.SelectedItem = _libraryRows.FirstOrDefault(row => string.Equals(row.Id, _renamingLibraryId, StringComparison.OrdinalIgnoreCase));
        _updatingLibraryRows = false;
        return false;
    }

    private async Task RestoreAllBuiltInValuesAsync(LibraryRow row)
    {
        if (_wordPackWorkspace is null ||
            !await ConfirmRiskyAsync(
                "Restore built-in versions",
                $"Restore every word in {row.Name} to the version Scribe ships? Your changes to its words are removed when you save.",
                "Restore all"))
        {
            return;
        }

        _wordPackWorkspace.RestoreAllBuiltInValues(row.Id);
        RefreshWordPackList(row.Id);
        UpdateLibraryDetail(LibraryGrid.SelectedItem as LibraryRow);
        RefreshDictionaryStatus();
    }

    // Drives the right-hand preview panel from the selected library row: header plus a read-only grid
    // of its spoken-to-written words. Resolving from the cached snapshot keeps clicking through
    // libraries instant (no per-click file reads).
    private void UpdateLibraryDetail(LibraryRow? row)
    {
        var library = row is null || _wordPackWorkspace is null
            ? null
            : _wordPackWorkspace.Draft.Find(row.Id);

        if (library is null)
        {
            _selectedLibraryId = null;
            _libraryTermRows.Clear();
            LibraryTermsGrid.Visibility = Visibility.Collapsed;
            LibraryDetailEmpty.Visibility = Visibility.Visible;
            LibraryDetailName.Text = string.Empty;
            LibraryDetailMeta.Text = string.Empty;
            LibraryDetailDesc.Text = string.Empty;
            LibraryDetailDesc.Visibility = Visibility.Collapsed;
            LibraryExportButton.IsEnabled = false;
            LibraryDetailMoreButton.IsEnabled = false;
            LibraryUseCheck.IsEnabled = false;
            LibraryAiCheck.IsEnabled = false;
            LibraryOffLine.Visibility = Visibility.Collapsed;
            WordDetailsPanel.Visibility = Visibility.Collapsed;
            UpdateLibraryTermCount();
            return;
        }

        _selectedLibraryId = library.Content.Id;
        var content = library.Content;
        var count = content.Rows.Count;
        LibraryDetailName.Text = content.Name;
        var unsaved = _wordPackWorkspace!.UnsavedLibraryIds.Contains(content.Id);
        LibraryDetailMeta.Text = WordPackUiText.HeaderMeta(content.BuiltIn, content.Category, count, unsaved);
        var description = content.Description ?? string.Empty;
        LibraryDetailDesc.Text = FirstDescriptionLine(description);
        LibraryDetailDescFull.Text = description;
        LibraryDetailDesc.Visibility =
            string.IsNullOrWhiteSpace(description) ? Visibility.Collapsed : Visibility.Visible;
        LibraryDetailsButton.Visibility = DescriptionHasMore(description) ? Visibility.Visible : Visibility.Collapsed;
        LibraryDetailDescFull.Visibility = Visibility.Collapsed;

        _updatingWordPackHeader = true;
        LibraryUseCheck.IsEnabled = true;
        LibraryAiCheck.IsEnabled = true;
        LibraryUseCheck.IsChecked = _wordPackWorkspace.Draft.LocalState.EnabledIds.Contains(content.Id);
        LibraryUseCheck.SetValue(AutomationProperties.NameProperty, $"Use the {content.Name} word pack");
        LibraryAiCheck.IsChecked = _wordPackWorkspace.ShowsAiPermission(content.Id);
        var shownAiCleanup = AiCleanupCheck.IsChecked == true;
        LibraryAiCheck.Visibility = shownAiCleanup ? Visibility.Visible : Visibility.Collapsed;
        LibraryAiCheck.SetValue(AutomationProperties.NameProperty, $"Use {content.Name} in AI cleanup");
        LibraryAiHelpText.Text = WordPackUiText.AiHelp(LibraryAiCheck.IsChecked == true, _savedAiProvider == CleanupProvider.FoundryLocal);
        LibraryAiHelpText.Visibility = shownAiCleanup ? Visibility.Visible : Visibility.Collapsed;
        _updatingWordPackHeader = false;

        RefreshTermRows(library.Content.Id);
        LibraryTermsGrid.Visibility = Visibility.Visible;
        LibraryDetailEmptyPanel.Visibility = _libraryTermRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryExportButton.IsEnabled = true;
        LibraryDetailMoreButton.IsEnabled = true;
        LibraryOffLine.Visibility = LibraryUseCheck.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        ApplyWordPackLayout();
    }

    private void SyncWordPackHeader(string libraryId)
    {
        if (_wordPackWorkspace is null || !string.Equals(libraryId, _selectedLibraryId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _updatingWordPackHeader = true;
        LibraryUseCheck.IsChecked = _wordPackWorkspace.Draft.LocalState.EnabledIds.Contains(libraryId);
        LibraryAiCheck.IsChecked = _wordPackWorkspace.ShowsAiPermission(libraryId);
        LibraryAiHelpText.Text = WordPackUiText.AiHelp(LibraryAiCheck.IsChecked == true, _savedAiProvider == CleanupProvider.FoundryLocal);
        _updatingWordPackHeader = false;
        LibraryOffLine.Visibility = LibraryUseCheck.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshWordPackAiPermissionState()
    {
        if (LibraryAiCheck is null)
        {
            return;
        }

        var shownAiCleanup = AiCleanupCheck?.IsChecked == true;
        LibraryAiCheck.Visibility = shownAiCleanup ? Visibility.Visible : Visibility.Collapsed;
        LibraryAiHelpText.Visibility = shownAiCleanup ? Visibility.Visible : Visibility.Collapsed;
        if (_selectedLibraryId is not null)
        {
            SyncWordPackHeader(_selectedLibraryId);
        }
    }

    private void UpdateSelectedLibraryDirtyState()
    {
        if (_selectedLibraryId is null || _wordPackWorkspace is null)
        {
            return;
        }

        if (_libraryRows.FirstOrDefault(row => string.Equals(row.Id, _selectedLibraryId, StringComparison.OrdinalIgnoreCase)) is { } listRow)
        {
            listRow.Unsaved = _wordPackWorkspace.UnsavedLibraryIds.Contains(_selectedLibraryId);
        }

        if (_wordPackWorkspace.Draft.Find(_selectedLibraryId) is { } library)
        {
            LibraryDetailMeta.Text = WordPackUiText.HeaderMeta(
                library.Content.BuiltIn,
                library.Content.Category,
                library.Content.Rows.Count,
                _wordPackWorkspace.UnsavedLibraryIds.Contains(_selectedLibraryId));
        }
    }

    // The enabled-set persisted in settings: the ids of every ticked library still in the list, in precedence order, so
    // what Save writes never depends on the order the rows are shown in (after a fresh load it is the list 0.4.3 wrote).
    // The rows are read-only in this build, and once the library state is stored the save keeps the stored list anyway.
    private List<string> CollectEnabledLibraryIds() =>
        _wordPackWorkspace?.Draft.LocalState.EnabledIds.ToList()
        ?? LibraryPrecedence.Order(_libraryRows.Where(r => r.Enabled), r => r.Id, r => r.BuiltIn).Select(r => r.Id).ToList();

    // Where a word pack comes from, shown under its name in the list and in the preview's meta line.
    private static string LibrarySource(bool builtIn) => builtIn ? "Built-in" : "Imported";

    private IReadOnlyList<DictionaryLibrary> CurrentWordPackLibraries()
    {
        if (_wordPackWorkspace is null)
        {
            return _libraryRows
                .Select(row => new DictionaryLibrary(
                    row.Id,
                    row.Name,
                    row.Category,
                    null,
                    row.BuiltIn,
                    []))
                .ToList();
        }

        return _wordPackWorkspace.Draft.Libraries
            .Where(library => !library.PendingDelete)
            .Select(library => new DictionaryLibrary(
                library.Content.Id,
                library.Content.Name,
                library.Content.Category,
                library.Content.Description,
                library.Content.BuiltIn,
                library.Content.Rows.Select(row => row.Values.ToEntry()).ToList())
            {
                FileName = library.FileName,
            })
            .ToList();
    }

    private void RefreshTermRows(string libraryId)
    {
        if (_wordPackWorkspace is null)
        {
            return;
        }

        foreach (var row in _libraryTermRows)
        {
            row.PropertyChanged -= LibraryTermRow_PropertyChanged;
        }

        var rows = _wordPackWorkspace.RowsOf(libraryId);
        if (_librarySearchResult?.IsActive == true)
        {
            var matches = _librarySearchResult.MatchesIn(libraryId).ToHashSet();
            rows = rows.Where(row => matches.Contains(row.RowId)).ToList();
        }

        rows = _libraryTermSort.Sort(rows, _libraryTermSortOrder);
        var composition = BuildLibraryCompositionPreview();
        _updatingLibraryTerms = true;
        _libraryTermRows.Clear();
        foreach (var row in rows)
        {
            var status = composition?.StatusOf(libraryId, row.Row.Key);
            var view = new LibraryTermRow(row.RowId, row.Row.Values, status, LibraryTermLint.Check(row.Row.Values));
            view.PropertyChanged += LibraryTermRow_PropertyChanged;
            _libraryTermRows.Add(view);
        }

        _updatingLibraryTerms = false;
        LibraryDetailEmptyPanel.Visibility = _libraryTermRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryDetailEmpty.Text = SearchNoMatchesText(libraryId);
        var addFromSearch = _librarySearchResult?.IsActive == true && _librarySearchResult.TotalMatches == 0;
        LibraryEmptyActionButton.Content = addFromSearch ? $"Add \"{_librarySearchResult!.Query}\" to this word pack" : "Add first word";
        LibraryEmptyActionButton.Visibility = _wordPackWorkspace.CanEditContent(libraryId) && (_librarySearchResult?.IsActive != true || addFromSearch)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshSearchLinks(libraryId);
        UpdateLibraryTermCount(rows.Count);
        RefreshWordDetails();
        ApplyWordDetailsComposition();
    }

    private void RefreshSearchLinks(string libraryId)
    {
        LibrarySearchLinksPanel.Children.Clear();
        if (_librarySearchResult?.IsActive != true || _wordPackWorkspace is null || _librarySearchResult.CountIn(libraryId) > 0)
        {
            return;
        }

        var matches = _librarySearchResult.FoundElsewhere(libraryId).ToList();
        foreach (var match in matches.Take(5))
        {
            if (_wordPackWorkspace.Draft.Find(match.LibraryId) is not { } pack)
            {
                continue;
            }

            var button = new Wpf.Ui.Controls.Button
            {
                Content = $"{pack.Content.Name} ({match.Count:N0})",
                Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
                Margin = new Thickness(0, 0, 8, 4),
            };
            button.Click += (_, _) =>
            {
                LibraryGrid.SelectedItem = _libraryRows.FirstOrDefault(row => string.Equals(row.Id, match.LibraryId, StringComparison.OrdinalIgnoreCase));
                if (_wordPackLayout?.SideBySide == false)
                {
                    ShowWordPackCardPage();
                }
            };
            LibrarySearchLinksPanel.Children.Add(button);
        }

        if (matches.Count > 5)
        {
            LibrarySearchLinksPanel.Children.Add(new TextBlock
            {
                Text = $"and {matches.Count - 5:N0} more",
                Style = (Style)FindResource("CardDescription"),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
    }

    private string SearchNoMatchesText(string libraryId)
    {
        if (_librarySearchResult?.IsActive != true || _librarySearchResult.CountIn(libraryId) > 0 || _wordPackWorkspace is null)
        {
            return _libraryDetailEmptyText;
        }

        var selected = _wordPackWorkspace.Draft.Find(libraryId)?.Content.Name ?? "this word pack";
        if (_librarySearchResult.TotalMatches == 0)
        {
            return $"No words match \"{_librarySearchResult.Query}\" in any word pack.";
        }

        return $"No matches in {selected}. Found in:";
    }

    private void LibraryTermRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updatingLibraryTerms || sender is not LibraryTermRow row || _selectedLibraryId is null || _wordPackWorkspace is null)
        {
            return;
        }

        if (!_wordPackWorkspace.CanEditContent(_selectedLibraryId))
        {
            ShowInfo("This word pack can't be edited right now.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        LibraryEditResult result;
        if (e.PropertyName == nameof(LibraryTermRow.Enabled))
        {
            _wordPackWorkspace.SetTermEnabled(_selectedLibraryId, row.RowId, row.Enabled);
            result = new LibraryEditResult(true, null);
        }
        else
        {
            result = _wordPackWorkspace.EditTerm(
                _selectedLibraryId,
                row.RowId,
                new TermValues(row.Pattern, row.Replacement, row.WholeWord, row.Enabled),
                removalIntent: false);
        }

        if (!result.Applied && result.Issue is { } issue)
        {
            var message = LibraryEditor.Message(issue, row.Pattern);
            ShowInfo(message, Wpf.Ui.Controls.InfoBarSeverity.Warning);
            AnnounceFrom(LibraryTermsGrid, message);
        }

        UpdateSelectedLibraryDirtyState();
        RefreshDictionaryStatus();
    }

    private void LibraryTermsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var header = e.Column.Header?.ToString();
        _libraryTermSortOrder = header switch
        {
            "Scribe hears" => _libraryTermSortOrder == LibraryTermSortOrder.SpokenAscending
                ? LibraryTermSortOrder.SpokenDescending
                : LibraryTermSortOrder.SpokenAscending,
            "Scribe writes" => _libraryTermSortOrder == LibraryTermSortOrder.WrittenAscending
                ? LibraryTermSortOrder.WrittenDescending
                : LibraryTermSortOrder.WrittenAscending,
            _ => LibraryTermSortOrder.SavedOrder,
        };
        if (_selectedLibraryId is not null)
        {
            RefreshTermRows(_selectedLibraryId);
        }
    }

    private void LibraryTermsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LibraryTermsGrid.SelectedItem is LibraryTermRow row)
        {
            OpenWordDetails(row);
        }
    }

    private void LibraryTermsGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z && !IsTextEditingKeyboardFocus())
        {
            e.Handled = true;
            if (_wordPackWorkspace?.CanUndo == true)
            {
                _wordPackWorkspace.Undo();
                RefreshWordPackList(_selectedLibraryId);
                if (_selectedLibraryId is not null)
                {
                    RefreshTermRows(_selectedLibraryId);
                }

                ShowWordPackNotice("Undo", "Undid the latest word pack change.", Wpf.Ui.Controls.InfoBarSeverity.Informational);
            }

            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y && !IsTextEditingKeyboardFocus())
        {
            e.Handled = true;
            if (_wordPackWorkspace?.CanRedo == true)
            {
                _wordPackWorkspace.Redo();
                RefreshWordPackList(_selectedLibraryId);
                if (_selectedLibraryId is not null)
                {
                    RefreshTermRows(_selectedLibraryId);
                }
                ShowWordPackNotice("Redo", "Redid the latest word pack change.", Wpf.Ui.Controls.InfoBarSeverity.Informational);
            }

            return;
        }

        if (LibraryTermsGrid.SelectedItem is not LibraryTermRow row)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OpenWordDetails(row);
        }
        else if (e.Key == Key.Delete && !IsTextEditingKeyboardFocus())
        {
            e.Handled = true;
            DeleteWordPackTerm(row);
        }
        else if (e.Key is Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            e.Handled = true;
            OpenTermMenu(row, LibraryTermsGrid);
        }
    }

    private static bool IsTextEditingKeyboardFocus() =>
        Keyboard.FocusedElement is TextBox or RichTextBox;

private void LibraryTermsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
{
    if (IsTextEditingKeyboardFocus() || LibraryTermsGrid.SelectedItem is not LibraryTermRow row)
    {
        return;
    }

    if (e.Key == Key.Enter)
    {
        e.Handled = true;
        OpenWordDetails(row);
    }
    else if (e.Key == Key.Delete && CanDeleteWordPackTerm(row))
    {
        e.Handled = true;
        DeleteWordPackTerm(row);
    }
}

private bool CanDeleteWordPackTerm(LibraryTermRow row)
{
    if (_selectedLibraryId is null || _wordPackWorkspace is null)
    {
        return false;
    }

    var draft = _wordPackWorkspace.RowsOf(_selectedLibraryId).FirstOrDefault(item => item.RowId == row.RowId);
    return draft is not null && LibraryEditor.AvailableCommands(draft.Row, editingText: false).HasFlag(TermCommands.Delete);
}

    private void LibrarySortButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = LibrarySortButton };
        AddSortItem(menu, "Saved order", LibraryTermSortOrder.SavedOrder);
        AddSortItem(menu, "Scribe hears A to Z", LibraryTermSortOrder.SpokenAscending);
        AddSortItem(menu, "Scribe hears Z to A", LibraryTermSortOrder.SpokenDescending);
        AddSortItem(menu, "Scribe writes A to Z", LibraryTermSortOrder.WrittenAscending);
        AddSortItem(menu, "Scribe writes Z to A", LibraryTermSortOrder.WrittenDescending);
        menu.IsOpen = true;
    }

    private void AddSortItem(ContextMenu menu, string header, LibraryTermSortOrder order)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = _libraryTermSortOrder == order };
        item.Click += (_, _) =>
        {
            _libraryTermSortOrder = order;
            if (_selectedLibraryId is not null)
            {
                RefreshTermRows(_selectedLibraryId);
            }
        };
        menu.Items.Add(item);
    }

    private void SectionWordPacks_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyWordPackLayout();

    private FrameworkElement? WordPackLayoutRoot() => Content as FrameworkElement;

    private IntPtr WordPackDpiChangedHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDpiChanged)
        {
            Dispatcher.BeginInvoke(ApplyWordPackLayout);
        }

        return IntPtr.Zero;
    }

    private void ApplyWordPackLayout()
    {
        if (WordPackLayoutRoot() is not { ActualWidth: > 0, ActualHeight: > 0 } root)
        {
            return;
        }

        var contentWidth = SectionWordPacks.ActualWidth > 0 ? SectionWordPacks.ActualWidth : root.ActualWidth;
        _wordPackLayout = LibraryLayoutPlanner.Plan(new LibraryLayoutInput(
            contentWidth,
            root.ActualHeight,
            TextScaleService.CurrentFactor,
            WordPackNoticeBar.IsOpen,
            WordDetailsPanel.Visibility == Visibility.Visible));
        WordPacksListColumn.Width = new GridLength(_wordPackLayout.ListWidth);
        LibraryTermUseColumn.Width = new DataGridLength(_wordPackLayout.UseColumnWidth);
        LibraryTermSpokenColumn.MinWidth = LibraryLayoutPlanner.MinimumTextColumn * TextScaleService.CurrentFactor;
        LibraryTermWrittenColumn.MinWidth = LibraryTermSpokenColumn.MinWidth;
        LibraryTermSpokenColumn.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        LibraryTermWrittenColumn.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        LibraryTermActionColumn.Width = new DataGridLength(_wordPackLayout.ActionColumnWidth);
        var rowHeight = (20 * TextScaleService.CurrentFactor) + 8;
        LibraryTermsGrid.MinHeight = (32 * TextScaleService.CurrentFactor) + rowHeight * LibraryLayoutPlanner.MinimumRows;
        WordPacksIntroText.Visibility = _wordPackLayout.Short ? Visibility.Collapsed : Visibility.Visible;
        WordPacksIntroInfoButton.Visibility = _wordPackLayout.Short ? Visibility.Visible : Visibility.Collapsed;
        WordDetailsBackButton.Visibility = _wordPackLayout.Short ? Visibility.Visible : Visibility.Collapsed;
        ApplyWordDetailsComposition();

        if (_wordPackLayout.SideBySide)
        {
            WordPacksListColumn.MinWidth = 220;
            WordPacksCardColumn.MinWidth = 0;
            WordPacksListCard.Visibility = Visibility.Visible;
            WordPacksPaneGap.Width = new GridLength(12);
            WordPacksCard.Visibility = Visibility.Visible;
            WordPacksCardColumn.Width = new GridLength(1, GridUnitType.Star);
            WordPacksBackButton.Visibility = Visibility.Collapsed;
            WordPacksCardBackButton.Visibility = Visibility.Collapsed;
        }
        else if (_wordPackStackedCardOpen)
        {
            ShowWordPackCardPage();
        }
        else
        {
            ShowWordPackListPage();
        }
    }

    private void ApplyWordDetailsComposition()
    {
        var detailsSubpage = _wordPackLayout?.Short == true && WordDetailsPanel.Visibility == Visibility.Visible;
        LibraryTermsGrid.Visibility = detailsSubpage ? Visibility.Collapsed : Visibility.Visible;
        LibraryDetailEmptyPanel.Visibility = detailsSubpage || _libraryTermRows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        LibraryAddWordButton.Visibility = detailsSubpage ? Visibility.Collapsed : Visibility.Visible;
        LibrarySortButton.Visibility = detailsSubpage ? Visibility.Collapsed : Visibility.Visible;
        LibraryTermCountText.Visibility = detailsSubpage ? Visibility.Collapsed : Visibility.Visible;
        LibraryDescriptionPanel.Visibility = detailsSubpage ? Visibility.Collapsed : Visibility.Visible;
        LibraryDetailDescFull.Visibility = detailsSubpage ? Visibility.Collapsed : LibraryDetailDescFull.Visibility;
        LibraryUseCheck.Visibility = detailsSubpage ? Visibility.Collapsed : Visibility.Visible;
        LibraryAiCheck.Visibility = detailsSubpage ? Visibility.Collapsed : (AiCleanupCheck?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed);
        LibraryAiHelpText.Visibility = detailsSubpage ? Visibility.Collapsed : (AiCleanupCheck?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed);
        LibraryExportButton.Visibility = detailsSubpage ? Visibility.Collapsed : Visibility.Visible;
        LibraryDetailMoreButton.Visibility = detailsSubpage ? Visibility.Collapsed : Visibility.Visible;
        LibraryOffLine.Visibility = detailsSubpage ? Visibility.Collapsed : LibraryOffLine.Visibility;
        // The subpage hides the notice through its host: WPF-UI's InfoBar shows and hides itself from IsOpen with a
        // template trigger on its own Visibility, which a local Visibility value would override for good, leaving a
        // closed notice on screen.
        WordPackNoticeHost.Visibility = detailsSubpage ? Visibility.Collapsed : Visibility.Visible;
        ApplyWordPackNoticeActionsVisibility();
    }

    // The actions belong to the notice: they show while it is open and has some, unless the short composition's Word
    // details subpage hides the notice. Closing the notice with its X changes only IsOpen, so the actions follow IsOpen.
    private void ApplyWordPackNoticeActionsVisibility()
    {
        var detailsSubpage = _wordPackLayout?.Short == true && WordDetailsPanel.Visibility == Visibility.Visible;
        WordPackNoticeActionsPanel.Visibility = !detailsSubpage && WordPackNoticeBar.IsOpen && WordPackNoticeActionsPanel.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void WordPackNoticeBar_IsOpenChanged(object? sender, EventArgs e) => ApplyWordPackNoticeActionsVisibility();

    // The card scrolls as a whole when its header and the words' minimum height don't fit, so its content is measured
    // without a height bound, and a DataGrid measured that way realizes every row. The words grid therefore gets the
    // height the viewport has left after the card's other rows, never less than its minimum, as an explicit Height
    // (the XAML's is only the bound for the first layout): it fills the card as it did before the card could scroll,
    // and its rows stay virtualized. ScrollChanged, not SizeChanged: the viewport and extent are current only once the
    // ScrollViewer has updated them after layout, and a change of either (a resize, a header row growing) raises it.
    private void WordPacksCardScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The words grid's own ScrollViewer raises ScrollChanged too, and it bubbles through this one.
        if (ReferenceEquals(e.OriginalSource, WordPacksCardScroll) && (e.ViewportHeightChange != 0 || e.ExtentHeightChange != 0))
        {
            FitLibraryTermsGridToCard();
        }
    }

    private void FitLibraryTermsGridToCard()
    {
        var viewport = WordPacksCardScroll.ViewportHeight;
        if (viewport <= 0 || LibraryTermsGrid.Parent is not FrameworkElement wordsHost)
        {
            return;
        }

        var wordsRow = Grid.GetRow(wordsHost);
        var otherRows = 0.0;
        for (var row = 0; row < WordPacksCardContent.RowDefinitions.Count; row++)
        {
            if (row != wordsRow)
            {
                otherRows += WordPacksCardContent.RowDefinitions[row].ActualHeight;
            }
        }

        var height = Math.Max(LibraryTermsGrid.MinHeight, Math.Floor(viewport - otherRows));
        if (double.IsNaN(LibraryTermsGrid.Height) || Math.Abs(LibraryTermsGrid.Height - height) >= 1)
        {
            LibraryTermsGrid.Height = height;
        }
    }

    private void ShowWordPackListPage()
    {
        _wordPackStackedCardOpen = false;
        WordPacksListColumn.MinWidth = 0;
        WordPacksCardColumn.MinWidth = 0;
        WordPacksListCard.Visibility = Visibility.Visible;
        WordPacksCard.Visibility = Visibility.Collapsed;
        WordPacksPaneGap.Width = new GridLength(0);
        WordPacksListColumn.Width = new GridLength(1, GridUnitType.Star);
        WordPacksCardColumn.Width = new GridLength(0);
        WordPacksBackButton.Visibility = Visibility.Collapsed;
        WordPacksCardBackButton.Visibility = Visibility.Collapsed;
        LibraryGrid.Focus();
    }

    private void ShowWordPackCardPage()
    {
        _wordPackStackedCardOpen = true;
        WordPacksListColumn.MinWidth = 0;
        WordPacksCardColumn.MinWidth = 0;
        WordPacksListCard.Visibility = Visibility.Collapsed;
        WordPacksCard.Visibility = Visibility.Visible;
        WordPacksPaneGap.Width = new GridLength(0);
        WordPacksListColumn.Width = new GridLength(0);
        WordPacksCardColumn.Width = new GridLength(1, GridUnitType.Star);
        WordPacksBackButton.Visibility = Visibility.Visible;
        WordPacksCardBackButton.Visibility = Visibility.Visible;
        WordPacksCard.Focus();
    }

    private LibraryComposition? BuildLibraryCompositionPreview()
    {
        if (_wordPackWorkspace is null || _wordPackCatalog is null)
        {
            return null;
        }

        try
        {
            return LibraryComposition.Preview(
                _wordPackWorkspace.Draft,
                _wordPackCatalog,
                BuildDictionaryEntries(_rows.ToList(), out _),
                GlossaryBudget.For(_settings.AiCleanupPromptStyle, _settings.AiCleanupProvider));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void UpdateLibraryTermCount(int visible = -1)
    {
        var total = _selectedLibraryId is null || _wordPackWorkspace is null
            ? 0
            : _wordPackWorkspace.RowsOf(_selectedLibraryId).Count;
        if (visible < 0)
        {
            visible = _libraryTermRows.Count;
        }

        LibraryTermCountText.Text = WordPackUiText.TermCount(visible, total);
    }

    private static string FirstDescriptionLine(string description)
    {
        var trimmed = description.Trim();
        var lineBreak = trimmed.IndexOfAny(['\r', '\n']);
        var sentence = trimmed.IndexOf(". ", StringComparison.Ordinal);
        var cut = lineBreak >= 0 ? lineBreak : sentence >= 0 ? sentence + 1 : -1;
        return cut > 0 ? trimmed[..cut] : trimmed;
    }

    private static bool DescriptionHasMore(string description) =>
        !string.Equals(FirstDescriptionLine(description), description.Trim(), StringComparison.Ordinal);

    private void LibraryDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        LibraryDetailDescFull.Visibility =
            LibraryDetailDescFull.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private LibraryRow NewLibraryRow(LibraryContent library) => new()
    {
        Id = library.Id,
        Name = library.Name,
        Category = library.Category,
        Terms = library.Rows.Count,
        Source = LibrarySource(library.BuiltIn),
        BuiltIn = library.BuiltIn,
        Unsaved = _wordPackWorkspace?.UnsavedLibraryIds.Contains(library.Id) == true,
        Enabled = _wordPackWorkspace?.Draft.LocalState.EnabledIds.Contains(library.Id) == true,
        AiCleanup = _wordPackWorkspace?.ShowsAiPermission(library.Id) == true,
    };

    private void RefreshWordPackList(string? selectId = null)
    {
        if (_wordPackWorkspace is null)
        {
            return;
        }

        selectId ??= (LibraryGrid.SelectedItem as LibraryRow)?.Id;
        foreach (var row in _libraryRows)
        {
            row.PropertyChanged -= LibraryRow_PropertyChanged;
        }

        _updatingLibraryRows = true;
        _libraryRows.Clear();
        var ordering = _libraryOrdering ??= LibraryOrdering.ForCurrentCulture();
        foreach (var library in ordering.Sort(
                     _wordPackWorkspace.Draft.Libraries.Where(l => !l.PendingDelete).Select(l => l.Content),
                     l => l.Name,
                     l => l.Id))
        {
            var row = NewLibraryRow(library);
            ApplySearchState(row);
            _libraryRows.Add(row);
        }

        _updatingLibraryRows = false;
        if (selectId is not null)
        {
            var selected = _libraryRows.FirstOrDefault(row => string.Equals(row.Id, selectId, StringComparison.OrdinalIgnoreCase));
            LibraryGrid.SelectedItem = selected;
            if (selected is null)
            {
                _selectedLibraryId = null;
                _libraryTermRows.Clear();
                WordDetailsPanel.Visibility = Visibility.Collapsed;
                UpdateLibraryDetail(null);
            }
        }
    }

    private void LibraryImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            if (_wordPackWorkspace is null)
            {
                return;
            }

            var bytes = File.ReadAllBytes(dialog.FileName);
            var document = LibraryCsvCodec.Instance.ReadImport(bytes);
            var plan = LibraryImportPlanner.Plan(
                document,
                new LibraryImportTarget.NewLibrary(Path.GetFileName(dialog.FileName)),
                _wordPackWorkspace.Draft);
            if (ShowImportWordPackDialog(plan) is not { } accepted)
            {
                return;
            }

            var before = _wordPackWorkspace.Draft.Libraries.Select(l => l.Content.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = _wordPackWorkspace.ApplyImport(accepted.Plan, accepted.Choice);
            if (!result.Applied)
            {
                ShowThemedMessage("Couldn't import the word pack", result.Issue is null
                    ? "Couldn't import the word pack. Your edits are still here."
                    : LibraryEditor.Message(result.Issue));
                return;
            }

            var importedId = _wordPackWorkspace.Draft.Libraries.FirstOrDefault(l => !before.Contains(l.Content.Id))?.Content.Id;
            RefreshWordPackList(importedId);
            RefreshDictionaryStatus();
            var importedName = importedId is null ? accepted.Plan.SuggestedName : _wordPackWorkspace.Draft.Find(importedId)?.Content.Name ?? accepted.Plan.SuggestedName;
            ShowInfo($"Imported {importedName}. Save to apply it.");
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not import a word pack from Settings.");
            ShowThemedMessage("Couldn't import the word pack", "Couldn't import the word pack. Check the file and try again.");
            return;
        }
    }

    private AcceptedImport? ShowImportWordPackDialog(LibraryImportPlan plan)
    {
        var nameBox = new TextBox { Text = plan.SuggestedName, MinWidth = 320, Margin = new Thickness(0, 8, 0, 8) };
        var summary = new TextBlock
        {
            Text = $"Adds {plan.Adds:N0}, {plan.WrittenDifferently:N0} written differently, {plan.AlreadyHere:N0} already here, {plan.RemovalRules:N0} remove words.",
            TextWrapping = TextWrapping.Wrap,
        };
        var conflict = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var keepMine = new RadioButton { Content = "Keep mine", IsChecked = true, GroupName = "ImportConflictChoice" };
        var useFile = new RadioButton { Content = "Use the file's version", GroupName = "ImportConflictChoice" };
        conflict.Children.Add(keepMine);
        conflict.Children.Add(useFile);
        var errors = new TextBlock
        {
            Text = plan.SkippedRows.Count == 0
                ? "No row errors."
                : string.Join(Environment.NewLine, plan.SkippedRows.Take(6).Select(row => $"Row {row.Line}: {row.Kind}")),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Replacement name" });
        panel.Children.Add(nameBox);
        panel.Children.Add(summary);
        panel.Children.Add(conflict);
        panel.Children.Add(errors);
        var dialog = new Wpf.Ui.Controls.FluentWindow
        {
            Title = "Import a word pack",
            Owner = this,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    panel,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children =
                        {
                            new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) },
                            new Button { Content = $"Import {plan.Operations.Count:N0} valid words", IsDefault = true, MinWidth = 150 },
                        },
                    },
                },
            },
        };
        ((Button)((StackPanel)((StackPanel)dialog.Content).Children[1]).Children[1]).Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true
            ? new AcceptedImport(
                plan with { SuggestedName = nameBox.Text },
                useFile.IsChecked == true ? ImportConflictChoice.UseFilesVersion : ImportConflictChoice.KeepMine)
            : null;
    }

    private async void LibraryExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLibrary() is not { } library)
        {
            return;
        }

        var draft = _wordPackWorkspace?.Draft.Find(library.Id);
        if (draft is null)
        {
            return;
        }

        var exportMessage = draft.Content.BuiltIn
            ? "Exports a standalone copy, not its built-in update history."
            : "Exports the selected word pack as a CSV file.";
        if (_wordPackWorkspace?.UnsavedLibraryIds.Contains(library.Id) == true)
        {
            exportMessage = $"Export includes unsaved edits. Save applies them to dictation.\n\n{exportMessage}";
        }

        if (!await ConfirmAsync("Export word pack", exportMessage, "Export"))
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = library.Id + ".csv",
            Title = "Export word pack",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var content = draft.Content;
            if (content is null)
            {
                return;
            }

            File.WriteAllBytes(dialog.FileName, LibraryCsvCodec.Instance.WriteExport(content));
            var off = content.Rows.Count(row => !row.Values.Enabled);
            ShowInfo($"Exported {content.Rows.Count:N0} {(content.Rows.Count == 1 ? "word" : "words")}, including {off:N0} turned-off {(off == 1 ? "word" : "words")}.");
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not export a word pack from Settings.");
            ShowThemedMessage("Couldn't export the word pack", "Couldn't export the word pack. Choose another location or try again.");
        }
    }

    private async void LibraryRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryGrid.SelectedItem is not LibraryRow row)
        {
            ShowInfo("Select a word pack to remove.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        if (row.BuiltIn)
        {
            ShowThemedMessage("Built-in word pack", "Built-in word packs can be turned off, but not removed.");
            return;
        }

        if (!await ConfirmRiskyAsync(
                "Delete word pack",
                $"Delete \"{row.Name}\" and its {row.Terms:N0} {(row.Terms == 1 ? "word" : "words")}? You can restore it from Recently deleted for 30 days. Your dictionary is unchanged.",
                "Delete word pack"))
        {
            return;
        }

        try
        {
            _wordPackWorkspace?.DeleteLibrary(row.Id);
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not stage a word pack deletion from Settings.");
            ShowThemedMessage("Couldn't delete the word pack", "Couldn't delete the word pack. Try again.");
            return;
        }

        RefreshWordPackList();
        UpdateLibraryDetail(LibraryGrid.SelectedItem as LibraryRow);

        RefreshDictionaryStatus();
        ShowInfo("Deleted the word pack. Save to apply it.");
    }

    // Resolves the grid's selected row back to its loaded library from the cached snapshot,
    // surfacing a friendly hint when nothing is selected or the file has since gone missing.
    private DictionaryLibrary? SelectedLibrary()
    {
        if (LibraryGrid.SelectedItem is not LibraryRow row)
        {
            ShowInfo("Select a word pack first.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return null;
        }

        var draft = _wordPackWorkspace?.Draft.Find(row.Id);
        if (draft is null)
        {
            ShowInfo("That word pack is no longer available.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return null;
        }

        return new DictionaryLibrary(
            draft.Content.Id,
            draft.Content.Name,
            draft.Content.Category,
            draft.Content.Description,
            draft.Content.BuiltIn,
            draft.Content.Rows.Select(row => row.Values.ToEntry()).ToList())
        {
            FileName = draft.FileName,
        };
    }

    private void OnLibraryVocabularyChanged(long generation) =>
        Dispatcher.BeginInvoke(new Action(async () => await TrySettlePendingWordPackSaveAsync()));

    private async Task TrySettlePendingWordPackSaveAsync()
    {
        if (!_wordPackSaveProtocol.HasPendingSave || _closed)
        {
            return;
        }

        var result = await _wordPackSaveProtocol.TrySettlePendingAsync(BuildWordPackSaveRequest(null, null, null, null, null, useVocabularyReload: true));
        ShowWordPackSaveResult(result);
    }

    private void RefreshWordPackRowsFromWorkspace()
    {
        if (_wordPackWorkspace is null)
        {
            return;
        }

        var enabled = _wordPackWorkspace.Draft.LocalState.EnabledIds;
        _updatingLibraryRows = true;
        foreach (var row in _libraryRows)
        {
            row.Enabled = enabled.Contains(row.Id);
        }
        _updatingLibraryRows = false;
        _libraryLoad.MarkSaved(LibrarySignature());
    }

    private WordPackSaveProtocolRequest BuildWordPackSaveRequest(
        IReadOnlyList<DictionaryEntry>? entries,
        IReadOnlyList<Snippet>? snippets,
        ExternalIntents? intents,
        string? dictionarySignature,
        string? snippetSignature,
        StartupRegistrationStatus? observedStartup = null,
        bool useVocabularyReload = false,
        IReadOnlyList<SnippetSubmission>? snippetSubmission = null,
        IReadOnlyList<ProfileSubmission>? profileSubmission = null,
        IReadOnlyList<DictionarySubmission>? dictionarySubmission = null,
        string? capturedDraft = null,
        SaveDraftSections.Capture? capturedSections = null)
    {
        var savedAiIntent = intents?.AiCleanup ?? 0;
        var savedMicrophoneIntent = intents?.Microphone ?? 0;
        var savedSections = capturedSections;
        return new WordPackSaveProtocolRequest(
            _wordPackWorkspace,
            payload => _settingsRepository.SaveBundle(
                _settings,
                entries,
                snippets,
                intents ?? new ExternalIntents(0, 0),
                payload),
            useVocabularyReload ? _reloadVocabulary : () => _applySettings(_settings),
            () => capturedDraft ?? SaveDraftSignature(BuildSaveDraftSections(), savedSections),
            () => SaveDraftSignature(BuildSaveDraftSections(), savedSections),
            Validate: null,
            OnSettingsCommitted,
            OnWordPacksChanged);

        void OnSettingsCommitted()
        {
            // The saved and running settings, which the page's notices compare against (Try dictation): refreshed only
            // once SaveBundle has stored the document.
            _committedSettings = _settings.Clone();
            OnCommittedSettingsChanged();

            // A Save that makes Foundry Local serve the model an earlier Set up or Load was for starts a new setup, and the
            // row shows that setup's own progress and result from now on.
            if (_foundryOperationStatus is { } foundryOutcome &&
                _foundryOperationAlias is { } foundryOutcomeAlias &&
                SavedActiveFoundryModelMatches(foundryOutcomeAlias) &&
                FoundryLocalSetup.RetiredBySaveThatServesIt(foundryOutcome))
            {
                _foundryOperationStatus = null;
                _foundryOperationAlias = null;
            }

            _settingsRecovered = false;
            _savedBinding = _settings.Hotkey;
            _savedDictationOnlyBinding = _settings.DictationOnlyHotkey;

            // Only now, once the document is stored: _settings already holds the picked provider, and a
            // Save that fails keeps it there while cleanup goes on serving the saved one.
            _savedAiProvider = _settings.AiCleanupProvider;
            _externalAiCleanup.SavedThrough(savedAiIntent);
            _externalMicrophone.SavedThrough(savedMicrophoneIntent);
            if (_externalAiCleanup.NewestRevision == 0 && (AiCleanupCheck.IsChecked == true) != _settings.EnableAiCleanup)
            {
                ShowExternalAiCleanup(_settings.EnableAiCleanup);
            }

            if (_externalMicrophone.NewestRevision == 0 && ShownMicrophone != MicrophoneSelection.From(_settings))
            {
                ShowMicrophones(MicrophoneSelection.From(_settings));
            }

            if (dictionarySignature is not null && entries is not null && dictionarySubmission is not null)
            {
                _dictionaryLoad.MarkSaved(dictionarySignature);
                MarkDictionaryRowsSaved(dictionarySubmission);
            }

            if (snippetSignature is not null && snippets is not null && snippetSubmission is not null)
            {
                _snippetLoad.MarkSaved(snippetSignature);
                MarkSnippetRowsSaved(snippetSubmission);
            }

            if (profileSubmission is not null)
            {
                MarkProfileRowsSaved(profileSubmission);
            }

            if (observedStartup is not null)
            {
                _startupSwitch.Show(observedStartup);
                ShowStartupStatus(observedStartup);
            }

            UpdateAiEnabledState();
        }

        // The protocol hands over the exact catalog it settled or rebased on, so the glossary composes against the same
        // generation as the workspace, and no catalog is read on the dispatcher.
        void OnWordPacksChanged(LibraryCatalog catalog)
        {
            _wordPackCatalog = catalog;
            RefreshWordPackList();
            RefreshWordPackRowsFromWorkspace();
            RefreshDictionaryStatus();
            ShowCurrentWordPackLoadNotice();
        }
    }

    private void ShowWordPackSaveResult(WordPackSaveProtocolResult result)
    {
        if (result.Message is null)
        {
            return;
        }

        var severity = result.Severity switch
        {
            WordPackSaveProtocolSeverity.Error => Wpf.Ui.Controls.InfoBarSeverity.Error,
            WordPackSaveProtocolSeverity.Warning => Wpf.Ui.Controls.InfoBarSeverity.Warning,
            _ => Wpf.Ui.Controls.InfoBarSeverity.Informational,
        };
        ShowWordPackNotice("Word packs", result.Message, severity, result.Actions ?? [], result.TargetLibraryIds?.FirstOrDefault());
    }

    private void ShowWordPackNotice(string title, string message, Wpf.Ui.Controls.InfoBarSeverity severity) =>
        ShowWordPackNotice(title, message, severity, []);

    private void ShowWordPackNotice(
        string title,
        string message,
        Wpf.Ui.Controls.InfoBarSeverity severity,
        IReadOnlyList<WordPackNoticeAction> actions,
        string? libraryId = null)
    {
        _noticeLibraryId = libraryId ?? _selectedLibraryId;
        WordPackNoticeBar.Title = title;
        WordPackNoticeBar.Message = message;
        WordPackNoticeBar.Severity = severity;
        WordPackNoticeBar.IsOpen = true;
        RenderWordPackNoticeActions(actions);
        AnnounceFrom(WordPackNoticeBar, message);
        ApplyWordPackLayout();
    }

    private void ShowCurrentWordPackLoadNotice()
    {
        if (_wordPackCatalog is null)
        {
            return;
        }

        var candidate = WordPackLoadNotices.Select(_wordPackCatalog, BuildLibraryCompositionPreview());
        if (candidate?.Key == _activeLoadNoticeKey)
        {
            return;
        }

        _activeLoadNoticeKey = candidate?.Key;
        if (candidate is null)
        {
            return;
        }

        var actions = candidate.Notice.Actions;
        if (!candidate.RestorePreviousAvailable)
        {
            actions = actions.Where(action => action != WordPackNoticeAction.RestorePreviousCopy).ToList();
        }

        ShowWordPackNotice("Word packs", candidate.Notice.Text, SeverityOf(candidate.Notice.Severity), actions, candidate.LibraryId);
    }

    private static Wpf.Ui.Controls.InfoBarSeverity SeverityOf(WordPackNoticeSeverity severity) =>
        severity switch
        {
            WordPackNoticeSeverity.Error => Wpf.Ui.Controls.InfoBarSeverity.Error,
            WordPackNoticeSeverity.Warning => Wpf.Ui.Controls.InfoBarSeverity.Warning,
            _ => Wpf.Ui.Controls.InfoBarSeverity.Informational,
        };

    private void RenderWordPackNoticeActions(IReadOnlyList<WordPackNoticeAction> actions)
    {
        WordPackNoticeActionsPanel.Children.Clear();
        foreach (var action in actions)
        {
            var button = new Wpf.Ui.Controls.Button
            {
                Content = WordPackNotices.Label(action),
                Margin = new Thickness(0, 0, 8, 0),
            };
            button.Click += async (_, _) => await HandleWordPackNoticeActionAsync(action);
            WordPackNoticeActionsPanel.Children.Add(button);
        }

        WordPackNoticeActionsPanel.Visibility = WordPackNoticeActionsPanel.Children.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async Task HandleWordPackNoticeActionAsync(WordPackNoticeAction action)
    {
        switch (action)
        {
            case WordPackNoticeAction.Retry:
                SaveButton_Click(this, new RoutedEventArgs());
                break;
            case WordPackNoticeAction.SaveCopy:
                LibraryExportButton_Click(this, new RoutedEventArgs());
                break;
            case WordPackNoticeAction.KeepEditing:
                WordPackNoticeBar.IsOpen = false;
                WordPackNoticeActionsPanel.Visibility = Visibility.Collapsed;
                break;
            case WordPackNoticeAction.ReloadSavedVersion:
                await ReloadSavedWordPackAsync();
                break;
            case WordPackNoticeAction.SaveDraftAsNew:
                await SaveWordPackDraftAsNewAsync();
                break;
            case WordPackNoticeAction.RestorePreviousCopy:
                RecoverBuiltInWordPack(BuiltInEditsRecovery.RestorePrevious);
                break;
            case WordPackNoticeAction.BackUpAndReset:
                await BackUpAndResetBuiltInWordPackAsync();
                break;
            case WordPackNoticeAction.UseTheseChoices:
                _wordPackWorkspace?.ConfirmAiPermissions();
                RefreshWordPackList(_selectedLibraryId);
                WordPackNoticeBar.IsOpen = false;
                WordPackNoticeActionsPanel.Visibility = Visibility.Collapsed;
                break;
            case WordPackNoticeAction.UseMySpelling:
                UseFirstLegacyMarkedSpelling();
                break;
        }
    }

    private async Task ReloadSavedWordPackAsync()
    {
        if (NoticeLibraryRow() is not { } row || _wordPackWorkspace is null)
        {
            return;
        }

        if (!await ConfirmRiskyAsync(
                "Reload saved version",
                $"Reload the saved version of \"{row.Name}\"? Your unsaved changes to it are removed.",
                "Reload"))
        {
            return;
        }

        await RebaseWordPacksFromCurrentCatalogAsync();
        _wordPackWorkspace.DiscardLibrary(row.Id);
        RefreshWordPackList(row.Id);
        UpdateLibraryDetail(LibraryGrid.SelectedItem as LibraryRow);
        RefreshDictionaryStatus();
        WordPackNoticeBar.IsOpen = false;
        WordPackNoticeActionsPanel.Visibility = Visibility.Collapsed;
    }

    private async Task SaveWordPackDraftAsNewAsync()
    {
        if (NoticeLibraryRow() is not { } row || _wordPackWorkspace is null)
        {
            return;
        }

        var copyId = _wordPackWorkspace.Duplicate(row.Id);
        await RebaseWordPacksFromCurrentCatalogAsync();
        _wordPackWorkspace.DiscardLibrary(row.Id);
        RefreshWordPackList(copyId);
        RefreshDictionaryStatus();
        WordPackNoticeBar.IsOpen = false;
        WordPackNoticeActionsPanel.Visibility = Visibility.Collapsed;
    }

    private async Task RebaseWordPacksFromCurrentCatalogAsync()
    {
        if (_wordPackWorkspace is null)
        {
            return;
        }

        var catalog = await Task.Run(_libraryStore.LoadCatalog);
        _wordPackCatalog = catalog;
        _wordPackWorkspace.Rebase(catalog);
    }

    private async Task BackUpAndResetBuiltInWordPackAsync()
    {
        if (NoticeLibraryRow() is not { } row || _wordPackWorkspace is null ||
            !await ConfirmRiskyAsync(
                "Back up and reset",
                $"Back up your unreadable edits to \"{row.Name}\" and go back to the built-in version? The backup is kept in Scribe's data folder.",
                "Back up and reset"))
        {
            return;
        }

        RecoverBuiltInWordPack(BuiltInEditsRecovery.BackUpAndReset);
    }

    private void RecoverBuiltInWordPack(BuiltInEditsRecovery recovery)
    {
        if (NoticeLibraryRow() is not { } row || _wordPackWorkspace is null)
        {
            return;
        }

        _wordPackWorkspace.RecoverBuiltIn(row.Id, recovery);
        RefreshWordPackList(row.Id);
        UpdateLibraryDetail(LibraryGrid.SelectedItem as LibraryRow);
        RefreshDictionaryStatus();
        WordPackNoticeBar.IsOpen = false;
        WordPackNoticeActionsPanel.Visibility = Visibility.Collapsed;
    }

    private void UseFirstLegacyMarkedSpelling()
    {
        if (_wordPackWorkspace is null)
        {
            return;
        }

        var composition = BuildLibraryCompositionPreview();
        (string LibraryId, DraftTermRow Row)? marked = null;
        foreach (var library in _wordPackWorkspace.Draft.Libraries)
        {
                foreach (var row in _wordPackWorkspace.RowsOf(library.Content.Id))
                {
                    var status = composition?.StatusOf(library.Content.Id, row.Row.Key);
                    if (status?.LegacyMarkerActive == true)
                    {
                        marked = (library.Content.Id, row);
                        break;
                    }
                }

                if (marked is not null)
                {
                    break;
                }
        }

        if (marked is null)
        {
            WordPackNoticeBar.IsOpen = false;
            WordPackNoticeActionsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _wordPackWorkspace.UseMySpelling(marked.Value.LibraryId, marked.Value.Row.RowId);
        RefreshWordPackList(marked.Value.LibraryId);
        RefreshTermRows(marked.Value.LibraryId);
        RefreshDictionaryStatus();
        WordPackNoticeBar.IsOpen = false;
        WordPackNoticeActionsPanel.Visibility = Visibility.Collapsed;
    }

    private LibraryRow? NoticeLibraryRow()
    {
        var id = _noticeLibraryId ?? _selectedLibraryId;
        return id is null
            ? LibraryGrid.SelectedItem as LibraryRow
            : _libraryRows.FirstOrDefault(row => string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase));
    }
    private sealed class WordPackSaveStore : IWordPackSaveProtocolStore
    {
        private readonly ILibraryCatalogStore _store;
        private readonly Action<LibrarySavePayload> _commitRepair;

        public WordPackSaveStore(ILibraryCatalogStore store, Action<LibrarySavePayload> commitRepair)
        {
            _store = store;
            _commitRepair = commitRepair;
        }

        public Task<LibraryPrepareResult> PrepareAsync(LibraryChangeSet changes) =>
            Task.Run(() => WordPackSaveSession.Prepare(_store, changes));

        public Task<LibrarySaveOutcome> CompleteAsync(PreparedLibrarySave prepared) =>
            Task.Run(() => WordPackSaveSession.Complete(_store, prepared));

        public Task<LibraryCatalog> LoadCatalogAsync() =>
            Task.Run(() => WordPackSaveSession.LoadCatalog(_store));

        public void CommitRepair(LibrarySavePayload payload) => _commitRepair(payload);
    }

    private sealed record AcceptedImport(LibraryImportPlan Plan, ImportConflictChoice Choice);

    /// <summary>One editable row of the selected word pack.</summary>
    private sealed class LibraryTermRow(long rowId, TermValues values, TermStatus? status = null, TermHints hints = TermHints.None) : INotifyPropertyChanged
    {
        private string _pattern = values.Spoken;
        private string _replacement = values.Written;
        private bool _wholeWord = values.WholeWord;
        private bool _enabled = values.Enabled;

        public long RowId { get; } = rowId;
        public TermStatus? Status { get; } = status;
        public TermHints Hints { get; } = hints;

        public string Pattern
        {
            get => _pattern;
            set => Set(ref _pattern, value ?? string.Empty);
        }

        public string Replacement
        {
            get => _replacement;
            set => Set(ref _replacement, value ?? string.Empty);
        }

        public bool WholeWord
        {
            get => _wholeWord;
            set => Set(ref _wholeWord, value);
        }

        public bool Enabled
        {
            get => _enabled;
            set => Set(ref _enabled, value);
        }

        public string MarkerLabel => Status is null ? string.Empty : WordPackUiText.MarkerLabel(Status.Marker);

        public string MarkerTooltip => Status is null ? string.Empty : DescribeTermStatus(Status);

        public Visibility MarkerVisibility => string.IsNullOrEmpty(MarkerLabel) ? Visibility.Collapsed : Visibility.Visible;

        public string MoreActionName => string.IsNullOrWhiteSpace(Pattern) ? "More actions for new word" : $"More actions for {Pattern}";

        public void SetValues(string pattern, string replacement, bool wholeWord, bool enabled)
        {
            _pattern = pattern ?? string.Empty;
            _replacement = replacement ?? string.Empty;
            _wholeWord = wholeWord;
            _enabled = enabled;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Pattern)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Replacement)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WholeWord)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MoreActionName)));
        }

        public override string ToString() => $"Spoken {Pattern}, written {Replacement}";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
    /// <summary>
    /// Library row backing the word packs grid. It notifies so staged changes update the workspace and the grid follows
    /// a Save's committed state.
    /// </summary>
    public sealed class LibraryRow : INotifyPropertyChanged
    {
        private bool _enabled;
        private bool _aiCleanup;
        private int? _matchCount;
        private bool _unsaved;
        private Brush? _textBrush;

        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int Terms { get; set; }

        /// <summary>Where the word pack comes from, "Built-in" or "Imported": the secondary line under its name.</summary>
        public string Source { get; set; } = string.Empty;
        public bool BuiltIn { get; set; }
        public string DisplaySource => WordPackUiText.ListMeta(BuiltIn, Terms, Unsaved, MatchCount);
        public int? MatchCount
        {
            get => _matchCount;
            set
            {
                if (_matchCount != value)
                {
                    _matchCount = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatchCount)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplaySource)));
                }
            }
        }

        public bool Unsaved
        {
            get => _unsaved;
            set
            {
                if (_unsaved != value)
                {
                    _unsaved = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Unsaved)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnsavedVisibility)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplaySource)));
                }
            }
        }

        public Visibility UnsavedVisibility => Unsaved ? Visibility.Visible : Visibility.Collapsed;

        public Brush? TextBrush
        {
            get => _textBrush;
            set
            {
                if (!Equals(_textBrush, value))
                {
                    _textBrush = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextBrush)));
                }
            }
        }

        /// <summary>What UI Automation reads for the name cell, which shows both lines.</summary>
        public string AccessibleName => $"{Name}, {Source}";

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled != value)
                {
                    _enabled = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
                }
            }
        }

        public bool AiCleanup
        {
            get => _aiCleanup;
            set
            {
                if (_aiCleanup != value)
                {
                    _aiCleanup = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AiCleanup)));
                }
            }
        }

        // A check box cell has no text of its own, so UI Automation names the focused cell after
        // ToString(), which would otherwise read out this type's name.
        public override string ToString() => Name;

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
