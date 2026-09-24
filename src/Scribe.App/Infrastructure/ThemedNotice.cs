using System.Windows;
using System.Windows.Media;

namespace Scribe.App.Infrastructure;

/// <summary>
/// The one-button Fluent notice ("OK") that every Scribe window shows for a message it only has to tell.
/// </summary>
/// <remarks>
/// WPF-UI 4.3.0's MessageBox hides a disabled close button only by giving its column no width (the
/// IsCloseButtonEnabled trigger in MessageBox.xaml); the button itself stays visible and enabled. A notice
/// therefore had a second Tab stop nobody could see, where focus seemed to vanish. That button is also the
/// dialog's Cancel button, the only thing that lets Escape dismiss a notice, so it stays and only leaves
/// the Tab order.
/// </remarks>
internal static class ThemedNotice
{
    public static Wpf.Ui.Controls.MessageBox Create(string title, string content)
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "OK",
            IsSecondaryButtonEnabled = false,
            IsCloseButtonEnabled = false,
        };
        dialog.Loaded += (_, _) => TakeCloseButtonOutOfTabOrder(dialog);
        return dialog;
    }

    private static bool TakeCloseButtonOutOfTabOrder(DependencyObject node)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
        {
            var child = VisualTreeHelper.GetChild(node, index);
            if (child is System.Windows.Controls.Button { CommandParameter: Wpf.Ui.Controls.MessageBoxButton.Close } close)
            {
                close.IsTabStop = false;
                return true;
            }

            if (TakeCloseButtonOutOfTabOrder(child))
            {
                return true;
            }
        }

        return false;
    }
}
