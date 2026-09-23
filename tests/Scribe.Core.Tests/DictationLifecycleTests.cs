using System.Collections.Concurrent;
using Scribe.Core.Audio;
using Scribe.Core.Lifecycle;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// The dictation controller's lifecycle core, driven through the same calls the controller makes, on a clock and timers
/// that only move when a test says so. Two of these replay confirmed defects exactly: a shutdown that missed a dictation
/// which had already returned to idle but was still finishing, and a duration ceiling tick queued for one recording that
/// ended the next.
/// </summary>
public sealed class DictationLifecycleTests
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(5);

    // A silence tracker started at 0 that has heard no speech asks to stop once this much time has passed.
    private const long LeadInLimitMs = 10_000;

    [Fact]
    public void An_idle_loop_starts_one_recording_and_turns_every_other_activation_away_until_it_returns_to_idle()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);

        var first = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("first"));
        Assert.Equal(ActivationDecision.Started, first.Decision);
        Assert.Equal(1, first.DictationId);
        Assert.Equal("first", first.Capture!.Name);
        Assert.Equal(DictationPhase.Recording, loop.Lifecycle.Phase);
        Assert.Same(first.Capture, loop.Lifecycle.CurrentCapture);

        Assert.Equal(ActivationDecision.AlreadyRecording, loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("x")).Decision);

        var stop = loop.Lifecycle.TryBeginProcessing();
        Assert.NotNull(stop.Admission);
        Assert.Equal(1, stop.Admission.DictationId);
        Assert.Same(first.Capture, stop.Admission.Capture);
        Assert.Equal(DictationPhase.Processing, loop.Lifecycle.Phase);

        var busy = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("x"));
        Assert.Equal(ActivationDecision.StillProcessing, busy.Decision);
        Assert.Equal(1, busy.RejectedWhileProcessing);
        Assert.Equal(2, loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("x")).RejectedWhileProcessing);

        var idle = loop.Lifecycle.ReturnToIdle(IdleDelay);
        Assert.Equal(2, idle.RejectedWhileProcessing);
        Assert.False(idle.Paused);
        Assert.Null(loop.Lifecycle.CurrentCapture);
        loop.Lifecycle.EndProcessing(stop.Admission);

        var second = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("second"));
        Assert.Equal(ActivationDecision.Started, second.Decision);
        Assert.Equal(2, second.DictationId);
        Assert.Equal(new[] { "first", "second" }, loop.CapturesCreated); // no factory ran for a rejected activation
    }

    [Fact]
    public void A_paused_loop_starts_nothing_and_reports_what_it_was_doing_when_the_pause_changed()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);

        Assert.Equal(
            new PauseChange(
                true,
                WasRecording: false,
                WasIdle: true,
                Sequence: 1,
                Presentation: new DictationPresentation(1, DictationPhase.Idle, Paused: true)),
            loop.Lifecycle.SetPaused(true));
        Assert.Equal(default, loop.Lifecycle.SetPaused(true));
        Assert.True(loop.Lifecycle.IsPaused);
        Assert.Equal(ActivationDecision.Paused, loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("x")).Decision);
        Assert.Empty(loop.CapturesCreated);

        loop.Lifecycle.SetPaused(false);
        loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("live"));
        Assert.Equal(new PauseChange(true, WasRecording: true, WasIdle: false, Sequence: 3), loop.Lifecycle.SetPaused(true));

        // A pause that lands mid-dictation takes effect on the way back to idle.
        var stop = loop.Lifecycle.TryBeginProcessing();
        Assert.True(loop.Lifecycle.ReturnToIdle(IdleDelay).Paused);
        loop.Lifecycle.EndProcessing(stop.Admission!);
    }

    [Fact]
    public void Every_pause_change_is_numbered_higher_than_the_last_and_a_request_that_changes_nothing_is_not_numbered()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);

        var sequences = new List<long>();
        foreach (var paused in new[] { true, true, false, false, true, false })
        {
            var change = loop.Lifecycle.SetPaused(paused);
            if (change.Changed)
            {
                sequences.Add(change.Sequence);
            }
            else
            {
                Assert.Equal(0, change.Sequence);
            }
        }

        Assert.Equal(new long[] { 1, 2, 3, 4 }, sequences);
    }

    [Fact]
    public void The_highest_numbered_pause_change_is_always_the_state_the_loop_ended_in()
    {
        // The controller forwards each change to the keyboard hook after the gate is released, and the hook keeps only
        // the highest number it has seen. That is only correct if the number and the state change are made together:
        // whichever change got the highest number must be the one that left the loop in its final state.
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);
        var requested = new ConcurrentBag<(long Sequence, bool Paused)>();
        using var start = new Barrier(4);

        var threads = Enumerable.Range(0, 4).Select(worker => new Thread(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 2_000; i++)
            {
                var paused = (i + worker) % 2 == 0;
                var change = loop.Lifecycle.SetPaused(paused);
                if (change.Changed)
                {
                    requested.Add((change.Sequence, paused));
                }
            }
        })).ToList();

        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());

        var all = requested.OrderBy(entry => entry.Sequence).ToList();
        Assert.NotEmpty(all);
        Assert.Equal(Enumerable.Range(1, all.Count).Select(n => (long)n), all.Select(entry => entry.Sequence));
        Assert.Equal(all[^1].Paused, loop.Lifecycle.IsPaused);

        // Consecutive numbers always alternate, because a request that changes nothing is never numbered.
        for (var i = 1; i < all.Count; i++)
        {
            Assert.NotEqual(all[i - 1].Paused, all[i].Paused);
        }
    }

    [Fact]
    public void A_capture_factory_that_throws_leaves_the_loop_exactly_as_it_was()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);
        var idleEvents = loop.IdleTimer.Events.Count;

        Assert.Throws<InvalidOperationException>(
            () => loop.Lifecycle.TryBeginRecording(() => throw new InvalidOperationException("no foreground window")));

        Assert.Equal(DictationPhase.Idle, loop.Lifecycle.Phase);
        Assert.Equal(0, loop.Lifecycle.CurrentDictationId);
        Assert.Equal(idleEvents, loop.IdleTimer.Events.Count); // the idle countdown was not disarmed either
    }

    [Fact]
    public void Every_change_the_shell_shows_is_numbered_higher_than_the_last_and_only_those_are_numbered()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);
        Assert.Equal(new DictationPresentation(0, DictationPhase.Idle, false), loop.Lifecycle.Presentation);

        var a = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("a"));
        Assert.Equal(0, loop.Lifecycle.Presentation.Revision); // shown only once its microphone opens
        Assert.Equal(
            new DictationPresentation(1, DictationPhase.Recording, false),
            loop.Lifecycle.TryPresentRecording(a.DictationId));
        Assert.Null(loop.Lifecycle.SetPaused(true).Presentation); // mid-recording: nothing to show yet

        var stop = loop.Lifecycle.TryBeginProcessing();
        Assert.Equal(new DictationPresentation(2, DictationPhase.Processing, false), stop.Presentation);
        Assert.Equal(default, loop.Lifecycle.TryBeginProcessing().Presentation); // an ignored stop shows nothing

        var idle = loop.Lifecycle.ReturnToIdle(IdleDelay);
        Assert.Equal(new DictationPresentation(3, DictationPhase.Idle, Paused: true), idle.Presentation);
        loop.Lifecycle.EndProcessing(stop.Admission!);

        Assert.Equal(
            new DictationPresentation(4, DictationPhase.Idle, Paused: false),
            loop.Lifecycle.SetPaused(false).Presentation);
        Assert.Null(loop.Lifecycle.SetPaused(false).Presentation); // no change, no number
        Assert.Equal(new DictationPresentation(4, DictationPhase.Idle, false), loop.Lifecycle.Presentation);
    }

    /// <summary>
    /// A pause can stop a recording while its device is still opening. When the open returns, that recording is over, and
    /// the processing that owns it has already shown Processing, or even idle; showing it as recording then would put back a
    /// state that has ended.
    /// </summary>
    [Fact]
    public void A_recording_that_ended_while_its_microphone_was_opening_is_never_shown_as_recording()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);

        var a = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("a"));
        Assert.True(loop.Lifecycle.SetPaused(true).WasRecording);
        var stop = loop.Lifecycle.TryBeginProcessing(); // the pause's stop, while the device is still opening
        Assert.Null(loop.Lifecycle.TryPresentRecording(a.DictationId));

        loop.Lifecycle.ReturnToIdle(IdleDelay);
        loop.Lifecycle.EndProcessing(stop.Admission!);
        Assert.Null(loop.Lifecycle.TryPresentRecording(a.DictationId));
        Assert.Equal(DictationPhase.Idle, loop.Lifecycle.Presentation.Phase);

        loop.Lifecycle.SetPaused(false);
        var b = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("b"));
        Assert.Null(loop.Lifecycle.TryPresentRecording(a.DictationId)); // a stale id never shows the newer recording
        Assert.NotNull(loop.Lifecycle.TryPresentRecording(b.DictationId));

        loop.Lifecycle.BeginShutdown();
        Assert.Null(loop.Lifecycle.TryPresentRecording(b.DictationId));
    }

    /// <summary>
    /// A device that fails to open sends its recording back to idle, but a pause can admit that recording to processing
    /// while the open is still failing. Returning to idle from the failed start then ended a phase the processing owned,
    /// with a newer number, so that stale idle could land on whatever started next.
    /// </summary>
    [Fact]
    public void A_failed_start_returns_to_idle_only_while_it_still_owns_the_recording()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);

        var failed = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("no device"));
        var abandoned = loop.Lifecycle.TryAbandonRecording(failed.DictationId, TimeSpan.FromMinutes(7));
        Assert.NotNull(abandoned);
        Assert.Equal(new DictationPresentation(1, DictationPhase.Idle, false), abandoned.Value.Presentation);
        Assert.Equal(DictationPhase.Idle, loop.Lifecycle.Phase);
        Assert.Null(loop.Lifecycle.CurrentCapture);
        Assert.Equal("change:00:07:00", loop.IdleTimer.Events[^1]); // back to idle, so the idle countdown runs again
        Assert.Null(loop.Lifecycle.TryAbandonRecording(failed.DictationId, IdleDelay)); // and only once

        var a = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("a"));
        loop.Lifecycle.SetPaused(true);
        var stop = loop.Lifecycle.TryBeginProcessing(); // the pause admitted it while its device was failing to open
        var revisionBefore = loop.Lifecycle.Presentation.Revision;

        Assert.Null(loop.Lifecycle.TryAbandonRecording(a.DictationId, IdleDelay));
        Assert.Equal(DictationPhase.Processing, loop.Lifecycle.Phase);
        Assert.Equal(revisionBefore, loop.Lifecycle.Presentation.Revision);

        loop.Lifecycle.ReturnToIdle(IdleDelay); // the processing that owns it returns it
        loop.Lifecycle.EndProcessing(stop.Admission!);
        loop.Lifecycle.SetPaused(false);
        var b = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("b"));

        Assert.Null(loop.Lifecycle.TryAbandonRecording(a.DictationId, IdleDelay)); // a late one never ends the next
        Assert.Equal(DictationPhase.Recording, loop.Lifecycle.Phase);
        Assert.Equal(b.DictationId, loop.Lifecycle.CurrentDictationId);
    }

    [Fact]
    public void A_recordings_warning_revision_exists_only_while_it_is_live_and_shown_as_recording()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);

        var a = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("a"));
        Assert.Null(loop.Lifecycle.TryGetRecordingPresentation(a.DictationId)); // its microphone has not opened yet
        var shown = loop.Lifecycle.TryPresentRecording(a.DictationId);
        Assert.Equal(shown, loop.Lifecycle.TryGetRecordingPresentation(a.DictationId));
        Assert.Null(loop.Lifecycle.TryGetRecordingPresentation(a.DictationId + 1));

        var stop = loop.Lifecycle.TryBeginProcessing();
        Assert.Null(loop.Lifecycle.TryGetRecordingPresentation(a.DictationId)); // over: nothing to warn about on the pill
        loop.Lifecycle.ReturnToIdle(IdleDelay);
        loop.Lifecycle.EndProcessing(stop.Admission!);

        var b = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("b"));
        loop.Lifecycle.TryPresentRecording(b.DictationId);
        Assert.Null(loop.Lifecycle.TryGetRecordingPresentation(a.DictationId)); // a late warning for A never lands on B
        Assert.NotNull(loop.Lifecycle.TryGetRecordingPresentation(b.DictationId));

        loop.Lifecycle.BeginShutdown();
        Assert.Null(loop.Lifecycle.TryGetRecordingPresentation(b.DictationId));
    }

    [Fact]
    public void An_opened_capture_goes_to_its_live_recording_else_to_the_processing_a_stop_admitted_else_to_nobody()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);

        var a = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("a"));
        var live = loop.Lifecycle.HandOffOpenedCapture(a.DictationId);
        Assert.Equal(OpenedCaptureOwner.Recording, live.Owner);
        Assert.Equal(new DictationPresentation(1, DictationPhase.Recording, false), live.Presentation);

        var stop = loop.Lifecycle.TryBeginProcessing();
        Assert.Equal(new OpenedCaptureHandOff(OpenedCaptureOwner.Processing, null), loop.Lifecycle.HandOffOpenedCapture(a.DictationId));
        loop.Lifecycle.BeginShutdown();
        Assert.Equal(OpenedCaptureOwner.Processing, loop.Lifecycle.HandOffOpenedCapture(a.DictationId).Owner); // still its
        loop.Lifecycle.ReturnToIdle(IdleDelay);
        loop.Lifecycle.EndProcessing(stop.Admission!);

        var other = new Loop();
        other.Lifecycle.Start(IdleDelay);
        var b = other.Lifecycle.TryBeginRecording(() => other.NewCapture("b"));
        other.Lifecycle.BeginShutdown(); // before any stop: nothing will ever be admitted
        Assert.Equal(new OpenedCaptureHandOff(OpenedCaptureOwner.Nobody, null), other.Lifecycle.HandOffOpenedCapture(b.DictationId));
    }

    [Fact]
    public void A_silence_stop_names_the_recording_its_tracker_belonged_to_and_fires_once()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);
        var a = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("a"));

        Assert.Null(loop.Lifecycle.UpdateSilence(0f, LeadInLimitMs)); // no tracker: nothing to decide
        Assert.True(loop.Lifecycle.TryAttachSilenceTracker(a.DictationId, new SilenceAutoStopTracker(0)));
        Assert.Null(loop.Lifecycle.UpdateSilence(0f, 1_000)); // quiet, but not for long enough yet

        var stop = loop.Lifecycle.UpdateSilence(0f, LeadInLimitMs);
        Assert.NotNull(stop);
        Assert.Equal(a.DictationId, stop.Value.DictationId);
        Assert.False(stop.Value.Tracker.HeardSpeech); // ended on the lead-in: nobody ever spoke
        Assert.Null(loop.Lifecycle.UpdateSilence(0f, LeadInLimitMs * 2)); // detached as it fired

        // What the controller does with it: a stop that can only end the recording it names.
        Assert.NotNull(loop.Lifecycle.TryBeginProcessing(stop.Value.DictationId).Admission);
    }

    [Fact]
    public void Only_the_live_recording_can_take_a_tracker_and_nothing_can_once_shutdown_began()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);

        Assert.False(loop.Lifecycle.TryAttachSilenceTracker(1, new SilenceAutoStopTracker(0))); // nothing is recording
        var a = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("a"));
        Assert.False(loop.Lifecycle.TryAttachSilenceTracker(a.DictationId + 1, new SilenceAutoStopTracker(0)));
        Assert.True(loop.Lifecycle.TryAttachSilenceTracker(a.DictationId, new SilenceAutoStopTracker(0)));

        loop.Lifecycle.BeginShutdown();
        Assert.Null(loop.Lifecycle.UpdateSilence(0f, LeadInLimitMs)); // shutdown dropped it
        Assert.False(loop.Lifecycle.TryAttachSilenceTracker(a.DictationId, new SilenceAutoStopTracker(0)));
    }

    /// <summary>
    /// The reviewed defect, both orders. A microphone fault or a pause can stop the recording between its microphone
    /// opening and the tracker attaching. Attached after that stop, the tracker was left behind: the next recording, a hold
    /// with no tracker of its own, was fed to it long past its limits, and it ended that recording a few buffers in.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_stop_racing_the_tracker_attach_never_leaves_a_tracker_for_the_next_recording(bool stopLandsFirst)
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);
        var a = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("toggle with auto-stop"));
        Assert.NotNull(loop.Lifecycle.TryPresentRecording(a.DictationId)); // its microphone has opened

        StopDecision<Capture> fault;
        if (stopLandsFirst)
        {
            fault = loop.Lifecycle.TryBeginProcessing(); // the fault, before the activation path attaches
            Assert.False(loop.Lifecycle.TryAttachSilenceTracker(a.DictationId, new SilenceAutoStopTracker(0)));
        }
        else
        {
            Assert.True(loop.Lifecycle.TryAttachSilenceTracker(a.DictationId, new SilenceAutoStopTracker(0)));
            fault = loop.Lifecycle.TryBeginProcessing(); // the fault, just after

            // Gone with the stop itself: A's capture keeps delivering levels until its processing stops it.
            Assert.Null(loop.Lifecycle.UpdateSilence(0f, LeadInLimitMs * 10));
        }

        loop.Lifecycle.ReturnToIdle(IdleDelay);
        loop.Lifecycle.EndProcessing(fault.Admission!);

        var b = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("hold, no auto-stop"));
        Assert.Null(loop.Lifecycle.UpdateSilence(0f, LeadInLimitMs * 10)); // long past any limit A's tracker had
        Assert.Equal(DictationPhase.Recording, loop.Lifecycle.Phase);
        Assert.Equal(b.DictationId, loop.Lifecycle.CurrentDictationId);
    }

    [Fact]
    public void A_stop_and_a_tracker_attach_racing_on_two_threads_never_leave_a_tracker_behind()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);
        using var start = new Barrier(2);

        for (var i = 0; i < 200; i++)
        {
            var recording = loop.Lifecycle.TryBeginRecording(() => new Capture("x"));
            StopDecision<Capture> stop = default;
            var attach = new Thread(() =>
            {
                start.SignalAndWait();
                loop.Lifecycle.TryAttachSilenceTracker(recording.DictationId, new SilenceAutoStopTracker(0));
            });
            var fault = new Thread(() =>
            {
                start.SignalAndWait();
                stop = loop.Lifecycle.TryBeginProcessing(recording.DictationId);
            });
            attach.Start();
            fault.Start();
            attach.Join();
            fault.Join();

            // Whichever order they ran in, nothing is left to feed.
            Assert.Null(loop.Lifecycle.UpdateSilence(0f, LeadInLimitMs * 10));
            loop.Lifecycle.ReturnToIdle(IdleDelay);
            loop.Lifecycle.EndProcessing(stop.Admission!);
        }

        Assert.Equal(DictationPhase.Idle, loop.Lifecycle.Phase);
    }

    [Fact]
    public void The_highest_numbered_change_is_always_what_the_loop_ended_in()
    {
        // The shell keeps only the highest number it has seen. That is only right if a number and the change it names are
        // made together: whichever change got the highest number must describe the state the loop was left in.
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);
        var shown = new ConcurrentBag<DictationPresentation>();
        using var start = new Barrier(2);

        var pauser = new Thread(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 2_000; i++)
            {
                if (loop.Lifecycle.SetPaused(i % 2 == 0).Presentation is { } change)
                {
                    shown.Add(change);
                }
            }
        });
        var dictations = new Thread(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 2_000; i++)
            {
                var recording = loop.Lifecycle.TryBeginRecording(() => new Capture("x"));
                if (recording.Capture is null)
                {
                    continue;
                }

                if (loop.Lifecycle.TryPresentRecording(recording.DictationId) is { } live)
                {
                    shown.Add(live);
                }

                var stop = loop.Lifecycle.TryBeginProcessing(recording.DictationId);
                shown.Add(stop.Presentation);
                shown.Add(loop.Lifecycle.ReturnToIdle(IdleDelay).Presentation);
                loop.Lifecycle.EndProcessing(stop.Admission!);
            }
        });

        pauser.Start();
        dictations.Start();
        pauser.Join();
        dictations.Join();

        var all = shown.OrderBy(change => change.Revision).ToList();
        Assert.Equal(Enumerable.Range(1, all.Count).Select(n => (long)n), all.Select(change => change.Revision));
        Assert.Equal(all[^1], loop.Lifecycle.Presentation);
        Assert.Equal(new DictationPresentation(all[^1].Revision, DictationPhase.Idle, loop.Lifecycle.IsPaused), all[^1]);
    }

    [Fact]
    public void Returning_to_idle_re_arms_the_idle_release_and_a_recording_disarms_it()
    {
        var loop = new Loop();

        loop.Lifecycle.RescheduleIdleRelease(IdleDelay); // before Start: nothing is armed
        Assert.Empty(loop.IdleTimer.Events);

        loop.Lifecycle.Start(IdleDelay);
        Assert.Equal("change:00:05:00", loop.IdleTimer.Events[^1]);

        loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("a"));
        Assert.Equal($"change:{Timeout.InfiniteTimeSpan}", loop.IdleTimer.Events[^1]);

        var eventsWhileRecording = loop.IdleTimer.Events.Count;
        loop.Lifecycle.RescheduleIdleRelease(TimeSpan.FromMinutes(3)); // a settings save mid-recording
        Assert.Equal(eventsWhileRecording, loop.IdleTimer.Events.Count);

        var stop = loop.Lifecycle.TryBeginProcessing();
        loop.Lifecycle.ReturnToIdle(TimeSpan.FromMinutes(7));
        Assert.Equal("change:00:07:00", loop.IdleTimer.Events[^1]);
        loop.Lifecycle.EndProcessing(stop.Admission!);

        loop.Lifecycle.RescheduleIdleRelease(Timeout.InfiniteTimeSpan); // the user turned idle release off
        Assert.Equal($"change:{Timeout.InfiniteTimeSpan}", loop.IdleTimer.Events[^1]);
    }

    [Fact]
    public void A_stop_is_admitted_only_for_the_live_recording_it_names()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);

        var idleStop = loop.Lifecycle.TryBeginProcessing();
        Assert.Null(idleStop.Admission);
        Assert.Equal(DictationPhase.Idle, idleStop.ObservedPhase);

        loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("a"));
        var wrongOne = loop.Lifecycle.TryBeginProcessing(expectedDictationId: 99);
        Assert.Null(wrongOne.Admission);
        Assert.Equal(DictationPhase.Recording, loop.Lifecycle.Phase);

        var stop = loop.Lifecycle.TryBeginProcessing(expectedDictationId: 1);
        Assert.NotNull(stop.Admission);

        var redundant = loop.Lifecycle.TryBeginProcessing();
        Assert.Null(redundant.Admission);
        Assert.Equal(DictationPhase.Processing, redundant.ObservedPhase);
        loop.Lifecycle.ReturnToIdle(IdleDelay);
        loop.Lifecycle.EndProcessing(stop.Admission);
    }

    [Fact]
    public void Only_the_stop_that_is_admitted_disarms_the_duration_ceiling()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);

        var a = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("a"));
        loop.Lifecycle.ArmDurationLimit(a.DictationId, Ceiling);
        var stopA = loop.Lifecycle.TryBeginProcessing();
        Assert.Equal($"change:{Timeout.InfiniteTimeSpan}", loop.DurationTimer.Events[^1]);
        loop.Lifecycle.ReturnToIdle(IdleDelay);
        loop.Lifecycle.EndProcessing(stopA.Admission!);

        var b = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("b"));
        loop.Lifecycle.ArmDurationLimit(b.DictationId, Ceiling);

        // A stop meant for A, arriving late: ignored, and B's ceiling stays armed.
        Assert.Null(loop.Lifecycle.TryBeginProcessing(expectedDictationId: a.DictationId).Admission);
        Assert.Equal("change:00:05:00", loop.DurationTimer.Events[^1]);
    }

    /// <summary>
    /// The reviewed defect: recording A's ceiling tick was already queued when A stopped, B started and armed the same
    /// ceiling, and the late tick then ended B seconds into it. The timer drops the stale tick, and the lifecycle refuses
    /// it anyway because B has not run for its ceiling.
    /// </summary>
    [Fact]
    public void A_late_ceiling_tick_queued_for_one_recording_never_ends_the_next()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);

        var a = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("a"));
        loop.Lifecycle.ArmDurationLimit(a.DictationId, Ceiling);
        loop.Clock.Advance(Ceiling); // A's tick is due and queued to the pool, but not yet delivered
        var stopA = loop.Lifecycle.TryBeginProcessing();
        loop.Lifecycle.ReturnToIdle(IdleDelay);
        loop.Lifecycle.EndProcessing(stopA.Admission!);

        var b = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("b"));
        loop.Lifecycle.ArmDurationLimit(b.DictationId, Ceiling);
        loop.Clock.Advance(TimeSpan.FromMinutes(1));

        loop.DurationTimer.Fire(); // A's stale tick finally runs

        Assert.Equal(0, loop.CeilingTicks);
        Assert.Null(loop.Lifecycle.TryAcceptDurationLimit()); // what the controller asks if a tick ever got through
        Assert.Equal(DictationPhase.Recording, loop.Lifecycle.Phase);

        loop.Clock.Advance(TimeSpan.FromMinutes(4));
        loop.DurationTimer.Fire(); // B's own tick

        Assert.Equal(1, loop.CeilingTicks);
        Assert.Equal(b.DictationId, loop.Lifecycle.TryAcceptDurationLimit());
    }

    [Fact]
    public void A_ceiling_armed_for_an_earlier_recording_is_never_honored_for_a_later_one()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);

        var a = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("a"));
        loop.Lifecycle.ArmDurationLimit(a.DictationId, Ceiling);
        var stopA = loop.Lifecycle.TryBeginProcessing();
        loop.Lifecycle.ReturnToIdle(IdleDelay);
        loop.Lifecycle.EndProcessing(stopA.Admission!);

        loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("b")); // the ceiling setting was turned off meanwhile
        loop.Clock.Advance(Ceiling * 2);

        Assert.Null(loop.Lifecycle.TryAcceptDurationLimit());
    }

    [Fact]
    public void A_recording_that_starts_during_an_idle_release_invalidates_its_claim()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);
        var steps = new List<string>();

        var outcome = loop.Lifecycle.RunIdleRelease(
            anythingResident: () => true,
            unload: () =>
            {
                steps.Add("unload");
                loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("mid-release"));
            },
            compact: () => steps.Add("compact"),
            announce: () => steps.Add("announce"));

        Assert.Equal(IdleReleaseOutcome.UnloadedThenActivityResumed, outcome);
        Assert.Equal(new[] { "unload" }, steps);
    }

    /// <summary>
    /// Processing tracking is independent of the phase it publishes: after a dictation returns to idle but before it has
    /// ended, an idle release must still see it as running. Clearing the tracking on the way back to idle fails this.
    /// </summary>
    [Fact]
    public void An_idle_release_is_refused_while_a_dictation_that_returned_to_idle_is_still_finishing()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);
        loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("a"));
        var stop = loop.Lifecycle.TryBeginProcessing();
        loop.Lifecycle.ReturnToIdle(IdleDelay);

        Assert.Equal(IdleReleaseOutcome.NotIdle, loop.ReleaseIdle());

        loop.Lifecycle.EndProcessing(stop.Admission!);

        Assert.Equal(IdleReleaseOutcome.Released, loop.ReleaseIdle());
    }

    [Fact]
    public void Only_one_idle_release_runs_at_a_time_and_buffers_go_even_with_no_model_resident()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);
        IdleReleaseOutcome? nested = null;
        var buffersReleased = 0;

        var outcome = loop.Lifecycle.RunIdleRelease(
            anythingResident: () => false,
            unload: () => { },
            compact: () => { },
            announce: () => { },
            releaseRetained: () =>
            {
                buffersReleased++;
                nested = loop.ReleaseIdle(); // a pause-triggered release racing the timer's
            });

        Assert.Equal(IdleReleaseOutcome.NothingResident, outcome);
        Assert.Equal(IdleReleaseOutcome.NotIdle, nested);
        Assert.Equal(1, buffersReleased);
        Assert.Equal(IdleReleaseOutcome.Released, loop.ReleaseIdle()); // the claim was released afterwards
    }

    [Fact]
    public void Once_shutdown_begins_nothing_new_starts()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);
        var recording = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("live"));

        Assert.True(loop.Lifecycle.BeginShutdown());
        Assert.False(loop.Lifecycle.BeginShutdown());
        Assert.True(loop.Lifecycle.IsClosing);

        var durationEvents = loop.DurationTimer.Events.Count;
        loop.Lifecycle.ArmDurationLimit(recording.DictationId, Ceiling);
        Assert.Equal(durationEvents, loop.DurationTimer.Events.Count);
        Assert.Null(loop.Lifecycle.TryBeginProcessing().Admission);
        Assert.Null(loop.Lifecycle.TryAcceptDurationLimit());

        var idleEvents = loop.IdleTimer.Events.Count;
        loop.Lifecycle.ReturnToIdle(IdleDelay);
        Assert.Equal(idleEvents, loop.IdleTimer.Events.Count);
        Assert.Equal(ActivationDecision.Closing, loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("x")).Decision);
        Assert.Equal(IdleReleaseOutcome.NotIdle, loop.ReleaseIdle());
        Assert.Equal(new[] { "live" }, loop.CapturesCreated);
    }

    /// <summary>
    /// The confirmed shutdown defect and its order: a dictation that has published idle is still finishing, so shutdown
    /// must wait for it, and only then complete history and release the token source. Clearing processing tracking on the
    /// way back to idle, or completing history before the wait, fails this.
    /// </summary>
    [Fact]
    public void Shutdown_waits_for_a_dictation_that_already_returned_to_idle_and_only_then_completes_history()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);
        loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("a"));
        var admission = loop.Lifecycle.TryBeginProcessing().Admission!;
        loop.Lifecycle.ReturnToIdle(IdleDelay); // published idle; its own cleanup is still running

        var order = new ConcurrentQueue<string>();
        var writer = new RecordingWriter(order);
        DictationShutdown? result = null;
        var shutdown = BlockedThreads.Start(() => result = loop.Lifecycle.Shutdown(
            [new TeardownStep("stop inputs", () => order.Enqueue("stop inputs"))],
            writer,
            BlockedThreads.SafetyTimeout,
            BlockedThreads.SafetyTimeout));
        BlockedThreads.WaitUntilBlocked(shutdown);

        Assert.Equal(new[] { "stop inputs" }, order); // waiting for processing; history not completed yet
        Assert.True(admission.Lifetime.IsCancellationRequested);
        Assert.False(loop.Lifecycle.LifetimeDisposed);

        order.Enqueue("processing ended");
        loop.Lifecycle.EndProcessing(admission);
        BlockedThreads.Join(shutdown);

        Assert.Equal(new[] { "stop inputs", "processing ended", "history completed" }, order);
        Assert.True(result!.Value.Processing.Drained);
        Assert.Null(result.Value.StillRunningDictationId);
        Assert.True(loop.Lifecycle.LifetimeDisposed);
    }

    /// <summary>
    /// The last dictation queues its history on its way out, after shutdown began. With the real writer, that entry is
    /// committed, because the writer completes only after processing drained. Completing it first refuses the entry.
    /// </summary>
    [Fact]
    public void History_queued_by_a_dictation_still_finishing_at_shutdown_is_committed_not_refused()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);
        loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("a"));
        var admission = loop.Lifecycle.TryBeginProcessing().Admission!;
        var history = new ListHistory();
        using var writer = new HistoryWriter(history, new CapturingLogger<HistoryWriter>());

        DictationShutdown? result = null;
        var shutdown = BlockedThreads.Start(() => result = loop.Lifecycle.Shutdown(
            [], writer, BlockedThreads.SafetyTimeout, BlockedThreads.SafetyTimeout));
        BlockedThreads.WaitUntilBlocked(shutdown);

        var entry = new HistoryEntry(0, DateTimeOffset.UnixEpoch, "last words", 1_000, 50);
        Assert.True(writer.Enqueue(entry, null, admission.DictationId));
        loop.Lifecycle.ReturnToIdle(IdleDelay);
        loop.Lifecycle.EndProcessing(admission);
        BlockedThreads.Join(shutdown);

        Assert.Equal(new HistoryDrainResult(Drained: true, StillWriting: 0, Abandoned: 0), result!.Value.History);
        Assert.Same(entry, Assert.Single(history.Added));
    }

    /// <summary>
    /// The interleaving from the original report, now at the lifecycle: shutdown has begun and is waiting when the
    /// dictation returns to idle and re-arms the idle timer. Nothing throws, nothing is armed on the closed timer, and the
    /// wait still lasts until the dictation ends.
    /// </summary>
    [Fact]
    public void A_dictation_returning_to_idle_after_shutdown_began_is_safe_and_still_waited_for()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);
        loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("a"));
        var admission = loop.Lifecycle.TryBeginProcessing().Admission!;

        DictationShutdown? result = null;
        var shutdown = BlockedThreads.Start(() => result = loop.Lifecycle.Shutdown(
            [], new RecordingWriter(new ConcurrentQueue<string>()), BlockedThreads.SafetyTimeout, BlockedThreads.SafetyTimeout));
        BlockedThreads.WaitUntilBlocked(shutdown);

        loop.Lifecycle.ReturnToIdle(IdleDelay);
        Assert.Equal("dispose", loop.IdleTimer.Events[^1]); // closed before the wait, and never re-armed after it
        Assert.Null(result);

        loop.Lifecycle.EndProcessing(admission);
        BlockedThreads.Join(shutdown);
        Assert.True(result!.Value.Processing.Drained);
    }

    [Fact]
    public void A_dictation_that_outlives_the_wait_is_named_and_keeps_its_token_source()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);
        var recording = loop.Lifecycle.TryBeginRecording(() => loop.NewCapture("slow"));
        var admission = loop.Lifecycle.TryBeginProcessing().Admission!;
        var order = new ConcurrentQueue<string>();

        var result = loop.Lifecycle.Shutdown([], new RecordingWriter(order), TimeSpan.Zero, TimeSpan.Zero);

        Assert.False(result.Processing.Drained);
        Assert.Equal(1, result.Processing.StillRunning);
        Assert.Equal(recording.DictationId, result.StillRunningDictationId);
        Assert.Equal(new[] { "history completed" }, order); // history still completes, with its own bound
        Assert.False(loop.Lifecycle.LifetimeDisposed); // the running dictation still holds its token
        Assert.True(admission.Lifetime.IsCancellationRequested);

        loop.Lifecycle.EndProcessing(admission); // ending late is harmless
    }

    [Fact]
    public void Shutdown_runs_once_and_a_failing_step_never_skips_the_rest()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);
        var order = new ConcurrentQueue<string>();
        var writer = new RecordingWriter(order);
        var failures = new List<string>();

        var first = loop.Lifecycle.Shutdown(
            [
                new TeardownStep("unhook", () => throw new InvalidOperationException("hook already gone")),
                new TeardownStep("stop capture", () => order.Enqueue("stop capture")),
            ],
            writer,
            BlockedThreads.SafetyTimeout,
            BlockedThreads.SafetyTimeout,
            (step, _) => failures.Add(step));
        var second = loop.Lifecycle.Shutdown([], writer, BlockedThreads.SafetyTimeout, BlockedThreads.SafetyTimeout);

        Assert.Equal(new[] { "unhook" }, failures);
        Assert.Equal(new[] { "stop capture", "history completed" }, order);
        Assert.False(first.AlreadyShutDown);
        Assert.True(first.Processing.Drained);
        Assert.True(second.AlreadyShutDown);
        Assert.Equal(1, writer.Completions);
        Assert.True(loop.IdleTimer.IsDisposed);
        Assert.True(loop.DurationTimer.IsDisposed);
    }

    [Fact]
    public void A_history_writer_that_fails_to_complete_is_reported_and_shutdown_still_finishes()
    {
        var loop = new Loop();
        loop.Lifecycle.Start(IdleDelay);
        var failures = new List<string>();

        var result = loop.Lifecycle.Shutdown(
            [],
            new RecordingWriter(new ConcurrentQueue<string>()) { FailCompletion = true },
            BlockedThreads.SafetyTimeout,
            BlockedThreads.SafetyTimeout,
            (step, _) => failures.Add(step));

        Assert.Equal(new[] { "complete history" }, failures);
        Assert.Null(result.History);
        Assert.True(loop.Lifecycle.LifetimeDisposed);
    }

    private sealed record Capture(string Name);

    // The controller's side of the lifecycle: a manual clock, and counters for the two timer callbacks.
    private sealed class Loop
    {
        private readonly List<string> _captures = [];
        private int _ceilingTicks;

        public Loop()
        {
            Lifecycle = new DictationLifecycle<Capture>(() => { }, () => Interlocked.Increment(ref _ceilingTicks), Clock);
        }

        public ManualTimeProvider Clock { get; } = new();

        public DictationLifecycle<Capture> Lifecycle { get; }

        // Created in this order by the lifecycle's constructor.
        public ManualTimer IdleTimer => Clock.Timers[0];

        public ManualTimer DurationTimer => Clock.Timers[1];

        public int CeilingTicks => Volatile.Read(ref _ceilingTicks);

        public IReadOnlyList<string> CapturesCreated => _captures;

        public Capture NewCapture(string name)
        {
            _captures.Add(name);
            return new Capture(name);
        }

        public IdleReleaseOutcome ReleaseIdle() =>
            Lifecycle.RunIdleRelease(() => true, () => { }, () => { }, () => { });
    }

    private sealed class RecordingWriter(ConcurrentQueue<string> order) : IHistoryWriter
    {
        private int _completions;

        public bool FailCompletion { get; init; }

        public int Completions => Volatile.Read(ref _completions);

        public bool Enqueue(HistoryEntry entry, CapturedAudio? audio, long dictationId = 0) => true;

        public bool WaitForAcceptedWrites(TimeSpan timeout) => true;

        public HistoryDrainResult Complete(TimeSpan timeout)
        {
            Interlocked.Increment(ref _completions);
            if (FailCompletion)
            {
                throw new IOException("the database went away");
            }

            order.Enqueue("history completed");
            return new HistoryDrainResult(true, 0, 0);
        }
    }

    private sealed class ListHistory : IHistoryRepository
    {
        private readonly ConcurrentQueue<HistoryEntry> _added = new();

        public IReadOnlyList<HistoryEntry> Added => [.. _added];

        public HistoryEntry Add(HistoryEntry entry) => Add(entry, null);

        public HistoryEntry Add(HistoryEntry entry, CapturedAudio? audio)
        {
            _added.Enqueue(entry);
            return entry;
        }

        public long AddAudioBlob(CapturedAudio audio) => 0;

        public IReadOnlyList<HistoryEntry> GetRecent(int limit = 100) => [.. _added];

        public CapturedAudio? GetAudio(long blobId) => null;

        public void SetAiRating(long id, AiRating rating)
        {
        }

        public void Delete(long id)
        {
        }

        public void Clear()
        {
        }

        public int PruneOlderThan(DateTimeOffset cutoffUtc) => 0;
    }
}
