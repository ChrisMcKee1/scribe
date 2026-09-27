using Scribe.Core.Overlay;

namespace Scribe.Core.Tests;

/// <summary>
/// When the shell warms the overlay helper besides the startup warmup: on the resume after a pause, which released it, and
/// when settings are applied with the pill on. A warmup on a running helper is only the no-op WARMUP write, so the rule is
/// about not warming a helper nobody wants, and never leaving the first dictation to a relaunch. And when the helper is
/// kept resident, so the idle deadline trims it instead of ending it: only while the pill is on and dictation is not paused.
/// </summary>
public sealed class OverlayWarmupTests
{
    [Theory]
    [InlineData(true, false, true)] // the pill is on and dictation runs: an idle helper is trimmed and kept for the next pill
    [InlineData(true, true, false)] // paused: the idle deadline ends it, whatever brought it back (a preview, a vetoed release)
    [InlineData(false, false, false)] // the pill is off: nothing will show
    [InlineData(false, true, false)]
    public void The_helper_is_kept_resident_only_while_the_pill_is_on_and_dictation_is_not_paused(bool pillOn, bool paused, bool keep)
    {
        Assert.Equal(keep, OverlayWarmup.KeepResident(pillOn, paused));
    }

    [Theory]
    [InlineData(true, false, true, true)] // resumed with the pill on: the pause released the helper, so warm it
    [InlineData(true, false, false, false)] // resumed with the pill off: nothing will show
    [InlineData(false, false, true, false)] // an ordinary change (a dictation ending, say): the helper was never released
    [InlineData(true, true, true, false)] // still paused, rendered again
    [InlineData(false, true, true, false)] // pausing: the release comes next, not a warmup
    [InlineData(false, false, false, false)]
    public void A_render_warms_the_helper_only_on_the_resume_with_the_pill_on(bool wasPaused, bool isPaused, bool pillOn, bool warm)
    {
        Assert.Equal(warm, OverlayWarmup.AfterRender(wasPaused, isPaused, pillOn));
    }

    [Theory]
    [InlineData(true, false, true)] // the pill is on: the next dictation must not wait for a launch
    [InlineData(false, false, false)] // the pill is off: nothing will show
    [InlineData(true, true, false)] // paused: the pause released the helper, and the resume warms it
    [InlineData(false, true, false)]
    public void Applied_settings_warm_the_helper_when_the_pill_is_on_and_dictation_is_not_paused(bool pillOn, bool paused, bool warm)
    {
        Assert.Equal(warm, OverlayWarmup.AfterSettingsApplied(pillOn, paused));
    }
}
