using Scribe.Core.Hotkeys;
using Scribe.Core.Lifecycle;
using Scribe.Core.Models;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// The hotkey and the dictation lifecycle together, wired as the controller wires them (the controller itself has no
/// tests): the hook's transitions reach a real <see cref="DictationLifecycle{TCapture}"/> through the service's real
/// consumer step, each recording keeps the press that started it, and every stop goes through
/// <see cref="DictationStopPolicy.BeginStop"/>, the controller's own first step. A stop Scribe makes itself releases only
/// the press that started the recording it ended, and only once the stop is admitted, so a press the user made meanwhile,
/// still queued behind a slow consumer, starts a recording whose release the hook still reports.
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

    // The controller's side, less the microphone and the speech pipeline: the hotkey's events drive the lifecycle, and
    // every stop takes the controller's first step.
    private sealed class Loop : IDisposable
    {
        private ProcessingAdmission<Press>? _processing;

        public Loop(HotkeyBinding binding, HotkeyBinding? dictationOnly = null)
        {
            Hook = new HotkeyEngineHarness(binding, dictationOnly);
            Lifecycle = new DictationLifecycle<Press>(() => { }, () => { }, Clock);

            // OnActivated and OnDeactivated: the recording keeps the press that started it.
            Hook.Service.Activated += (_, e) => Lifecycle.TryBeginRecording(() => new Press(e.Trigger, e.Activation));
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
