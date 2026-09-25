using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Wpf.Ui.Controls;

namespace Scribe.App.Infrastructure;

/// <summary>
/// Gives every WPF-UI <c>ui:Button</c> the labels <see cref="AccentContrastResources"/> chooses for a danger button and
/// for a pressed one, in place of the labels WPF-UI 4.3.0 draws there.
/// </summary>
/// <remarks>
/// <para>
/// WPF-UI's Button template (Button.xaml) sets a pressed button's Foreground to
/// <c>{Binding PressedForeground, RelativeSource={RelativeSource TemplatedParent}}</c> on the button itself. A button in a
/// window or a dialog has no templated parent, so the binding fails and the label takes Control.Foreground's default,
/// SystemColors.ControlTextBrush, black, whatever the appearance and the theme: 1.41:1 on a pressed standard button in
/// the dark theme, 2.70:1 on a pressed accent button with the accent #0E0E70. A danger button draws the ordinary button
/// text on the palette red, 3.68:1 in the dark theme.
/// </para>
/// <para>
/// A style trigger outranks a template trigger, so the triggers here draw the planned labels instead, on the template's
/// own conditions (a pressed label while pressed under the mouse, as WPF-UI's; a key held down keeps the label at rest
/// in both). Each also requires <see cref="AccentContrastFlag"/>, which Scribe turns on only in a light or dark theme:
/// in a contrast theme none of them is active, and WPF-UI's template draws exactly what it drew before. The pressed
/// triggers come after the danger one, so a pressed danger button takes its pressed label.
/// </para>
/// <para>
/// Built in code on WPF-UI's own style instance, as <see cref="TitleBarButtonNames"/> is, because an implicit style in
/// App.xaml cannot be based on the style it replaces. Every Scribe style for <c>ui:Button</c> is based on the
/// application's, so each one keeps these triggers.
/// </para>
/// </remarks>
internal static class ButtonLabelContrast
{
    /// <summary>
    /// Replaces the application's <c>ui:Button</c> style with WPF-UI's plus these triggers. Call once, before any window
    /// is created.
    /// </summary>
    public static void Apply(ResourceDictionary applicationResources)
    {
        ArgumentNullException.ThrowIfNull(applicationResources);

        // App.xaml defines no style of its own for this key, so this is WPF-UI's, from the merged ControlsDictionary.
        if (applicationResources[typeof(Wpf.Ui.Controls.Button)] is not Style wpfUiStyle)
        {
            return;
        }

        var style = new Style(typeof(Wpf.Ui.Controls.Button), wpfUiStyle);
        style.Setters.Add(new Setter(AccentContrastFlag.AppliesProperty, new DynamicResourceExtension(AccentContrastKeys.Applies)));
        style.Triggers.Add(AtRest(ControlAppearance.Danger, AccentContrastKeys.DangerButtonForeground));
        style.Triggers.Add(Pressed(ControlAppearance.Primary, AccentContrastKeys.PrimaryPressedForeground));
        style.Triggers.Add(Pressed(ControlAppearance.Secondary, AccentContrastKeys.SecondaryPressedForeground));
        style.Triggers.Add(Pressed(ControlAppearance.Transparent, AccentContrastKeys.SecondaryPressedForeground));
        style.Triggers.Add(Pressed(ControlAppearance.Danger, AccentContrastKeys.DangerPressedForeground));
        applicationResources[typeof(Wpf.Ui.Controls.Button)] = style;
    }

    // At rest and hovered; a disabled button keeps WPF-UI's disabled colours.
    private static MultiTrigger AtRest(ControlAppearance appearance, string key)
    {
        var trigger = Applied(appearance);
        trigger.Conditions.Add(new Condition(UIElement.IsEnabledProperty, true));
        trigger.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(key)));
        return trigger;
    }

    // WPF-UI's pressed MultiTrigger holds on IsMouseOver and IsPressed together; these hold on the same two.
    private static MultiTrigger Pressed(ControlAppearance appearance, string key)
    {
        var trigger = Applied(appearance);
        trigger.Conditions.Add(new Condition(UIElement.IsMouseOverProperty, true));
        trigger.Conditions.Add(new Condition(ButtonBase.IsPressedProperty, true));
        trigger.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(key)));
        return trigger;
    }

    private static MultiTrigger Applied(ControlAppearance appearance)
    {
        var trigger = new MultiTrigger();
        trigger.Conditions.Add(new Condition(AccentContrastFlag.AppliesProperty, true));
        trigger.Conditions.Add(new Condition(Wpf.Ui.Controls.Button.AppearanceProperty, appearance));
        return trigger;
    }
}
