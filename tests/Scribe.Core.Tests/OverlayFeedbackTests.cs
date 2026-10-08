using Scribe.Core.Lifecycle;
using Scribe.Core.Overlay;
using Scribe.Core.Tray;

namespace Scribe.Core.Tests;

public sealed class OverlayFeedbackTests
{
    [Fact]
    public void Every_problem_uses_notice_routing_when_the_helper_is_unavailable_not_just_when_the_setting_is_off()
    {
        foreach (var problem in Enum.GetValues<DictationProblem>())
        {
            var expected = DictationProblemRouting.Decide(problem, recordingIndicatorOn: false);
            foreach (var availability in new OverlayAvailability?[]
                     {
                         null, OverlayAvailability.Initial, new(false, true, 1),
                     })
            {
                Assert.Equal(expected, OverlayFeedback.DecideProblem(problem, indicatorOn: true, availability));
                Assert.Equal(expected, OverlayFeedback.DecideRecordingWarning(
                    problem, indicatorOn: true, availability, recordingRevision: 5, lastRenderedRevision: 5));
            }

            var ready = new OverlayAvailability(true, false, 1);
            Assert.Equal(expected, OverlayFeedback.DecideProblem(problem, indicatorOn: false, ready));
            Assert.Equal(DictationProblemRouting.Decide(problem, true), OverlayFeedback.DecideProblem(problem, true, ready));
        }
    }

    [Theory]
    [InlineData(DictationProblem.MicrophoneMuted)]
    [InlineData(DictationProblem.FallbackMicrophone)]
    public void A_queued_warning_rechecks_both_availability_and_its_recording_revision(DictationProblem problem)
    {
        var ready = new OverlayAvailability(true, false, 1);
        Assert.Equal(DictationProblemSurface.RecordingPill, OverlayFeedback.DecideRecordingWarning(problem, true, ready, 5, 5));
        Assert.Equal(DictationProblemSurface.Notice, OverlayFeedback.DecideRecordingWarning(problem, false, ready, 5, 5));
        Assert.Equal(DictationProblemSurface.Notice, OverlayFeedback.DecideRecordingWarning(problem, true, new(false, true, 2), 5, 5));
        Assert.Equal(DictationProblemSurface.Notice, OverlayFeedback.DecideRecordingWarning(problem, true, ready, 5, 6));
    }

    [Fact]
    public void A_failure_notice_is_once_per_real_episode_and_only_for_current_demand()
    {
        var feedback = new OverlayFeedback();
        var failed = new OverlayAvailability(false, true, 1);
        Assert.False(feedback.TryTakeFailureNotice(failed, true, OverlayDemand.None, 5, 5));
        Assert.False(feedback.TryTakeFailureNotice(failed, true, OverlayDemand.Transient, 5, 5));
        Assert.False(feedback.TryTakeFailureNotice(failed, false, OverlayDemand.Sustained, 5, 5));
        Assert.False(feedback.TryTakeFailureNotice(failed, true, OverlayDemand.Sustained, 5, 6));
        Assert.False(feedback.TryTakeFailureNotice(failed, true, OverlayDemand.Sustained, 0, 0));
        Assert.True(feedback.TryTakeFailureNotice(failed, true, OverlayDemand.Sustained, 6, 6));
        Assert.False(feedback.TryTakeFailureNotice(failed, true, OverlayDemand.Sustained, 7, 7));
        Assert.False(feedback.TryTakeFailureNotice(new(true, false, 1), true, OverlayDemand.Sustained, 8, 8));
        Assert.True(feedback.TryTakeFailureNotice(new(false, true, 2), true, OverlayDemand.Sustained, 9, 9));
        Assert.False(feedback.TryTakeFailureNotice(OverlayAvailability.Initial, true, OverlayDemand.Sustained, 10, 10));
    }

    [Fact]
    public void Repeated_early_crashes_are_one_notice_until_stable_recovery()
    {
        var lifetime = new OverlayHelperLifetime();
        var feedback = new OverlayFeedback();
        lifetime.OnLaunchCompleted(0, OverlayLaunchResult.Failed, OverlayDemand.None);
        Assert.True(feedback.TryTakeFailureNotice(lifetime.Availability, true, OverlayDemand.Sustained, 1, 1));
        lifetime.OnLaunchCompleted(1_000, OverlayLaunchResult.Launched, OverlayDemand.Sustained);
        Assert.False(feedback.TryTakeFailureNotice(lifetime.Availability, true, OverlayDemand.Sustained, 1, 1));
        lifetime.OnHelperChanged(2_000, OverlayDemand.Sustained, OverlayHelperObservation.Lost(2_000));
        Assert.False(feedback.TryTakeFailureNotice(lifetime.Availability, true, OverlayDemand.Sustained, 2, 2));
        lifetime.OnLaunchCompleted(4_000, OverlayLaunchResult.Launched, OverlayDemand.Sustained);
        lifetime.TakeDueWork(14_000, OverlayDemand.Sustained, OverlayHelperObservation.Alive);
        Assert.False(feedback.TryTakeFailureNotice(lifetime.Availability, true, OverlayDemand.Sustained, 2, 2));
        lifetime.OnHelperChanged(15_000, OverlayDemand.Sustained, OverlayHelperObservation.Lost(15_000));
        Assert.True(feedback.TryTakeFailureNotice(lifetime.Availability, true, OverlayDemand.Sustained, 3, 3));
    }

    [Fact]
    public void Outcome_delivery_failure_falls_back_once_and_an_already_shown_problem_is_not_duplicated()
    {
        var outcome = PillOutcome.Of(null, false, null, new DictationProblemReport(DictationProblem.RecognitionFailed))!;
        var feedback = new OverlayOutcomeFeedback(5, outcome, problemAlreadyNoticed: false);
        var notice = feedback.TakeNotice(delivered: false, lastRenderedRevision: 5);
        Assert.NotNull(notice);
        Assert.Equal("Nothing typed", notice.Title);
        Assert.Contains("Something went wrong", notice.Body, StringComparison.Ordinal);
        Assert.Null(feedback.TakeNotice(false, 5));

        var alreadyNoticed = new OverlayOutcomeFeedback(5, outcome, problemAlreadyNoticed: true);
        Assert.Null(alreadyNoticed.TakeNotice(false, 5));
        var noticedAfterQueueing = new OverlayOutcomeFeedback(5, outcome, problemAlreadyNoticed: false);
        noticedAfterQueueing.NoteProblemNoticed();
        Assert.Null(noticedAfterQueueing.TakeNotice(false, 5));
        Assert.Null(new OverlayOutcomeFeedback(5, outcome, false).TakeNotice(true, 5));
    }

    [Fact]
    public void Every_problem_normally_left_to_an_outcome_has_a_failed_delivery_notice()
    {
        foreach (var problem in Enum.GetValues<DictationProblem>())
        {
            if (DictationProblemRouting.Decide(problem, true) != DictationProblemSurface.PillOutcome)
            {
                continue;
            }

            var outcome = PillOutcome.Of(null, false, null, new DictationProblemReport(problem));
            Assert.NotNull(outcome);
            var notice = new OverlayOutcomeFeedback(5, outcome, false).TakeNotice(false, 5);
            Assert.NotNull(notice);
            Assert.InRange(notice.Body.Length, 1, 255);
        }
    }

    [Fact]
    public void Cleanup_that_did_not_run_has_a_notice_even_without_a_controller_error_report()
    {
        var outcome = PillOutcome.Of(new(true, "unicode", 10, 10), true,
            new Cleanup.CleanupResult("x", Cleanup.CleanupOutcome.Failed, "AI cleanup timed out."), null)!;
        var notice = new OverlayOutcomeFeedback(5, outcome, false).TakeNotice(false, 5);

        Assert.NotNull(notice);
        Assert.Equal("Typed without AI cleanup", notice.Title);
        Assert.Equal(TrayNoticeAction.OpenSettingsAiCleanup, notice.Action);
        Assert.Equal(TrayNoticeKind.Warning, notice.Kind);
    }

    [Fact]
    public void A_delayed_failed_delivery_cannot_notify_or_rebind_the_recovery_copy_of_a_newer_revision()
    {
        var posted = new List<Action>();
        var notices = new List<TrayNotice>();
        var outcome = PillOutcome.Of(new(false, "none", 0, 10), false, null, null)!;
        var feedback = new OverlayOutcomeFeedback(5, outcome, false);
        var relay = new PresentationRelay<int>(posted.Add, _ => { }, () => false);
        relay.Publish(5, 5);
        posted[0]();
        relay.PublishIfCurrent(5, () =>
        {
            if (feedback.TakeNotice(false, relay.LastRenderedRevision) is { } notice)
            {
                notices.Add(notice);
            }
        });
        relay.Publish(6, 6);
        posted[2](); // The newer recording renders before the old consumer completion reaches the dispatcher.
        posted[1]();

        Assert.Empty(notices);
        Assert.Null(feedback.TakeNotice(false, 6));
        var live = new OverlayOutcomeFeedback(6, outcome, false).TakeNotice(false, 6);
        Assert.NotNull(live);
        Assert.Equal(TrayNoticeAction.CopyLastDictation, live.Action);
    }

    [Fact]
    public void A_delayed_full_problem_notice_cannot_suppress_a_newer_outcome_s_fallback()
    {
        var outcome = PillOutcome.Of(new(false, "none", 0, 10), false, null, null)!;
        var feedback = new OverlayOutcomeFeedback(6, outcome, false);
        if (OverlayFeedback.IsCurrent(5, 6))
        {
            feedback.NoteProblemNoticed();
        }

        Assert.NotNull(feedback.TakeNotice(false, 6));
        Assert.False(OverlayFeedback.IsCurrent(0, 0));
        Assert.True(OverlayFeedback.IsCurrent(6, 6));
    }

    [Fact]
    public void No_success_check_turns_into_a_warning_when_the_indicator_is_unavailable()
    {
        var typed = PillOutcome.Of(new(true, "unicode", 10, 10), false, null, null)!;
        Assert.Null(new OverlayOutcomeFeedback(5, typed, false).TakeNotice(false, 5));
    }

    [Fact]
    public void Failure_notice_has_fixed_safe_text_and_fits_the_tray()
    {
        var notice = OverlayFeedback.FailureNotice();
        Assert.InRange(notice.Title.Length, 1, 63);
        Assert.InRange(notice.Body.Length, 1, 255);
        Assert.Equal(TrayNoticeKind.Warning, notice.Kind);
        Assert.Equal(TrayNoticeAction.OpenSettingsDiagnostics, notice.Action);
        Assert.Contains("still running", notice.Body, StringComparison.Ordinal);
        Assert.DoesNotContain('\u2013', notice.Body);
        Assert.DoesNotContain('\u2014', notice.Body);
    }
}
