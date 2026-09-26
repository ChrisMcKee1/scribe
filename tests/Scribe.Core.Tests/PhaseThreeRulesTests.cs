using Scribe.Core.Models;
using Scribe.Core.Settings;
using Scribe.Core.Transcription;
using Scribe.Core.Hotkeys;

namespace Scribe.Core.Tests;

public sealed class PhaseThreeRulesTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Recording_indicator_position_and_preview_follow_the_parent_setting(bool shown)
    {
        var state = RecordingIndicatorRules.Describe(shown);

        Assert.Equal(shown, state.ShowPositionPicker);
        Assert.Equal(shown, state.ShowPreview);
    }

    [Theory]
    [InlineData(0x22, "Page Down")]
    [InlineData(0x21, "Page Up")]
    public void Shortcut_caveats_explain_bare_page_keys(uint virtualKey, string name)
    {
        var text = ShortcutCaveats.For(new HotkeyBinding(virtualKey, KeyModifiers.None, HotkeyMode.Hold, true, name));

        Assert.NotNull(text);
        Assert.Contains($"{name} on its own doesn't reach other apps", text);
    }

    [Theory]
    [InlineData(0xA1, "Filter Keys")]
    [InlineData(0x90, "Toggle Keys")]
    [InlineData(0xA2, "Sticky Keys")]
    public void Shortcut_caveats_explain_accessibility_keys(uint virtualKey, string expected)
    {
        var text = ShortcutCaveats.For(new HotkeyBinding(virtualKey, KeyModifiers.None, HotkeyMode.Hold, true));

        Assert.NotNull(text);
        Assert.Contains(expected, text);
    }

    [Fact]
    public void Shortcut_caveats_explain_windows_key_combinations()
    {
        var text = ShortcutCaveats.For(new HotkeyBinding(0x4B, KeyModifiers.Win, HotkeyMode.Hold, true));

        Assert.Equal("This combination replaces a Windows shortcut while Scribe runs.", text);
    }

    [Fact]
    public void Shortcut_caveats_explain_mouse_buttons()
    {
        var text = ShortcutCaveats.For(new HotkeyBinding(MouseButtons.Middle, KeyModifiers.None, HotkeyMode.Hold, true));

        Assert.Equal(
            "While Scribe runs, this mouse button doesn't do its usual job in other apps, unless you hold Ctrl, Shift, Alt or the Windows key.",
            text);
    }

    [Fact]
    public void Shortcut_caveats_are_empty_for_regular_keys()
    {
        var text = ShortcutCaveats.For(new HotkeyBinding(0x4B, KeyModifiers.None, HotkeyMode.Hold, true));

        Assert.Null(text);
    }

    [Fact]
    public void Transcription_model_choices_show_install_until_downloaded()
    {
        var installed = new HashSet<string> { TranscriptionModelCatalog.DefaultId };

        var choices = TranscriptionModelChoices.Build(TranscriptionModelCatalog.DefaultId, installed);

        var selected = choices.Single(choice => choice.Id == TranscriptionModelCatalog.DefaultId);
        Assert.True(selected.IsSelected);
        Assert.True(selected.IsInstalled);
        Assert.False(selected.ShowInstall);
        Assert.Equal("Downloaded", selected.StatusText);
        Assert.Equal("Parakeet, 25 languages (recommended)", selected.Label);
        Assert.Contains("Understands about 25 European languages.", selected.Hint);
        Assert.Contains("Downloaded.", selected.Hint);
        Assert.Contains(choices, choice => choice.Label == "Moonshine Base, English only");
        Assert.Contains(choices, choice => choice.ShowInstall && choice.StatusText == string.Empty);
    }
}
