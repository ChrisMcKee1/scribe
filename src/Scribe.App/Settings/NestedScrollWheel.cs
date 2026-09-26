using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Scribe.App.Settings;

/// <summary>
/// Hands the mouse wheel to the scrolling page around a list that has nothing of its own to scroll.
/// </summary>
/// <remarks>
/// A list sized to its rows inside a scrolling page still has a <see cref="ScrollViewer"/> in its template, and WPF's
/// <see cref="ScrollViewer"/> marks every wheel event handled, whether or not it could move. With the pointer over such
/// a list the page stopped scrolling, which is the nested-scrolling trap the settings plan's page shells rule out
/// (RD-07). While the list can scroll itself it keeps the wheel; once it cannot, the wheel goes to the page.
/// </remarks>
public static class NestedScrollWheel
{
    public static readonly DependencyProperty ForwardToPageProperty = DependencyProperty.RegisterAttached(
        "ForwardToPage",
        typeof(bool),
        typeof(NestedScrollWheel),
        new PropertyMetadata(false, OnForwardToPageChanged));

    public static bool GetForwardToPage(DependencyObject element) => (bool)element.GetValue(ForwardToPageProperty);

    public static void SetForwardToPage(DependencyObject element, bool value) => element.SetValue(ForwardToPageProperty, value);

    private static void OnForwardToPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (e.NewValue is true)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement element)
        {
            return;
        }

        if (FindDescendant<ScrollViewer>(element) is { } own && CanMove(own, e.Delta))
        {
            return;
        }

        if (ParentElement(element) is not { } parent)
        {
            return;
        }

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = element,
        });
    }

    // A positive delta scrolls up. At either end the list has no room left in that direction, so the page takes over.
    private static bool CanMove(ScrollViewer viewer, int delta) =>
        viewer.ScrollableHeight > 0 &&
        (delta > 0 ? viewer.VerticalOffset > 0 : viewer.VerticalOffset < viewer.ScrollableHeight);

    private static UIElement? ParentElement(DependencyObject element)
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement parent)
            {
                return parent;
            }
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
