using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;

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

    public HistoryDeletionNotifier()
        : this(NullLogger<HistoryDeletionNotifier>.Instance)
    {
    }

    public HistoryDeletionNotifier(ILogger<HistoryDeletionNotifier> log) => _log = log;

    public event Action<HistoryDeletion>? Deleted;

    internal void Notify(HistoryDeletion deletion)
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
                _log.LogWarning("A history deletion subscriber failed ({Failure}).", FailureShape.Describe(ex));
            }
        }
    }
}
