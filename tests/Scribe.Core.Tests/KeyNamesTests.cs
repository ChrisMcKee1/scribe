using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Keys are named from their virtual-key codes, never from WPF's <c>Key</c> names: that enum gives Page Down the second
/// name <c>Next</c> (and Page Up <c>Prior</c>, Caps Lock <c>Capital</c>, Print Screen <c>Snapshot</c>) and .NET does
/// not promise which name <c>ToString</c> returns, so a user who bound Page Down could read "Next". Bindings stored that
/// way read correctly too, because the code, not the stored name, decides.
/// </summary>
public sealed class KeyNamesTests
{
    [Theory]
    [InlineData(0x21u, "Page Up")]
    [InlineData(0x22u, "Page Down")]
    [InlineData(0x14u, "Caps Lock")]
    [InlineData(0x2Cu, "Print Screen")]
    [InlineData(0x91u, "Scroll Lock")]
    [InlineData(0x90u, "Num Lock")]
    [InlineData(0x2Du, "Insert")]
    [InlineData(0x2Eu, "Delete")]
    [InlineData(0x24u, "Home")]
    [InlineData(0x23u, "End")]
    [InlineData(0x08u, "Backspace")]
    [InlineData(0x0Du, "Enter")]
    [InlineData(0x13u, "Pause")]
    [InlineData(0x5Du, "Menu")]
    [InlineData(0x1Bu, "Esc")]
    [InlineData(0x09u, "Tab")]
    [InlineData(0x20u, "Space")]
    [InlineData(0x25u, "Left Arrow")]
    [InlineData(0x26u, "Up Arrow")]
    [InlineData(0x27u, "Right Arrow")]
    [InlineData(0x28u, "Down Arrow")]
    [InlineData(0x60u, "Num 0")]
    [InlineData(0x69u, "Num 9")]
    [InlineData(0x6Au, "Num Multiply")]
    [InlineData(0x6Bu, "Num Plus")]
    [InlineData(0x6Du, "Num Minus")]
    [InlineData(0x6Eu, "Num Decimal")]
    [InlineData(0x6Fu, "Num Divide")]
    [InlineData(0x30u, "0")]
    [InlineData(0x35u, "5")]
    [InlineData(0x41u, "A")]
    [InlineData(0x5Au, "Z")]
    [InlineData(0x70u, "F1")]
    [InlineData(0x7Bu, "F12")]
    [InlineData(0x87u, "F24")]
    [InlineData(0xA0u, "Left Shift")]
    [InlineData(0xA1u, "Right Shift")]
    [InlineData(0xA2u, "Left Ctrl")]
    [InlineData(0xA3u, "Right Ctrl")]
    [InlineData(0xA4u, "Left Alt")]
    [InlineData(0xA5u, "Right Alt")]
    [InlineData(0x5Bu, "Left Win")]
    [InlineData(0x5Cu, "Right Win")]
    [InlineData(0xADu, "Volume Mute")]
    [InlineData(0xB3u, "Play/Pause")]
    public void A_layout_independent_key_has_its_canonical_name(uint virtualKey, string name)
    {
        Assert.Equal(name, KeyNames.Of(virtualKey));
        Assert.Equal(name, HotkeyText.KeyName(virtualKey, _ => "from the layout"));
    }

    [Fact]
    public void No_name_in_the_table_is_a_wpf_alias_a_chord_separator_or_ambiguous()
    {
        string[] aliases = ["Next", "Prior", "PageDown", "PageUp", "Capital", "CapsLock", "Snapshot", "PrintScreen",
            "Return", "Apps", "Scroll", "Back", "Escape", "LWin", "RWin", "NumPad0", "D0", "Oem1", "OemSemicolon"];
        var names = KeyNames.Known.Select(key => KeyNames.Of(key)!).ToList();

        Assert.Empty(names.Intersect(aliases));
        Assert.All(names, name =>
        {
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.DoesNotContain('+', name);
            Assert.DoesNotContain('\u2014', name);
            Assert.DoesNotContain('\u2013', name);
        });
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(0xBAu)] // VK_OEM_1: ";" on US ANSI, "Ü" on German
    [InlineData(0xBBu)] // VK_OEM_PLUS
    [InlineData(0xC0u)] // VK_OEM_3: "`" on US ANSI
    [InlineData(0xDEu)] // VK_OEM_7
    [InlineData(0xE2u)] // VK_OEM_102, the extra key on ISO keyboards
    [InlineData(0x15u)] // VK_KANA, also VK_HANGUL
    [InlineData(0x19u)] // VK_KANJI, also VK_HANJA
    public void A_key_whose_meaning_depends_on_the_layout_is_named_by_the_layout(uint virtualKey)
    {
        Assert.Null(KeyNames.Of(virtualKey));
        Assert.Equal("Ö", HotkeyText.KeyName(virtualKey, _ => "Ö"));
        Assert.Null(HotkeyText.KeyName(virtualKey, _ => "  "));
        Assert.Null(HotkeyText.KeyName(virtualKey));
    }

    [Theory]
    [InlineData(0u, null)]
    [InlineData((uint)';', ";")]
    [InlineData((uint)'`', "`")]
    [InlineData((uint)'a', "A")]
    [InlineData((uint)'ö', "Ö")]
    [InlineData((uint)'+', "Plus")]
    [InlineData(0x80000000u | '^', "^")] // a dead key: the top bit marks it
    [InlineData((uint)'\t', null)]
    [InlineData((uint)'\r', null)]
    [InlineData((uint)' ', null)]
    public void The_layout_character_becomes_a_key_name(uint mapped, string? name)
    {
        Assert.Equal(name, KeyNames.FromMappedCharacter(mapped));
    }

    [Theory]
    [InlineData(0x22u, "Next", "Page Down")]
    [InlineData(0x21u, "Prior", "Page Up")]
    [InlineData(0x22u, "PageDown", "Page Down")]
    [InlineData(0x14u, "Capital", "Caps Lock")]
    [InlineData(0x2Cu, "Snapshot", "Print Screen")]
    [InlineData(0x0Du, "Return", "Enter")]
    [InlineData(0x35u, "D5", "5")]
    [InlineData(0x63u, "NumPad3", "Num 3")]
    [InlineData(0xA3u, "RightCtrl", "Right Ctrl")]
    public void A_binding_stored_under_a_wpf_name_reads_as_the_key(uint virtualKey, string stored, string shown)
    {
        var binding = new HotkeyBinding(virtualKey, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, stored);

        Assert.Equal(shown, HotkeyText.Describe(binding));
    }

    [Fact]
    public void Modifiers_and_chords_are_named_from_their_codes()
    {
        var ctrlShiftSpace = new HotkeyBinding(
            0x20, KeyModifiers.Control | KeyModifiers.Shift, HotkeyMode.Toggle, Suppress: false, "Ctrl+Shift+Space");
        var chord = new HotkeyBinding(
            0xA3, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "RightCtrl+RightShift",
            SecondaryVirtualKey: 0xA1, SuppressChordMembers: true);
        var winNext = new HotkeyBinding(0x22, KeyModifiers.Win | KeyModifiers.Alt, HotkeyMode.Hold, Suppress: true);

        Assert.Equal("Ctrl+Shift+Space", HotkeyText.Describe(ctrlShiftSpace));
        Assert.Equal("Right Ctrl+Right Shift", HotkeyText.Describe(chord));
        Assert.Equal("Alt+Win+Page Down", HotkeyText.Describe(winNext));
    }

    [Fact]
    public void A_key_the_table_leaves_to_the_layout_is_named_by_it_in_a_binding()
    {
        string? Layout(uint key) => key == 0xBA ? ";" : null;
        var single = new HotkeyBinding(0xBA, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "OemSemicolon");
        var chord = new HotkeyBinding(
            0xA3, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Right Ctrl+Oem1", SecondaryVirtualKey: 0xBA);

        Assert.Equal(";", HotkeyText.Describe(single, Layout));
        Assert.Equal("Right Ctrl+;", HotkeyText.Describe(chord, Layout));
    }

    [Fact]
    public void A_key_nothing_else_can_name_keeps_its_part_of_the_stored_name()
    {
        var single = new HotkeyBinding(0xBA, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "OemSemicolon");
        var withModifier = new HotkeyBinding(0xBA, KeyModifiers.Control, HotkeyMode.Hold, Suppress: true, "OemSemicolon");
        var chord = new HotkeyBinding(
            0xBA, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Oem1", SecondaryVirtualKey: 0xA1);

        Assert.Equal("OemSemicolon", HotkeyText.Describe(single));
        Assert.Equal("Ctrl+OemSemicolon", HotkeyText.Describe(withModifier));
        Assert.Equal("Oem1+Right Shift", HotkeyText.Describe(chord));
    }

    [Theory]
    // A known key beside one only the stored name can name keeps its true name: the reported "Next+Oem1".
    [InlineData(0x22u, 0xBAu, "Next+Oem1", "Page Down+Oem1")]
    [InlineData(0x22u, 0x1Cu, "Next+ImeConvert", "Page Down+ImeConvert")] // VK_CONVERT, an IME key
    [InlineData(0xBAu, 0x22u, "Oem1+Next", "Oem1+Page Down")]
    [InlineData(0x21u, 0xDEu, " Prior + OemQuotes ", "Page Up+OemQuotes")]
    // Older builds stored only the first key's name and added the second when they showed it.
    [InlineData(0x22u, 0xBAu, "Next", "Page Down+Key 0xBA")]
    [InlineData(0xBAu, 0x22u, "Oem1", "Oem1+Page Down")]
    // Nothing stored: the unnamed key reads as its code, the named one by its name.
    [InlineData(0x22u, 0xBAu, null, "Page Down+Key 0xBA")]
    [InlineData(0xBAu, 0xDEu, "Oem1+Oem7", "Oem1+Oem7")]
    public void A_chord_names_each_key_on_its_own_without_the_layout(uint primary, uint secondary, string? stored, string shown)
    {
        var chord = new HotkeyBinding(
            primary, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, stored, SecondaryVirtualKey: secondary);

        // As the session banner and the hook and controller logs see it: no layout to ask.
        Assert.Equal(shown, HotkeyText.Describe(chord));
    }

    [Theory]
    [InlineData(0x22u, 0xBAu, "Next+Oem1", "Page Down+;")]
    [InlineData(0x22u, 0x1Cu, "Next+ImeConvert", "Page Down+ImeConvert")] // the layout types nothing for an IME key
    [InlineData(0xBAu, 0x22u, "Oem1+Next", ";+Page Down")]
    [InlineData(0x22u, 0xBAu, "Next", "Page Down+;")]
    [InlineData(0x1Cu, 0xBAu, null, "Key 0x1C+;")]
    public void A_chord_names_each_key_on_its_own_with_the_layout(uint primary, uint secondary, string? stored, string shown)
    {
        string? Layout(uint key) => key == 0xBA ? ";" : null;
        var chord = new HotkeyBinding(
            primary, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, stored, SecondaryVirtualKey: secondary);

        Assert.Equal(shown, HotkeyText.Describe(chord, Layout));
    }

    [Fact]
    public void Modifier_words_in_a_stored_name_are_not_taken_for_keys()
    {
        // The modifiers are named from the binding itself; a stored name that spelled them out still names its keys.
        string? Layout(uint key) => key == 0xBA ? ";" : null;
        var withModifier = new HotkeyBinding(0xBA, KeyModifiers.Control, HotkeyMode.Hold, Suppress: true, "Ctrl+OemSemicolon");
        var chordWithModifier = new HotkeyBinding(
            0xBA, KeyModifiers.Control, HotkeyMode.Hold, Suppress: true, "Ctrl+Oem1", SecondaryVirtualKey: 0xDE);
        var staleModifier = new HotkeyBinding(0x7B, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Ctrl+F12");

        Assert.Equal("Ctrl+OemSemicolon", HotkeyText.Describe(withModifier));
        Assert.Equal("Ctrl+;", HotkeyText.Describe(withModifier, Layout));
        Assert.Equal("Ctrl+Oem1+Key 0xDE", HotkeyText.Describe(chordWithModifier));
        Assert.Equal("F12", HotkeyText.Describe(staleModifier));
    }

    [Fact]
    public void A_key_nothing_can_name_reads_as_its_code()
    {
        var binding = new HotkeyBinding(0xE5, KeyModifiers.None, HotkeyMode.Hold, Suppress: true);

        Assert.Equal("Key 0xE5", HotkeyText.Describe(binding));
    }

    [Fact]
    public void The_welcome_teaches_both_default_keys()
    {
        var (title, body) = HotkeyText.Gesture(AppSettings.CreateDefault());

        Assert.Equal("Hold, speak, release", title);
        Assert.Equal(
            "Hold Page Down and start talking. Release when you are done, and the text appears wherever your cursor " +
            "is. Hold Page Up instead to dictate without AI cleanup.",
            body);
    }

    [Fact]
    public void The_welcome_names_the_keys_this_install_really_uses_and_how_they_are_pressed()
    {
        var legacy = AppSettings.CreateForExistingInstall();
        var toggled = AppSettings.CreateDefault();
        toggled.Hotkey = toggled.Hotkey with { Mode = HotkeyMode.Toggle, DisplayName = "Next" };
        toggled.DictationOnlyHotkey = toggled.DictationOnlyHotkey! with { Mode = HotkeyMode.Toggle };

        var (legacyTitle, legacyBody) = HotkeyText.Gesture(legacy);
        var (toggleTitle, toggleBody) = HotkeyText.Gesture(toggled);
        var (unknownTitle, unknownBody) = HotkeyText.Gesture(settings: null);

        Assert.Equal("Hold, speak, release", legacyTitle);
        Assert.StartsWith("Hold Right Ctrl and start talking.", legacyBody);
        Assert.DoesNotContain("without AI cleanup", legacyBody);

        Assert.Equal("Press, speak, press again", toggleTitle);
        Assert.StartsWith("Press Page Down and start talking. Press it again when you are done", toggleBody);
        Assert.EndsWith("Press Page Up instead to dictate without AI cleanup.", toggleBody);

        Assert.Equal("Hold, speak, release", unknownTitle);
        Assert.StartsWith("Hold your push-to-talk key", unknownBody);
    }

    [Fact]
    public void Nothing_this_shows_the_user_carries_a_dash()
    {
        var texts = new List<string>
        {
            DefaultHotkeyRestore.Hint,
            DefaultHotkeyRestore.Restore(HotkeyBinding.Legacy, null, HotkeyBinding.Legacy, null).Message,
            DefaultHotkeyRestore.Restore(
                HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly, HotkeyBinding.Legacy, null).Message,
            DefaultHotkeyRestore.Restore(
                HotkeyBinding.Legacy, null, HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly).Message,
            DefaultHotkeyRestore.Restore(
                HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly,
                HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly).Message,
        };
        texts.AddRange(new AppSettings?[] { AppSettings.CreateDefault(), AppSettings.CreateForExistingInstall(), null }
            .SelectMany(settings =>
            {
                var (title, body) = HotkeyText.Gesture(settings);
                return new[] { title, body };
            }));

        Assert.All(texts, text =>
        {
            Assert.DoesNotContain('\u2014', text);
            Assert.DoesNotContain('\u2013', text);
        });
    }
}
