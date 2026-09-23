using System.Collections.Concurrent;
using Scribe.Core.Lifecycle;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// How dictation state reaches the shell: numbered under the lifecycle's gate, raised afterwards from whichever thread made
/// the change, posted to the UI thread without the raising thread ever waiting for it, and shown only when newer than what
/// is already shown. Two tests replay the reviewed defect exactly: the previous dictation's idle, and a pause shown late,
/// each arriving after the next recording's start and hiding its pill.
/// </summary>
public sealed class PresentationRelayTests
{
    [Fact]
    public void Changes_render_in_revision_order_and_one_older_than_the_last_rendered_is_dropped()
    {
        var posted = new Queue<Action>();
        var rendered = new List<string>();
        var relay = new PresentationRelay<string>(posted.Enqueue, rendered.Add, () => false);

        relay.Publish(2, "two");
        relay.Publish(1, "one");  // older, and arrives later
        relay.Publish(2, "two again"); // the same change delivered twice
        relay.Publish(3, "three");
        Assert.Empty(rendered); // nothing runs until the rendering thread gets to it
        RunAll(posted);

        Assert.Equal(["two", "three"], rendered);
        Assert.Equal(3, relay.LastRenderedRevision);
    }

    [Fact]
    public void Publishing_never_waits_for_the_rendering_thread_even_while_it_is_blocked()
    {
        using var ui = new FakeUiThread();
        using var uiMayGoOn = new ManualResetEventSlim();
        ui.Post(uiMayGoOn.Wait); // the UI thread is busy, as it is for the whole of the app's exit
        var rendered = new ConcurrentQueue<long>();
        var relay = new PresentationRelay<long>(ui.Post, rendered.Enqueue, () => false);

        var publisher = BlockedThreads.Start(() =>
        {
            for (var revision = 1; revision <= 3; revision++)
            {
                relay.Publish(revision, revision);
            }
        });
        BlockedThreads.Join(publisher); // returned with the UI thread still blocked

        Assert.Empty(rendered);
        uiMayGoOn.Set();
        ui.Drain();
        Assert.Equal([1L, 2L, 3L], rendered);
    }

    [Fact]
    public void Nothing_renders_once_closed_including_changes_posted_before_the_close()
    {
        var posted = new Queue<Action>();
        var rendered = new List<string>();
        var closed = false;
        var relay = new PresentationRelay<string>(posted.Enqueue, rendered.Add, () => closed);

        relay.Publish(1, "recording");
        closed = true;
        relay.Publish(2, "processing");
        RunAll(posted);

        Assert.Empty(rendered);
        Assert.Equal(0, relay.LastRenderedRevision);
    }

    [Fact]
    public void A_render_or_post_that_throws_is_reported_and_never_reaches_the_publisher()
    {
        var posted = new Queue<Action>();
        var failures = new List<Exception>();
        var rendered = new List<string>();
        var relay = new PresentationRelay<string>(
            posted.Enqueue,
            change =>
            {
                if (change == "bad")
                {
                    throw new InvalidOperationException("tray icon disposed");
                }

                rendered.Add(change);
            },
            () => false,
            failures.Add);

        relay.Publish(1, "bad");
        relay.Publish(2, "good");
        RunAll(posted);

        var brokenPost = new PresentationRelay<string>(
            _ => throw new InvalidOperationException("dispatcher gone"),
            rendered.Add,
            () => false,
            _ => throw new InvalidOperationException("the reporter failed as well"));
        brokenPost.Publish(3, "never shown");

        Assert.Equal(["good"], rendered);
        Assert.Single(failures);
    }

    [Fact]
    public void Whatever_the_threads_publishing_the_newest_change_is_the_last_rendered()
    {
        using var ui = new FakeUiThread();
        var rendered = new ConcurrentQueue<long>();
        var relay = new PresentationRelay<long>(ui.Post, rendered.Enqueue, () => false);
        long next = 0;
        using var start = new Barrier(4);

        var publishers = Enumerable.Range(0, 4).Select(_ => BlockedThreads.Start(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 2_000; i++)
            {
                var revision = Interlocked.Increment(ref next);
                relay.Publish(revision, revision);
            }
        })).ToList();
        publishers.ForEach(BlockedThreads.Join);
        ui.Drain();

        var shown = rendered.ToList();
        Assert.Equal(8_000, shown[^1]);
        Assert.Equal(shown.OrderBy(revision => revision), shown); // never an older one after a newer one
        Assert.Equal(shown.Distinct(), shown);
    }

    /// <summary>
    /// The reviewed defect, exactly: dictation A publishes idle under the lifecycle's gate, which lets B start, but A's
    /// thread is preempted before it raises the change. B's microphone opens and B raises Recording; then A raises its
    /// Idle. Shown in arrival order, that Idle hid B's pill for the whole recording.
    /// </summary>
    [Fact]
    public void The_previous_dictations_idle_raised_after_the_next_recording_never_hides_its_pill()
    {
        using var ui = new FakeUiThread();
        var lifecycle = NewLifecycle();
        var shell = new FakeShell();
        var relay = new PresentationRelay<DictationPresentation>(ui.Post, shell.Render, () => lifecycle.IsClosing);

        var a = lifecycle.TryBeginRecording(() => "a");
        Publish(relay, lifecycle.TryPresentRecording(a.DictationId)!.Value);
        var stopA = lifecycle.TryBeginProcessing(a.DictationId);
        Publish(relay, stopA.Presentation);
        ui.Drain();

        using var aIsIdle = new ManualResetEventSlim();
        using var bRaised = new ManualResetEventSlim();
        var processingA = BlockedThreads.Start(() =>
        {
            var idle = lifecycle.ReturnToIdle(Timeout.InfiniteTimeSpan);
            aIsIdle.Set();
            bRaised.Wait(); // preempted between the transition and its raise
            Publish(relay, idle.Presentation);
            lifecycle.EndProcessing(stopA.Admission!);
        });
        var hotkeyB = BlockedThreads.Start(() =>
        {
            aIsIdle.Wait();
            var b = lifecycle.TryBeginRecording(() => "b");
            Publish(relay, lifecycle.TryPresentRecording(b.DictationId)!.Value);
            bRaised.Set();
        });
        BlockedThreads.Join(hotkeyB);
        BlockedThreads.Join(processingA);
        ui.Drain();

        Assert.Equal(["show recording", "show processing", "show recording"], shell.Commands);
        Assert.Equal(DictationPhase.Recording, lifecycle.Presentation.Phase);
    }

    /// <summary>
    /// Paused, then Recording, reversed. The user paused while A was processing, so A's return to idle publishes Paused, and
    /// A is preempted before raising it. The user resumes (Idle, raised from the UI thread) and B starts and raises
    /// Recording. A's late Paused then hid B's pill and asked the overlay helper to end under it.
    /// </summary>
    [Fact]
    public void A_pause_raised_after_the_next_recording_never_hides_its_pill_or_releases_the_overlay()
    {
        using var ui = new FakeUiThread();
        var lifecycle = NewLifecycle();
        var shell = new FakeShell();
        var relay = new PresentationRelay<DictationPresentation>(ui.Post, shell.Render, () => lifecycle.IsClosing);

        var a = lifecycle.TryBeginRecording(() => "a");
        Publish(relay, lifecycle.TryPresentRecording(a.DictationId)!.Value);
        var stopA = lifecycle.TryBeginProcessing(a.DictationId);
        Publish(relay, stopA.Presentation);
        Assert.Null(lifecycle.SetPaused(true).Presentation); // mid-dictation: shown when A returns to idle
        ui.Drain();

        using var aIsPaused = new ManualResetEventSlim();
        using var bRaised = new ManualResetEventSlim();
        var pausedWhenAReturned = false;
        var processingA = BlockedThreads.Start(() =>
        {
            var idle = lifecycle.ReturnToIdle(Timeout.InfiniteTimeSpan);
            pausedWhenAReturned = idle.Paused;
            aIsPaused.Set();
            bRaised.Wait(); // preempted between the transition and its raise
            Publish(relay, idle.Presentation);
            lifecycle.EndProcessing(stopA.Admission!);
        });

        Assert.True(aIsPaused.Wait(BlockedThreads.SafetyTimeout));
        Assert.True(pausedWhenAReturned);
        Publish(relay, lifecycle.SetPaused(false).Presentation!.Value); // the user resumes from the tray
        ui.Drain();

        var hotkeyB = BlockedThreads.Start(() =>
        {
            var b = lifecycle.TryBeginRecording(() => "b");
            Publish(relay, lifecycle.TryPresentRecording(b.DictationId)!.Value);
            bRaised.Set();
        });
        BlockedThreads.Join(hotkeyB);
        BlockedThreads.Join(processingA);
        ui.Drain();

        Assert.Equal(["show recording", "show processing", "hide", "show recording"], shell.Commands);
        Assert.DoesNotContain("release when idle", shell.Commands);
    }

    /// <summary>Every order the four changes of one dictation and the start of the next can arrive in.</summary>
    [Fact]
    public void Every_delivery_order_ends_on_the_newest_change_and_never_shows_an_older_one_after_it()
    {
        var lifecycle = NewLifecycle();
        var a = lifecycle.TryBeginRecording(() => "a");
        var aRecording = lifecycle.TryPresentRecording(a.DictationId)!.Value;
        var stopA = lifecycle.TryBeginProcessing(a.DictationId);
        var aIdle = lifecycle.ReturnToIdle(Timeout.InfiniteTimeSpan).Presentation;
        lifecycle.EndProcessing(stopA.Admission!);
        var b = lifecycle.TryBeginRecording(() => "b");
        var bRecording = lifecycle.TryPresentRecording(b.DictationId)!.Value;
        DictationPresentation[] changes = [aRecording, stopA.Presentation, aIdle, bRecording];

        var orders = 0;
        foreach (var order in Permutations(changes))
        {
            var posted = new Queue<Action>();
            var shown = new List<DictationPresentation>();
            var relay = new PresentationRelay<DictationPresentation>(posted.Enqueue, shown.Add, () => false);
            foreach (var change in order)
            {
                Publish(relay, change);
            }

            RunAll(posted);
            Assert.Equal(bRecording, shown[^1]);
            Assert.Equal(shown.OrderBy(change => change.Revision), shown);
            orders++;
        }

        Assert.Equal(24, orders);
    }

    /// <summary>
    /// The dictation path never waits for the UI thread. A dictation's state changes and tray notices, raised while the
    /// UI thread is busy (a Settings window at work, or the app's own exit), are queued in order and the dictation carries
    /// on. Invoked instead, each of them held the dictation until the UI thread got round to it.
    /// </summary>
    [Fact]
    public void A_dictation_never_waits_for_a_busy_UI_thread_to_show_its_notices()
    {
        using var ui = new FakeUiThread();
        var lifecycle = NewLifecycle();
        var shown = new ConcurrentQueue<string>();
        var relay = new PresentationRelay<DictationPresentation>(
            ui.Post, change => shown.Enqueue(change.Phase.ToString()), () => lifecycle.IsClosing);
        var tray = new UiThreadDispatch(() => ui.IsCurrent, ui.Post, () => false);
        using var uiMayGoOn = new ManualResetEventSlim();
        ui.Post(uiMayGoOn.Wait);

        var dictation = BlockedThreads.Start(() =>
        {
            var a = lifecycle.TryBeginRecording(() => "a");
            Publish(relay, lifecycle.TryPresentRecording(a.DictationId)!.Value);
            var stop = lifecycle.TryBeginProcessing(a.DictationId);
            Publish(relay, stop.Presentation);
            tray.Run(() => shown.Enqueue("error tooltip"));
            tray.Run(() => shown.Enqueue("recovery balloon"));
            Publish(relay, lifecycle.ReturnToIdle(Timeout.InfiniteTimeSpan).Presentation);
            lifecycle.EndProcessing(stop.Admission!);
        });
        BlockedThreads.Join(dictation); // finished while the UI thread is still busy
        Assert.Empty(shown);

        uiMayGoOn.Set();
        ui.Drain();
        Assert.Equal(["Recording", "Processing", "error tooltip", "recovery balloon", "Idle"], shown);
    }

    [Fact]
    public void Work_bound_to_a_change_runs_only_while_that_change_is_the_last_one_rendered()
    {
        var posted = new Queue<Action>();
        var ran = new List<string>();
        var closed = false;
        var failures = new List<Exception>();
        var relay = new PresentationRelay<string>(posted.Enqueue, _ => { }, () => closed, failures.Add);

        relay.Publish(1, "recording");
        relay.PublishIfCurrent(1, () => ran.Add("warning while recording"));
        relay.PublishIfCurrent(2, () => ran.Add("warning for a change not shown yet"));
        relay.PublishIfCurrent(0, () => ran.Add("warning for a recording that was already over"));
        relay.Publish(2, "processing");
        relay.PublishIfCurrent(1, () => ran.Add("late warning for the recording"));
        relay.PublishIfCurrent(2, () => throw new InvalidOperationException("overlay helper gone"));
        RunAll(posted);
        closed = true;
        relay.PublishIfCurrent(2, () => ran.Add("after close"));
        RunAll(posted);

        Assert.Equal(["warning while recording"], ran);
        Assert.Single(failures);
    }

    /// <summary>
    /// The reviewed defect, exactly: a muted recording is shown as recording, then its activation is preempted before it
    /// raises the muted warning. The pause and its processing finish and the pill is hidden and released. The activation
    /// resumes and raises the warning; showing it put the pill back in its recording state, and nothing ever hid it again.
    /// </summary>
    [Fact]
    public void A_warning_raised_after_a_pause_ended_its_recording_never_brings_the_pill_back()
    {
        using var ui = new FakeUiThread();
        var lifecycle = NewLifecycle();
        var shell = new FakeShell();
        var relay = new PresentationRelay<DictationPresentation>(ui.Post, shell.Render, () => lifecycle.IsClosing);

        var a = lifecycle.TryBeginRecording(() => "muted microphone");
        Publish(relay, lifecycle.TryPresentRecording(a.DictationId)!.Value);
        EndWithAPause(lifecycle, relay);
        ui.Drain();

        // The activation resumes and warns, as the controller does: the revision taken under the gate, then published.
        var revision = lifecycle.TryGetRecordingPresentation(a.DictationId)?.Revision ?? 0;
        PublishWarning(relay, shell, revision);
        ui.Drain();

        Assert.Equal(0, revision);
        Assert.Equal(["show recording", "show processing", "hide", "release when idle"], shell.Commands);
    }

    /// <summary>The same, with the warning's revision taken just before the pause and delivered after it.</summary>
    [Fact]
    public void A_warning_tagged_before_a_pause_but_delivered_after_it_is_dropped()
    {
        using var ui = new FakeUiThread();
        var lifecycle = NewLifecycle();
        var shell = new FakeShell();
        var relay = new PresentationRelay<DictationPresentation>(ui.Post, shell.Render, () => lifecycle.IsClosing);

        var a = lifecycle.TryBeginRecording(() => "muted microphone");
        Publish(relay, lifecycle.TryPresentRecording(a.DictationId)!.Value);
        var revision = lifecycle.TryGetRecordingPresentation(a.DictationId)!.Value.Revision; // preempted right after this
        EndWithAPause(lifecycle, relay);
        ui.Drain();

        PublishWarning(relay, shell, revision);
        ui.Drain();

        Assert.Equal(["show recording", "show processing", "hide", "release when idle"], shell.Commands);
    }

    [Fact]
    public void A_warning_for_the_recording_on_screen_is_shown()
    {
        using var ui = new FakeUiThread();
        var lifecycle = NewLifecycle();
        var shell = new FakeShell();
        var relay = new PresentationRelay<DictationPresentation>(ui.Post, shell.Render, () => lifecycle.IsClosing);

        var a = lifecycle.TryBeginRecording(() => "muted microphone");
        Publish(relay, lifecycle.TryPresentRecording(a.DictationId)!.Value);
        PublishWarning(relay, shell, lifecycle.TryGetRecordingPresentation(a.DictationId)?.Revision ?? 0);
        ui.Drain();

        Assert.Equal(["show recording", "show recording warning"], shell.Commands);
    }

    /// <summary>
    /// The reviewed defect, with the order the controller really uses (<see cref="ProcessingHandOff"/>): processing that
    /// fails at once queues its failure flash, and is preempted before it returns to idle. Had the Processing change been
    /// announced only after the processing started, it would have landed between the flash and the idle: showing
    /// Processing clears the flash's hold, and the idle then hid the pill, so the failure vanished unseen.
    /// </summary>
    [Fact]
    public void A_processing_that_fails_at_once_keeps_its_failure_on_screen()
    {
        var posted = new ConcurrentQueue<Action>(); // the UI thread's queue, first in first out
        var lifecycle = NewLifecycle();
        var pill = new FakePill();
        var relay = new PresentationRelay<DictationPresentation>(posted.Enqueue, pill.Render, () => false);

        var a = lifecycle.TryBeginRecording(() => "empty capture");
        Publish(relay, lifecycle.TryPresentRecording(a.DictationId)!.Value);
        var stop = lifecycle.TryBeginProcessing(a.DictationId);

        using var failureQueued = new ManualResetEventSlim();
        using var mayReturnToIdle = new ManualResetEventSlim();
        Thread? processing = null;
        ProcessingHandOff.Run(
            announce: () => Publish(relay, stop.Presentation),
            start: () =>
            {
                processing = BlockedThreads.Start(() =>
                {
                    posted.Enqueue(pill.ShowFailed); // the empty capture's failure flash
                    failureQueued.Set();
                    mayReturnToIdle.Wait(); // preempted before its last step returns it to idle
                    Publish(relay, lifecycle.ReturnToIdle(Timeout.InfiniteTimeSpan).Presentation);
                    lifecycle.EndProcessing(stop.Admission!);
                });

                // The worst case for the order: the processing reaches its failure before the thread that started it
                // does anything else.
                Assert.True(failureQueued.Wait(BlockedThreads.SafetyTimeout));
            });
        mayReturnToIdle.Set();
        BlockedThreads.Join(processing!);
        while (posted.TryDequeue(out var work))
        {
            work();
        }

        Assert.Equal("failed", pill.State);
        Assert.True(pill.FailureHeld);
    }

    [Fact]
    public void Work_runs_inline_on_the_UI_thread_and_is_posted_from_anywhere_else()
    {
        using var ui = new FakeUiThread();
        var ran = new ConcurrentQueue<string>();
        var dispatch = new UiThreadDispatch(() => ui.IsCurrent, ui.Post, () => false);

        ui.Invoke(() =>
        {
            dispatch.Run(() => ran.Enqueue("inline"));
            ran.Enqueue("after the inline call returned");
        });
        using var uiMayGoOn = new ManualResetEventSlim();
        ui.Post(uiMayGoOn.Wait);
        dispatch.Run(() => ran.Enqueue("posted")); // returns although the UI thread is busy
        Assert.Equal(["inline", "after the inline call returned"], ran);

        uiMayGoOn.Set();
        ui.Drain();
        Assert.Equal(["inline", "after the inline call returned", "posted"], ran);
    }

    [Fact]
    public void Work_posted_before_its_owner_was_disposed_is_dropped_when_it_runs()
    {
        var posted = new Queue<Action>();
        var ran = new List<string>();
        var disposed = false;
        var dispatch = new UiThreadDispatch(() => false, posted.Enqueue, () => disposed);

        dispatch.Run(() => ran.Add("tooltip"));
        disposed = true;
        RunAll(posted);
        dispatch.Run(() => ran.Add("balloon"));
        RunAll(posted);

        Assert.Empty(ran);
    }

    [Fact]
    public void A_dispatch_failure_is_reported_and_never_thrown_at_the_caller()
    {
        var failures = new List<Exception>();
        var inline = new UiThreadDispatch(() => true, _ => { }, () => false, failures.Add);
        inline.Run(() => throw new ObjectDisposedException("TaskbarIcon"));

        var posted = new Queue<Action>();
        var viaPost = new UiThreadDispatch(() => false, posted.Enqueue, () => false, failures.Add);
        viaPost.Run(() => throw new InvalidOperationException("shell refused the icon"));
        RunAll(posted);

        var brokenPost = new UiThreadDispatch(
            () => false, _ => throw new InvalidOperationException("dispatcher gone"), () => false, failures.Add);
        brokenPost.Run(() => { });

        Assert.Equal(3, failures.Count);
    }

    private static DictationLifecycle<string> NewLifecycle()
    {
        var lifecycle = new DictationLifecycle<string>(() => { }, () => { }, new ManualTimeProvider());
        lifecycle.Start(Timeout.InfiniteTimeSpan);
        return lifecycle;
    }

    // The pause stops the live recording and its processing returns to idle, paused, each change published as the
    // controller does.
    private static void EndWithAPause(DictationLifecycle<string> lifecycle, PresentationRelay<DictationPresentation> relay)
    {
        Assert.True(lifecycle.SetPaused(true).WasRecording);
        var pause = lifecycle.TryBeginProcessing();
        Publish(relay, pause.Presentation);
        Publish(relay, lifecycle.ReturnToIdle(Timeout.InfiniteTimeSpan).Presentation);
        lifecycle.EndProcessing(pause.Admission!);
    }

    // As the app shows a pill warning: nothing for a recording that was already over, otherwise bound to its revision.
    private static void PublishWarning(PresentationRelay<DictationPresentation> relay, FakeShell shell, long revision)
    {
        if (revision > 0)
        {
            relay.PublishIfCurrent(revision, shell.ShowRecordingWarning);
        }
    }

    private static void Publish(PresentationRelay<DictationPresentation> relay, DictationPresentation change) =>
        relay.Publish(change.Revision, change);

    private static void RunAll(Queue<Action> posted)
    {
        while (posted.TryDequeue(out var work))
        {
            work();
        }
    }

    private static IEnumerable<T[]> Permutations<T>(T[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, index) => index != i).ToArray();
            foreach (var tail in Permutations(rest))
            {
                yield return [items[i], .. tail];
            }
        }
    }

    // What the shell does with each change it shows, as the app's renderer does: the pill follows the state, and a pause
    // also ends the idle overlay helper.
    private sealed class FakeShell
    {
        private readonly ConcurrentQueue<string> _commands = new();

        public IReadOnlyList<string> Commands => [.. _commands];

        // Showing a warning puts the pill in its recording state (OverlayWindow.ShowRecordingWarning).
        public void ShowRecordingWarning() => _commands.Enqueue("show recording warning");

        public void Render(DictationPresentation shown)
        {
            switch (shown.Phase)
            {
                case DictationPhase.Recording:
                    _commands.Enqueue("show recording");
                    break;
                case DictationPhase.Processing:
                    _commands.Enqueue("show processing");
                    break;
                default:
                    _commands.Enqueue("hide");
                    if (shown.Paused)
                    {
                        _commands.Enqueue("release when idle");
                    }

                    break;
            }
        }
    }

    // The overlay pill's behaviour around a failure flash (OverlayWindow): the flash holds the pill on screen, a hide during
    // the hold is ignored, and showing recording or processing clears the hold.
    private sealed class FakePill
    {
        public string State { get; private set; } = "hidden";

        public bool FailureHeld { get; private set; }

        public void Render(DictationPresentation shown)
        {
            switch (shown.Phase)
            {
                case DictationPhase.Recording:
                    FailureHeld = false;
                    State = "recording";
                    break;
                case DictationPhase.Processing:
                    FailureHeld = false;
                    State = "processing";
                    break;
                default:
                    if (!FailureHeld)
                    {
                        State = "hidden";
                    }

                    break;
            }
        }

        public void ShowFailed()
        {
            FailureHeld = true;
            State = "failed";
        }
    }
}
