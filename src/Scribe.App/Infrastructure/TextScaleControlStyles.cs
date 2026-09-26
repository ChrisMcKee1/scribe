using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace Scribe.App.Infrastructure;

internal static class TextScaleControlStyles
{
    public static void Apply(ResourceDictionary applicationResources)
    {
        ArgumentNullException.ThrowIfNull(applicationResources);

        Replace<DataGrid>(applicationResources);
        Replace<DataGridCell>(applicationResources);
        Replace<DataGridColumnHeader>(applicationResources);
        Replace<AccessText>(applicationResources);
    }

    private static void Replace<T>(ResourceDictionary resources)
        where T : FrameworkElement
    {
        var key = typeof(T);
        var style = new Style(key, resources[key] as Style);
        style.Setters.Add(new Setter(Control.FontSizeProperty, new DynamicResourceExtension("ScribeFontBody")));
        resources[key] = style;
    }
}
