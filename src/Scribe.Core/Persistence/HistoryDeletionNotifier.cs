using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using System.Collections.Concurrent;

namespace Scribe.Core.Persistence;

public enum HistoryDeletionKind
{
    Entry,
    Clear,
    OlderThan,
}

public sealed record HistoryDeletion(HistoryDeletionKind Kind, HistoryEntry? Entry = null, DateTimeOffset? CutoffUtc = null);

public sealed class HistoryDeletionNotifier
{
    private readonly ILogger<HistoryDeletionNotifier> _log;
    private readonly ConcurrentQueue<HistoryDeletion> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private long _revision;

    public HistoryDeletionNotifier()
        : this(NullLogger<HistoryDeletionNotifier>.Instance)
    {
    }

    public HistoryDeletionNotifier(ILogger<HistoryDeletionNotifier> log)
    {
        _log = log;
        _ = Task.Run(DrainAsync);
    }

    public event Action<HistoryDeletion>? Deleted;

    public long Revision => Interlocked.Read(ref _revision);

    internal void Notify(HistoryDeletion deletion)
    {
        Interlocked.Increment(ref _revision);
        _queue.Enqueue(deletion);
        _signal.Release();
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            await _signal.WaitAsync().ConfigureAwait(false);
            while (_queue.TryDequeue(out var deletion))
            {
                Deliver(deletion);
            }
        }
    }

    private void Deliver(HistoryDeletion deletion)
    {
        if (Deleted is not { } handlers)
        {
            return;
        }

        foreach (Action<HistoryDeletion> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(deletion);
            }
            catch (Exception ex)
            {
                TryLog(ex);
            }
        }
    }

    private void TryLog(Exception ex)
    {
        try
        {
            _log.LogWarning("A history deletion subscriber failed ({Failure}).", FailureShape.Describe(ex));
        }
        catch
        {
            // Notifications must never fail because diagnostics did.
        }
    }
}
