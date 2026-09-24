using System.Windows;
using System.Windows.Media;

namespace Scribe.App.Infrastructure;

/// <summary>
/// Finds the buttons WPF-UI 4.3.0's MessageBox builds from its template, which it does not expose. Each is a
/// Button whose CommandParameter names it (MessageBox.xaml in WPF-UI): the primary button is the one marked
/// IsDefault, and the close button is IsCancel, which is how Escape dismisses the dialog.
/// </summary>
internal static class MessageBoxTemplate
{
    /// <returns>The button, or null when the template is not applied or no longer has it.</returns>
    public static System.Windows.Controls.Button? FindButton(DependencyObject node, Wpf.Ui.Controls.MessageBoxButton which)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
        {
            var child = VisualTreeHelper.GetChild(node, index);
            if (child is System.Windows.Controls.Button button && Equals(button.CommandParameter, which))
            {
                return button;
            }

            if (FindButton(child, which) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
