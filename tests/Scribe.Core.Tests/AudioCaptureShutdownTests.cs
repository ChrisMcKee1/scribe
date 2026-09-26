using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Scribe.Core.Lifecycle;
using Scribe.Core.Models;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// The capture service's disposal against what can still be running inside it at shutdown, through fake capture endpoints
/// whose capture behaves like NAudio 3.0.1's WasapiCapture where it matters: callbacks run on the capture thread one packet
/// at a time, a stop is only a flag the loop reads between packets, RecordingStopped is raised on that thread as it ends,
/// and disposing the capture joins the thread with no bound. Two tests replay reviewed interleavings exactly: the device
/// enumerator released under an open still in progress, and a silence auto-stop callback that notifies the UI while the
/// UI thread is joining it.
/// </summary>
public sealed class AudioCaptureShutdownTests
{
    // 100 ms of 16 kHz mono float.
    private static readonly byte[] Packet = new byte[16_000 / 10 * sizeof(float)];

    /// <summary>
    /// The first press after launch is still resolving its device, holding the service's lock, when the user quits. The
    /// controller's stop step reads no capture running and stops nothing, the hotkey thread outlives its bounded join, and
    /// the host disposes the service: disposing the enumerator there released it under the open still using it.
    /// </summary>
    [Fact]
    public void Disposal_waits_out_a_device_open_in_progress_and_releases_the_enumerator_only_after_it()
    {
        var stack = new FakeCaptureStack();
        using var openMayFinish = new ManualResetEventSlim();
        using var releaseAtExit = new ReleaseAtExit(openMayFinish);
        stack.Devices.DuringOpen = openMayFinish.Wait;

        // Disposal waits out the open for as long as this bound, and the test checks it is still waiting: longer than every guard
        // here, so only the test's release ends the wait (stream TR round 5, A9).
        var service = stack.CreateService(TimeSpan.FromMinutes(10));

        var hotkeyThread = BlockedThreads.Start(() => service.Start());
        Assert.True(stack.Devices.OpenEntered.Wait(BlockedThreads.SafetyTimeout));
        Assert.False(service.IsCapturing); // all the controller's stop step sees, so it asks nothing to stop

        var hostDisposal = BlockedThreads.Start(service.Dispose);
        BlockedThreads.WaitUntilBlocked(hostDisposal);
        Assert.Equal(0, stack.Devices.DisposeCount); // waiting for the lock the open holds, having released nothing

        openMayFinish.Set();
        BlockedThreads.Join(hotkeyThread);
        BlockedThreads.Join(hostDisposal);

        Assert.Equal(
            [
                "open started", "open finished", "capture started", "capture stop requested", "capture thread ended",
                "capture disposed", "device released", "devices disposed",
            ],
            stack.Events);
        Assert.False(service.IsCapturing);

        // Stopped as a requested stop, not reported as a stream that ended on its own.
        Assert.DoesNotContain(stack.Log.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public void Past_its_bound_disposal_leaves_the_enumerator_to_the_process_and_logs_only_the_shape()
    {
        var stack = new FakeCaptureStack();
        using var openMayFinish = new ManualResetEventSlim();
        using var releaseAtExit = new ReleaseAtExit(openMayFinish);
        stack.Devices.DuringOpen = openMayFinish.Wait;
        var service = stack.CreateService(TimeSpan.Zero);

        var hotkeyThread = BlockedThreads.Start(() => service.Start());
        Assert.True(stack.Devices.OpenEntered.Wait(BlockedThreads.SafetyTimeout));

        service.Dispose(); // the bound is already over, and the open still holds the lock

        Assert.Equal(0, stack.Devices.DisposeCount);
        var warning = Assert.Single(stack.Log.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.False(warning.Mentions(FakeCaptureStack.DeviceName));
        Assert.False(warning.Mentions(FakeCaptureStack.DeviceId));

        // The open finishes afterwards; the controller, finding shutdown under way when Start returns, asks it to stop.
        openMayFinish.Set();
        BlockedThreads.Join(hotkeyThread);
        Assert.True(service.IsCapturing);
        service.RequestStop();
        Assert.True(stack.Capture.ThreadEnded.Wait(BlockedThreads.SafetyTimeout));

        // Never released, and nothing new may start.
        Assert.Equal(0, stack.Devices.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => service.Start());
        Assert.Throws<ObjectDisposedException>(() => service.GetInputDevices());
    }

    [Fact]
    public void A_start_that_reaches_the_service_after_disposal_is_refused_before_it_touches_the_enumerator()
    {
        var stack = new FakeCaptureStack();
        var service = stack.CreateService(BlockedThreads.SafetyTimeout);

        service.Dispose();
        service.Dispose();

        Assert.Throws<ObjectDisposedException>(() => service.Start(FakeCaptureStack.DeviceId));
        Assert.Throws<ObjectDisposedException>(() => service.GetInputDevices());
        Assert.Equal(["devices disposed"], stack.Events); // released once, and no open ever began
        Assert.Equal(CapturedAudio.Empty, service.Stop()); // teardown calls stay harmless
        service.RequestStop();
    }

    /// <summary>
    /// Disposal stops a live capture after releasing the lock. The capture thread's own callbacks take that lock (the
    /// controller's auto-stop and fault handlers request a stop), so stopping under it parks the capture thread on the lock
    /// while disposal waits for it and then joins it.
    /// </summary>
    [Fact]
    public void Disposal_stops_a_live_capture_outside_the_lock_its_capture_thread_needs()
    {
        var stack = new FakeCaptureStack();
        var service = stack.CreateService(BlockedThreads.SafetyTimeout);
        service.Start();

        using var callbackEntered = new ManualResetEventSlim();
        using var callbackMayGoOn = new ManualResetEventSlim();
        using var releaseAtExit = new ReleaseAtExit(callbackMayGoOn);
        service.LevelChanged += (_, _) =>
        {
            callbackEntered.Set();
            callbackMayGoOn.Wait();
            service.RequestStop(); // what the controller's silence auto-stop does, on the capture thread
        };
        stack.Capture.Deliver(Packet);
        Assert.True(callbackEntered.Wait(BlockedThreads.SafetyTimeout));

        var hostDisposal = BlockedThreads.Start(service.Dispose);
        BlockedThreads.WaitUntilBlocked(hostDisposal); // waiting for the stop, which the parked callback holds up
        callbackMayGoOn.Set();
        BlockedThreads.Join(hostDisposal);

        Assert.True(stack.Capture.ThreadEnded.IsSet);
        Assert.Equal(1, stack.Devices.DisposeCount);
        Assert.Equal("devices disposed", stack.Events[^1]);
    }

    /// <summary>
    /// The reviewed deadlock, replayed: the silence auto-stop callback, on the capture thread, wins admission to processing,
    /// asks the capture to stop and passes the closing check, then is preempted. The UI thread begins the exit and reaches
    /// the host's disposal of the capture service, which waits for the capture to stop and joins its thread with no bound.
    /// The callback resumes and tells the UI about the change. Invoked synchronously, that notice waited for the UI thread,
    /// which was joining the capture thread: neither ever moved again. Posted, the callback returns, the capture thread
    /// ends, the join completes, and the notices find the app closing when they finally run.
    /// </summary>
    [Fact]
    public void A_silence_auto_stop_that_notifies_the_UI_while_the_UI_thread_disposes_the_capture_never_deadlocks()
    {
        using var ui = new FakeUiThread();
        var stack = new FakeCaptureStack();
        var service = stack.CreateService(BlockedThreads.SafetyTimeout);
        var lifecycle = new DictationLifecycle<string>(() => { }, () => { }, new ManualTimeProvider());
        lifecycle.Start(Timeout.InfiniteTimeSpan);

        var rendered = new ConcurrentQueue<DictationPhase>();
        var trayNotices = new ConcurrentQueue<string>();
        var trayDisposed = 0;
        var relay = new PresentationRelay<DictationPresentation>(
            ui.Post, shown => rendered.Enqueue(shown.Phase), () => lifecycle.IsClosing);
        var tray = new UiThreadDispatch(() => ui.IsCurrent, ui.Post, () => Volatile.Read(ref trayDisposed) != 0);

        var recording = lifecycle.TryBeginRecording(() => "live");
        service.Start();
        var live = lifecycle.TryPresentRecording(recording.DictationId)!.Value;
        relay.Publish(live.Revision, live);
        ui.Drain();

        using var preempted = new ManualResetEventSlim();
        using var resumed = new ManualResetEventSlim();
        using var notified = new ManualResetEventSlim();
        using var releaseAtExit = new ReleaseAtExit(resumed);
        service.LevelChanged += (_, _) =>
        {
            var stop = lifecycle.TryBeginProcessing(recording.DictationId);
            if (stop.Admission is null)
            {
                return;
            }

            service.RequestStop();
            var passedClosingCheck = !lifecycle.IsClosing;
            preempted.Set();
            resumed.Wait();
            if (passedClosingCheck)
            {
                relay.Publish(stop.Presentation.Revision, stop.Presentation);
                tray.Run(() => trayNotices.Enqueue("error tooltip"));
            }

            notified.Set();
        };

        stack.Capture.Deliver(Packet);
        Assert.True(preempted.Wait(BlockedThreads.SafetyTimeout));

        using var exiting = new ManualResetEventSlim();
        using var exited = new ManualResetEventSlim();
        ui.Post(() =>
        {
            lifecycle.BeginShutdown();
            exiting.Set();
            service.Dispose(); // the host's disposal of the capture service, inside the app's exit
            Interlocked.Exchange(ref trayDisposed, 1); // the tray is gone by the time posted work can run
            exited.Set();
        });
        Assert.True(exiting.Wait(BlockedThreads.SafetyTimeout));
        BlockedThreads.WaitUntilBlocked(ui.Thread); // waiting for the capture to stop; the callback holds it up
        resumed.Set();

        Assert.True(exited.Wait(BlockedThreads.SafetyTimeout), "The UI thread never finished disposing the capture.");
        Assert.True(notified.IsSet);
        ui.Drain();

        Assert.Equal([DictationPhase.Recording], rendered); // the Processing notice ran only after the exit began
        Assert.Empty(trayNotices);
        Assert.True(stack.Capture.ThreadEnded.IsSet);
        Assert.Equal("devices disposed", stack.Events[^1]);
        Assert.Empty(ui.Faults);
    }
}
