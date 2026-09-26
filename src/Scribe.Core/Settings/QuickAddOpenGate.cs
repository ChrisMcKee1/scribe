namespace Scribe.Core.Settings;

public sealed class QuickAddOpenGate
{
    private readonly object _gate = new();
    private Task? _openTask;
    private long _generation;

    public Task RunAsync(Func<Task> openAsync, Func<Task>? coalescedAsync = null)
    {
        ArgumentNullException.ThrowIfNull(openAsync);

        Task task;
        lock (_gate)
        {
            if (_openTask is { IsCompleted: false } current)
            {
                return AwaitExistingAsync(current, coalescedAsync);
            }

            var generation = ++_generation;
            task = RunAndClearAsync(openAsync, generation);
            _openTask = task;
        }

        return task;
    }

    private async Task RunAndClearAsync(Func<Task> openAsync, long generation)
    {
        try
        {
            await openAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (_generation == generation)
                {
                    _openTask = null;
                }
            }
        }
    }

    private static async Task AwaitExistingAsync(Task current, Func<Task>? coalescedAsync)
    {
        await current.ConfigureAwait(false);
        if (coalescedAsync is not null)
        {
            await coalescedAsync().ConfigureAwait(false);
        }
    }
}