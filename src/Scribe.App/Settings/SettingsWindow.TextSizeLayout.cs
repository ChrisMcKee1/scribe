using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Scribe.App.Infrastructure;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private void UsageMetricsCard_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateUsageMetricLayout();

    private void WordPacksToolbarGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateWordPacksToolbarLayout();

    private void HistoryToolbarGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateHistoryToolbarLayout();

    private void UpdateTextSizeAdaptiveLayouts()
    {
        ApplyLargeTextToRealizedControls();
        ApplyDictionaryGridTextColumnMinimums();
        ApplyListPaneWidths();
        UpdateWordPacksToolbarLayout();
        UpdateHistoryToolbarLayout();
        UpdateUsageMetricLayout();
    }

    private void ApplyLargeTextToRealizedControls()
    {
        if (TextScaleService.CurrentFactor <= 1 || Content is not DependencyObject root)
        {
            return;
        }

        var fontSize = TryFindResource("ScribeFontBody") is double body ? body : 14;
        foreach (var grid in FindVisualDescendants<DataGrid>(root))
        {
            grid.FontSize = fontSize;
        }

        foreach (var header in FindVisualDescendants<DataGridColumnHeader>(root))
        {
            header.FontSize = fontSize;
        }

        foreach (var accessText in FindVisualDescendants<AccessText>(root))
        {
            accessText.FontSize = fontSize;
        }
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var nested in FindVisualDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private void ApplyDictionaryGridTextColumnMinimums()
    {
        if (TextScaleService.CurrentFactor <= 1)
        {
            return;
        }

        var minimum = 120 * TextScaleService.CurrentFactor;
        DictionarySpokenColumn.MinWidth = minimum;
        DictionaryWrittenColumn.MinWidth = minimum;
    }

    private void ApplyListPaneWidths()
    {
        ApplyListPaneWidth(SnippetListColumn, SectionVoiceSnippets);
        ApplyListPaneWidth(ProfileListColumn, ProfileMainGrid);
    }

    private static void ApplyListPaneWidth(ColumnDefinition column, FrameworkElement scope)
    {
        if (scope.ActualWidth <= 0)
        {
            return;
        }

        var width = ListPaneLayoutPlanner.ListWidth(new ListPaneLayoutInput(scope.ActualWidth, TextScaleService.CurrentFactor));
        column.Width = new GridLength(width);
    }

    private void UpdateWordPacksToolbarLayout()
    {
        if (TextScaleService.CurrentFactor <= 1 || WordPacksToolbarGrid.ActualWidth <= 0)
        {
            return;
        }

        LibraryImportButton.Content = "Import...";
        Grid.SetRow(LibrarySearchBox, 1);
        Grid.SetColumn(LibrarySearchBox, 0);
        Grid.SetColumnSpan(LibrarySearchBox, 3);
        LibrarySearchBox.Margin = new Thickness(0, 8, 0, 0);
    }

    private void UpdateHistoryToolbarLayout()
    {
        if (TextScaleService.CurrentFactor <= 1 || HistoryToolbarGrid.ActualWidth <= 0)
        {
            return;
        }

        if (HistoryToolbarGrid.RowDefinitions.Count == 0)
        {
            HistoryToolbarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            HistoryToolbarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        Grid.SetRow(HistoryToolbarActions, 1);
        Grid.SetColumn(HistoryToolbarActions, 0);
        Grid.SetColumnSpan(HistoryToolbarActions, 2);
        HistoryToolbarActions.Margin = new Thickness(0, 8, 0, 0);
        HistoryToolbarActions.HorizontalAlignment = HorizontalAlignment.Left;

        Grid.SetColumnSpan(HistorySearchBox, 2);
        HistorySearchBox.HorizontalAlignment = HorizontalAlignment.Stretch;
    }
}
