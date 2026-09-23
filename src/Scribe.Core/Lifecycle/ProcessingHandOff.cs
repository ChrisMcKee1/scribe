namespace Scribe.Core.Lifecycle;

/// <summary>
/// Hands a stop that the lifecycle admitted to processing on to that processing, in the one order that keeps the shell
/// right: the Processing change is announced first, and only then does the processing start.
/// </summary>
/// <remarks>
/// The shell hears about everything through one queue (the UI thread's), in the order it was queued, and processing that
/// fails quickly (an empty capture, say) queues its failure flash the moment it starts. Announced after the start,
/// Processing could land between that flash and the return to idle; showing Processing clears the flash's hold, and the
/// idle that follows then hides the pill, so the failure vanished unseen. Announcing first costs nothing, because the
/// announcement only queues and never waits for the UI thread.
/// </remarks>
public static class ProcessingHandOff
{
    /// <param name="announce">Raises the Processing change. Must not block; the shell only queues it.</param>
    /// <param name="start">Starts the processing, which can queue its outcome at any moment from then on.</param>
    public static void Run(Action announce, Action start)
    {
        ArgumentNullException.ThrowIfNull(announce);
        ArgumentNullException.ThrowIfNull(start);

        announce();
        start();
    }
}
