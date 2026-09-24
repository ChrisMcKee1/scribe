using System.Globalization;

namespace Scribe.Core.Hotkeys;

/// <summary>
/// The name Scribe shows for a key, taken from its virtual-key code and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Settings used to name a key with WPF's <c>Key.ToString()</c>, and that enum gives several keys two names for one
/// value: <c>PageUp</c> and <c>Prior</c>, <c>PageDown</c> and <c>Next</c>, <c>CapsLock</c> and <c>Capital</c>,
/// <c>PrintScreen</c> and <c>Snapshot</c>, <c>Enter</c> and <c>Return</c>, and most of the <c>Oem</c> keys. .NET
/// promises only that one of the names comes back, not which, so a user who bound Page Down could read "Next". The
/// low-level hook matches bindings by virtual-key code alone, so the name never changed what a key does; it only
/// changed what Settings said.
/// </para>
/// <para>
/// The table covers the keys whose meaning does not depend on the keyboard layout. The punctuation keys
/// (<c>VK_OEM_*</c>) and the IME keys type something different on each layout, so they are named from the layout
/// when the shell can ask it (<see cref="FromMappedCharacter"/>) and otherwise fall back to what was stored when the
/// key was bound.
/// </para>
/// </remarks>
public static class KeyNames
{
    private static readonly Dictionary<uint, string> Names = Build();

    /// <summary>The canonical name of a key whose meaning is the same on every layout, or null for any other key.</summary>
    public static string? Of(uint virtualKey) => Names.GetValueOrDefault(virtualKey);

    /// <summary>The virtual-key codes the table names, for tests.</summary>
    internal static IReadOnlyCollection<uint> Known => Names.Keys;

    /// <summary>
    /// A key name from what <c>MapVirtualKeyW(virtualKey, MAPVK_VK_TO_CHAR)</c> returned for the current layout: the
    /// unshifted character in the low word, with the top bit set for a dead key, and 0 when the key types nothing.
    /// Null for no character, a control character or white space. A letter is upper-cased, as a key cap shows it, and
    /// a plus sign is spelled out, because "+" is what joins the keys of a chord.
    /// </summary>
    public static string? FromMappedCharacter(uint mapped)
    {
        var character = (char)(mapped & 0xFFFF);
        if (character == '\0' || char.IsControl(character) || char.IsWhiteSpace(character) || char.IsSurrogate(character))
        {
            return null;
        }

        if (character == '+')
        {
            return "Plus";
        }

        return char.IsLetter(character)
            ? char.ToUpper(character, CultureInfo.InvariantCulture).ToString()
            : character.ToString();
    }

    // Values from winuser.h (https://learn.microsoft.com/windows/win32/inputdev/virtual-key-codes). Numeric-keypad
    // operators are spelled out rather than drawn ("Num Plus", not "Num +"), for the same reason as the plus sign above.
    private static Dictionary<uint, string> Build()
    {
        var names = new Dictionary<uint, string>
        {
            [0x03] = "Break", // VK_CANCEL: Ctrl+Pause
            [0x08] = "Backspace",
            [0x09] = "Tab",
            [0x0C] = "Clear", // VK_CLEAR: numeric keypad 5 with Num Lock off
            [0x0D] = "Enter",
            [0x10] = "Shift",
            [0x11] = "Ctrl",
            [0x12] = "Alt",
            [0x13] = "Pause",
            [0x14] = "Caps Lock",
            [0x1B] = "Esc",
            [0x20] = "Space",
            [0x21] = "Page Up", // VK_PRIOR
            [0x22] = "Page Down", // VK_NEXT
            [0x23] = "End",
            [0x24] = "Home",
            [0x25] = "Left Arrow",
            [0x26] = "Up Arrow",
            [0x27] = "Right Arrow",
            [0x28] = "Down Arrow",
            [0x29] = "Select",
            [0x2A] = "Print",
            [0x2B] = "Execute",
            [0x2C] = "Print Screen", // VK_SNAPSHOT
            [0x2D] = "Insert",
            [0x2E] = "Delete",
            [0x2F] = "Help",
            [0x5B] = "Left Win",
            [0x5C] = "Right Win",
            [0x5D] = "Menu", // VK_APPS, the application key
            [0x5F] = "Sleep",
            [0x6A] = "Num Multiply",
            [0x6B] = "Num Plus",
            [0x6C] = "Num Separator",
            [0x6D] = "Num Minus",
            [0x6E] = "Num Decimal",
            [0x6F] = "Num Divide",
            [0x90] = "Num Lock",
            [0x91] = "Scroll Lock",
            [0xA0] = "Left Shift",
            [0xA1] = "Right Shift",
            [0xA2] = "Left Ctrl",
            [0xA3] = "Right Ctrl",
            [0xA4] = "Left Alt",
            [0xA5] = "Right Alt",
            [0xA6] = "Browser Back",
            [0xA7] = "Browser Forward",
            [0xA8] = "Browser Refresh",
            [0xA9] = "Browser Stop",
            [0xAA] = "Browser Search",
            [0xAB] = "Browser Favorites",
            [0xAC] = "Browser Home",
            [0xAD] = "Volume Mute",
            [0xAE] = "Volume Down",
            [0xAF] = "Volume Up",
            [0xB0] = "Next Track",
            [0xB1] = "Previous Track",
            [0xB2] = "Stop Media",
            [0xB3] = "Play/Pause",
            [0xB4] = "Mail",
            [0xB5] = "Select Media",
            [0xB6] = "Launch App 1",
            [0xB7] = "Launch App 2",
        };

        for (uint digit = 0; digit <= 9; digit++)
        {
            names[0x30 + digit] = digit.ToString(CultureInfo.InvariantCulture);
            names[0x60 + digit] = "Num " + digit.ToString(CultureInfo.InvariantCulture);
        }

        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            names[letter] = letter.ToString();
        }

        for (uint function = 1; function <= 24; function++)
        {
            names[0x6F + function] = "F" + function.ToString(CultureInfo.InvariantCulture);
        }

        return names;
    }
}
