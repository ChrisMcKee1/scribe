using Scribe.Core.Audio;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class SilenceAutoStopTrackerTests
{
    private const float Voice = 0.3f;
    private const float Quiet = 0.005f;

    [Fact]
    public void Fires_after_the_hold_window_of_silence_following_speech()
    {
        var tracker = new SilenceAutoStopTracker(startedMs: 0, silenceHoldMs: 4_000);

        Assert.False(tracker.Update(Voice, 500));    // speaking — the window measures from here
        Assert.False(tracker.Update(Quiet, 1_000));  // silence begins
        Assert.False(tracker.Update(Quiet, 4_400));  // 3.9s since the last voice — not yet
        Assert.True(tracker.Update(Quiet, 4_500));   // 4.0s — stop
    }

    [Fact]
    public void Speech_resets_the_silence_window()
    {
        var tracker = new SilenceAutoStopTracker(startedMs: 0, silenceHoldMs: 4_000);

        tracker.Update(Voice, 500);
        tracker.Update(Quiet, 3_000);
        Assert.False(tracker.Update(Voice, 4_000));  // spoke again — window restarts
        Assert.False(tracker.Update(Quiet, 7_900));
        Assert.True(tracker.Update(Quiet, 8_000));
    }

    [Fact]
    public void Continuous_speech_never_fires()
    {
        var tracker = new SilenceAutoStopTracker(startedMs: 0, silenceHoldMs: 4_000, leadInLimitMs: 10_000);

        for (long t = 0; t <= 60_000; t += 100)
        {
            Assert.False(tracker.Update(Voice, t));
        }
    }

    [Fact]
    public void Pure_silence_fires_at_the_lead_in_limit_so_a_muted_mic_cannot_stay_hot()
    {
        var tracker = new SilenceAutoStopTracker(startedMs: 0, silenceHoldMs: 4_000, leadInLimitMs: 10_000);

        Assert.False(tracker.Update(Quiet, 9_900));
        Assert.True(tracker.Update(Quiet, 10_000));
    }

    [Fact]
    public void Lead_in_is_longer_than_the_post_speech_hold()
    {
        // A thinking pause before the first word must not cut the dictation off at the (shorter)
        // hold window; only the lead-in limit applies until speech is heard.
        var tracker = new SilenceAutoStopTracker(startedMs: 0, silenceHoldMs: 4_000, leadInLimitMs: 10_000);

        Assert.False(tracker.Update(Quiet, 5_000));  // 5s of thinking silence — still recording
        Assert.False(tracker.Update(Voice, 6_000));  // first words arrive
        Assert.False(tracker.Update(Quiet, 9_900));
        Assert.True(tracker.Update(Quiet, 10_000));  // 4s after the last word
    }
}
