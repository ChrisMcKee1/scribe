using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Scribe.App.Settings;

/// <summary>
/// Makes one click on a <see cref="DataGridCheckBoxColumn"/> check box toggle it, through the grid's own edit.
/// </summary>
/// <remarks>
/// <para>
/// A stock check box cell starts its edit, the only thing that toggles its box, on a click that lands
/// while the cell already has keyboard focus and is selected; a first click only focuses and selects.
/// This handler runs after that stock handling, so the grid has already focused the cell, selected the
/// row and recorded its range anchor exactly as for any other click. It must be registered for handled
/// events because the cell marks the press handled. For a plain press on the box of a cell that is not
/// editing, it then begins the edit with that same press, and the column toggles the box once while
/// preparing it, inside the row's edit transaction, so a sorted view places the row when it commits.
/// </para>
/// <para>
/// Presses beside the box and Ctrl or Shift presses are left to the stock handling, exactly as before.
/// The display check box must not take hit tests, or its own button logic would take the press first.
/// </para>
/// </remarks>
public static class DataGridCheckBoxClick
{
    public static void Attach(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        grid.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnMouseLeftButtonDown), handledEventsToo: true);
    }

    /// <summary>
    /// Whether a press falls on the box: inside the area its template draws, which is also where the
    /// column's own hit test finds the box once it is editing and takes the mouse. The display box takes
    /// no hit tests, so this is measured rather than hit tested.
    /// </summary>
    /// <param name="positionRelativeTo">
    /// The press position relative to a given element, as <see cref="MouseEventArgs.GetPosition"/> reports it.
    /// </param>
    public static bool IsOverBox(CheckBox box, Func<IInputElement, Point> positionRelativeTo)
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(positionRelativeTo);
        if (VisualTreeHelper.GetChildrenCount(box) == 0 || VisualTreeHelper.GetChild(box, 0) is not FrameworkElement drawn)
        {
            return false;
        }

        var point = positionRelativeTo(drawn);
        return point.X >= 0 && point.Y >= 0 && point.X < drawn.ActualWidth && point.Y < drawn.ActualHeight;
    }

    private static void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid ||
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0 ||
            FindCell(e.OriginalSource as DependencyObject) is not { IsEditing: false, IsReadOnly: false } cell ||
            cell.Column is not DataGridCheckBoxColumn ||
            cell.Content is not CheckBox box ||
            DataGridRow.GetRowContainingElement(cell) is not { } row ||
            !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(row), grid) ||
            !IsOverBox(box, e.GetPosition))
        {
            return;
        }

        // Focusing the cell made it current. Should focus not have landed, BeginEdit would edit whichever
        // cell was current instead, so point it at this one.
        if (!ReferenceEquals(grid.CurrentCell.Item, row.Item) || grid.CurrentCell.Column != cell.Column)
        {
            grid.CurrentCell = new DataGridCellInfo(row.Item, cell.Column);
        }

        grid.BeginEdit(e);
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
