using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;

namespace Scribe.Core.TextInjection;

public enum PlainTextOnceStatus
{
    Off,
    Armed,
    Consumed,
    Expired,
    Cancelled,
    Shutdown,
}

public readonly record struct PlainTextOnceState(PlainTextOnceStatus Status, long Revision)
{
    public bool IsArmed => Status == PlainTextOnceStatus.Armed;
}

/// <summary>One transient, process-owned preference for the next admitted capture, never a cleanup or rule bypass.</summary>
public sealed class PlainTextOnce : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly ILogger<PlainTextOnce> _log;
    private readonly ITimer _timer;
    private PlainTextOnceState _state;
    private long _armedAt;
    private bool _closed;

    public PlainTextOnce(TimeProvider? time = null, ILogger<PlainTextOnce>? logger = null)
    {
        _time = time ?? TimeProvider.System;
        _log = logger ?? NullLogger<PlainTextOnce>.Instance;
        _timer = _time.CreateTimer(_ => Expire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Subscribers receive revisions, outside this owner's lock, and must not block capture admission.</summary>
    public event Action<PlainTextOnceState>? Changed;

    public PlainTextOnceState Current
    {
        get
        {
            PlainTextOnceState snapshot;
            bool changed;
            lock (_gate)
            {
                changed = ExpireLocked();
                snapshot = _state;
            }

            if (changed) Publish(snapshot);
            return snapshot;
        }
    }

    public void Arm()
    {
        PlainTextOnceState state;
        lock (_gate)
        {
            if (_closed) return;
            _armedAt = _time.GetTimestamp();
            state = SetLocked(PlainTextOnceStatus.Armed);
            _timer.Change(Lifetime, Timeout.InfiniteTimeSpan);
        }

        Publish(state);
    }

    public void Cancel()
    {
        PlainTextOnceState state;
        lock (_gate)
        {
            if (_closed || !_state.IsArmed) return;
            state = SetLocked(PlainTextOnceStatus.Cancelled);
        }

        Publish(state);
    }

    /// <summary>
    /// Called only inside the lifecycle's admitted capture factory, never from a hotkey callback or a refused press.
    /// A failed microphone open still belongs to an admitted capture and consumes the preference.
    /// </summary>
    public bool ConsumeForAdmittedCapture(out PlainTextOnceState? change)
    {
        PlainTextOnceState state;
        bool consumed;
        bool changed;
        lock (_gate)
        {
            changed = ExpireLocked();
            consumed = !_closed && _state.IsArmed;
            state = consumed ? SetLocked(PlainTextOnceStatus.Consumed) : _state;
        }

        change = consumed || changed ? state : null;
        return consumed;
    }

    /// <summary>Publish after the lifecycle has released its admission gate, never inside the capture factory.</summary>
    public void NotifyConsumption(PlainTextOnceState? change)
    {
        if (change is { } state) Publish(state);
    }

    private void Expire()
    {
        PlainTextOnceState state;
        bool changed;
        lock (_gate)
        {
            if (_closed) return;
            changed = ExpireLocked();
            state = _state;
            if (state.IsArmed)
            {
                var remaining = Lifetime - _time.GetElapsedTime(_armedAt);
                _timer.Change(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }

        if (changed) Publish(state);
    }

    private bool ExpireLocked()
    {
        if (!_state.IsArmed || _time.GetElapsedTime(_armedAt) < Lifetime) return false;
        SetLocked(PlainTextOnceStatus.Expired);
        return true;
    }

    private PlainTextOnceState SetLocked(PlainTextOnceStatus status)
    {
        _state = new PlainTextOnceState(status, _state.Revision + 1);
        if (status != PlainTextOnceStatus.Armed)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        return _state;
    }

    private void Publish(PlainTextOnceState state) =>
        ResilientEvent.InvokeAll(Changed, state, error =>
            _log.LogWarning("Could not publish the plain text once state ({Failure}).", FailureShape.Describe(error)));

    public void Dispose()
    {
        PlainTextOnceState state;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            state = SetLocked(PlainTextOnceStatus.Shutdown);
        }

        _timer.Dispose();
        Publish(state);
    }
}
