using System.Windows;
using System.Windows.Automation;
using Wpf.Ui.Controls;

namespace Scribe.App.Infrastructure;

/// <summary>
/// Names the caption buttons of every WPF-UI title bar in the app. WPF-UI 4.3.0 gives them automation ids but no
/// names (TitleBar.xaml), so a screen reader found unnamed buttons at the top of every window and dialog.
/// </summary>
/// <remarks>
/// The style is built in code, on WPF-UI's own style instance, because both ways of writing it in App.xaml broke
/// the compiled app although each parses fine on its own. With <c>BasedOn="{StaticResource {x:Type ui:TitleBarButton}}"</c>
/// in the application dictionary, the reference resolved to the style itself, which WPF returns as null, so every
/// title bar button lost WPF-UI's size, template, command and <c>Focusable="False"</c>. In a dictionary of its own
/// merged after ControlsDictionary, the compiled App.xaml also gave the application dictionary an entry with no
/// value under the same key, so the first title bar button created got no style at all.
/// </remarks>
internal static class TitleBarButtonNames
{
    private static readonly (TitleBarButtonType Type, string Name)[] Names =
    [
        (TitleBarButtonType.Help, "Help"),
        (TitleBarButtonType.Minimize, "Minimize"),
        (TitleBarButtonType.Maximize, "Maximize"),

        // WPF-UI turns the maximize button into this one while the window is maximized.
        (TitleBarButtonType.Restore, "Restore"),
        (TitleBarButtonType.Close, "Close"),
    ];

    /// <summary>
    /// Replaces the application's TitleBarButton style with WPF-UI's plus the names. Call once, before any window
    /// is created.
    /// </summary>
    public static void Apply(ResourceDictionary applicationResources)
    {
        ArgumentNullException.ThrowIfNull(applicationResources);

        // App.xaml defines no style of its own for this key, so this is WPF-UI's, from the merged ControlsDictionary.
        if (applicationResources[typeof(TitleBarButton)] is not Style wpfUiStyle)
        {
            return;
        }

        var style = new Style(typeof(TitleBarButton), wpfUiStyle);
        foreach (var (type, name) in Names)
        {
            var trigger = new Trigger { Property = TitleBarButton.ButtonTypeProperty, Value = type };
            trigger.Setters.Add(new Setter(AutomationProperties.NameProperty, name));
            style.Triggers.Add(trigger);
        }

        applicationResources[typeof(TitleBarButton)] = style;
    }
}
