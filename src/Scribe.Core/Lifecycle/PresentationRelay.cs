namespace Scribe.Core.Lifecycle;

/// <summary>
/// Carries numbered presentation changes, raised on any thread, to the one thread that shows them, and shows only the
/// newest. A change is numbered where it is made (see <see cref="DictationPresentation.Revision"/>) but raised afterwards,
/// so two threads can raise theirs in the opposite order: the dictation that just returned to idle can be preempted before
/// it raises its idle, while the next recording starts and raises its own. A change is rendered only when its revision is
/// higher than the last one rendered, so a late, older change can never undo a newer one, whatever the order of delivery.
/// </summary>
/// <remarks>
/// Publishing never waits for the rendering thread; it only posts. The threads that publish (the keyboard hook's dispatch
/// thread, the audio capture thread, timers, processing) must never wait for the UI thread, because the UI thread waits for
/// them at shutdown, and a capture thread parked in a synchronous call to the UI thread while the UI thread joins it never
/// returns. Nothing renders once <c>isClosed</c> reports true; that is checked when the work runs, on the rendering thread,
/// because posted work can run after shutdown began.
/// </remarks>
/// <typeparam name="T">What the renderer needs to show one change.</typeparam>
public sealed class PresentationRelay<T>
{
    private readonly Action<Action> _post;
    private readonly Action<T> _render;
    private readonly Func<bool> _isClosed;
    private readonly Action<Exception>? _onFailure;
    private long _lastRendered;

    /// <param name="post">Queues work for the rendering thread and returns without waiting for it to run.</param>
    /// <param name="render">Shows one change. Runs on the rendering thread.</param>
    /// <param name="isClosed">True once nothing may render any more. Read on the rendering thread before each render.</param>
    /// <param name="onFailure">Told about a post or a render that threw. Must not throw; if it does, that is ignored.</param>
    public PresentationRelay(Action<Action> post, Action<T> render, Func<bool> isClosed, Action<Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(isClosed);
        _post = post;
        _render = render;
        _isClosed = isClosed;
        _onFailure = onFailure;
    }

    /// <summary>The revision of the change rendered last; 0 before the first.</summary>
    public long LastRenderedRevision => Interlocked.Read(ref _lastRendered);

    /// <summary>
    /// Queues one change for the rendering thread and returns at once, without waiting for it to run. Never throws.
    /// </summary>
    /// <param name="revision">The change's number, from the transition that made it.</param>
    /// <param name="change">What the renderer shows for it.</param>
    public void Publish(long revision, T change)
    {
        try
        {
            _post(() => Render(revision, change));
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    /// <summary>
    /// Queues work that belongs to the change numbered <paramref name="revision"/>, such as a warning shown on that
    /// recording's pill, in the same queue as the changes themselves. It runs only if that change is still the last one
    /// rendered when the work's turn comes, so a notice raised for a state that has since been replaced (the recording
    /// was paused, stopped or superseded) can never bring that state back. Returns at once; never throws.
    /// </summary>
    public void PublishIfCurrent(long revision, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        try
        {
            _post(() => RunIfCurrent(revision, work));
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private void RunIfCurrent(long revision, Action work)
    {
        try
        {
            if (revision <= 0 || _isClosed() || Interlocked.Read(ref _lastRendered) != revision)
            {
                return;
            }

            work();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private void Render(long revision, T change)
    {
        try
        {
            if (_isClosed() || !TryAdvance(revision))
            {
                return;
            }

            _render(change);
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    // Claims the revision only if it is newer than every one rendered before. Rendering runs on one thread in practice; the
    // compare-and-swap keeps the rule true even for a post that runs work inline on the publishing threads.
    private bool TryAdvance(long revision)
    {
        var seen = Interlocked.Read(ref _lastRendered);
        while (revision > seen)
        {
            var previous = Interlocked.CompareExchange(ref _lastRendered, revision, seen);
            if (previous == seen)
            {
                return true;
            }

            seen = previous;
        }

        return false;
    }

    private void Report(Exception ex)
    {
        try
        {
            _onFailure?.Invoke(ex);
        }
        catch
        {
            // Reporting is best-effort; nothing useful is left to do.
        }
    }
}
