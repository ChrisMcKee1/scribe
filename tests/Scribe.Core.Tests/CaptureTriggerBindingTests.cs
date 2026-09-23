using Scribe.Core.Hotkeys;
using Scribe.Core.Lifecycle;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

/// <summary>
/// Silence auto-stop follows the binding that actually fired. The capture settings keep the standard binding whichever
/// trigger fired, and reading its mode for every capture ended a held dictation-only recording on a four-second pause
/// (losing everything said after it) and left a toggled dictation-only recording that should have stopped running on.
/// </summary>
public sealed class CaptureTriggerBindingTests
{
    private static readonly HotkeyBinding StandardHold = HotkeyBinding.Default;
    private static readonly HotkeyBinding StandardToggle = HotkeyBinding.Default with { Mode = HotkeyMode.Toggle };
    private static readonly HotkeyBinding DictationOnlyHold =
        new(0x7B, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "F12");
    private static readonly HotkeyBinding DictationOnlyToggle = DictationOnlyHold with { Mode = HotkeyMode.Toggle };

    [Theory]
    // The reviewed configurations: standard toggle with a held dictation-only key, and the reverse.
    [InlineData(HotkeyMode.Toggle, HotkeyMode.Hold, HotkeyTrigger.Standard, true)]
    [InlineData(HotkeyMode.Toggle, HotkeyMode.Hold, HotkeyTrigger.DictationOnly, false)]
    [InlineData(HotkeyMode.Hold, HotkeyMode.Toggle, HotkeyTrigger.Standard, false)]
    [InlineData(HotkeyMode.Hold, HotkeyMode.Toggle, HotkeyTrigger.DictationOnly, true)]
    [InlineData(HotkeyMode.Toggle, HotkeyMode.Toggle, HotkeyTrigger.Standard, true)]
    [InlineData(HotkeyMode.Toggle, HotkeyMode.Toggle, HotkeyTrigger.DictationOnly, true)]
    [InlineData(HotkeyMode.Hold, HotkeyMode.Hold, HotkeyTrigger.Standard, false)]
    [InlineData(HotkeyMode.Hold, HotkeyMode.Hold, HotkeyTrigger.DictationOnly, false)]
    public void Only_a_toggle_that_actually_fired_stops_on_silence(
        HotkeyMode standardMode, HotkeyMode dictationOnlyMode, HotkeyTrigger trigger, bool expected)
    {
        var settings = Settings(standardMode, dictationOnlyMode, autoStop: true);

        // Asked of the snapshot the controller really uses: the per-trigger capture settings, which keep the standard
        // binding in Hotkey even for the dictation-only trigger.
        var capture = DictationCaptureSettingsResolver.Resolve(settings, trigger);

        Assert.Equal(expected, CaptureTriggerBinding.StopsOnSilence(capture, trigger));
        Assert.False(CaptureTriggerBinding.StopsOnSilence(Settings(standardMode, dictationOnlyMode, autoStop: false), trigger));
    }

    [Fact]
    public void The_binding_is_the_one_the_trigger_belongs_to()
    {
        var settings = Settings(HotkeyMode.Toggle, HotkeyMode.Hold, autoStop: true);

        Assert.Same(settings.Hotkey, CaptureTriggerBinding.For(settings, HotkeyTrigger.Standard));
        Assert.Same(settings.DictationOnlyHotkey, CaptureTriggerBinding.For(settings, HotkeyTrigger.DictationOnly));
        Assert.Equal("F12", CaptureTriggerBinding.For(settings, HotkeyTrigger.DictationOnly)!.DisplayName);
    }

    [Fact]
    public void A_dictation_only_trigger_whose_binding_was_removed_meanwhile_counts_as_a_hold()
    {
        var settings = Settings(HotkeyMode.Toggle, HotkeyMode.Toggle, autoStop: true);
        settings.DictationOnlyHotkey = null; // a save that removed it raced the key press

        Assert.Null(CaptureTriggerBinding.For(settings, HotkeyTrigger.DictationOnly));
        Assert.False(CaptureTriggerBinding.StopsOnSilence(settings, HotkeyTrigger.DictationOnly));
        Assert.True(CaptureTriggerBinding.StopsOnSilence(settings, HotkeyTrigger.Standard));
    }

    private static AppSettings Settings(HotkeyMode standardMode, HotkeyMode dictationOnlyMode, bool autoStop)
    {
        var settings = AppSettings.CreateDefault();
        settings.Hotkey = standardMode == HotkeyMode.Toggle ? StandardToggle : StandardHold;
        settings.DictationOnlyHotkey = dictationOnlyMode == HotkeyMode.Toggle ? DictationOnlyToggle : DictationOnlyHold;
        settings.AutoStopOnSilence = autoStop;
        return settings;
    }
}
