using Scribe.Core.Overlay;

namespace Scribe.Core.Tests;

/// <summary>
/// When a new text scale reaches the pill's window: before an appearing pill's first frame, at once on a pill on screen,
/// after its fade in when one is running, and at the next show when it is hidden or fading out. Never mid-fade.
/// </summary>
public sealed class PillTextScaleTests
{
    [Fact]
    public void A_pill_starts_at_its_100_percent_size()
    {
        Assert.Equal(1.0, new PillTextScale().Current);
    }

    [Fact]
    public void A_pill_that_appears_takes_the_scale_read_at_its_show_before_its_first_frame()
    {
        var scale = new PillTextScale();
        scale.OnShow(1.5, appearing: true, fadingIn: false);

        Assert.Equal(1.5, scale.Current);
    }

    [Fact]
    public void A_change_while_the_pill_is_hidden_applies_at_its_next_show()
    {
        var scale = new PillTextScale();
        Assert.False(scale.OnChanged(2.0, visible: false, fadingIn: false, fadingOut: false));
        Assert.Equal(1.0, scale.Current);

        scale.OnShow(2.0, appearing: true, fadingIn: false);
        Assert.Equal(2.0, scale.Current);
    }

    [Fact]
    public void A_change_on_a_pill_on_screen_resizes_it_at_once()
    {
        var scale = new PillTextScale();
        scale.OnShow(1.0, appearing: true, fadingIn: false);

        Assert.True(scale.OnChanged(1.5, visible: true, fadingIn: false, fadingOut: false));
        Assert.Equal(1.5, scale.Current);
    }

    [Fact]
    public void The_same_scale_again_resizes_nothing()
    {
        var scale = new PillTextScale();
        scale.OnShow(1.5, appearing: true, fadingIn: false);

        Assert.False(scale.OnChanged(1.5, visible: true, fadingIn: false, fadingOut: false));
        Assert.Equal(1.5, scale.Current);
    }

    [Fact]
    public void A_change_during_the_fade_in_waits_for_it_to_finish()
    {
        var scale = new PillTextScale();
        scale.OnShow(1.0, appearing: true, fadingIn: false);

        Assert.False(scale.OnChanged(1.5, visible: true, fadingIn: true, fadingOut: false));
        Assert.Equal(1.0, scale.Current);

        Assert.True(scale.OnFadeInCompleted(visible: true, fadingOut: false));
        Assert.Equal(1.5, scale.Current);
        Assert.False(scale.OnFadeInCompleted(visible: true, fadingOut: false)); // applied once
    }

    [Fact]
    public void A_show_during_the_fade_in_keeps_the_size_until_the_fade_has_finished()
    {
        // A state change within the first 120 ms, such as a warning on a recording that has just appeared.
        var scale = new PillTextScale();
        scale.OnShow(1.0, appearing: true, fadingIn: false);

        scale.OnShow(2.0, appearing: false, fadingIn: true);
        Assert.Equal(1.0, scale.Current);

        Assert.True(scale.OnFadeInCompleted(visible: true, fadingOut: false));
        Assert.Equal(2.0, scale.Current);
    }

    [Fact]
    public void A_change_while_the_pill_fades_out_applies_at_its_next_show()
    {
        var scale = new PillTextScale();
        scale.OnShow(1.0, appearing: true, fadingIn: false);

        Assert.False(scale.OnChanged(1.5, visible: true, fadingIn: false, fadingOut: true));
        Assert.Equal(1.0, scale.Current);

        scale.OnShow(1.5, appearing: true, fadingIn: false);
        Assert.Equal(1.5, scale.Current);
    }

    [Fact]
    public void A_change_held_back_by_the_fade_in_is_dropped_when_the_pill_hides()
    {
        var scale = new PillTextScale();
        scale.OnShow(1.0, appearing: true, fadingIn: false);
        Assert.False(scale.OnChanged(1.5, visible: true, fadingIn: true, fadingOut: false));

        scale.OnHidden();
        Assert.False(scale.OnFadeInCompleted(visible: true, fadingOut: false));
        Assert.Equal(1.0, scale.Current);

        scale.OnShow(1.75, appearing: true, fadingIn: false); // the next show reads it again
        Assert.Equal(1.75, scale.Current);
    }

    [Fact]
    public void A_fade_in_that_ends_as_the_pill_goes_applies_nothing()
    {
        var scale = new PillTextScale();
        scale.OnShow(1.0, appearing: true, fadingIn: false);
        Assert.False(scale.OnChanged(1.5, visible: true, fadingIn: true, fadingOut: false));

        Assert.False(scale.OnFadeInCompleted(visible: true, fadingOut: true));
        Assert.Equal(1.0, scale.Current);
    }

    [Fact]
    public void A_change_back_to_the_current_scale_during_the_fade_in_leaves_nothing_to_apply()
    {
        var scale = new PillTextScale();
        scale.OnShow(1.0, appearing: true, fadingIn: false);
        Assert.False(scale.OnChanged(1.5, visible: true, fadingIn: true, fadingOut: false));
        Assert.False(scale.OnChanged(1.0, visible: true, fadingIn: true, fadingOut: false));

        Assert.False(scale.OnFadeInCompleted(visible: true, fadingOut: false));
        Assert.Equal(1.0, scale.Current);
    }
}
