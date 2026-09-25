using Scribe.Core.Models;
using Scribe.Core.Tray;

namespace Scribe.Core.Tests;

public sealed class TrayToolTipTests
{
    [Theory]
    [InlineData(TrayState.Ready, HotkeyMode.Hold, "Scribe: ready. Hold Page Down and speak.")]
    [InlineData(TrayState.Ready, HotkeyMode.Toggle, "Scribe: ready. Press Page Down to start and stop.")]
    [InlineData(TrayState.Recording, HotkeyMode.Hold, "Scribe: listening...")]
    [InlineData(TrayState.Processing, HotkeyMode.Hold, "Scribe: writing your text...")]
    [InlineData(TrayState.Paused, HotkeyMode.Hold, "Scribe: paused. Your shortcuts work as usual in other apps until you resume.")]
    public void Each_state_has_exact_text(TrayState state, HotkeyMode mode, string expected)
    {
        Assert.Equal(expected, TrayToolTip.Compose(state, "Page Down", mode));
    }

    [Fact]
    public void Condition_lines_follow_priority_order_by_call_site_choice()
    {
        Assert.EndsWith("No speech model is installed. Choose one in Settings.", TrayToolTip.Compose(TrayState.Ready, "Page Down", HotkeyMode.Hold, TrayCondition.NoSpeechModel));
        Assert.EndsWith("Using default settings. Open Settings to review them.", TrayToolTip.Compose(TrayState.Ready, "Page Down", HotkeyMode.Hold, TrayCondition.DefaultSettings));
        Assert.EndsWith("The speech model didn't load. Scribe tries again when you dictate.", TrayToolTip.Compose(TrayState.Ready, "Page Down", HotkeyMode.Hold, TrayCondition.SpeechModelFailed));
        Assert.EndsWith("Scribe 0.4.5 is ready. Right-click for Restart to update.", TrayToolTip.Compose(TrayState.Ready, "Page Down", HotkeyMode.Hold, TrayCondition.UpdateReady, "0.4.5"));
    }

    [Fact]
    public void Never_longer_than_127_characters_with_long_shortcut_and_every_condition()
    {
        foreach (var condition in Enum.GetValues<TrayCondition>())
        {
            var text = TrayToolTip.Compose(TrayState.Ready, new string('x', 100), HotkeyMode.Toggle, condition, "0.4.5");
            Assert.True(text.Length <= TrayToolTip.MaxLength, text);
        }
    }

    [Fact]
    public void Always_starts_with_scribe_and_has_no_dashes()
    {
        foreach (var state in Enum.GetValues<TrayState>())
        {
            var text = TrayToolTip.Compose(state, "Page Down", HotkeyMode.Hold, TrayCondition.UpdateReady, "0.4.5");
            Assert.StartsWith("Scribe:", text);
            Assert.DoesNotContain('\u2013', text);
            Assert.DoesNotContain('\u2014', text);
        }
    }


    [Fact]
    public void Drops_condition_before_shortening_state_line()
    {
        var shortcut = new string('x', 140);
        var text = TrayToolTip.Compose(TrayState.Ready, shortcut, HotkeyMode.Toggle, TrayCondition.SpeechModelFailed);

        Assert.DoesNotContain(Environment.NewLine, text);
        Assert.DoesNotContain("speech model", text, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Scribe: ready. Press ", text);
        Assert.True(text.Length <= TrayToolTip.MaxLength, text);
    }

    [Fact]
    public void The_limit_is_the_shell_tooltip_limit()
    {
        // NOTIFYICONDATA.szTip holds 128 UTF-16 units including the terminating null.
        Assert.Equal(127, TrayToolTip.MaxLength);
    }

    [Fact]
    public void Shortening_never_splits_a_surrogate_pair()
    {
        // "Scribe: ready. Hold " is 20 units, so the high surrogate lands at index 125, right where the cut falls.
        var shortcut = new string('a', 105) + "\U0001F9D1" + new string('b', 20);

        var text = TrayToolTip.Compose(TrayState.Ready, shortcut, HotkeyMode.Hold);

        Assert.Equal("Scribe: ready. Hold " + new string('a', 105) + "\u2026", text);
        for (var i = 0; i < text.Length; i++)
        {
            Assert.False(char.IsHighSurrogate(text[i]) && (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])));
        }
    }

    [Fact]
    public void A_condition_that_does_not_fit_is_dropped_and_the_state_stays_whole()
    {
        var text = TrayToolTip.Compose(TrayState.Paused, "Page Down", HotkeyMode.Hold, TrayCondition.SpeechModelFailed);

        Assert.Equal("Scribe: paused. Your shortcuts work as usual in other apps until you resume.", text);
    }
}
