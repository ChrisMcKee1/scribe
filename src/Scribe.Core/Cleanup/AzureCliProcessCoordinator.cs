namespace Scribe.Core.Cleanup;

/// <summary>
/// Coordinates every Azure CLI process that can read or update the shared CLI account cache.
/// </summary>
public static class AzureCliProcessCoordinator
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static T Run<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        Gate.Wait(cancellationToken);
        try
        {
            return action();
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static async ValueTask<T> RunAsync<T>(
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }
}
