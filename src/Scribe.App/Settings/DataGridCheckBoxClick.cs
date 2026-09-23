using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Scribe.App.Settings;

/// <summary>
/// Makes one click toggle a <see cref="DataGridCheckBoxColumn"/> check box, through the grid's own edit.
/// </summary>
/// <remarks>
/// <para>
/// A stock check box cell starts its edit, the only thing that toggles its box, on a click that lands
/// while the cell already has keyboard focus and is selected; the first click only selects. Toggling
/// the box outside that edit is no better: the row's edit transaction is what a sorted view moves an
/// item on when it commits, so a row ticked that way kept its old place in a view sorted on the column.
/// </para>
/// <para>
/// So a plain press on a check box cell that is not editing makes the cell current and selected, then
/// begins the edit with that same press, and the column toggles the box once while preparing the edit
/// (it does so for a left press over the box). Handling the preview means the mouse device raises no
/// press for anything else to act on. Ctrl and Shift keep their stock selection gestures, and a cell
/// already editing leaves its box to handle its own clicks. The display check box must not be hit
/// testable, or a press this skips would reach it and toggle it outside the edit.
/// </para>
/// </remarks>
public static class DataGridCheckBoxClick
{
    public static void Attach(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        grid.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The selection made below is a whole row, which is all these grids use.
        if (sender is not DataGrid grid ||
            grid.SelectionUnit != DataGridSelectionUnit.FullRow ||
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0 ||
            FindCell(e.OriginalSource as DependencyObject) is not { IsEditing: false, IsReadOnly: false } cell ||
            cell.Column is not DataGridCheckBoxColumn ||
            DataGridRow.GetRowContainingElement(cell) is not { } row ||
            !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(row), grid))
        {
            return;
        }

        // BeginEdit edits the current cell, and setting it commits an edit pending elsewhere, just as
        // moving focus there would. Focus follows so Space and the arrow keys carry on from this cell.
        var item = row.Item;
        grid.CurrentCell = new DataGridCellInfo(item, cell.Column);
        if (!cell.IsKeyboardFocusWithin)
        {
            cell.Focus();
        }

        if (grid.SelectedItems.Count != 1 || !ReferenceEquals(grid.SelectedItem, item))
        {
            grid.SelectedItem = item;
        }

        if (grid.BeginEdit(e))
        {
            e.Handled = true;
        }
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
