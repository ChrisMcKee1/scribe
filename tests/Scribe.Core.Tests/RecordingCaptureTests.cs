using System.Diagnostics;
using System.Runtime.InteropServices;
using Scribe.Core.Audio;
using Scribe.Core.Lifecycle;
using Scribe.Core.Models;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// Opening a recording's microphone while a stop can land before, during or just after the open, with the real lifecycle,
/// the real capture service over fake endpoints, and speech-like audio. Every test forces one owner and asserts exactly
/// what the processing receives. Two reviewed defects are replayed. First, an open that came after the processing had
/// already stopped (and found nothing) left a microphone running that nobody would stop. Second, the fix for that, which
/// stopped any capture the recording could no longer take, threw away the audio of a recording a pause or a fault had just
/// admitted to processing, which then received nothing.
/// </summary>
public sealed class RecordingCaptureTests
{
    // 100 ms of 16 kHz mono float per packet.
    private const int SamplesPerPacket = 1_600;

    /// <summary>
    /// The reviewed defect, pause variant, in both orders of the stop request and the open's hand-off: the microphone
    /// opens and records, a pause admits the recording, and the activation resumes before the processing reaches its stop.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_pause_after_the_microphone_opened_leaves_every_sample_to_processing(bool stopRequestedBeforeHandOff)
    {
        var (stack, service, lifecycle) = NewLoop();
        var a = lifecycle.TryBeginRecording(() => "a");
        var openDuration = OpenAsOwner(service, a.DictationId);
        Speak(stack, service, packets: 2);

        Assert.True(lifecycle.SetPaused(true).WasRecording);
        var pause = lifecycle.TryBeginProcessing();
        if (stopRequestedBeforeHandOff)
        {
            service.RequestStop(a.DictationId); // what the stop path does right after the admission
        }

        var open = RecordingCapture.HandOff(lifecycle, service, a.DictationId, openDuration); // the activation resumes
        Assert.Equal(RecordingOpenOutcome.LeftToProcessing, open.Outcome);
        Assert.Equal(0, stack.Capture.DisposeCount); // the open left it alone

        if (!stopRequestedBeforeHandOff)
        {
            service.RequestStop(a.DictationId);
        }

        var captured = service.Stop(a.DictationId); // the processing reaches its stop

        Assert.Equal(2 * SamplesPerPacket, captured.Samples.Length);
        Assert.Equal(1, stack.Capture.DisposeCount);
        Assert.False(service.IsCapturing);
        Finish(lifecycle, pause);
    }

    /// <summary>The same defect, microphone-fault variant: the fault's stop path admits the recording on the capture thread.</summary>
    [Fact]
    public void A_microphone_fault_after_the_microphone_opened_leaves_every_sample_to_processing()
    {
        var (stack, service, lifecycle) = NewLoop();
        var a = lifecycle.TryBeginRecording(() => "a");
        var openDuration = OpenAsOwner(service, a.DictationId);
        Speak(stack, service, packets: 3);

        using var faultHandled = new ManualResetEventSlim();
        StopDecision<string> fault = default;
        service.CaptureFaulted += (_, _) =>
        {
            // What the controller does with a fault, on the capture thread: admit the recording, ask its capture to stop.
            fault = lifecycle.TryBeginProcessing();
            service.RequestStop(a.DictationId);
            faultHandled.Set();
        };
        stack.Capture.Fault(new IOException("The device was unplugged."));
        Assert.True(faultHandled.Wait(BlockedThreads.SafetyTimeout));

        var open = RecordingCapture.HandOff(lifecycle, service, a.DictationId, openDuration);
        var captured = service.Stop(a.DictationId);

        Assert.Equal(RecordingOpenOutcome.LeftToProcessing, open.Outcome);
        Assert.NotNull(fault.Admission);
        Assert.Equal(3 * SamplesPerPacket, captured.Samples.Length);
        Assert.Equal(1, stack.Capture.DisposeCount);
        Finish(lifecycle, fault);
    }

    /// <summary>
    /// The pause lands while the device is still opening: the admission comes first, and the open finishes afterwards. The
    /// capture belongs to the processing, whose stop still receives everything recorded before it arrives.
    /// </summary>
    [Fact]
    public void A_pause_while_the_device_is_opening_leaves_every_sample_to_processing()
    {
        var (stack, service, lifecycle) = NewLoop();
        using var openMayFinish = new ManualResetEventSlim();
        using var releaseAtExit = new ReleaseAtExit(openMayFinish);
        stack.Devices.DuringOpen = openMayFinish.Wait;
        var a = lifecycle.TryBeginRecording(() => "a");

        RecordingOpen? open = null;
        var activation = BlockedThreads.Start(
            () => open = RecordingCapture.Open(lifecycle, service, a.DictationId, deviceId: null));
        Assert.True(stack.Devices.OpenEntered.Wait(BlockedThreads.SafetyTimeout));
        lifecycle.SetPaused(true);
        var pause = lifecycle.TryBeginProcessing();
        openMayFinish.Set();
        BlockedThreads.Join(activation);

        Assert.NotNull(open);
        Assert.Equal(RecordingOpenOutcome.LeftToProcessing, open.Value.Outcome);
        Assert.Equal(0, stack.Capture.DisposeCount);

        Speak(stack, service, packets: 2);
        service.RequestStop(a.DictationId);
        var captured = service.Stop(a.DictationId);

        Assert.Equal(2 * SamplesPerPacket, captured.Samples.Length);
        Assert.Equal(1, stack.Capture.DisposeCount);
        Finish(lifecycle, pause);
    }

    /// <summary>
    /// The first reviewed defect, gated: the pause's stop reaches the capture service before the activation does (with
    /// or without its processing having stopped as well). The capture service refuses the open, so there is nothing a
    /// nobody-owned microphone could keep recording, and the next recording opens a capture of its own.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_stop_that_reached_the_capture_service_before_the_open_means_nothing_is_opened(bool processingStoppedToo)
    {
        var (stack, service, lifecycle) = NewLoop();
        var a = lifecycle.TryBeginRecording(() => "a");
        using var activationMayOpen = new ManualResetEventSlim();
        using var releaseAtExit = new ReleaseAtExit(activationMayOpen);
        RecordingOpen? open = null;
        var activation = BlockedThreads.Start(() =>
        {
            activationMayOpen.Wait(); // preempted after winning admission, before reaching the capture service
            open = RecordingCapture.Open(lifecycle, service, a.DictationId, deviceId: null);
        });

        lifecycle.SetPaused(true);
        var pause = lifecycle.TryBeginProcessing();
        service.RequestStop(a.DictationId);
        if (processingStoppedToo)
        {
            Assert.Same(CapturedAudio.Empty, service.Stop(a.DictationId));
        }

        activationMayOpen.Set();
        BlockedThreads.Join(activation);

        Assert.NotNull(open);
        Assert.Equal(RecordingOpenOutcome.NotOpened, open.Value.Outcome);
        Assert.Empty(stack.Captures);
        Assert.False(service.IsCapturing);
        Assert.Same(CapturedAudio.Empty, service.Stop(a.DictationId)); // the processing's stop, whenever it comes
        Finish(lifecycle, pause);

        lifecycle.SetPaused(false);
        var b = lifecycle.TryBeginRecording(() => "b");
        Assert.Equal(RecordingOpenOutcome.Live, RecordingCapture.Open(lifecycle, service, b.DictationId, null).Outcome);
        Assert.Single(stack.Captures);
        service.Stop(b.DictationId);
    }

    /// <summary>
    /// Shutdown variant: shutdown begins before any stop admitted the recording, so no processing will ever ask for the
    /// capture. The open stops it itself, discarding what it recorded, and nothing else can receive it.
    /// </summary>
    [Fact]
    public void Shutdown_before_any_stop_admitted_the_recording_has_the_open_stop_its_microphone()
    {
        var (stack, service, lifecycle) = NewLoop();
        var a = lifecycle.TryBeginRecording(() => "a");
        var openDuration = OpenAsOwner(service, a.DictationId);
        Speak(stack, service, packets: 2);
        lifecycle.BeginShutdown();

        var open = RecordingCapture.HandOff(lifecycle, service, a.DictationId, openDuration);

        Assert.Equal(RecordingOpenOutcome.Reclaimed, open.Outcome);
        Assert.Null(open.ReclaimFailure);
        Assert.Equal(200, (int)Math.Round(open.Discarded.TotalMilliseconds));
        Assert.Null(lifecycle.TryBeginProcessing(a.DictationId).Admission); // nothing is admitted once closing
        Assert.Same(CapturedAudio.Empty, service.Stop(a.DictationId));
        Assert.False(service.IsCapturing);
        Assert.Equal(1, stack.Capture.DisposeCount);
    }

    /// <summary>Shutdown variant, after a stop admitted the recording: its processing still owns the capture.</summary>
    [Fact]
    public void Shutdown_after_a_stop_admitted_the_recording_still_leaves_every_sample_to_processing()
    {
        var (stack, service, lifecycle) = NewLoop();
        var a = lifecycle.TryBeginRecording(() => "a");
        var openDuration = OpenAsOwner(service, a.DictationId);
        Speak(stack, service, packets: 2);
        lifecycle.SetPaused(true);
        var pause = lifecycle.TryBeginProcessing();
        service.RequestStop(a.DictationId);
        lifecycle.BeginShutdown();

        var open = RecordingCapture.HandOff(lifecycle, service, a.DictationId, openDuration);
        var captured = service.Stop(a.DictationId); // processing stops its capture before it honors the cancellation

        Assert.Equal(RecordingOpenOutcome.LeftToProcessing, open.Outcome);
        Assert.Equal(2 * SamplesPerPacket, captured.Samples.Length);
        Finish(lifecycle, pause);
    }

    [Fact]
    public void An_open_that_finishes_after_shutdown_began_stops_its_microphone_again()
    {
        var (stack, service, lifecycle) = NewLoop();
        using var openMayFinish = new ManualResetEventSlim();
        using var releaseAtExit = new ReleaseAtExit(openMayFinish);
        stack.Devices.DuringOpen = openMayFinish.Wait;
        var a = lifecycle.TryBeginRecording(() => "a");

        RecordingOpen? open = null;
        var activation = BlockedThreads.Start(
            () => open = RecordingCapture.Open(lifecycle, service, a.DictationId, deviceId: null));
        Assert.True(stack.Devices.OpenEntered.Wait(BlockedThreads.SafetyTimeout));
        lifecycle.BeginShutdown();
        openMayFinish.Set();
        BlockedThreads.Join(activation);

        Assert.NotNull(open);
        Assert.Equal(RecordingOpenOutcome.Reclaimed, open.Value.Outcome);
        Assert.False(service.IsCapturing);
        Assert.Equal(1, stack.Capture.DisposeCount);
    }

    [Fact]
    public void A_recording_still_live_when_its_microphone_opens_takes_the_capture()
    {
        var (stack, service, lifecycle) = NewLoop();
        var a = lifecycle.TryBeginRecording(() => "a");

        var open = RecordingCapture.Open(lifecycle, service, a.DictationId, deviceId: null);

        Assert.Equal(RecordingOpenOutcome.Live, open.Outcome);
        Assert.Equal(new DictationPresentation(1, DictationPhase.Recording, false), open.Presentation);
        Assert.True(service.IsCapturing);
        Speak(stack, service, packets: 1);
        Assert.Equal(SamplesPerPacket, service.Stop(a.DictationId).Samples.Length);
    }

    [Fact]
    public void A_device_that_fails_to_open_throws_and_leaves_nothing_behind()
    {
        var (stack, service, lifecycle) = NewLoop();
        stack.Devices.DuringOpen = () => throw new InvalidOperationException("No active microphone was found.");
        var a = lifecycle.TryBeginRecording(() => "a");

        Assert.Throws<InvalidOperationException>(
            () => RecordingCapture.Open(lifecycle, service, a.DictationId, deviceId: null));

        Assert.False(service.IsCapturing);
        Assert.Empty(stack.Captures);
        Assert.Equal(DictationPhase.Recording, lifecycle.Phase); // the controller abandons it from its own catch
    }

    [Fact]
    public void A_stop_that_names_one_recording_never_touches_another_recordings_capture()
    {
        var (stack, service, _) = NewLoop();
        Assert.True(service.Start(deviceId: null, owner: 2));
        Speak(stack, service, packets: 1);

        service.RequestStop(owner: 1);
        Assert.Same(CapturedAudio.Empty, service.Stop(owner: 1));
        Assert.True(service.IsCapturing);
        Assert.Equal(0, stack.Capture.DisposeCount);

        Assert.Equal(SamplesPerPacket, service.Stop(owner: 2).Samples.Length); // exactly once, to its own recording
        Assert.Same(CapturedAudio.Empty, service.Stop(owner: 2));
    }

    [Fact]
    public void A_start_for_a_recording_whose_stop_already_arrived_opens_nothing_and_later_recordings_open_normally()
    {
        var (stack, service, _) = NewLoop();

        service.RequestStop(owner: 5);
        Assert.False(service.Start(deviceId: null, owner: 5));
        Assert.False(service.Start(deviceId: null, owner: 4)); // an older recording's late open, likewise
        Assert.Empty(stack.Captures);

        Assert.True(service.Start(deviceId: null, owner: 6));
        Assert.False(service.Start(deviceId: null, owner: 7)); // one capture at a time
        Assert.Single(stack.Captures);
        service.Stop(owner: 6);
    }

    private static (FakeCaptureStack Stack, AudioCaptureService Service, DictationLifecycle<string> Lifecycle) NewLoop()
    {
        var stack = new FakeCaptureStack();
        var lifecycle = new DictationLifecycle<string>(() => { }, () => { }, new ManualTimeProvider());
        lifecycle.Start(Timeout.InfiniteTimeSpan);
        return (stack, stack.CreateService(BlockedThreads.SafetyTimeout), lifecycle);
    }

    // The first half of RecordingCapture.Open, as the activation path runs it, for a test to land a stop after it.
    private static TimeSpan OpenAsOwner(AudioCaptureService service, long dictationId)
    {
        var started = Stopwatch.GetTimestamp();
        Assert.True(service.Start(deviceId: null, owner: dictationId));
        return Stopwatch.GetElapsedTime(started);
    }

    // Delivers speech-like audio to the live capture and returns once the capture service has recorded all of it.
    private static void Speak(FakeCaptureStack stack, AudioCaptureService service, int packets)
    {
        using var recorded = new CountdownEvent(packets);
        void OnLevel(object? sender, float level) => recorded.Signal();
        service.LevelChanged += OnLevel;
        try
        {
            for (var i = 0; i < packets; i++)
            {
                stack.Capture.Deliver(SpeechPacket());
            }

            Assert.True(recorded.Wait(BlockedThreads.SafetyTimeout));
        }
        finally
        {
            service.LevelChanged -= OnLevel;
        }
    }

    private static byte[] SpeechPacket()
    {
        var samples = new float[SamplesPerPacket];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.25f * MathF.Sin(i * 0.05f);
        }

        return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }

    private static void Finish(DictationLifecycle<string> lifecycle, StopDecision<string> stop)
    {
        lifecycle.ReturnToIdle(Timeout.InfiniteTimeSpan);
        lifecycle.EndProcessing(stop.Admission!);
    }
}
