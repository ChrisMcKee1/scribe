using System.Diagnostics;

namespace Scribe.Core.Lifecycle;

/// <summary>Bounded condition waits on a monitor the caller already holds.</summary>
internal static class MonitorWait
{
    // Monitor.Wait takes at most int.MaxValue milliseconds per call; longer bounds are waited in slices.
    private static readonly TimeSpan MaxSlice = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// Waits until <paramref name="condition"/> holds or <paramref name="timeout"/> elapses, releasing
    /// <paramref name="sync"/> while blocked. The caller must hold <paramref name="sync"/>, and whoever changes the
    /// state the condition reads must pulse it. Returns the condition's final value.
    /// </summary>
    /// <param name="beforeFirstWait">
    /// Runs once, still under the lock, just before the first time the caller actually blocks. Must not re-enter
    /// anything guarded by <paramref name="sync"/>.
    /// </param>
    public static bool Until(object sync, Func<bool> condition, TimeSpan timeout, Action? beforeFirstWait = null)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "A wait bound must be non-negative or infinite.");
        }

        if (condition())
        {
            return true;
        }

        if (timeout == TimeSpan.Zero)
        {
            return false;
        }

        beforeFirstWait?.Invoke();
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                Monitor.Wait(sync);
            }
            else
            {
                var remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    return condition();
                }

                Monitor.Wait(sync, remaining < MaxSlice ? remaining : MaxSlice);
            }

            if (condition())
            {
                return true;
            }
        }
    }
}
