using Scribe.Core.Audio;

namespace Scribe.Core.Tests;

/// <summary>
/// When the user hears that the microphone they chose is away and the Windows default recorded instead: once when it
/// starts, never on every press while it lasts, and again only after it came back or the choice changed.
/// </summary>
public sealed class UnavailableMicrophoneNoticeTests
{
    [Fact]
    public void The_first_press_that_falls_back_is_told_and_the_rest_of_the_episode_is_not()
    {
        var notice = new UnavailableMicrophoneNotice();

        Assert.True(notice.ShouldNotify("yeti", fellBack: true));
        Assert.False(notice.ShouldNotify("yeti", fellBack: true));
        Assert.False(notice.ShouldNotify("yeti", fellBack: true));
    }

    [Fact]
    public void Once_the_microphone_is_back_the_next_time_it_goes_away_is_told_again()
    {
        var notice = new UnavailableMicrophoneNotice();
        notice.ShouldNotify("yeti", fellBack: true);

        Assert.False(notice.ShouldNotify("yeti", fellBack: false));

        Assert.True(notice.ShouldNotify("yeti", fellBack: true));
    }

    [Fact]
    public void Choosing_another_microphone_that_is_also_away_is_its_own_episode()
    {
        var notice = new UnavailableMicrophoneNotice();
        notice.ShouldNotify("yeti", fellBack: true);

        Assert.True(notice.ShouldNotify("usb", fellBack: true));
        Assert.False(notice.ShouldNotify("usb", fellBack: true));
    }

    [Fact]
    public void Following_windows_ends_the_episode_and_is_never_told()
    {
        var notice = new UnavailableMicrophoneNotice();
        notice.ShouldNotify("yeti", fellBack: true);

        Assert.False(notice.ShouldNotify(null, fellBack: false));
        Assert.False(notice.ShouldNotify("", fellBack: true));

        Assert.True(notice.ShouldNotify("yeti", fellBack: true));
    }
}
