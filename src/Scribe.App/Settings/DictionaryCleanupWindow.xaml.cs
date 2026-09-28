using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.Settings;
using Wpf.Ui.Controls;

namespace Scribe.App.Settings;

/// <summary>
/// Review step for <see cref="DictionaryUsageAnalyzer"/>. Shows the evidence behind every proposed
/// removal and never applies anything on its own: it returns a choice, and the settings window
/// stages that into the grid so the user's normal Save is still the only thing that writes.
/// </summary>
public partial class DictionaryCleanupWindow : FluentWindow
{
    private readonly List<CleanupRow> _entryRows;
    private readonly List<CleanupRow> _libraryRows;
    private bool _syncingSelectAll;
    private DictionaryCleanupChoice? _choice;

    // BatchCleanupSelectionCounts: Select everything sets every row inside its guard and then counts once, so the rows it
    // changes no longer count every row each (two full counts per row, quadratic in the rows).
    private readonly bool _batchSelectionCounts;

    private DictionaryCleanupWindow(DictionaryUsageReport report, PerfFlags perfFlags)
    {
        _batchSelectionCounts = perfFlags.IsOn(PerfFlags.BatchCleanupSelectionCounts);
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this, Wpf.Ui.Controls.WindowBackdropType.Mica, updateAccents: false);
        InitializeComponent();

        SummaryText.Text = report.Summary;

        var window = $"your last {report.TranscriptsScanned:N0} dictations";

        _entryRows = report.UnusedEntries
            .Select(u => new CleanupRow(
                $"\"{u.Entry.Pattern}\" becomes \"{u.Entry.Replacement}\"",
                u.Entry.Enabled
                    ? $"Currently on. Neither word came up in {window}."
                    : $"Already off. Neither word came up in {window}.",
                u.Entry))
            .ToList();

        _libraryRows = report.Libraries
            .Select(l => new CleanupRow(l.Name, DescribeLibrary(l, window), l))
            .ToList();

        EntriesList.ItemsSource = _entryRows;
        LibrariesList.ItemsSource = _libraryRows;

        var hasEntries = _entryRows.Count > 0;
        EntriesHeader.Visibility = hasEntries ? Visibility.Visible : Visibility.Collapsed;
        EntriesList.Visibility = EntriesHeader.Visibility;

        var hasLibraries = _libraryRows.Count > 0;
        LibrariesHeader.Visibility = hasLibraries ? Visibility.Visible : Visibility.Collapsed;
        LibrariesList.Visibility = LibrariesHeader.Visibility;

        FootnoteText.Text = hasLibraries
            ? "Turning a word off is reversible: it stays in your dictionary with its check cleared and "
                + "stops being applied. Turning a word pack off keeps the words you still use working, "
                + "copying them into your own dictionary where needed. A word pack whose words overlap other "
                + "words dictation applies stays on, because turning it off could change what dictation "
                + "writes. Nothing is written until you save the settings window."
            : "Turning a word off is reversible: it stays in your dictionary with its check cleared and "
                + "stops being applied. Deleting removes it for good. Either way, nothing is written "
                + "until you save the settings window.";

        foreach (var row in _entryRows.Concat(_libraryRows))
        {
            row.PropertyChanged += (_, _) =>
            {
                if (_batchSelectionCounts && _syncingSelectAll)
                {
                    return;
                }

                UpdateButtons();
                SyncSelectAll();
            };
        }

        UpdateButtons();
        SyncSelectAll();
    }

    /// <summary>
    /// Runs the review modally. Returns <see langword="null"/> when the user cancels, so a closed
    /// dialog and an empty selection are never confused.
    /// </summary>
    public static DictionaryCleanupChoice? Show(Window owner, DictionaryUsageReport report, PerfFlags? perfFlags = null)
    {
        var window = new DictionaryCleanupWindow(report, perfFlags ?? PerfFlags.None) { Owner = owner };
        window.ShowDialog();
        return window._choice;
    }

    /// <summary>
    /// States the size of the win and, crucially, what survives. A user will not turn off a word pack
    /// they believe they are partly relying on unless they are told the working words are carried over.
    /// </summary>
    /// <remarks>
    /// It quotes no count of copies: the plan copies a word only where nothing else would write it the
    /// same way, which depends on everything else switched off together, so the notice after the switch
    /// reports the real number.
    /// </remarks>
    private static string DescribeLibrary(LibraryUsage usage, string window)
    {
        var unused = $"{usage.UnusedCount:N0} of {usage.TermCount:N0} "
            + $"{(usage.TermCount == 1 ? "word" : "words")} did not come up in {window}.";

        return usage.KeepTerms.Count == 0
            ? $"{unused} Turning this word pack off removes nothing you are using."
            : $"{unused} Turning it off keeps the words you still use working, copying them into your "
                + "own dictionary where needed.";
    }

    private void UpdateButtons()
    {
        var entries = _entryRows.Count(r => r.Selected);
        var libraries = _libraryRows.Count(r => r.Selected);

        DisableButton.IsEnabled = entries + libraries > 0;

        // A word pack is composed in memory and has no dictionary rows, so there is nothing to delete.
        // Enabling this on a word pack-only selection would promise a permanence the button cannot
        // deliver, since all it can do to a word pack is turn it off.
        DeleteButton.IsEnabled = entries > 0;
    }

    /// <summary>
    /// Keeps the header box honest. It starts checked because every row starts checked, and it goes
    /// indeterminate rather than clearing itself the moment one row is unchecked.
    /// </summary>
    private void SyncSelectAll()
    {
        if (_syncingSelectAll) return;

        var rows = _entryRows.Concat(_libraryRows).ToList();
        var selected = rows.Count(r => r.Selected);

        _syncingSelectAll = true;
        SelectAllBox.IsChecked = selected == 0 ? false : selected == rows.Count ? true : null;
        _syncingSelectAll = false;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingSelectAll) return;

        // A three-state box cycles through indeterminate when clicked; the user clicking it always
        // means "all" or "none", never "put it back the way it was".
        var value = SelectAllBox.IsChecked == true;

        _syncingSelectAll = true;
        foreach (var row in _entryRows.Concat(_libraryRows))
        {
            row.Selected = value;
        }

        SelectAllBox.IsChecked = value;
        _syncingSelectAll = false;

        UpdateButtons();
    }

    private void DisableButton_Click(object sender, RoutedEventArgs e) => Complete(delete: false);

    private void DeleteButton_Click(object sender, RoutedEventArgs e) => Complete(delete: true);

    private void Complete(bool delete)
    {
        _choice = new DictionaryCleanupChoice(
            delete,
            _entryRows.Where(r => r.Selected).Select(r => (DictionaryEntry)r.Payload).ToList(),
            _libraryRows.Where(r => r.Selected).Select(r => (LibraryUsage)r.Payload).ToList());

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _choice = null;
        Close();
    }

    /// <summary>One reviewable finding. Needs change notification so "Select everything" reaches the UI.</summary>
    private sealed class CleanupRow(string title, string detail, object payload) : INotifyPropertyChanged
    {
        private bool _selected = true;

        public string Title { get; } = title;

        public string Detail { get; } = detail;

        /// <summary>The <see cref="DictionaryEntry"/> or <see cref="LibraryUsage"/> this row stands for.</summary>
        public object Payload { get; } = payload;

        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        // UI Automation names each row of the list after ToString(), which would otherwise read out this type's name.
        public override string ToString() => Title;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>What the user asked for in the cleanup review.</summary>
/// <param name="Delete">Remove the entries outright rather than just switching them off.</param>
/// <param name="Entries">Base dictionary entries to act on, identified by their spoken form.</param>
/// <param name="Libraries">
/// Word packs to turn off, carrying the words that must be preserved. Always a turn-off: word pack
/// words have no database rows, so they are never deleted here regardless of which button was used.
/// </param>
public sealed record DictionaryCleanupChoice(
    bool Delete,
    IReadOnlyList<DictionaryEntry> Entries,
    IReadOnlyList<LibraryUsage> Libraries);
