using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

/// <summary>
/// The hook callback owns its key state and never waits for another thread (R12). These tests pin
/// the contract without installing a real hook: requests from other threads queue up and are
/// applied by the hook thread in request order, cross-thread reads are served from published
/// state, and nothing on the hook path needs the configuration gate that requesting threads share.
/// </summary>
public class HotkeyEngineTests
{
    private const uint RightCtrl = 0xA3;
    private const uint F8 = 0x77;
    private const uint F9 = 0x78;

    private static readonly HotkeyBinding F8Hold =
        new(F8, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "F8");

    private static readonly HotkeyBinding F9Hold =
        new(F9, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "F9");

    [Fact]
    public async Task Hook_thread_processes_keys_while_a_configuration_writer_is_stalled_on_the_gate()
    {
        var gate = new object();
        using var h = new HotkeyEngineHarness(HotkeyBinding.Default, gate: gate);

        // A request already queued: applying it on the hook thread must not need the gate either.
        h.Router.CancelToggle();

        Task writer;
        (HookDecision Down, HookDecision Up) press;
        lock (gate)
        {
            // Stand-in for any requesting thread stuck inside a configuration change: Settings
            // saving a binding, arming binding capture, a toggle reset, the controller pausing.
            writer = Task.Run(() => h.Router.SetCaptureMode(true));
            press = HotkeyEngineHarness.RunBounded(() => (h.Down(RightCtrl), h.Up(RightCtrl)));
            Assert.False(writer.IsCompleted);
        }

        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(press.Down.Suppress);
        Assert.True(press.Up.Suppress);
        Assert.True(press.Up.RequestReconcile);
        Assert.Equal(
            [HotkeyTransition.Activated, HotkeyTransition.Deactivated],
            h.TakeTransitions().Select(t => t.Transition));

        // The stalled request lands at the hook thread's next event, never on the writer's thread.
        Assert.False(h.Down(RightCtrl).Suppress);
    }

    [Fact]
    public void Cross_thread_reads_never_wait_for_the_configuration_gate()
    {
        var gate = new object();
        using var h = new HotkeyEngineHarness(HotkeyBinding.Default, gate: gate);
        h.Down(RightCtrl);
        var activation = Assert.Single(h.TakeTransitions());

        lock (gate)
        {
            // The dispatcher's staleness test, the leak reconciler's physical view, and the
            // binding reads Settings and the controller make.
            var reads = HotkeyEngineHarness.RunBounded(() => (
                Current: h.Router.IsCurrent(activation.Generation),
                Pressed: h.Router.IsPressed(RightCtrl),
                Binding: h.Router.Binding,
                DictationOnly: h.Router.DictationOnlyBinding));

            Assert.True(reads.Current);
            Assert.True(reads.Pressed);
            Assert.Equal(HotkeyBinding.Default, reads.Binding);
            Assert.Null(reads.DictationOnly);
        }
    }

    [Fact]
    public async Task Requests_from_another_thread_take_effect_on_the_hook_thread_in_request_order()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Default);
        Assert.True(h.Down(RightCtrl).Suppress);
        var activation = Assert.Single(h.TakeTransitions());

        await Task.Run(() =>
        {
            h.Router.SetCaptureMode(true);
            h.Router.SetCaptureMode(false);
        });

        // Nothing happened on the requesting thread: no transition, key state untouched.
        Assert.Empty(h.TakeTransitions());
        Assert.True(h.Engine.IsPressed(RightCtrl));

        h.Engine.OnWake();

        // Entering capture stopped the hold; leaving it came second and cleared state again.
        var stop = Assert.Single(h.TakeTransitions());
        Assert.Equal(HotkeyTransition.Deactivated, stop.Transition);
        Assert.Equal(HotkeyTrigger.Standard, stop.Trigger);
        Assert.False(stop.AllowReconcile);
        Assert.False(h.Engine.IsPressed(RightCtrl));
        Assert.False(h.WouldDispatch(activation));

        // Capture is over, so a fresh press is live again. The release of the key held across the
        // clear passes through, exactly as it did when the clear ran on the requesting thread.
        Assert.False(h.Up(RightCtrl).Suppress);
        Assert.True(h.Down(RightCtrl).Suppress);
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);
    }

    [Fact]
    public void An_activation_computed_before_a_queued_request_is_stale_before_the_hook_applies_it()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Default);
        h.Down(RightCtrl);
        var activation = Assert.Single(h.TakeTransitions());
        Assert.True(h.WouldDispatch(activation));

        h.Router.SetCaptureMode(true); // queued; the hook thread has not run since

        Assert.False(h.WouldDispatch(activation));
    }

    [Fact]
    public async Task A_toggle_reset_requested_before_a_key_event_applies_before_that_event()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Default with { Mode = HotkeyMode.Toggle });
        h.Down(RightCtrl);
        h.Up(RightCtrl);
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);

        // Silence auto-stop ended the dictation from another thread; no wake was processed yet.
        await Task.Run(() => h.Router.CancelToggle());

        h.Down(RightCtrl);
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);
    }

    [Fact]
    public void Wakes_are_coalesced_until_the_hook_thread_consumes_them()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Default);

        // A thread's posted-message queue is finite, so one outstanding wake covers any number of
        // requests made before the hook thread gets to it.
        Assert.Same(h.Engine, h.Router.CancelToggle());
        Assert.Null(h.Router.CancelToggle());
        Assert.Null(h.Router.SetCaptureMode(false));

        h.Engine.OnWake();
        Assert.Same(h.Engine, h.Router.CancelToggle());

        // An undeliverable wake is released so the next request asks again.
        h.Engine.CancelWake();
        Assert.Same(h.Engine, h.Router.CancelToggle());
    }

    [Fact]
    public void Attaching_the_owner_applies_requests_queued_before_the_hook_existed()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Default);
        Assert.Equal(0u, h.Engine.OwnerThreadId);
        h.Down(RightCtrl);
        _ = h.TakeTransitions();
        h.Router.SetCaptureMode(true); // no owner to wake yet

        h.Engine.AttachOwner(4242);

        Assert.Equal(4242u, h.Engine.OwnerThreadId);
        Assert.Equal(HotkeyTransition.Deactivated, Assert.Single(h.TakeTransitions()).Transition);

        h.Engine.DetachOwner();
        Assert.Equal(0u, h.Engine.OwnerThreadId);
    }

    [Fact]
    public void Reinstall_stops_the_interrupted_dictation_and_starts_the_replacement_clean()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Default);
        h.Down(RightCtrl);
        var activation = Assert.Single(h.TakeTransitions());

        var (replacement, interrupted) = h.Router.BeginEngine(h.Transitions);

        Assert.True(h.Engine.IsRetired);
        Assert.False(replacement.IsRetired);
        Assert.Same(replacement, h.Router.CurrentEngine);
        Assert.Equal(HotkeyTrigger.Standard, interrupted);
        Assert.False(h.WouldDispatch(activation));

        // No key state crosses a reinstall: the old hold's release passes through, and the
        // binding then works from a fresh press.
        Assert.False(replacement.IsPressed(RightCtrl));
        Assert.False(replacement.OnKeyEvent(RightCtrl, isDown: false).Suppress);
        Assert.True(replacement.OnKeyEvent(RightCtrl, isDown: true).Suppress);
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);
    }

    [Fact]
    public void An_idle_engine_reports_no_interrupted_dictation()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Default);
        h.Down(RightCtrl);
        h.Up(RightCtrl);

        var (_, interrupted) = h.Router.BeginEngine(h.Transitions);

        Assert.Null(interrupted);
    }

    [Fact]
    public void Replacement_engine_is_built_from_the_published_configuration()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Default);
        h.Router.UpdateBindings(F8Hold, null);
        h.Router.SetCaptureMode(true); // queued on the old engine and never applied there

        var (replacement, _) = h.Router.BeginEngine(h.Transitions);

        Assert.False(replacement.OnKeyEvent(F8, isDown: true).Suppress); // born in capture mode
        h.Router.SetCaptureMode(false);
        replacement.OnWake();
        replacement.OnKeyEvent(F8, isDown: false);
        Assert.True(replacement.OnKeyEvent(F8, isDown: true).Suppress); // and bound to F8
        Assert.False(replacement.OnKeyEvent(RightCtrl, isDown: true).Suppress);
    }

    [Fact]
    public async Task Rebinding_from_another_thread_stops_whichever_trigger_was_active()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Default, F9Hold);
        h.Down(F9);
        Assert.Equal(HotkeyTrigger.DictationOnly, Assert.Single(h.TakeTransitions()).Trigger);

        await Task.Run(() => h.Router.UpdateBindings(F8Hold, F9Hold));
        h.Engine.OnWake();

        var stop = Assert.Single(h.TakeTransitions());
        Assert.Equal(HotkeyTransition.Deactivated, stop.Transition);
        Assert.Equal(HotkeyTrigger.DictationOnly, stop.Trigger);
        Assert.False(stop.AllowReconcile);
    }

    [Fact]
    public void Failed_install_detaches_only_its_own_engine()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Default);
        var (replacement, _) = h.Router.BeginEngine(h.Transitions);

        Assert.Null(h.Router.EndEngine(h.Engine));
        Assert.Same(replacement, h.Router.CurrentEngine);

        Assert.Same(replacement, h.Router.EndEngine());
        Assert.True(replacement.IsRetired);
        Assert.Null(h.Router.CurrentEngine);
        Assert.False(h.Router.IsPressed(RightCtrl));
    }

    // ---- Retired engines: a hook thread that outlives its join ------------------------------

    [Fact]
    public void A_replaced_engine_can_no_longer_stop_a_dictation_started_on_its_replacement()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Default);
        h.Down(RightCtrl);
        _ = h.TakeTransitions();

        // A rebind queued on the old engine and never applied there: its hook thread, stalled past
        // the join, only gets to it after the replacement has started a new dictation.
        h.Router.UpdateBindings(F8Hold, null);
        var (replacement, interrupted) = h.Router.BeginEngine(h.Transitions);
        Assert.Equal(HotkeyTrigger.Standard, interrupted); // the reinstall stops the old dictation itself

        Assert.True(replacement.OnKeyEvent(F8, isDown: true).Suppress);
        var started = Assert.Single(h.TakeTransitions());
        Assert.Equal(HotkeyTransition.Activated, started.Transition);
        Assert.True(h.WouldDispatch(started));

        // The old thread finally runs: its queued wake, then key events its hook still receives.
        h.Engine.OnWake();
        PassesThrough(h.Engine.OnKeyEvent(RightCtrl, isDown: false));
        PassesThrough(h.Engine.OnKeyEvent(RightCtrl, isDown: true));
        PassesThrough(h.Engine.OnKeyEvent(F8, isDown: false));

        Assert.Empty(h.TakeTransitions()); // nothing that could stop the new dictation
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void An_interrupted_dictation_is_stopped_exactly_once_whichever_side_takes_it_first(
        bool ownerThreadFirst, bool byStateClear)
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Default);
        h.Down(RightCtrl);
        _ = h.TakeTransitions();
        if (byStateClear)
        {
            h.Router.SetCaptureMode(true); // the old owner applies it at its next wake
        }

        // The old owner thread's own stop: the key's release, or the queued capture request.
        void OwnerThreadMoves()
        {
            if (byStateClear)
            {
                h.Engine.OnWake();
            }
            else
            {
                h.Engine.OnKeyEvent(RightCtrl, isDown: false);
            }
        }

        HotkeyTrigger? interrupted;
        if (ownerThreadFirst)
        {
            OwnerThreadMoves();
            (_, interrupted) = h.Router.BeginEngine(h.Transitions);
        }
        else
        {
            (_, interrupted) = h.Router.BeginEngine(h.Transitions);
            OwnerThreadMoves();
        }

        var ownerStops = h.TakeTransitions().Count(t => t.Transition == HotkeyTransition.Deactivated);
        Assert.Equal(ownerThreadFirst ? 1 : 0, ownerStops);
        Assert.Equal(ownerThreadFirst ? (HotkeyTrigger?)null : HotkeyTrigger.Standard, interrupted);
    }

    [Fact]
    public void Trigger_arbiter_hands_an_active_trigger_to_exactly_one_of_a_take_and_a_retirement()
    {
        // The owner thread's state clear takes it first, so the owner sends the stop.
        var ownerFirst = new HotkeyTriggerArbiter();
        Assert.True(ownerFirst.TryActivate(HotkeyTrigger.DictationOnly));
        Assert.Equal(HotkeyTrigger.DictationOnly, ownerFirst.TryTake(HotkeyTrigger.Standard));
        Assert.Null(ownerFirst.Retire());

        // Retired first: the retirement reports it, and the owner's later take yields nothing.
        var retiredFirst = new HotkeyTriggerArbiter();
        Assert.True(retiredFirst.TryActivate(HotkeyTrigger.DictationOnly));
        Assert.Equal(HotkeyTrigger.DictationOnly, retiredFirst.Retire());
        Assert.Null(retiredFirst.TryTake(HotkeyTrigger.Standard));

        // Retirement is terminal: nothing starts, stops, clears or reports anything afterwards.
        retiredFirst.Reset();
        Assert.False(retiredFirst.TryActivate(HotkeyTrigger.Standard));
        Assert.False(retiredFirst.TryDeactivate(HotkeyTrigger.DictationOnly));
        Assert.Null(retiredFirst.TryTake(HotkeyTrigger.Standard));
        Assert.Null(retiredFirst.Retire());

        // A live arbiter with nothing active still yields the fallback, as state clears expect.
        Assert.Equal(HotkeyTrigger.Standard, new HotkeyTriggerArbiter().TryTake(HotkeyTrigger.Standard));
    }

    [Fact]
    public void A_stopped_engine_passes_keys_through_if_its_hook_thread_outlives_the_stop()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Default);
        h.Down(RightCtrl);
        _ = h.TakeTransitions();

        Assert.Same(h.Engine, h.Router.EndEngine());

        Assert.True(h.Engine.IsRetired);
        PassesThrough(h.Engine.OnKeyEvent(RightCtrl, isDown: false));
        PassesThrough(h.Engine.OnKeyEvent(RightCtrl, isDown: true));
        Assert.Empty(h.TakeTransitions());
    }

    [Fact]
    public void A_reinstall_stops_only_a_dictation_the_app_was_told_about()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Default with { Mode = HotkeyMode.Toggle }, F9Hold);
        h.Down(F9);        // the dictation-only binding starts a dictation
        h.Down(RightCtrl); // the standard toggle latches, but the arbiter refuses a second dictation
        h.Up(RightCtrl);
        h.Up(F9);          // and the dictation-only one ends
        Assert.Equal(
            [HotkeyTransition.Activated, HotkeyTransition.Deactivated],
            h.TakeTransitions().Select(t => t.Transition));

        var (_, interrupted) = h.Router.BeginEngine(h.Transitions);

        Assert.Null(interrupted); // nothing is recording, so there is nothing to stop
    }

    [Fact]
    public void Requests_while_stopped_only_update_the_published_configuration()
    {
        var router = new HotkeyCommandRouter(HotkeyBinding.Default);

        Assert.Equal((true, (HotkeyEngine?)null), router.UpdateBindings(F8Hold, F9Hold));
        Assert.Null(router.SetCaptureMode(true));
        Assert.Null(router.CancelToggle());
        Assert.Equal(F8Hold, router.Binding);
        Assert.Equal(F9Hold, router.DictationOnlyBinding);
        Assert.False(router.IsPressed(F8));
    }

    [Fact]
    public void Stopped_service_accepts_every_request_without_a_hook()
    {
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance);

        service.SetPaused(true);
        service.SetPaused(true);
        service.SetCaptureMode(true);
        service.SetCaptureMode(false);
        service.CancelToggle();
        service.SetPaused(false);
        service.SetPaused(true, requestSequence: 1);
        service.SetPaused(false, requestSequence: 2);
        service.SetPaused(true, requestSequence: 1); // older than one already applied: ignored
        service.UpdateBindings(F8Hold, F9Hold);

        Assert.False(service.IsRunning);
        Assert.Equal(F8Hold, service.Binding);
        Assert.Equal(F9Hold, service.DictationOnlyBinding);
    }

    [Fact]
    public void Transition_queue_keeps_order_and_stops_after_completion()
    {
        using var queue = new HotkeyTransitionQueue();
        var first = new HotkeyService.QueuedTransition(HotkeyTransition.Activated, HotkeyTrigger.Standard, 1, true);
        var second = new HotkeyService.QueuedTransition(HotkeyTransition.Deactivated, HotkeyTrigger.Standard, 1, true);

        Assert.True(queue.TryEnqueue(first));
        Assert.True(queue.TryEnqueue(second));

        Assert.True(queue.WaitForWork());
        var drained = new List<HotkeyService.QueuedTransition>();
        foreach (var transition in queue.TakeAll())
        {
            drained.Add(transition);
        }

        Assert.Equal([first, second], drained);

        queue.Complete();
        Assert.True(queue.IsCompleted);
        Assert.False(queue.WaitForWork());
        Assert.False(queue.TryEnqueue(first));
    }

    [Fact]
    public void Dispatcher_wakes_for_a_transition_enqueued_while_it_waits()
    {
        var item = new HotkeyService.QueuedTransition(
            HotkeyTransition.Activated, HotkeyTrigger.Standard, 1, AllowReconcile: true);

        // Run the queue's code once on another instance first, so the waiter's only blocking call is
        // the event wait: a JIT or type-initialization wait would also read as WaitSleepJoin.
        using (var warmUp = new HotkeyTransitionQueue())
        {
            warmUp.TryEnqueue(item);
            Assert.True(warmUp.WaitForWork());
            _ = warmUp.TakeAll();
        }

        using var queue = new HotkeyTransitionQueue();
        var woke = false;
        var waiter = new Thread(() => woke = queue.WaitForWork())
        {
            IsBackground = true,
            Name = "hotkey-test-dispatcher",
        };
        waiter.Start();

        // Spin, never sleep, until the dispatcher has found the queue empty and is blocked on the
        // event, so the enqueue below can only be seen through its signal.
        var spinning = System.Diagnostics.Stopwatch.StartNew();
        while ((waiter.ThreadState & ThreadState.WaitSleepJoin) == 0)
        {
            Assert.True(spinning.Elapsed < TimeSpan.FromSeconds(10), "The dispatcher never started waiting.");
            Thread.Yield();
        }

        queue.TryEnqueue(item);

        var returned = waiter.Join(TimeSpan.FromSeconds(10));
        if (!returned)
        {
            queue.Complete(); // release the stuck waiter before failing
        }

        Assert.True(returned, "The dispatcher slept through a transition enqueued while it waited.");
        Assert.True(woke);
    }

    [Fact]
    public void A_thread_that_ensured_its_message_queue_receives_a_posted_wake()
    {
        // Only this test's own thread and its own queue take part: no hook, no input.
        using var ready = new ManualResetEventSlim(false);
        uint threadId = 0;
        var result = -1;
        NativeMethods.MSG received = default;
        var pump = new Thread(() =>
        {
            NativeMethods.EnsureMessageQueue();
            threadId = NativeMethods.GetCurrentThreadId();
            ready.Set();
            result = NativeMethods.GetMessage(
                out received, 0, NativeMethods.WM_HOTKEY_COMMANDS, NativeMethods.WM_HOTKEY_COMMANDS);
        })
        {
            IsBackground = true,
            Name = "hotkey-test-message-queue",
        };
        pump.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));

        Assert.True(NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_HOTKEY_COMMANDS, 0, 0));

        Assert.True(pump.Join(TimeSpan.FromSeconds(10)));
        Assert.True(result > 0);
        Assert.Equal(nint.Zero, received.hwnd);
        Assert.Equal(NativeMethods.WM_HOTKEY_COMMANDS, received.message);
    }

    [Fact]
    public void A_released_transition_queue_never_throws_at_its_producer()
    {
        var queue = new HotkeyTransitionQueue();
        queue.Dispose();

        // A hook thread that outlived Stop can still reach here; an exception escaping the hook
        // callback would take the process down.
        queue.TryEnqueue(new HotkeyService.QueuedTransition(
            HotkeyTransition.Activated, HotkeyTrigger.Standard, 1, AllowReconcile: true));
    }

    [Fact]
    public async Task Leak_check_signal_runs_the_check_on_the_pool_and_rearms_after_each_run()
    {
        using var checks = new SemaphoreSlim(0);
        using var signal = new HotkeyReconcileSignal(() => checks.Release());

        // The dispatcher plays no part: the hook callback's SetEvent alone gets the check run.
        signal.Signal();
        Assert.True(await checks.WaitAsync(TimeSpan.FromSeconds(10)));

        signal.Signal();
        Assert.True(await checks.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void A_released_leak_check_signal_never_throws_at_the_hook()
    {
        var signal = new HotkeyReconcileSignal(() => { });
        signal.Dispose();

        signal.Signal();
    }

    [Fact]
    public void Inbox_delivers_every_item_once_and_in_order_per_producer_under_concurrent_pushes()
    {
        const int producers = 4;
        const int perProducer = 5000;
        var inbox = new LockFreeInbox<(int Producer, int Sequence)>();
        using var start = new Barrier(producers + 1);
        var threads = Enumerable.Range(0, producers)
            .Select(producer => new Thread(() =>
            {
                start.SignalAndWait();
                for (var sequence = 0; sequence < perProducer; sequence++)
                {
                    inbox.Push((producer, sequence));
                }
            }))
            .ToList();
        threads.ForEach(thread => thread.Start());

        var next = new int[producers];
        var received = 0;
        void Drain()
        {
            foreach (var (producer, sequence) in inbox.TakeAll())
            {
                Assert.Equal(next[producer], sequence);
                next[producer]++;
                received++;
            }
        }

        start.SignalAndWait();
        while (threads.Any(thread => thread.IsAlive))
        {
            Drain();
        }

        Drain();
        Assert.Equal(producers * perProducer, received);
        Assert.True(inbox.IsEmpty);
    }

    [Fact]
    public void Key_set_tracks_every_code_the_hook_can_report_and_nothing_else()
    {
        var keys = new KeySet();

        Assert.True(keys.Add(0x01));
        Assert.True(keys.Add(0x3F));
        Assert.True(keys.Add(0x40));
        Assert.True(keys.Add(0xFE));
        Assert.False(keys.Add(0x40));
        Assert.True(keys.Contains(0x01) && keys.Contains(0x3F) && keys.Contains(0x40) && keys.Contains(0xFE));
        Assert.False(keys.Contains(0x41));

        // Outside the documented 1 to 254 range of KBDLLHOOKSTRUCT.vkCode.
        Assert.False(keys.Add(0x100));
        Assert.False(keys.Contains(0x100));

        Assert.True(keys.Remove(0x40));
        Assert.False(keys.Remove(0x40));
        Assert.False(keys.Contains(0x40));
        Assert.True(keys.Contains(0x3F));

        keys.Clear();
        Assert.False(keys.Contains(0x01));
        Assert.False(keys.Contains(0xFE));
    }

    [Fact]
    public void Codes_outside_the_hook_range_pass_through_untouched()
    {
        var state = new ChordStateMachine(HotkeyBinding.Default);

        var update = state.Process(0x1A3, isDown: true);

        Assert.Equal(HotkeyTransition.None, update.Transition);
        Assert.False(update.ShouldSuppress);
        Assert.Equal(HotkeyTransition.Activated, state.Process(RightCtrl, isDown: true).Transition);
    }

    private static void PassesThrough(HookDecision decision)
    {
        Assert.False(decision.Suppress);
        Assert.False(decision.RequestReconcile);
    }
}
