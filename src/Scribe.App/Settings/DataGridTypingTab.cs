using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

/// <summary>
/// Lets Tab and Shift+Tab move between the typed cells of one row while the user types in a grid, so a new
/// Dictionary entry is filled in left to right, spoken form then replacement.
/// </summary>
/// <remarks>
/// Every settings grid is a single Tab stop (<c>KeyboardNavigation.TabNavigation</c> Once, in the window's grid
/// style), which is what keeps Tab from walking hundreds of cells. Without this, the same rule would carry Tab out
/// of the grid from a half-typed entry. The cell being typed in is committed first, as the grid's own navigation
/// does, and past the last typed cell in the direction of the key focus leaves the grid from that cell. Where the
/// key goes is decided by <see cref="GridTypingTab"/>.
/// </remarks>
public static class DataGridTypingTab
{
    public static void Attach(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        grid.PreviewKeyDown += OnPreviewKeyDown;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab ||
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0 ||
            sender is not DataGrid grid ||
            e.OriginalSource is not TextBox ||
            FindCell(e.OriginalSource as DependencyObject) is not { IsEditing: true, Column: DataGridTextColumn } cell ||
            DataGridRow.GetRowContainingElement(cell) is not { } row ||
            !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(row), grid))
        {
            return;
        }

        var columns = grid.Columns.OrderBy(column => column.DisplayIndex).ToList();
        var typeable = columns
            .Select(column => column is DataGridTextColumn && column.Visibility == Visibility.Visible && !column.IsReadOnly)
            .ToList();
        var backwards = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var next = GridTypingTab.Next(typeable, columns.IndexOf(cell.Column), backwards);

        e.Handled = true;
        if (!grid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true))
        {
            // The cell did not take the value, so it stays open for correction, as it would for Enter.
            return;
        }

        if (next is { } index)
        {
            grid.CurrentCell = new DataGridCellInfo(row.Item, columns[index]);
            grid.BeginEdit();
            return;
        }

        cell.MoveFocus(new TraversalRequest(backwards ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
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
