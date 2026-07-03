using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.App.History;

/// <summary>
/// A read-only viewer over the dictation history. Lists the most recent captures (newest first)
/// with their target app and timing, and lets the user copy a transcript back to the clipboard,
/// delete a single entry, or clear everything. All audio and text stay local — this only reads
/// the SQLite history already written by the dictation loop.
/// </summary>
public partial class HistoryWindow : Wpf.Ui.Controls.FluentWindow
{
    private const int MaxRows = 200;

    private readonly IHistoryRepository _history;
    private readonly ObservableCollection<HistoryRow> _rows = new();

    public HistoryWindow(IHistoryRepository history)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));

        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        InitializeComponent();

        HistoryGrid.ItemsSource = _rows;
        Load();
    }

    private void Load()
    {
        _rows.Clear();
        foreach (var entry in _history.GetRecent(MaxRows))
        {
            _rows.Add(HistoryRow.From(entry));
        }

        var hasRows = _rows.Count > 0;
        EmptyHint.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.IsEnabled = hasRows;
    }

    private HistoryRow? Selected => HistoryGrid.SelectedItem as HistoryRow;

    private void CopyButton_Click(object sender, RoutedEventArgs e) => CopySelected();

    private void HistoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => CopySelected();

    private void CopySelected()
    {
        if (Selected is not { } row)
        {
            return;
        }

        try
        {
            Clipboard.SetText(row.Text);
        }
        catch (Exception)
        {
            // The clipboard can be transiently locked by another app; copying history is best-effort.
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            return;
        }

        _history.Delete(row.Id);
        Load();
    }

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Clear history",
            Content = "Delete all dictation history and any stored audio? This cannot be undone.",
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            Owner = this,
        };

        if (await dialog.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            _history.Clear();
            Load();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>A display projection of a <see cref="HistoryEntry"/> for the grid.</summary>
    private sealed record HistoryRow(long Id, string When, string Text, string App, string Audio, string Decode)
    {
        public static HistoryRow From(HistoryEntry entry) => new(
            entry.Id,
            entry.TimestampUtc.ToLocalTime().ToString("MMM d, h:mm tt"),
            entry.Text,
            string.IsNullOrWhiteSpace(entry.TargetApp) ? "—" : entry.TargetApp!,
            $"{entry.AudioMilliseconds / 1000.0:0.0} s",
            $"{entry.DecodeMilliseconds} ms");
    }
}
