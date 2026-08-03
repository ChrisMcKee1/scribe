namespace Scribe.Core.Infrastructure;

/// <summary>
/// Invokes every subscriber of a multicast delegate, even when one of them throws.
///
/// A plain <c>Handler?.Invoke(x)</c> wrapped in a single try/catch looks safe and is not: .NET
/// stops walking the invocation list at the first exception, so every subscriber registered after
/// the failing one silently stops receiving the event. In production a disposed tray icon threw
/// out of the first dictation state subscriber, which left later subscribers, including the
/// recording overlay, stuck on whatever state they last saw while dictation itself kept working.
/// </summary>
public static class ResilientEvent
{
    /// <summary>
    /// Calls each handler in turn. An exception from one handler is reported through
    /// <paramref name="onError"/> and never prevents the remaining handlers from running.
    /// </summary>
    public static void InvokeAll<T>(Action<T>? handlers, T argument, Action<Exception>? onError = null)
    {
        if (handlers is null) return;

        foreach (var entry in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T>)entry)(argument);
            }
            catch (Exception ex)
            {
                // Reporting is best-effort as well: a logger that throws must not stop the fan-out
                // it was only meant to describe.
                try
                {
                    onError?.Invoke(ex);
                }
                catch
                {
                    // Nothing useful is left to do here.
                }
            }
        }
    }
}
