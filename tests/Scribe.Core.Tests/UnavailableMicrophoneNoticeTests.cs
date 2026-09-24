using Scribe.Core.Audio;
using Scribe.Core.Lifecycle;

namespace Scribe.Core.Tests;

/// <summary>
/// Episodes of the chosen microphone being unavailable, fed every committed choice and every capture that opened. The two
/// reviewed defects are replayed: a fallback told for microphone A stayed "told" through choosing B and then A again
/// without dictating, so A going away a second time was silent; and a capture that a pause or a fault took to processing
/// as its microphone opened (or that shutdown reclaimed) neither announced its fallback nor ended an episode.
/// </summary>
public sealed class UnavailableMicrophoneNoticeTests
{
    private const string A = "{0.0.1.00000000}.{a}";
    private const string B = "{0.0.1.00000000}.{b}";

    private static readonly DictationPresentation Recording = new(1, DictationPhase.Recording, false);

    private static RecordingOpen Opened(RecordingOpenOutcome outcome, bool fellBack) => new(
        outcome,
        outcome == RecordingOpenOutcome.Live ? Recording : null,
        TimeSpan.Zero,
        TimeSpan.Zero,
        null,
        RequestedDeviceUnavailable: fellBack,
        DeviceName: outcome == RecordingOpenOutcome.NotOpened ? null : "Windows default device");

    private static UnavailableMicrophoneNotice Chosen(string? deviceId)
    {
        var notice = new UnavailableMicrophoneNotice();
        notice.SelectionCommitted(deviceId);
        return notice;
    }

    private static UnavailableMicrophoneAnnouncement Press(
        UnavailableMicrophoneNotice notice, string? deviceId, RecordingOpenOutcome outcome, bool fellBack) =>
        notice.ReportOpen(notice.Begin(deviceId), Opened(outcome, fellBack));

    [Fact]
    public void A_fallback_whose_recording_is_live_is_told_once_per_episode_with_the_pill()
    {
        var notice = Chosen(A);

        Assert.Equal(UnavailableMicrophoneAnnouncement.WhileRecording, Press(notice, A, RecordingOpenOutcome.Live, fellBack: true));
        Assert.Equal(UnavailableMicrophoneAnnouncement.None, Press(notice, A, RecordingOpenOutcome.Live, fellBack: true));
        Assert.Equal(UnavailableMicrophoneAnnouncement.None, Press(notice, A, RecordingOpenOutcome.Live, fellBack: true));
    }

    [Fact]
    public void A_fallback_a_pause_or_fault_took_to_processing_is_told_in_the_tray_and_counts_as_told()
    {
        var notice = Chosen(A);

        Assert.Equal(
            UnavailableMicrophoneAnnouncement.AfterRecording,
            Press(notice, A, RecordingOpenOutcome.LeftToProcessing, fellBack: true));
        Assert.Equal(UnavailableMicrophoneAnnouncement.None, Press(notice, A, RecordingOpenOutcome.Live, fellBack: true));
        Assert.Equal(UnavailableMicrophoneAnnouncement.None, Press(notice, A, RecordingOpenOutcome.LeftToProcessing, fellBack: true));
    }

    [Fact]
    public void A_fallback_shutdown_reclaimed_is_not_told_and_does_not_count_as_told()
    {
        var notice = Chosen(A);

        Assert.Equal(UnavailableMicrophoneAnnouncement.None, Press(notice, A, RecordingOpenOutcome.Reclaimed, fellBack: true));

        Assert.Equal(UnavailableMicrophoneAnnouncement.WhileRecording, Press(notice, A, RecordingOpenOutcome.Live, fellBack: true));
    }

    [Theory]
    [InlineData(RecordingOpenOutcome.Live)]
    [InlineData(RecordingOpenOutcome.LeftToProcessing)]
    [InlineData(RecordingOpenOutcome.Reclaimed)]
    public void The_chosen_microphone_opening_ends_the_episode_whoever_owns_the_capture(RecordingOpenOutcome outcome)
    {
        var notice = Chosen(A);
        Press(notice, A, RecordingOpenOutcome.Live, fellBack: true);

        Assert.Equal(UnavailableMicrophoneAnnouncement.None, Press(notice, A, outcome, fellBack: false));

        // It went away again: a new episode, told again.
        Assert.Equal(UnavailableMicrophoneAnnouncement.WhileRecording, Press(notice, A, RecordingOpenOutcome.Live, fellBack: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_open_that_opened_nothing_reports_nothing_whatever_it_carries(bool staleFallback)
    {
        // Told, then an open that reached nothing: it neither ends the episode nor tells again.
        var told = Chosen(A);
        Press(told, A, RecordingOpenOutcome.Live, fellBack: true);
        Assert.Equal(UnavailableMicrophoneAnnouncement.None, Press(told, A, RecordingOpenOutcome.NotOpened, staleFallback));
        Assert.Equal(UnavailableMicrophoneAnnouncement.None, Press(told, A, RecordingOpenOutcome.Live, fellBack: true));

        // Not told yet: an open that reached nothing does not count as told either.
        var fresh = Chosen(A);
        Assert.Equal(UnavailableMicrophoneAnnouncement.None, Press(fresh, A, RecordingOpenOutcome.NotOpened, staleFallback));
        Assert.Equal(UnavailableMicrophoneAnnouncement.WhileRecording, Press(fresh, A, RecordingOpenOutcome.Live, fellBack: true));
    }

    [Fact]
    public void Choosing_another_microphone_and_then_the_first_again_without_dictating_starts_a_new_episode()
    {
        var notice = Chosen(A);
        Assert.Equal(UnavailableMicrophoneAnnouncement.WhileRecording, Press(notice, A, RecordingOpenOutcome.Live, fellBack: true));

        // The user picks B, reconnects A, and picks A again, all without a single dictation.
        notice.SelectionCommitted(B);
        notice.SelectionCommitted(A);

        // A goes away again.
        Assert.Equal(UnavailableMicrophoneAnnouncement.WhileRecording, Press(notice, A, RecordingOpenOutcome.Live, fellBack: true));
    }

    [Fact]
    public void Committing_the_same_microphone_again_keeps_the_episode()
    {
        // A save that changed something else (the AI cleanup switch, say) applies the same choice again.
        var notice = Chosen(A);
        Press(notice, A, RecordingOpenOutcome.Live, fellBack: true);

        notice.SelectionCommitted(A);

        Assert.Equal(UnavailableMicrophoneAnnouncement.None, Press(notice, A, RecordingOpenOutcome.Live, fellBack: true));
    }

    [Fact]
    public void Choosing_the_windows_default_ends_the_episode_and_is_never_told()
    {
        var notice = Chosen(A);
        Press(notice, A, RecordingOpenOutcome.Live, fellBack: true);

        notice.SelectionCommitted(null);
        Assert.Equal(UnavailableMicrophoneAnnouncement.None, Press(notice, null, RecordingOpenOutcome.Live, fellBack: false));
        Assert.Equal(UnavailableMicrophoneAnnouncement.None, Press(notice, "  ", RecordingOpenOutcome.Live, fellBack: true));

        notice.SelectionCommitted(A);
        Assert.Equal(UnavailableMicrophoneAnnouncement.WhileRecording, Press(notice, A, RecordingOpenOutcome.Live, fellBack: true));
    }

    [Fact]
    public void A_blank_choice_is_the_windows_default()
    {
        var notice = Chosen(null);
        var before = notice.Begin(null);

        notice.SelectionCommitted("   ");

        Assert.Equal(before, notice.Begin(""));
    }

    [Fact]
    public void A_late_fallback_asked_for_under_an_older_choice_is_told_about_that_capture_but_leaves_the_new_choice_alone()
    {
        var notice = Chosen(A);
        var slowOpen = notice.Begin(A); // the press, whose microphone takes seconds to open
        notice.SelectionCommitted(B); // the user chooses B meanwhile

        // That capture recorded from the Windows default instead of A: worth saying, about that capture.
        Assert.Equal(
            UnavailableMicrophoneAnnouncement.WhileRecording,
            notice.ReportOpen(slowOpen, Opened(RecordingOpenOutcome.Live, fellBack: true)));

        // B's own episode was not touched: its first fallback is still told, once.
        Assert.Equal(UnavailableMicrophoneAnnouncement.WhileRecording, Press(notice, B, RecordingOpenOutcome.Live, fellBack: true));
        Assert.Equal(UnavailableMicrophoneAnnouncement.None, Press(notice, B, RecordingOpenOutcome.Live, fellBack: true));
    }

    [Fact]
    public void A_late_report_never_re_arms_or_ends_the_episode_of_the_choice_committed_since()
    {
        var notice = Chosen(A);
        var slowSuccess = notice.Begin(A);
        var slowReclaimed = notice.Begin(A);
        notice.SelectionCommitted(B);
        Assert.Equal(UnavailableMicrophoneAnnouncement.WhileRecording, Press(notice, B, RecordingOpenOutcome.Live, fellBack: true));

        // A's chosen open succeeding late does not end B's episode, and a late reclaimed fallback says nothing.
        Assert.Equal(
            UnavailableMicrophoneAnnouncement.None,
            notice.ReportOpen(slowSuccess, Opened(RecordingOpenOutcome.Live, fellBack: false)));
        Assert.Equal(
            UnavailableMicrophoneAnnouncement.None,
            notice.ReportOpen(slowReclaimed, Opened(RecordingOpenOutcome.Reclaimed, fellBack: true)));

        Assert.Equal(UnavailableMicrophoneAnnouncement.None, Press(notice, B, RecordingOpenOutcome.Live, fellBack: true));
    }

    [Fact]
    public void A_late_report_that_comes_back_to_the_same_microphone_is_still_late()
    {
        // A, then B, then A again: a capture asked for under the first A belongs to neither of the choices since.
        var notice = Chosen(A);
        var first = notice.Begin(A);
        notice.SelectionCommitted(B);
        notice.SelectionCommitted(A);
        Assert.Equal(UnavailableMicrophoneAnnouncement.WhileRecording, Press(notice, A, RecordingOpenOutcome.Live, fellBack: true));

        // The first A's chosen open succeeding now must not end the second A's episode.
        Assert.Equal(
            UnavailableMicrophoneAnnouncement.None,
            notice.ReportOpen(first, Opened(RecordingOpenOutcome.Live, fellBack: false)));
        Assert.Equal(UnavailableMicrophoneAnnouncement.None, Press(notice, A, RecordingOpenOutcome.Live, fellBack: true));
    }

    [Fact]
    public void A_request_taken_halfway_through_a_commit_is_late_whichever_half_it_read()
    {
        // The activation read the settings on one side of a commit and the generation on the other.
        var newSettingsOldGeneration = Chosen(A);
        var oldGeneration = newSettingsOldGeneration.Begin(A).Generation;
        newSettingsOldGeneration.SelectionCommitted(B);
        Assert.Equal(
            UnavailableMicrophoneAnnouncement.WhileRecording,
            newSettingsOldGeneration.ReportOpen(
                new MicrophoneRequest(B, oldGeneration), Opened(RecordingOpenOutcome.Live, fellBack: true)));
        Assert.Equal(
            UnavailableMicrophoneAnnouncement.WhileRecording,
            Press(newSettingsOldGeneration, B, RecordingOpenOutcome.Live, fellBack: true));

        var oldSettingsNewGeneration = Chosen(A);
        oldSettingsNewGeneration.SelectionCommitted(B);
        var newGeneration = oldSettingsNewGeneration.Begin(B).Generation;
        Assert.Equal(
            UnavailableMicrophoneAnnouncement.WhileRecording,
            oldSettingsNewGeneration.ReportOpen(
                new MicrophoneRequest(A, newGeneration), Opened(RecordingOpenOutcome.Live, fellBack: true)));
        Assert.Equal(
            UnavailableMicrophoneAnnouncement.WhileRecording,
            Press(oldSettingsNewGeneration, B, RecordingOpenOutcome.Live, fellBack: true));
    }

    [Fact]
    public void A_late_fallback_that_processing_took_is_told_in_the_tray()
    {
        var notice = Chosen(A);
        var slowOpen = notice.Begin(A);
        notice.SelectionCommitted(B);

        Assert.Equal(
            UnavailableMicrophoneAnnouncement.AfterRecording,
            notice.ReportOpen(slowOpen, Opened(RecordingOpenOutcome.LeftToProcessing, fellBack: true)));
    }
}
