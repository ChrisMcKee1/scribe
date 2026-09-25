using Scribe.Core.Models;

namespace Scribe.Core.Hotkeys;

/// <summary>
/// The mouse buttons a hotkey can use, stored like keys: by their virtual-key codes, as the primary or second input of a
/// <see cref="HotkeyBinding"/>, so a binding needs no new field and an older build reads it unchanged. Only the middle
/// button and the two side buttons (Back and Forward, XBUTTON1 and XBUTTON2) can be bound: the left and right buttons
/// are every app's clicks. Windows reports no button beyond the fifth by itself; a mouse's own software maps its extra
/// buttons to keys (F13 to F24 are the usual choice), and those are bound as keys.
/// </summary>
public static class MouseButtons
{
    /// <summary>VK_LBUTTON. Never bindable.</summary>
    public const uint Left = 0x01;

    /// <summary>VK_RBUTTON. Never bindable.</summary>
    public const uint Right = 0x02;

    /// <summary>VK_MBUTTON, the middle button (usually the wheel pressed down).</summary>
    public const uint Middle = 0x04;

    /// <summary>VK_XBUTTON1, the side button browsers use for Back (button 4).</summary>
    public const uint Back = 0x05;

    /// <summary>VK_XBUTTON2, the side button browsers use for Forward (button 5).</summary>
    public const uint Forward = 0x06;

    /// <summary>Whether a hotkey can use this code as a mouse button: the middle, Back or Forward button.</summary>
    public static bool IsBindable(uint virtualKey) => virtualKey is Middle or Back or Forward;

    /// <summary>
    /// Whether the code is any of the five mouse buttons' virtual-key codes. The keyboard hook passes a keyboard event
    /// carrying one of these straight through (only injected input can): the mouse hook alone reports the buttons.
    /// </summary>
    public static bool IsMouseButton(uint virtualKey) => virtualKey is Left or Right or Middle or Back or Forward;

    /// <summary>Whether the binding presses a mouse button, alone or as either input of a chord.</summary>
    public static bool Uses(HotkeyBinding? binding) =>
        binding is not null &&
        (IsBindable(binding.VirtualKey) || (binding.SecondaryVirtualKey is { } second && IsBindable(second)));

    /// <summary>
    /// What kinds of input a binding presses, for the log: "key", "mouse", or "key+mouse" when it needs both (a chord of
    /// a key and a button, or a button with a modifier). A shape, never a name.
    /// </summary>
    public static string InputKind(HotkeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var buttons = IsBindable(binding.VirtualKey) ? 1 : 0;
        var keys = buttons == 0 ? 1 : 0;
        if (binding.SecondaryVirtualKey is { } second)
        {
            if (IsBindable(second))
            {
                buttons++;
            }
            else
            {
                keys++;
            }
        }

        if (binding.Modifiers != KeyModifiers.None)
        {
            keys++;
        }

        return buttons == 0 ? "key" : keys == 0 ? "mouse" : "key+mouse";
    }
}
