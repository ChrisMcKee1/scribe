using System.Diagnostics;

namespace Scribe.Core.Infrastructure;

/// <summary>
/// Waits for a process whose output is redirected, without letting something it left running hold the wait.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Process.WaitForExitAsync"/> waits for the process to exit and then for the end of its redirected
/// output. A process it started that inherited the output pipe, and is still running, keeps that output open, so the
/// wait lasted until that process exited too. Measured with Scribe's own AzureCliInstaller: an az whose child had the
/// pipe passed on (cmd's start does that) exited after 5 s, and the call returned after 30 s, holding Azure CLI's
/// single gate all that time. A child started through os.startfile, as az's browser sign-in starts a browser, did not
/// hold it.
/// </para>
/// <para>
/// So the exit alone is awaited first, and then the output is given a bounded time to end. By the time the process
/// has exited, everything it wrote is in the pipe, and the reads that were already running take it in; only a last
/// line with no line break waits for the end of the output.
/// </para>
/// </remarks>
public static class ProcessExit
{
    /// <summary>
    /// Waits until <paramref name="process"/> exits, then up to <paramref name="outputLimit"/> for the end of its
    /// redirected output. <paramref name="cancellationToken"/> cancels either wait.
    /// </summary>
    public static Task WaitAsync(Process process, TimeSpan outputLimit, CancellationToken cancellationToken) =>
        WaitAsync(process, outputLimit, TimeProvider.System, cancellationToken);

    /// <summary>
    /// The same wait with <paramref name="outputLimit"/> counted on <paramref name="time"/>. On the system clock, which the
    /// public overload passes, the limit's source arms the same timer <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>
    /// did, from the same point; a test passes a clock only it moves, so it decides when the limit passes.
    /// </summary>
    internal static async Task WaitAsync(Process process, TimeSpan outputLimit, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(time);

        await WaitForTheExitAloneAsync(process, cancellationToken).ConfigureAwait(false);

        using var limit = new CancellationTokenSource(outputLimit, time);
        using var output = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            // The process has exited, so this now waits only for the end of its output.
            await process.WaitForExitAsync(output.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Something the process started still holds its output open.
        }
    }

    // The exit as WaitForExitAsync detects it, without its wait for the end of the output.
    private static async Task WaitForTheExitAloneAsync(Process process, CancellationToken cancellationToken)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onExited = (_, _) => exited.TrySetResult();
        process.Exited += onExited;
        try
        {
            try
            {
                process.EnableRaisingEvents = true;
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // It exited before the event could be enabled, which WaitForExitAsync also allows for.
            }

            if (!process.HasExited)
            {
                await exited.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            process.Exited -= onExited;
        }
    }
}
