namespace Scribe.Core.Lifecycle;

/// <summary>
/// Runs work on the UI thread without ever making another thread wait for it: inline when the caller is already on the UI
/// thread, posted otherwise. Posted work checks <c>isClosed</c> again when it runs, on the UI thread, because it can run
/// after its owner was disposed.
/// </summary>
/// <remarks>
/// Posting rather than invoking is the point. The callers include the audio capture thread and dictation processing, and
/// the UI thread waits for both at shutdown: it drains processing, and the capture service's disposal joins the capture
/// thread with no bound. A synchronous invoke from either while the UI thread is in that wait never returns. Failures are
/// reported, never thrown, so a notification cannot turn into a failure of the thread that raised it.
/// </remarks>
public sealed class UiThreadDispatch
{
    private readonly Func<bool> _isOnUiThread;
    private readonly Action<Action> _post;
    private readonly Func<bool> _isClosed;
    private readonly Action<Exception>? _onFailure;

    /// <param name="isOnUiThread">True when the calling thread is the UI thread.</param>
    /// <param name="post">Queues work for the UI thread and returns without waiting for it to run.</param>
    /// <param name="isClosed">True once the owner is disposed; work is then dropped.</param>
    /// <param name="onFailure">Told about work or a post that threw. Must not throw; if it does, that is ignored.</param>
    public UiThreadDispatch(
        Func<bool> isOnUiThread,
        Action<Action> post,
        Func<bool> isClosed,
        Action<Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(isOnUiThread);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(isClosed);
        _isOnUiThread = isOnUiThread;
        _post = post;
        _isClosed = isClosed;
        _onFailure = onFailure;
    }

    /// <summary>Runs <paramref name="work"/> now on the UI thread, or posts it there. Never waits, never throws.</summary>
    public void Run(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        try
        {
            if (_isOnUiThread())
            {
                RunNow(work);
                return;
            }

            _post(() => RunNow(work));
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private void RunNow(Action work)
    {
        try
        {
            if (!_isClosed())
            {
                work();
            }
        }
        catch (Exception ex)
        {
            Report(ex);
        }
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
