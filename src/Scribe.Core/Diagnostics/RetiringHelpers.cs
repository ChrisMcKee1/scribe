using System.Collections.Concurrent;
using System.Diagnostics;

namespace Scribe.Core.Diagnostics;

/// <summary>
/// The overlay helpers the app has ended whose exit it has not seen yet (DATA-IMPL-A-01). Ending a process only starts its
/// end: TerminateProcess is asynchronous, and a process exits only once its pending I/O has completed or been cancelled,
/// so a helper told to end can still land a write it had in flight, at the offset that write captured. The pair's way of
/// appending to the shared log changes only once every helper handed over here has been seen to exit
/// (<see cref="AppendOnlyLogMode.DecideForLaunch"/>).
/// </summary>
public sealed class RetiringHelpers
{
    private readonly ConcurrentQueue<Process> _helpers = new();
    private readonly Lock _waiting = new();

    /// <summary>How many helpers handed over have not been seen to exit yet.</summary>
    public int Count => _helpers.Count;

    /// <summary>
    /// Hands over <paramref name="helper"/>, ended, told to end or already gone; it is released once it has been seen to
    /// exit. Never waits and never throws.
    /// </summary>
    public void Add(Process? helper)
    {
        if (helper is not null)
        {
            _helpers.Enqueue(helper);
        }
    }

    /// <summary>
    /// Waits at most <paramref name="timeout"/> in all for every helper handed over to exit, releasing each one seen to
    /// exit, and returns whether none is left. One still running at the bound, or whose exit cannot be read, stays for the
    /// next call. Zero only releases the ones already gone. For the launcher's thread; never throws.
    /// </summary>
    public bool WaitForAllToExit(TimeSpan timeout)
    {
        lock (_waiting)
        {
            var clock = Stopwatch.StartNew();
            var running = new List<Process>();
            while (_helpers.TryDequeue(out var helper))
            {
                var left = timeout - clock.Elapsed;
                if (HasExited(helper, left > TimeSpan.Zero ? left : TimeSpan.Zero))
                {
                    Release(helper);
                }
                else
                {
                    running.Add(helper);
                }
            }

            foreach (var helper in running)
            {
                _helpers.Enqueue(helper);
            }

            return running.Count == 0;
        }
    }

    // Seen to exit: the process object is signaled, so every write it made has completed or been cancelled.
    private static bool HasExited(Process helper, TimeSpan wait)
    {
        try
        {
            return helper.WaitForExit(wait);
        }
        catch (Exception)
        {
            // An exit that cannot be read is not an exit seen: the helper stays, and the pair keeps its way.
            return false;
        }
    }

    private static void Release(Process helper)
    {
        try
        {
            helper.Dispose();
        }
        catch (Exception)
        {
            // Releasing the handle is best-effort; the exit has been seen either way.
        }
    }
}
