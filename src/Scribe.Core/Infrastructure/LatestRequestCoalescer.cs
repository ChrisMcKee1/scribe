namespace Scribe.Core.Infrastructure;

/// <summary>One request handed out by <see cref="LatestRequestCoalescer{TRequest}"/> to be computed.</summary>
public sealed record CoalescedRequest<TRequest>(TRequest Value, long Version, CancellationToken Cancellation);

/// <summary>
/// Runs at most one computation at a time and keeps only the newest request waiting behind it.
/// </summary>
/// <remarks>
/// <para>
/// For a refresh that can be asked for faster than it computes, such as the Settings usage page,
/// where every period change, refresh click and dictionary add used to start another full history
/// read and analysis in parallel. Now a request that arrives while one is running replaces any
/// request already waiting, and cancels the running one because its answer is already out of date.
/// The waiting request starts only once the running one has actually finished, because a
/// cancellation token cannot interrupt a synchronous SQLite call or an analysis already under way;
/// the computation checks it between steps.
/// </para>
/// <para>
/// <see cref="IsCurrent"/> answers whether a finished request may still publish, success or
/// failure alike: only the newest request may, and nothing may after <see cref="Close"/>.
/// </para>
/// <para>
/// Not thread-safe by design: the owner submits and completes on its dispatcher thread, and only the
/// computation itself runs elsewhere. Cancellation is signalled on the caller's thread with no lock
/// held, so a callback registered on the token cannot deadlock against this type.
/// </para>
/// </remarks>
public sealed class LatestRequestCoalescer<TRequest>
{
    private long _version;
    private bool _closed;
    private CoalescedRequest<TRequest>? _running;
    private CancellationTokenSource? _runningCancellation;
    private CoalescedRequest<TRequest>? _pending;

    public bool IsRunning => _running is not null;

    public bool HasPending => _pending is not null;

    /// <summary>
    /// Records a request. Returns it when it should start now, or null when a computation is
    /// already running (the request then waits as the only pending one) or the owner has closed.
    /// </summary>
    public CoalescedRequest<TRequest>? Submit(TRequest request)
    {
        if (_closed)
        {
            return null;
        }

        var version = ++_version;
        if (_running is not null)
        {
            // The token is filled in when the request actually starts.
            _pending = new CoalescedRequest<TRequest>(request, version, CancellationToken.None);
            _runningCancellation?.Cancel();
            return null;
        }

        return Start(request, version);
    }

    /// <summary>
    /// Call once the running computation has finished, however it ended. Returns the pending
    /// request to start next, or null when there is none.
    /// </summary>
    public CoalescedRequest<TRequest>? Complete(CoalescedRequest<TRequest> finished)
    {
        ArgumentNullException.ThrowIfNull(finished);
        if (!ReferenceEquals(finished, _running))
        {
            return null;
        }

        _runningCancellation?.Dispose();
        _runningCancellation = null;
        _running = null;

        if (_closed || _pending is not { } next)
        {
            _pending = null;
            return null;
        }

        _pending = null;
        return Start(next.Value, next.Version);
    }

    /// <summary>Whether a finished request may publish its result or its failure.</summary>
    public bool IsCurrent(CoalescedRequest<TRequest> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return !_closed && request.Version == _version;
    }

    /// <summary>
    /// The owner closed: cancels the running computation, drops the pending request, and makes every
    /// request stale.
    /// </summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _version++;
        _pending = null;

        // Disposed by Complete once the computation holding this token has finished.
        _runningCancellation?.Cancel();
    }

    private CoalescedRequest<TRequest> Start(TRequest request, long version)
    {
        _runningCancellation = new CancellationTokenSource();
        _running = new CoalescedRequest<TRequest>(request, version, _runningCancellation.Token);
        return _running;
    }
}
