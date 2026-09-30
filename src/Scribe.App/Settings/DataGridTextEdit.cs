using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Scribe.App.Settings;

/// <summary>Realizes and focuses a text cell's actual editor, not just the grid's remembered cell.</summary>
public static class DataGridTextEdit
{
    public static void Attach(DataGrid grid, Action failed)
    {
        grid.AddHandler(UIElement.MouseLeftButtonDownEvent,
            new MouseButtonEventHandler((_, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.None ||
                    FindCell(e.OriginalSource as DependencyObject) is not { IsEditing: false, IsReadOnly: false } cell ||
                    cell.Column is not DataGridTextColumn ||
                    DataGridRow.GetRowContainingElement(cell) is not { } row ||
                    !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(row), grid))
                {
                    return;
                }

                if (!Begin(grid, row.Item, cell.Column, selectAll: false, e))
                {
                    failed();
                }
            }), handledEventsToo: true);
    }

    public static bool Begin(DataGrid grid, object item, DataGridColumn column, bool selectAll, RoutedEventArgs? editEvent = null)
    {
        if (!Focus(grid, item, column) || !grid.BeginEdit(editEvent) ||
            column.GetCellContent(item) is not TextBox editor)
        {
            return false;
        }

        var focused = editor.Focus();
        if (selectAll)
        {
            editor.SelectAll();
        }

        return focused;
    }

    public static bool Focus(DataGrid grid, object item, DataGridColumn column)
    {
        grid.CurrentCell = new DataGridCellInfo(item, column);
        grid.ScrollIntoView(item, column);
        grid.UpdateLayout();
        var cell = FindCell(column.GetCellContent(item));
        return cell is not null && cell.Focus();
    }

    private static DataGridCell? FindCell(DependencyObject? node)
    {
        while (node is not null and not DataGridCell)
        {
            node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return node as DataGridCell;
    }
}
