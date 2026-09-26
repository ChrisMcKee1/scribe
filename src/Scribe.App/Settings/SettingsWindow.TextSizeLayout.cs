using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Scribe.App.Infrastructure;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    // The Word packs search box keeps at least this much beside the buttons, or it moves to a row of its own.
    private const double WordPacksSearchMinimum = 180;
    private const double ToolbarGap = 12;

    private void UsageMetricsCard_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateUsageMetricLayout();

    private void WordPacksToolbarGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateWordPacksToolbarLayout();

    private void HistoryToolbarGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateHistoryToolbarLayout();

    private void UpdateTextSizeAdaptiveLayouts()
    {
        ApplyListPaneWidths();
        UpdateUsageMetricLayout();

        // The toolbars decide from their buttons' measured widths, which a text size change updates only on the next layout
        // pass.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            UpdateWordPacksToolbarLayout();
            UpdateHistoryToolbarLayout();
        });
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

    // The natural width of a toolbar's buttons: a horizontal panel measures its children without a width limit, so their
    // desired sizes don't depend on whether the search box shares their row, and the decision can't flip back and forth.
    private static double NaturalWidth(Panel panel) =>
        panel.Children.OfType<FrameworkElement>().Where(child => child.Visibility != Visibility.Collapsed).Sum(child => child.DesiredSize.Width);

    // The search box moves under the buttons when both don't fit on one row, and comes back when they do; on its own row
    // the buttons may wrap too, so none is cut off at the largest text sizes or the smallest windows.
    private void UpdateWordPacksToolbarLayout()
    {
        var available = WordPacksToolbarGrid.ActualWidth;
        if (available <= 0)
        {
            return;
        }

        var twoRows = NaturalWidth(WordPacksToolbarButtons) + WordPacksSearchMinimum + ToolbarGap > available;
        Grid.SetRow(LibrarySearchBox, twoRows ? 1 : 0);
        Grid.SetColumn(LibrarySearchBox, twoRows ? 0 : 2);
        Grid.SetColumnSpan(LibrarySearchBox, twoRows ? 3 : 1);
        LibrarySearchBox.Margin = twoRows ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        Grid.SetColumnSpan(WordPacksToolbarButtons, twoRows ? 3 : 1);
        if (twoRows)
        {
            WordPacksToolbarButtons.MaxWidth = available;
        }
        else
        {
            WordPacksToolbarButtons.ClearValue(MaxWidthProperty);
        }
    }

    private void UpdateHistoryToolbarLayout()
    {
        var available = HistoryToolbarGrid.ActualWidth;
        if (available <= 0)
        {
            return;
        }

        var twoRows = HistorySearchBox.Visibility == Visibility.Visible &&
            NaturalWidth(HistoryToolbarActions) + HistorySearchBox.MinWidth + ToolbarGap > available;
        Grid.SetRow(HistoryToolbarActions, twoRows ? 1 : 0);
        Grid.SetColumn(HistoryToolbarActions, twoRows ? 0 : 1);
        Grid.SetColumnSpan(HistoryToolbarActions, twoRows ? 2 : 1);
        HistoryToolbarActions.Margin = twoRows ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        HistoryToolbarActions.HorizontalAlignment = twoRows ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Grid.SetColumnSpan(HistorySearchBox, twoRows ? 2 : 1);
        HistorySearchBox.HorizontalAlignment = twoRows ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    }
}
