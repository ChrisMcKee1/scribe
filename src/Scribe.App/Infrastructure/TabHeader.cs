using System.Windows;
using Wpf.Ui.Controls;

namespace Scribe.App.Infrastructure;

/// <summary>
/// What a Settings tab shows beside its name: an icon, and a short summary under the name that the page keeps current
/// (the Dictionary page's "31 words" and "11 of 11 on"). The tab's Header stays its plain name, so UI Automation, Find a
/// setting and the tests read the name exactly as before.
/// </summary>
internal static class TabHeader
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(SymbolRegular), typeof(TabHeader), new FrameworkPropertyMetadata(SymbolRegular.Empty));

    public static readonly DependencyProperty SummaryProperty = DependencyProperty.RegisterAttached(
        "Summary", typeof(string), typeof(TabHeader), new FrameworkPropertyMetadata(string.Empty));

    public static SymbolRegular GetIcon(DependencyObject element) => (SymbolRegular)element.GetValue(IconProperty);

    public static void SetIcon(DependencyObject element, SymbolRegular value) => element.SetValue(IconProperty, value);

    public static string GetSummary(DependencyObject element) => (string)element.GetValue(SummaryProperty);

    public static void SetSummary(DependencyObject element, string value) => element.SetValue(SummaryProperty, value);
}
