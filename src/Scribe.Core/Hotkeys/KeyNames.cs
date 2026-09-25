using System.Globalization;

namespace Scribe.Core.Hotkeys;

/// <summary>
/// The name Scribe shows for a key, or for a mouse button a hotkey uses, taken from its virtual-key code and nothing
/// else.
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
/// when the shell can ask it: by the character the key types (<see cref="FromMappedCharacter"/>), else by the key name
/// the layout gives it (<see cref="FromLayoutKeyName"/>). Otherwise they fall back to what was stored when the key was
/// bound, unless that is one of WPF's names shared by two different keys (<see cref="IsAmbiguousWpfName"/>), and last
/// to the code itself.
/// </para>
/// </remarks>
public static class KeyNames
{
    private static readonly Dictionary<uint, string> Names = Build();

    // The table the other way round, for telling whether a name a layout reports already belongs to another key. The
    // table's names are unique (a test pins it), and TryAdd keeps a later duplicate from failing the type initializer.
    private static readonly Dictionary<string, uint> CodesByName = BuildCodesByName();

    // WPF's Key enum gives each of these codes two names that belong to different keys, and .NET does not promise which
    // one ToString returns: 0x15 is Kana on a Japanese keyboard and Hangul on a Korean one, 0x19 Kanji or Hanja, and
    // 0xF0 to 0xFD are both the Japanese IME (DBE) keys and old terminal keys (VK_OEM_ATTN to VK_OEM_BACKTAB, VK_ATTN
    // to VK_PA1). A stored one says nothing reliable.
    private static readonly Dictionary<uint, string[]> AmbiguousWpfNames = new()
    {
        [0x15] = ["KanaMode", "HangulMode"],
        [0x19] = ["HanjaMode", "KanjiMode"],
        [0xF0] = ["DbeAlphanumeric", "OemAttn"],
        [0xF1] = ["DbeKatakana", "OemFinish"],
        [0xF2] = ["DbeHiragana", "OemCopy"],
        [0xF3] = ["DbeSbcsChar", "OemAuto"],
        [0xF4] = ["DbeDbcsChar", "OemEnlw"],
        [0xF5] = ["DbeRoman", "OemBackTab"],
        [0xF6] = ["Attn", "DbeNoRoman"],
        [0xF7] = ["CrSel", "DbeEnterWordRegisterMode"],
        [0xF8] = ["ExSel", "DbeEnterImeConfigureMode"],
        [0xF9] = ["EraseEof", "DbeFlushString"],
        [0xFA] = ["Play", "DbeCodeInput"],
        [0xFB] = ["Zoom", "DbeNoCodeInput"],
        [0xFC] = ["NoName", "DbeDetermineString"],
        [0xFD] = ["Pa1", "DbeEnterDialogConversionMode"],
    };

    /// <summary>The canonical name of a key whose meaning is the same on every layout, or null for any other key.</summary>
    public static string? Of(uint virtualKey) => Names.GetValueOrDefault(virtualKey);

    /// <summary>The virtual-key codes the table names, for tests.</summary>
    internal static IReadOnlyCollection<uint> Known => Names.Keys;

    /// <summary>The codes whose WPF names are shared by two different keys, with those names, for tests.</summary>
    internal static IReadOnlyDictionary<uint, string[]> AmbiguousWpfNamesByCode => AmbiguousWpfNames;

    /// <summary>
    /// Whether <paramref name="name"/> is one of the two WPF names for <paramref name="virtualKey"/> that belong to
    /// different keys, so a binding stored under it cannot say which key it was.
    /// </summary>
    public static bool IsAmbiguousWpfName(uint virtualKey, string name) =>
        AmbiguousWpfNames.TryGetValue(virtualKey, out var names) &&
        names.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A key name from what <c>GetKeyNameTextW</c> returned for the scan code of <paramref name="virtualKey"/> on the
    /// current layout, for a key the table leaves out that types no character (an IME key, say). Null for nothing, white
    /// space or a control character, and null for a name the table gives another key: a layout can report one for a code
    /// it has no name of its own for (on the US layout the scan code of VK_ABNT_C2 is named "F15"), and shown for this
    /// key it would name the wrong one. A plus sign is spelled out, because "+" is what joins the keys of a chord.
    /// </summary>
    public static string? FromLayoutKeyName(string? name, uint virtualKey)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
        {
            return null;
        }

        var trimmed = name.Trim();
        if (CodesByName.TryGetValue(trimmed, out var named) && named != virtualKey)
        {
            return null;
        }

        return trimmed.Replace("+", "Plus", StringComparison.Ordinal);
    }

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
            // The mouse buttons a hotkey can use (MouseButtons), named with the button numbers mouse software shows.
            [0x04] = "Middle mouse button", // VK_MBUTTON
            [0x05] = "Mouse Back (button 4)", // VK_XBUTTON1
            [0x06] = "Mouse Forward (button 5)", // VK_XBUTTON2
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

    private static Dictionary<string, uint> BuildCodesByName()
    {
        var codes = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, name) in Names)
        {
            codes.TryAdd(name, code);
        }

        return codes;
    }
}
