using System.Runtime.InteropServices;
using Scribe.Core.Audio;
using Scribe.Core.Lifecycle;
using Scribe.Core.Models;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// A microphone that opens and then sends nothing at all. A production log showed three dictations in a row on a USB
/// microphone that had been unplugged and plugged back in: each opened in under 100 ms, was held for 47 to 58 s, and
/// captured nothing, which nobody heard about until the shortcut was let go. These drive the real lifecycle and the real
/// capture service over fake endpoints, on a clock the test moves, through the check the controller runs a second in.
/// </summary>
public sealed class AudioArrivalCheckTests
{
    private const int SamplesPerPacket = 1_600;

    [Fact]
    public void A_microphone_that_opens_and_sends_nothing_is_reported_once_its_wait_has_passed()
    {
        var loop = new Loop();
        var id = loop.OpenRecording();
        loop.Lifecycle.ArmAudioArrivalCheck(id, RecordingCapture.AudioArrivalWait);
        Assert.Equal(AudioArrival.NotYet, loop.Service.GetAudioArrival(id));

        loop.Clock.Advance(RecordingCapture.AudioArrivalWait - TimeSpan.FromMilliseconds(1));
        Assert.Null(RecordingCapture.FindRecordingWithoutAudio(loop.Lifecycle, loop.Service));

        loop.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(id, RecordingCapture.FindRecordingWithoutAudio(loop.Lifecycle, loop.Service));
        Assert.Null(RecordingCapture.FindRecordingWithoutAudio(loop.Lifecycle, loop.Service)); // once per recording

        // Reported, not stopped: audio that starts late is still recorded and reaches the processing.
        Assert.Equal(DictationPhase.Recording, loop.Lifecycle.Phase);
        Assert.True(loop.Service.IsCapturing);
        loop.Speak(packets: 2);
        Assert.Equal(2 * SamplesPerPacket, loop.Finish(id).Samples.Length);
    }

    [Fact]
    public void A_microphone_that_sends_audio_is_never_reported()
    {
        var loop = new Loop();
        var id = loop.OpenRecording();
        loop.Lifecycle.ArmAudioArrivalCheck(id, RecordingCapture.AudioArrivalWait);
        loop.Speak(packets: 1);

        loop.Clock.Advance(RecordingCapture.AudioArrivalWait);
        Assert.Equal(AudioArrival.Arrived, loop.Service.GetAudioArrival(id));
        Assert.Null(RecordingCapture.FindRecordingWithoutAudio(loop.Lifecycle, loop.Service));
        loop.Finish(id);
    }

    /// <summary>
    /// A muted microphone sends packets of zeros and has a warning of its own, so zeros count as audio here. A callback
    /// that carries no bytes is not audio.
    /// </summary>
    [Fact]
    public void A_packet_of_zeros_counts_as_audio_and_an_empty_one_does_not()
    {
        var loop = new Loop();
        var id = loop.OpenRecording();

        loop.Service.OnPacket(loop.Stack.Capture, [], 0);
        Assert.Equal(AudioArrival.NotYet, loop.Service.GetAudioArrival(id));

        loop.Speak(packets: 1, Packet(0f));
        Assert.Equal(AudioArrival.Arrived, loop.Service.GetAudioArrival(id));
        loop.Finish(id);
    }

    [Fact]
    public void A_packet_that_comes_before_the_stream_start_returns_counts_for_that_capture()
    {
        var loop = new Loop();
        loop.Stack.PacketsBeforeStartReturns = [Packet(0.25f)];

        var id = loop.OpenRecording();

        Assert.Equal(AudioArrival.Arrived, loop.Service.GetAudioArrival(id));
        loop.Finish(id);
    }

    /// <summary>
    /// The flag belongs to one capture: the next starts without audio whatever the last one heard, and the service answers
    /// only the recording that owns the live capture, and only until its stop is asked for.
    /// </summary>
    [Fact]
    public void Each_capture_starts_without_audio_and_only_its_live_owner_is_answered()
    {
        var loop = new Loop();
        var first = loop.OpenRecording();
        loop.Speak(packets: 1);
        Assert.Equal(AudioArrival.Arrived, loop.Service.GetAudioArrival(first));
        loop.Finish(first);
        Assert.Equal(AudioArrival.Unknown, loop.Service.GetAudioArrival(first));

        var second = loop.OpenRecording();
        Assert.Equal(AudioArrival.NotYet, loop.Service.GetAudioArrival(second));
        Assert.Equal(AudioArrival.Unknown, loop.Service.GetAudioArrival(first));
        Assert.Equal(AudioArrival.Unknown, loop.Service.GetAudioArrival(0));

        loop.Service.RequestStop(second);
        Assert.Equal(AudioArrival.Unknown, loop.Service.GetAudioArrival(second));
        loop.Finish(second);
    }

    [Fact]
    public void A_recording_that_ended_before_its_check_is_not_reported()
    {
        var loop = new Loop();
        var id = loop.OpenRecording();
        loop.Lifecycle.ArmAudioArrivalCheck(id, RecordingCapture.AudioArrivalWait);

        var stop = loop.Lifecycle.TryBeginProcessing(id); // a quick tap: released before the check is due
        loop.Clock.Advance(RecordingCapture.AudioArrivalWait);

        Assert.Null(RecordingCapture.FindRecordingWithoutAudio(loop.Lifecycle, loop.Service));
        loop.Service.Stop(id);
        loop.Lifecycle.ReturnToIdle(Timeout.InfiniteTimeSpan);
        loop.Lifecycle.EndProcessing(stop.Admission!);
    }

    [Fact]
    public void A_capture_service_that_does_not_track_arrival_reports_nothing()
    {
        var lifecycle = new DictationLifecycle<string>(() => { }, () => { }, new ManualTimeProvider());
        lifecycle.Start(Timeout.InfiniteTimeSpan);
        var recording = lifecycle.TryBeginRecording(() => "a");
        lifecycle.TryPresentRecording(recording.DictationId);
        lifecycle.ArmAudioArrivalCheck(recording.DictationId, TimeSpan.Zero);

        IAudioCaptureService untracked = new UntrackedCapture();
        Assert.Equal(AudioArrival.Unknown, untracked.GetAudioArrival(recording.DictationId));
        Assert.Null(RecordingCapture.FindRecordingWithoutAudio(lifecycle, untracked));
    }

    /// <summary>
    /// The controller has no tests of its own, so its wiring is read from source: the check is armed once the microphone
    /// has opened, its tick goes through the Core decision, and what it finds is a warning on the live recording.
    /// </summary>
    [Fact]
    public void The_controller_arms_the_check_once_the_microphone_opens_and_warns_the_live_recording()
    {
        var controller = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Dictation", "DictationController.cs"));

        Assert.Contains("onAudioArrivalCheckDue: OnAudioArrivalCheckDue", controller, StringComparison.Ordinal);
        var open = controller.IndexOf("RecordingCapture.Open(_lifecycle, _audio, id,", StringComparison.Ordinal);
        var arm = controller.IndexOf(
            "_lifecycle.ArmAudioArrivalCheck(id, RecordingCapture.AudioArrivalWait);", StringComparison.Ordinal);
        Assert.True(open >= 0 && arm > open, "The audio arrival check must be armed after the microphone opened.");
        Assert.Contains(
            "RecordingCapture.FindRecordingWithoutAudio(_lifecycle, _audio) is not { } id", controller, StringComparison.Ordinal);
        Assert.Contains(
            "RaiseWarning(new DictationProblemReport(DictationProblem.MicrophoneNotSending), id);",
            controller,
            StringComparison.Ordinal);
    }

    private static byte[] Packet(float amplitude)
    {
        var samples = new float[SamplesPerPacket];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = amplitude * MathF.Sin(i * 0.05f);
        }

        return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    // The activation path's side: the lifecycle on a manual clock and the capture service over fake endpoints.
    private sealed class Loop
    {
        public Loop()
        {
            Lifecycle = new DictationLifecycle<string>(() => { }, () => { }, Clock);
            Lifecycle.Start(Timeout.InfiniteTimeSpan);
            Service = Stack.CreateService(BlockedThreads.SafetyTimeout);
        }

        public ManualTimeProvider Clock { get; } = new();

        public FakeCaptureStack Stack { get; } = new();

        public DictationLifecycle<string> Lifecycle { get; }

        public AudioCaptureService Service { get; }

        public long OpenRecording()
        {
            var recording = Lifecycle.TryBeginRecording(() => "recording");
            Assert.Equal(ActivationDecision.Started, recording.Decision);
            var open = RecordingCapture.Open(Lifecycle, Service, recording.DictationId, deviceId: null);
            Assert.Equal(RecordingOpenOutcome.Live, open.Outcome);
            return recording.DictationId;
        }

        // Delivers packets to the live capture and returns once the capture service has handled all of them.
        public void Speak(int packets, byte[]? packet = null)
        {
            using var recorded = new CountdownEvent(packets);
            void OnLevel(object? sender, float level) => recorded.Signal();
            Service.LevelChanged += OnLevel;
            try
            {
                for (var i = 0; i < packets; i++)
                {
                    Stack.Capture.Deliver(packet ?? Packet(0.25f));
                }

                Assert.True(recorded.Wait(BlockedThreads.SafetyTimeout));
            }
            finally
            {
                Service.LevelChanged -= OnLevel;
            }
        }

        public CapturedAudio Finish(long id)
        {
            var stop = Lifecycle.TryBeginProcessing(id);
            Assert.NotNull(stop.Admission);
            var captured = Service.Stop(id);
            Lifecycle.ReturnToIdle(Timeout.InfiniteTimeSpan);
            Lifecycle.EndProcessing(stop.Admission);
            return captured;
        }
    }

    // A capture service written before arrival was tracked: it keeps the interface's default answer.
    private sealed class UntrackedCapture : IAudioCaptureService
    {
        public bool IsCapturing => true;

        public string? LastDeviceName => null;

        public bool LastDeviceMuted => false;

        public bool LastCaptureWasSilent => false;

        public bool LastRequestedDeviceUnavailable => false;

        public CaptureSignalReport? LastSignalReport => null;

        public event EventHandler<float>? LevelChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<Exception>? CaptureFaulted
        {
            add { }
            remove { }
        }

        public event Action<IReadOnlyList<AudioDevice>>? InputDevicesChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<AudioDevice> GetInputDevices() => [];

        public bool Start(string? deviceId = null, long owner = 0) => true;

        public void RequestStop(long owner = 0)
        {
        }

        public CapturedAudio Stop(long owner = 0) => CapturedAudio.Empty;

        public void Dispose()
        {
        }
    }
}
