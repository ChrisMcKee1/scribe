namespace Scribe.Core.Settings;

/// <summary>
/// Coalesces a page's status refreshes (CoalesceDictionaryStatus): the Dictionary page recomputes its glossary hint,
/// coverage badges and tab summaries from every row, so it should do that once per burst of edits, not once per
/// notification. <see cref="Request"/> posts one refresh unless one is already waiting; <see cref="RefreshNow"/> refreshes
/// at once, as a row being added or removed always has; inside a <see cref="Batch"/> both wait, and the outermost batch's
/// end refreshes once, at once, if anything asked. After <see cref="Close"/> nothing refreshes again.
/// </summary>
/// <remarks>
/// For the UI thread only: the page's rows and controls live there, and so do every request and the posted refresh. A
/// posted refresh clears its waiting mark before it runs, so a request made while it runs posts one more rather than being
/// lost; the refresh itself must not request one (the Dictionary page ignores what its own badges write).
/// </remarks>
public sealed class StatusRefreshScheduler
{
    private readonly Action _refresh;
    private readonly Action<Action> _post;
    private bool _pending;
    private int _batchDepth;
    private bool _requestedInBatch;

    /// <summary>A scheduler that runs <paramref name="refresh"/>, posting it through <paramref name="post"/>.</summary>
    public StatusRefreshScheduler(Action refresh, Action<Action> post)
    {
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _post = post ?? throw new ArgumentNullException(nameof(post));
    }

    /// <summary>Whether a posted refresh is waiting to run.</summary>
    public bool IsPending => _pending;

    /// <summary>Whether a batch is open.</summary>
    public bool IsBatching => _batchDepth > 0;

    /// <summary>Whether <see cref="Close"/> was called.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>Asks for a refresh soon: posts one unless one is waiting, or, inside a batch, leaves it to the batch's end.</summary>
    public void Request()
    {
        if (IsClosed)
        {
            return;
        }

        if (_batchDepth > 0)
        {
            _requestedInBatch = true;
            return;
        }

        if (_pending)
        {
            return;
        }

        _pending = true;
        _post(RunPosted);
    }

    /// <summary>Refreshes now, or, inside a batch, once at the batch's end.</summary>
    public void RefreshNow()
    {
        if (IsClosed)
        {
            return;
        }

        if (_batchDepth > 0)
        {
            _requestedInBatch = true;
            return;
        }

        _refresh();
    }

    /// <summary>
    /// Holds every refresh until the returned scope is disposed; the outermost scope's end refreshes once, at once, if
    /// anything asked for one inside it. Scopes nest.
    /// </summary>
    public IDisposable Batch()
    {
        _batchDepth++;
        return new BatchScope(this);
    }

    /// <summary>The page closed: nothing refreshes again, a posted refresh included.</summary>
    public void Close() => IsClosed = true;

    private void RunPosted()
    {
        _pending = false;
        if (!IsClosed)
        {
            _refresh();
        }
    }

    private void EndBatch()
    {
        if (--_batchDepth > 0 || !_requestedInBatch)
        {
            return;
        }

        _requestedInBatch = false;
        if (!IsClosed)
        {
            _refresh();
        }
    }

    private sealed class BatchScope(StatusRefreshScheduler owner) : IDisposable
    {
        private StatusRefreshScheduler? _owner = owner;

        public void Dispose()
        {
            var owner = _owner;
            _owner = null;
            owner?.EndBatch();
        }
    }
}
