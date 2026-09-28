namespace Scribe.Core.TextInjection;

/// <summary>Elapsed times and event counts for one insertion, without any input or clipboard content.</summary>
public sealed record InjectionTimings(
    double CallerMs,
    double WorkerMs,
    double NativeSendMs,
    double RetrySleepMs,
    double SettleMs,
    double ClipboardSleepMs,
    double OtherWorkerMs,
    double MaxNativeCallMs,
    int NativeCalls,
    long RequestedEvents,
    long AcceptedEvents,
    int Retries);

internal sealed class InjectionTimingMeasurement(TimeProvider time)
{
    private readonly long _callerStarted = time.GetTimestamp();
    private long _workerStarted;
    private double _workerMs;

    public double NativeSendMs { get; private set; }
    public double RetrySleepMs { get; private set; }
    public double SettleMs { get; private set; }
    public double ClipboardSleepMs { get; private set; }
    public double MaxNativeCallMs { get; private set; }
    public int NativeCalls { get; private set; }
    public long RequestedEvents { get; private set; }
    public long AcceptedEvents { get; private set; }
    public int Retries { get; private set; }
    public bool WorkerStarted { get; private set; }

    public long Timestamp() => time.GetTimestamp();

    public void StartWorker()
    {
        _workerStarted = Timestamp();
        WorkerStarted = true;
    }

    public void StopWorker() => _workerMs = Elapsed(_workerStarted);

    public void BeginSend(int count)
    {
        NativeCalls++;
        RequestedEvents += count;
    }

    public void Accepted(uint count) => AcceptedEvents += count;

    public void EndSend(long started)
    {
        var elapsed = Elapsed(started);
        NativeSendMs += elapsed;
        MaxNativeCallMs = Math.Max(MaxNativeCallMs, elapsed);
    }

    public void EndWait(long started, InjectionWait kind)
    {
        var elapsed = Elapsed(started);
        switch (kind)
        {
            case InjectionWait.Retry:
                RetrySleepMs += elapsed;
                Retries++;
                break;
            case InjectionWait.Settle:
                SettleMs += elapsed;
                break;
            case InjectionWait.Clipboard:
                ClipboardSleepMs += elapsed;
                break;
        }
    }

    public InjectionTimings Snapshot() => new(
        Elapsed(_callerStarted), _workerMs, NativeSendMs, RetrySleepMs, SettleMs, ClipboardSleepMs,
        Math.Max(0, _workerMs - NativeSendMs - RetrySleepMs - SettleMs - ClipboardSleepMs),
        MaxNativeCallMs, NativeCalls, RequestedEvents, AcceptedEvents, Retries);

    private double Elapsed(long started) => time.GetElapsedTime(started).TotalMilliseconds;
}

internal enum InjectionWait
{
    Retry,
    Settle,
    Clipboard,
}
