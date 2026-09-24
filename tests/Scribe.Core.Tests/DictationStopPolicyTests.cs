using Scribe.Core.Lifecycle;

namespace Scribe.Core.Tests;

/// <summary>
/// Which stops release the hotkey's toggle latch (the controller's shared stop path asks this, and has no tests of its
/// own). A stop Scribe makes itself must, or the next press is swallowed as the toggle-off of a dictation that already
/// ended; a stop the hook sent itself must not, or it could cancel a fresh press the hook took after sending it.
/// </summary>
public sealed class DictationStopPolicyTests
{
    [Theory]
    [InlineData(DictationStopReason.SilenceAutoStop)]
    [InlineData(DictationStopReason.MicrophoneFault)]
    [InlineData(DictationStopReason.Paused)]
    [InlineData(DictationStopReason.DurationLimit)]
    public void A_stop_scribe_makes_itself_releases_the_hotkey_toggle(DictationStopReason reason) =>
        Assert.True(DictationStopPolicy.ReleasesHotkeyToggle(reason));

    [Theory]
    [InlineData(DictationStopReason.HotkeyReleased)]
    [InlineData(DictationStopReason.DesktopSwitch)]
    public void A_stop_the_hook_sent_itself_leaves_the_toggle_alone(DictationStopReason reason) =>
        Assert.False(DictationStopPolicy.ReleasesHotkeyToggle(reason));

    [Fact]
    public void Every_stop_reason_is_classified_above()
    {
        // A reason added later has to join one of the two lists, so its effect on the toggle is decided, not inherited.
        DictationStopReason[] classified =
        [
            DictationStopReason.SilenceAutoStop,
            DictationStopReason.MicrophoneFault,
            DictationStopReason.Paused,
            DictationStopReason.DurationLimit,
            DictationStopReason.HotkeyReleased,
            DictationStopReason.DesktopSwitch,
        ];
        Assert.Equal(Enum.GetValues<DictationStopReason>().Order(), classified.Order());
    }
}
