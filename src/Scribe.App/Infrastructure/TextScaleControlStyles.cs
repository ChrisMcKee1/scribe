using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Scribe.App.Infrastructure;

// WPF-UI 4.3.0 gives the DataGrid a static font size (its style reads DefaultDataGridFontSize through StaticResource) and
// AccessText, which draws every button label, a literal 14, so neither follows the text scale keys. One derived style for
// each goes into the application resources once, before any window exists, taking the size from a dynamic resource the
// text scale service writes: grids and labels in every window then follow a live text size change both ways, and are the
// same 14 as before at 100%. Cells and column headers inherit the grid's size (WPF-UI's styles for them set none), so the
// Settings window's KeyboardCell and header styles stay exactly as they are.
internal static class TextScaleControlStyles
{
    private static bool _installed;

    public static void EnsureInstalled(ResourceDictionary applicationResources)
    {
        ArgumentNullException.ThrowIfNull(applicationResources);
        if (_installed)
        {
            return;
        }

        _installed = true;
        Derive<DataGrid>(applicationResources, "DefaultDataGridFontSize");
        Derive<AccessText>(applicationResources, "ControlContentThemeFontSize");
    }

    private static void Derive<T>(ResourceDictionary resources, string fontSizeKey)
        where T : FrameworkElement
    {
        var key = typeof(T);
        var style = new Style(key, resources[key] as Style);
        style.Setters.Add(new Setter(TextElement.FontSizeProperty, new DynamicResourceExtension(fontSizeKey)));
        resources[key] = style;
    }
}
