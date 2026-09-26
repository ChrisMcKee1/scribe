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

    private void YourWordsToolbarGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateYourWordsToolbarLayout();

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
            UpdateYourWordsToolbarLayout();
            UpdateDictionaryColumnMinimums();
        });
    }

    // Your words keeps a usable grid on short windows at large text: the tab scrolls as a whole, and the grid's row gets
    // the height the viewport leaves after the tab's other rows, never less than a header and four rows at the current
    // text size, as an explicit height (a star row in a ScrollViewer would measure the grid without a bound and realize
    // every row; the XAML's height only bounds the first layout). At normal sizes the fitted height is exactly what the
    // star row had, so nothing scrolls.
    private void YourWordsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The grid's own ScrollViewer raises ScrollChanged too, and it bubbles through this one.
        if (ReferenceEquals(e.OriginalSource, YourWordsScroll) && (e.ViewportHeightChange != 0 || e.ExtentHeightChange != 0))
        {
            FitYourWordsGridToScroll();
        }
    }

    private void FitYourWordsGridToScroll()
    {
        var viewport = YourWordsScroll.ViewportHeight;
        if (viewport <= 0)
        {
            return;
        }

        // Exactly what the star row had (no rounding), so the tab looks the same as before whenever it fits.
        var otherRows = YourWordsContent.RowDefinitions.Where(row => !ReferenceEquals(row, YourWordsGridRow)).Sum(row => row.ActualHeight);
        var height = Math.Max(180 * TextScaleService.CurrentFactor, viewport - otherRows);
        if (!YourWordsGridRow.Height.IsAbsolute || Math.Abs(YourWordsGridRow.Height.Value - height) >= 0.5)
        {
            YourWordsGridRow.Height = new GridLength(height);
        }
    }

    // Above 100% the search box moves under the buttons when both don't fit on one row, and the buttons wrap across the
    // whole width; at 100% the row stays as it always was.
    private void UpdateYourWordsToolbarLayout()
    {
        var available = YourWordsToolbarGrid.ActualWidth;
        if (available <= 0)
        {
            return;
        }

        var twoRows = TextScaleService.CurrentFactor > 1 &&
            NaturalWidth(YourWordsToolbarButtons) + DictionarySearchBox.MinWidth + ToolbarGap > available;
        Grid.SetRow(DictionarySearchBox, twoRows ? 1 : 0);
        Grid.SetColumn(DictionarySearchBox, twoRows ? 0 : 1);
        Grid.SetColumnSpan(DictionarySearchBox, twoRows ? 2 : 1);
        Grid.SetColumnSpan(YourWordsToolbarButtons, twoRows ? 2 : 1);

        // Back on the button row the box takes the XAML's width, maximum and alignment again.
        DictionarySearchBox.Width = twoRows ? double.NaN : 180;
        DictionarySearchBox.MaxWidth = twoRows ? double.PositiveInfinity : 260;
        DictionarySearchBox.HorizontalAlignment = twoRows ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        DictionarySearchBox.Margin = twoRows ? new Thickness(0, 8, 0, 0) : new Thickness(0);
    }

    // Above 100% each of Your words' text columns keeps room for a few words, and the grid scrolls sideways instead of
    // cutting every word to a few letters. The other columns get their natural width as a minimum too: with a star column
    // present the DataGrid otherwise takes the text columns' minimums out of the other columns, down to 20 DIP. A new text
    // size first clears every minimum and lets the Auto columns measure again (a DataGrid's Auto column never shrinks by
    // itself), then sets them after that layout pass. At 100% no column has a minimum, as in the XAML.
    private double _dictionaryColumnsFactor = 1;

    private void UpdateDictionaryColumnMinimums()
    {
        var factor = TextScaleService.CurrentFactor;
        if (!_dictionaryColumnsFactor.Equals(factor))
        {
            _dictionaryColumnsFactor = factor;
            foreach (var column in DictionaryGrid.Columns)
            {
                column.ClearValue(DataGridColumn.MinWidthProperty);
                if (column.Width.IsAuto)
                {
                    column.Width = DataGridLength.Auto;
                }
            }

            if (factor > 1)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateDictionaryColumnMinimums);
            }

            return;
        }

        if (factor <= 1)
        {
            return;
        }

        var textColumns = new DataGridColumn[] { DictionarySpokenColumn, DictionaryWrittenColumn };
        var others = DictionaryGrid.Columns.Where(column => !textColumns.Contains(column) && column.Visibility == Visibility.Visible).ToList();

        // Until the grid has measured its columns (its page not shown yet) there are no natural widths, and minimums on the
        // text columns alone would squeeze the others; the page's first layout calls this again.
        if (others.Any(column => !(column.Width.DesiredValue is > 0 and < double.PositiveInfinity)))
        {
            return;
        }

        foreach (var column in others)
        {
            column.MinWidth = column.Width.DesiredValue;
        }

        foreach (var column in textColumns)
        {
            column.MinWidth = 120 * factor;
        }
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
