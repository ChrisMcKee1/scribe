using System.Collections.Concurrent;
using NAudio.Wave;
using Scribe.Core.Audio;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;

namespace Scribe.Core.Tests.Concurrency;

/// <summary>
/// Fake capture endpoints for the capture service, whose captures behave like NAudio 3.0.1's WasapiCapture where it
/// matters: callbacks run on the capture thread one packet at a time, a stop is only a flag the loop reads between
/// packets, RecordingStopped is raised on that thread as it ends, and disposing a capture joins its thread with no bound.
/// Every open creates a new capture; everything that happens is written to one ordered event list.
/// </summary>
internal sealed class FakeCaptureStack
{
    public const string DeviceName = "Studio microphone";
    public const string DeviceId = "{0.0.1.00000000}.{fake-endpoint}";

    private readonly ConcurrentQueue<string> _events = new();
    private readonly ConcurrentQueue<FakeCapture> _captures = new();

    public FakeCaptureStack()
    {
        Devices = new FakeDevices(_events, new FakeDevice(this));
    }

    public FakeDevices Devices { get; }

    public CapturingLogger<AudioCaptureService> Log { get; } = new();

    public IReadOnlyList<string> Events => [.. _events];

    /// <summary>Every capture opened so far, oldest first.</summary>
    public IReadOnlyList<FakeCapture> Captures => [.. _captures];

    /// <summary>The capture opened most recently.</summary>
    public FakeCapture Capture => Captures is [.., var last] ? last : throw new InvalidOperationException("Nothing was opened.");

    /// <summary>The format every capture opened from now on reports (16 kHz mono float when null, as before).</summary>
    public WaveFormat? CaptureFormat { get; set; }

    /// <summary>
    /// Packets every capture opened from now on delivers from inside <see cref="FakeCapture.StartRecording"/>, each one
    /// handled before it returns, as NAudio's capture thread can deliver its first packet before StartRecording returns.
    /// </summary>
    public IReadOnlyList<byte[]>? PacketsBeforeStartReturns { get; set; }

    /// <summary>What <see cref="FakeCapture.StartRecording"/> throws for every capture opened from now on (nothing when null).</summary>
    public Exception? StartRecordingFailure { get; set; }

    public AudioCaptureService CreateService(TimeSpan disposeOpenWait) => new(Log, Devices, disposeOpenWait);

    public AudioCaptureService CreateService(TimeSpan disposeOpenWait, PerfFlags perfFlags) =>
        new(Log, Devices, disposeOpenWait, watch: null, perfFlags);

    internal void Record(string item) => _events.Enqueue(item);

    internal FakeCapture NewCapture()
    {
        var capture = new FakeCapture(_events)
        {
            PacketsBeforeStartReturns = PacketsBeforeStartReturns,
            StartRecordingFailure = StartRecordingFailure,
        };
        if (CaptureFormat is not null)
        {
            capture.WaveFormat = CaptureFormat;
        }

        _captures.Enqueue(capture);
        return capture;
    }
}

internal sealed class FakeDevices(ConcurrentQueue<string> events, FakeDevice device) : ICaptureDevices
{
    private int _disposeCount;

    /// <summary>Runs inside the open, which Start runs under the service's lock.</summary>
    public Action? DuringOpen { get; set; }

    public ManualResetEventSlim OpenEntered { get; } = new();

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public ICaptureDevice Open(string deviceId) => OpenDefault()!;

    public ICaptureDevice? OpenDefault()
    {
        events.Enqueue("open started");
        OpenEntered.Set();
        DuringOpen?.Invoke();
        events.Enqueue("open finished");
        return device;
    }

    public IReadOnlyList<AudioDevice> GetInputDevices() =>
        [new AudioDevice(FakeCaptureStack.DeviceId, FakeCaptureStack.DeviceName, IsDefault: true)];

    public void Dispose()
    {
        Interlocked.Increment(ref _disposeCount);
        events.Enqueue("devices disposed");
    }
}

internal sealed class FakeDevice(FakeCaptureStack stack) : ICaptureDevice
{
    public string FriendlyName => FakeCaptureStack.DeviceName;

    public bool IsMuted => false;

    public IWaveIn CreateCapture() => stack.NewCapture();

    public void Dispose() => stack.Record("device released");
}

internal sealed class FakeCapture(ConcurrentQueue<string> events) : IWaveIn
{
    private readonly BlockingCollection<byte[]> _packets = new();
    private Thread? _thread;
    private int _stopping;
    private int _disposeCount;
    private volatile Exception? _fault;
    private CountdownEvent? _handledBeforeStartReturns;

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(16_000, 1);

    /// <summary>Delivered, each one handled, from inside <see cref="StartRecording"/> before it returns (none when null).</summary>
    public IReadOnlyList<byte[]>? PacketsBeforeStartReturns { get; init; }

    /// <summary>Thrown by <see cref="StartRecording"/> before any thread starts (nothing when null).</summary>
    public Exception? StartRecordingFailure { get; init; }

    public ManualResetEventSlim ThreadEnded { get; } = new();

    public bool Started => _thread is not null;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public void StartRecording()
    {
        if (StartRecordingFailure is { } failure)
        {
            events.Enqueue("capture start failed");
            throw failure;
        }

        events.Enqueue("capture started");
        _thread = new Thread(Run) { IsBackground = true, Name = "fake capture thread" };
        _thread.Start();

        if (PacketsBeforeStartReturns is { Count: > 0 } early)
        {
            using var handled = new CountdownEvent(early.Count);
            _handledBeforeStartReturns = handled;
            foreach (var packet in early)
            {
                Deliver(packet);
            }

            handled.Wait();
            _handledBeforeStartReturns = null;
            events.Enqueue("early packets handled");
        }
    }

    // Only a flag, as in NAudio: the loop reads it between packets, so a callback that never returns holds the thread.
    public void StopRecording()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0)
        {
            events.Enqueue("capture stop requested");
            _packets.Add([]);
        }
    }

    public void Deliver(byte[] packet) => _packets.Add(packet);

    // A device fault as NAudio reports one: the capture loop ends, and RecordingStopped carries the exception.
    public void Fault(Exception error)
    {
        _fault = error;
        if (Interlocked.Exchange(ref _stopping, 1) == 0)
        {
            events.Enqueue("capture faulted");
            _packets.Add([]);
        }
    }

    // As in NAudio: stop, then join the capture thread with no bound.
    public void Dispose()
    {
        StopRecording();
        _thread?.Join();
        Interlocked.Increment(ref _disposeCount);
        events.Enqueue("capture disposed");
    }

    private void Run()
    {
        while (Volatile.Read(ref _stopping) == 0)
        {
            var packet = _packets.Take();
            if (Volatile.Read(ref _stopping) != 0)
            {
                break;
            }

            DataAvailable?.Invoke(this, new WaveInEventArgs(packet, packet.Length));
            Volatile.Read(ref _handledBeforeStartReturns)?.Signal();
        }

        RecordingStopped?.Invoke(this, new StoppedEventArgs(_fault));
        events.Enqueue("capture thread ended");
        ThreadEnded.Set();
    }
}
