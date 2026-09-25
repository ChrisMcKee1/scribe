using Scribe.Core.Hotkeys;
using Scribe.Core.Lifecycle;
using Scribe.Core.Models;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// The hotkey and the dictation lifecycle together, wired as the controller wires them (the controller itself has no
/// tests): the hook's transitions reach a real <see cref="DictationLifecycle{TCapture}"/> through the service's real
/// consumer step, each recording keeps the press that started it, every stop goes through
/// <see cref="DictationStopPolicy.BeginStop"/> and every start through <see cref="DictationStartPolicy.BeginRecording"/>,
/// the controller's own first steps. For every stop Scribe makes itself, with a hold or a toggle binding: the first press
/// after the stopped dictation has processed starts a dictation; an activation handled while it still processes is refused
/// and leaves no latch behind (a press still queued when the processing finishes can start the next dictation when it is
/// handled); and no queued press can start a recording whose release the hook will not report, because a stop releases
/// only the press that started the recording it ended, and only once the stop is admitted.
/// </summary>
public sealed class HotkeyStopReleaseTests
{
    private const uint PageDown = 0x22;
    private const uint PageUp = 0x21;

    private static readonly HotkeyBinding PageDownToggle = HotkeyBinding.DefaultDictation with { Mode = HotkeyMode.Toggle };

    [Fact]
    public void A_release_names_one_press_and_leaves_a_newer_press_its_latch()
    {
        // The stop Scribe made names the press that started its recording. Since then that press was released and the key
        // pressed again: the newer press stays current (no new epoch) and keeps its latch, so its release is still sent.
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation);
        h.Down(PageDown);
        var first = Assert.Single(h.TakeTransitions());
        h.Up(PageDown);
        h.Down(PageDown);
        var queued = h.TakeTransitions();
        Assert.Equal(
            new[] { HotkeyTransition.Deactivated, HotkeyTransition.Activated },
            queued.Select(t => t.Transition).ToArray());

        h.Service.CancelToggle(first.Activation);
        h.Engine.OnWake();

        Assert.True(h.Engine.HoldsAnyLatch);
        Assert.True(h.WouldDispatch(queued[1]));
        Assert.True(h.Up(PageDown).Suppress);
        Assert.Equal(HotkeyTransition.Deactivated, Assert.Single(h.TakeTransitions()).Transition);
    }

    [Theory]
    [InlineData(true)] // the press being released still owns the dictation
    [InlineData(false)] // it was already released by the hook, and nothing owns one
    public void A_release_forgets_a_latch_the_arbiter_refused(bool ownerStillHeld)
    {
        // Page Down owns the dictation and Page Up is tapped meanwhile: the arbiter refuses it, but its machine latches
        // the toggle. Once Page Down's press is released no latch owns anything, so Page Up's next tap starts a
        // dictation instead of being taken as the toggle-off of one that never started.
        var toggledOnly = HotkeyBinding.DefaultDictationOnly with { Mode = HotkeyMode.Toggle };
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation, toggledOnly);
        h.Down(PageDown);
        var owner = Assert.Single(h.TakeTransitions());
        h.Tap(PageUp);
        if (!ownerStillHeld)
        {
            h.Up(PageDown);
        }

        h.TakeTransitions();
        h.Service.CancelToggle(owner.Activation);
        h.Engine.OnWake();
        Assert.False(h.Engine.HoldsAnyLatch);

        h.Tap(PageUp);
        var start = Assert.Single(h.TakeTransitions());
        Assert.Equal((HotkeyTransition.Activated, HotkeyTrigger.DictationOnly), (start.Transition, start.Trigger));
    }

    [Fact]
    public void A_press_made_while_a_fault_ends_a_hold_starts_a_recording_its_release_still_stops()
    {
        // A hold dictation is recording when its microphone faults. Before the fault handling reaches its stop, the user
        // releases Page Down and presses it again; the consumer is behind, so the old release and the new press are both
        // still queued. The fault's stop is admitted and the first recording processed; back at Idle the consumer raises
        // the old release (a stop that finds nothing recording) and then the new press, which starts a recording.
        // Releasing Page Down must end that recording: before the fix the fault's release cleared the new press's latch,
        // no stop came, and the microphone kept recording.
        using var loop = new Loop(HotkeyBinding.DefaultDictation);
        loop.Hook.Down(PageDown);
        loop.Dispatch();
        var first = loop.Recording(1);

        loop.Hook.Up(PageDown);
        loop.Hook.Down(PageDown);

        Assert.NotNull(loop.Stop(DictationStopReason.MicrophoneFault).Admission);
        loop.HookWakes();
        loop.FinishProcessing();
        loop.Dispatch();

        var second = loop.Recording(2);
        Assert.NotEqual(first.Activation, second.Activation);

        loop.Hook.Up(PageDown);
        loop.Dispatch();
        Assert.Equal(
            new[] { (DictationStopReason.MicrophoneFault, 1L), (DictationStopReason.HotkeyReleased, 2L) },
            loop.Stops.ToArray());
    }

    [Fact]
    public void A_toggle_tapped_off_and_on_while_a_fault_ends_the_recording_starts_one_its_next_tap_stops()
    {
        using var loop = new Loop(PageDownToggle);
        loop.Tap(PageDown);
        loop.Dispatch();
        loop.Recording(1);

        loop.Tap(PageDown); // off, and queued
        loop.Tap(PageDown); // on again, and queued

        Assert.NotNull(loop.Stop(DictationStopReason.MicrophoneFault).Admission);
        loop.HookWakes();
        loop.FinishProcessing();
        loop.Dispatch();
        loop.Recording(2);

        loop.Tap(PageDown);
        loop.Dispatch();
        Assert.Equal(
            new[] { (DictationStopReason.MicrophoneFault, 1L), (DictationStopReason.HotkeyReleased, 2L) },
            loop.Stops.ToArray());
    }

    [Fact]
    public void A_press_of_the_other_key_made_while_a_fault_ends_the_recording_keeps_its_release()
    {
        // The same ordering with the dictation-only key: Page Down is released and Page Up pressed behind the consumer.
        using var loop = new Loop(HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly);
        loop.Hook.Down(PageDown);
        loop.Dispatch();
        loop.Recording(1);

        loop.Hook.Up(PageDown);
        loop.Hook.Down(PageUp);

        Assert.NotNull(loop.Stop(DictationStopReason.MicrophoneFault).Admission);
        loop.HookWakes();
        loop.FinishProcessing();
        loop.Dispatch();
        Assert.Equal(HotkeyTrigger.DictationOnly, loop.Recording(2).Trigger);

        loop.Hook.Up(PageUp);
        loop.Dispatch();
        Assert.Equal(
            new[] { (DictationStopReason.MicrophoneFault, 1L), (DictationStopReason.HotkeyReleased, 2L) },
            loop.Stops.ToArray());
    }

    [Fact]
    public void A_hold_a_fault_ended_ends_nothing_on_its_release_and_the_next_press_starts_afresh()
    {
        // The key is still held when the fault's stop releases it: its autorepeat starts nothing and its release sends
        // nothing, as before, and the next press starts a new dictation.
        using var loop = new Loop(HotkeyBinding.DefaultDictation);
        loop.Hook.Down(PageDown);
        loop.Dispatch();
        loop.Recording(1);

        Assert.NotNull(loop.Stop(DictationStopReason.MicrophoneFault).Admission);
        loop.HookWakes();
        Assert.True(loop.Hook.Down(PageDown).Suppress);
        Assert.True(loop.Hook.Up(PageDown).Suppress);
        Assert.Empty(loop.Dispatch());
        loop.FinishProcessing();

        loop.Hook.Down(PageDown);
        loop.Dispatch();
        loop.Recording(2);
        loop.Hook.Up(PageDown);
        loop.Dispatch();
        Assert.Equal(
            new[] { (DictationStopReason.MicrophoneFault, 1L), (DictationStopReason.HotkeyReleased, 2L) },
            loop.Stops.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // the hook thread was not woken: the release applies at its next key event, before that event
    public void After_the_silence_auto_stop_the_next_tap_starts_a_new_dictation(bool hookWoken)
    {
        using var loop = new Loop(PageDownToggle);
        loop.Tap(PageDown);
        loop.Dispatch();
        loop.Recording(1);

        Assert.NotNull(loop.Stop(DictationStopReason.SilenceAutoStop, expectedDictationId: 1).Admission);
        if (hookWoken)
        {
            loop.HookWakes();
        }

        loop.FinishProcessing();

        // Not taken as the toggle-off of the recording the auto-stop ended: a new dictation.
        loop.Tap(PageDown);
        loop.Dispatch();
        loop.Recording(2);
        loop.Tap(PageDown);
        loop.Dispatch();
        Assert.Equal(
            new[] { (DictationStopReason.SilenceAutoStop, 1L), (DictationStopReason.HotkeyReleased, 2L) },
            loop.Stops.ToArray());
    }

    [Theory]
    [InlineData(HotkeyMode.Hold)]
    [InlineData(HotkeyMode.Toggle)]
    public void After_the_duration_ceiling_the_next_press_starts_a_new_dictation(HotkeyMode mode)
    {
        using var loop = new Loop(HotkeyBinding.DefaultDictation with { Mode = mode });
        loop.Hook.Down(PageDown);
        if (mode == HotkeyMode.Toggle)
        {
            loop.Hook.Up(PageDown);
        }

        loop.Dispatch();
        loop.Recording(1);
        loop.Lifecycle.ArmDurationLimit(1, TimeSpan.FromMinutes(10));
        loop.Clock.Advance(TimeSpan.FromMinutes(10));
        var ceiling = Assert.IsType<long>(loop.Lifecycle.TryAcceptDurationLimit());

        Assert.NotNull(loop.Stop(DictationStopReason.DurationLimit, ceiling).Admission);
        loop.HookWakes();
        if (mode == HotkeyMode.Hold)
        {
            // Held for the whole ten minutes: an autorepeat starts nothing and the release stops nothing.
            Assert.True(loop.Hook.Down(PageDown).Suppress);
            Assert.True(loop.Hook.Up(PageDown).Suppress);
        }

        Assert.Empty(loop.Dispatch());
        loop.FinishProcessing();

        loop.Hook.Down(PageDown);
        if (mode == HotkeyMode.Toggle)
        {
            loop.Hook.Up(PageDown);
        }

        loop.Dispatch();
        loop.Recording(2);
        if (mode == HotkeyMode.Toggle)
        {
            loop.Tap(PageDown);
        }
        else
        {
            loop.Hook.Up(PageDown);
        }

        loop.Dispatch();
        Assert.Equal(
            new[] { (DictationStopReason.DurationLimit, 1L), (DictationStopReason.HotkeyReleased, 2L) },
            loop.Stops.ToArray());
    }

    [Fact]
    public void Pausing_ends_the_recording_and_the_first_press_after_resuming_starts_a_new_one()
    {
        using var loop = new Loop(HotkeyBinding.DefaultDictation);
        loop.Hook.Down(PageDown);
        loop.Dispatch();
        loop.Recording(1);

        loop.SetPaused(true);
        loop.HookWakes();
        Assert.True(loop.Hook.Up(PageDown).Suppress); // swallowed before the pause, so swallowed through its release
        Assert.False(loop.Hook.Down(PageDown).Suppress); // paused: a new press reaches the app and starts nothing
        Assert.False(loop.Hook.Up(PageDown).Suppress);
        Assert.Empty(loop.Dispatch());
        loop.FinishProcessing();

        loop.SetPaused(false);
        loop.HookWakes();
        loop.Hook.Down(PageDown);
        loop.Dispatch();
        loop.Recording(2);
        loop.Hook.Up(PageDown);
        loop.Dispatch();
        Assert.Equal(
            new[] { (DictationStopReason.Paused, 1L), (DictationStopReason.HotkeyReleased, 2L) },
            loop.Stops.ToArray());
    }

    // Every stop Scribe makes itself, for a hold and a toggle binding, with the consumer seeing the press in time or only once
    // the stopped dictation has finished processing. The four it makes today are named, so a policy that stopped releasing
    // for one still runs (and fails) its rows, and any other reason the policy says releases joins by itself. The
    // controller arms the silence auto-stop only for a toggle (CaptureTriggerBinding.StopsOnSilence); its hold rows stay,
    // because the stop path treats every stop Scribe makes alike.
    public static TheoryData<DictationStopReason, HotkeyMode, bool> EveryStopScribeMakes()
    {
        DictationStopReason[] known =
        [
            DictationStopReason.SilenceAutoStop,
            DictationStopReason.MicrophoneFault,
            DictationStopReason.DurationLimit,
            DictationStopReason.Paused,
        ];
        var rows = new TheoryData<DictationStopReason, HotkeyMode, bool>();
        foreach (var reason in known.Union(
                     Enum.GetValues<DictationStopReason>().Where(DictationStopPolicy.ReleasesHotkeyToggle)))
        {
            foreach (var mode in new[] { HotkeyMode.Hold, HotkeyMode.Toggle })
            {
                rows.Add(reason, mode, false);
                rows.Add(reason, mode, true);
            }
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(EveryStopScribeMakes))]
    public void After_a_stop_scribe_makes_a_press_during_processing_leaves_no_latch_and_the_next_press_starts(
        DictationStopReason reason, HotkeyMode mode, bool consumerSeesItLate)
    {
        // Scribe ends recording 1 itself, and while it still processes the user presses the key again. Seen in time, that
        // press is refused and leaves nothing behind, so the first press after the processing starts recording 2: before
        // G1's fix a toggle's refused press stayed latched, and the next tap only ended a dictation that never began.
        // Seen only once the processing has ended, the press starts recording 2 itself, and its own release or second tap
        // ends it.
        using var loop = new Loop(HotkeyBinding.DefaultDictation with { Mode = mode });
        loop.Begin();
        loop.Dispatch();
        loop.Recording(1);

        StopAsScribe(loop, reason);
        loop.HookWakes();
        ResumeIfPaused(loop, reason);
        if (mode == HotkeyMode.Hold)
        {
            // The key still held from recording 1 is let go: the stop released that press, so its release sends nothing.
            Assert.True(loop.Hook.Up(PageDown).Suppress);
            Assert.Empty(loop.Dispatch());
        }

        loop.Begin(); // while recording 1 still processes
        if (consumerSeesItLate)
        {
            loop.FinishProcessing();
            Assert.Equal(HotkeyTransition.Activated, Assert.Single(loop.Dispatch()).Transition);
            loop.Recording(2);
        }
        else
        {
            // A new start, not the toggle-off of recording 1 (the stop released that press), and refused.
            Assert.Equal(HotkeyTransition.Activated, Assert.Single(loop.Dispatch()).Transition);
            Assert.Equal(DictationPhase.Processing, loop.Lifecycle.Phase);
            loop.HookWakes();
            Assert.False(loop.Hook.Engine.HoldsAnyLatch);
            if (mode == HotkeyMode.Hold)
            {
                Assert.True(loop.Hook.Up(PageDown).Suppress); // its release sends nothing either
            }

            Assert.Empty(loop.Dispatch());
            loop.FinishProcessing();
            loop.Begin(); // the first press after the processing
            loop.Dispatch();
            loop.Recording(2);
        }

        loop.End();
        loop.Dispatch();
        Assert.Equal(new[] { (reason, 1L), (DictationStopReason.HotkeyReleased, 2L) }, loop.Stops.ToArray());
        Assert.False(loop.Hook.Engine.HoldsAnyLatch);
    }

    [Theory]
    [MemberData(nameof(EveryStopScribeMakes))]
    public void A_press_queued_behind_the_consumer_when_scribe_stops_the_recording_starts_none_its_release_cannot_end(
        DictationStopReason reason, HotkeyMode mode, bool consumerCatchesUpAfterProcessing)
    {
        // Recording 1's own end (its release, or the second tap) and a new press are both still queued behind the consumer
        // when Scribe stops recording 1 itself. The new press either starts recording 2, which its own release or second
        // tap ends, or starts nothing and leaves nothing behind: refused while recording 1 still processes, or dropped as
        // computed before a pause. Either way the next press starts a dictation that its release ends.
        using var loop = new Loop(HotkeyBinding.DefaultDictation with { Mode = mode });
        loop.Begin();
        loop.Dispatch();
        loop.Recording(1);
        loop.End(); // queued
        loop.Begin(); // queued

        StopAsScribe(loop, reason);
        loop.HookWakes();
        ResumeIfPaused(loop, reason);
        List<HotkeyService.QueuedTransition> caughtUp;
        if (consumerCatchesUpAfterProcessing)
        {
            loop.FinishProcessing();
            caughtUp = loop.Dispatch();
        }
        else
        {
            caughtUp = loop.Dispatch();
            Assert.Equal(DictationPhase.Processing, loop.Lifecycle.Phase);
            loop.HookWakes();
            loop.FinishProcessing();
        }

        Assert.Equal(
            new[] { HotkeyTransition.Deactivated, HotkeyTransition.Activated },
            caughtUp.Select(t => t.Transition).ToArray());

        // Only a press the consumer sees after the processing, and that no pause made stale, can start recording 2.
        var queuedPressStarted = consumerCatchesUpAfterProcessing && reason != DictationStopReason.Paused;
        Assert.Equal(queuedPressStarted ? DictationPhase.Recording : DictationPhase.Idle, loop.Lifecycle.Phase);
        if (!queuedPressStarted)
        {
            Assert.False(loop.Hook.Engine.HoldsAnyLatch);
            if (mode == HotkeyMode.Hold)
            {
                Assert.True(loop.Hook.Up(PageDown).Suppress); // the held press is let go, and nothing is sent
            }

            Assert.Empty(loop.Dispatch());
            loop.Begin();
            loop.Dispatch();
        }

        loop.Recording(2);
        loop.End();
        loop.Dispatch();
        Assert.Equal(new[] { (reason, 1L), (DictationStopReason.HotkeyReleased, 2L) }, loop.Stops.ToArray());
        Assert.False(loop.Hook.Engine.HoldsAnyLatch);
    }

    [Fact]
    public void A_late_stop_for_an_earlier_recording_is_turned_away_and_releases_nothing()
    {
        // A silence auto-stop computed for recording 1 reaches the stop path only after the user had toggled 1 off and
        // a new tap had started recording 2. It is turned away, and it must not touch the hook either: releasing what the
        // hook held would cancel recording 2's toggle, and its next tap would be taken as a new start instead of its stop.
        using var loop = new Loop(PageDownToggle);
        loop.Tap(PageDown);
        loop.Dispatch();
        loop.Recording(1);
        loop.Tap(PageDown);
        loop.Dispatch();
        loop.FinishProcessing();
        loop.Tap(PageDown);
        loop.Dispatch();
        loop.Recording(2);

        Assert.Null(loop.Stop(DictationStopReason.SilenceAutoStop, expectedDictationId: 1).Admission);
        loop.HookWakes();

        loop.Tap(PageDown);
        loop.Dispatch();
        Assert.Equal(
            new[] { (DictationStopReason.HotkeyReleased, 1L), (DictationStopReason.HotkeyReleased, 2L) },
            loop.Stops.ToArray());
    }

    private sealed record Press(HotkeyTrigger Trigger, long Activation);

    // The stops Scribe makes itself, each as the controller makes it for the live recording.
    private static void StopAsScribe(Loop loop, DictationStopReason reason)
    {
        switch (reason)
        {
            case DictationStopReason.Paused:
                loop.SetPaused(true);
                break;

            case DictationStopReason.DurationLimit:
                loop.Lifecycle.ArmDurationLimit(loop.Lifecycle.CurrentDictationId, TimeSpan.FromMinutes(10));
                loop.Clock.Advance(TimeSpan.FromMinutes(10));
                var id = Assert.IsType<long>(loop.Lifecycle.TryAcceptDurationLimit());
                Assert.NotNull(loop.Stop(reason, id).Admission);
                break;

            case DictationStopReason.SilenceAutoStop:
                Assert.NotNull(loop.Stop(reason, loop.Lifecycle.CurrentDictationId).Admission);
                break;

            default:
                Assert.NotNull(loop.Stop(reason).Admission);
                break;
        }
    }

    // A pause is lifted while the paused dictation still processes, so the presses that follow reach the lifecycle.
    private static void ResumeIfPaused(Loop loop, DictationStopReason reason)
    {
        if (reason == DictationStopReason.Paused)
        {
            loop.SetPaused(false);
            loop.HookWakes();
        }
    }

    // The controller's side, less the microphone and the speech pipeline: the hotkey's events drive the lifecycle, and
    // every stop and every start takes the controller's first step.
    private sealed class Loop : IDisposable
    {
        private readonly HotkeyMode _mode;
        private ProcessingAdmission<Press>? _processing;

        public Loop(HotkeyBinding binding, HotkeyBinding? dictationOnly = null)
        {
            _mode = binding.Mode;
            Hook = new HotkeyEngineHarness(binding, dictationOnly);
            Lifecycle = new DictationLifecycle<Press>(() => { }, () => { }, Clock);

            // OnActivated and OnDeactivated: the recording keeps the press that started it, and a press turned away as
            // still processing releases its own latch.
            Hook.Service.Activated += (_, e) => DictationStartPolicy.BeginRecording(
                Lifecycle, () => new Press(e.Trigger, e.Activation), () => Hook.Service.CancelToggle(e.Activation));
            Hook.Service.Deactivated += (_, e) => Stop(
                e.Deactivation == HotkeyDeactivation.DesktopSwitch
                    ? DictationStopReason.DesktopSwitch
                    : DictationStopReason.HotkeyReleased);
        }

        public HotkeyEngineHarness Hook { get; }

        public ManualTimeProvider Clock { get; } = new();

        public DictationLifecycle<Press> Lifecycle { get; }

        public List<(DictationStopReason Reason, long DictationId)> Stops { get; } = [];

        // StopAndProcess up to its hand-off: an admitted stop is processing until FinishProcessing.
        public StopDecision<Press> Stop(DictationStopReason reason, long expectedDictationId = 0)
        {
            var stop = DictationStopPolicy.BeginStop(
                Lifecycle,
                reason,
                expectedDictationId,
                admitted => Hook.Service.CancelToggle(admitted.Activation),
                out var releaseFailure);
            Assert.Null(releaseFailure);
            if (stop.Admission is { } admission)
            {
                Stops.Add((reason, admission.DictationId));
                _processing = admission;
            }

            return stop;
        }

        // The controller's SetPaused: the lifecycle first, then the hook with the lifecycle's number, then the stop.
        public void SetPaused(bool paused)
        {
            var change = Lifecycle.SetPaused(paused);
            Assert.True(change.Changed);
            Hook.Service.SetPaused(paused, change.Sequence);
            if (paused && change.WasRecording)
            {
                Stop(DictationStopReason.Paused);
            }
        }

        // The processing task's end: idle again, then ended.
        public void FinishProcessing()
        {
            var admission = _processing ?? throw new InvalidOperationException("Nothing is processing.");
            _processing = null;
            Lifecycle.ReturnToIdle(Timeout.InfiniteTimeSpan);
            Lifecycle.EndProcessing(admission);
        }

        // The consumer thread catching up with everything queued so far.
        public List<HotkeyService.QueuedTransition> Dispatch() => Hook.DispatchAll();

        // The hook thread woken by the requests posted to it, as its message loop would be.
        public void HookWakes() => Hook.Engine.OnWake();

        public void Tap(uint key) => Hook.Tap(key);

        // The Page Down binding's gesture that starts a dictation (a press for a hold, a tap for a toggle), and the one
        // that ends it (the release, or the second tap).
        public void Begin()
        {
            if (_mode == HotkeyMode.Hold)
            {
                Hook.Down(PageDown);
            }
            else
            {
                Hook.Tap(PageDown);
            }
        }

        public void End()
        {
            if (_mode == HotkeyMode.Hold)
            {
                Hook.Up(PageDown);
            }
            else
            {
                Hook.Tap(PageDown);
            }
        }

        // The live recording, which must be the one numbered dictationId.
        public Press Recording(long dictationId)
        {
            Assert.Equal(DictationPhase.Recording, Lifecycle.Phase);
            Assert.Equal(dictationId, Lifecycle.CurrentDictationId);
            return Assert.IsType<Press>(Lifecycle.CurrentCapture);
        }

        public void Dispose() => Hook.Dispose();
    }
}
