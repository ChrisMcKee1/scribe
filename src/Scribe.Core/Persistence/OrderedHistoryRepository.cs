using Microsoft.Extensions.Logging;
using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <summary>
/// The history repository as the rest of the app sees it: reads and maintenance first wait, with a bound, for the
/// history writes accepted before them, then run against the concrete repository.
/// </summary>
/// <remarks>
/// <para>
/// Dictation history is committed in the background by <see cref="IHistoryWriter"/>, so without this barrier a Clear
/// clicked right after a dictation could run before that dictation's row landed and leave it behind, and the History
/// page could miss the dictation that was just made. Waiting here gives the ordering the user expects: Clear, Delete
/// and PruneOlderThan act on everything accepted before the call, and GetRecent reads its own writes.
/// </para>
/// <para>
/// The waits are bounded so that a stuck write can never freeze a caller, and the UI thread in particular. Reads wait
/// at most <see cref="ReadWait"/>, because some are still made on the dispatcher; past it they return what is committed
/// and the newest entry appears on the next refresh. Maintenance calls wait at most <see cref="MaintenanceWait"/>,
/// which covers SQLite's own busy timeout; they are made off the UI thread today, and past the bound they go ahead
/// (a write still committing may then survive a Clear). Both cases are logged. Calls that address one committed row
/// by id (ratings, audio) and orphan audio blobs have nothing to order against, so they pass straight through.
/// </para>
/// </remarks>
public sealed class OrderedHistoryRepository : IHistoryRepository
{
    internal static readonly TimeSpan ReadWait = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaintenanceWait = TimeSpan.FromSeconds(10);

    private readonly IHistoryRepository _inner;
    private readonly IHistoryWriter _writer;
    private readonly ILogger<OrderedHistoryRepository> _logger;
    private readonly TimeSpan _readWait;
    private readonly TimeSpan _maintenanceWait;

    public OrderedHistoryRepository(
        IHistoryRepository inner, IHistoryWriter writer, ILogger<OrderedHistoryRepository> logger)
        : this(inner, writer, logger, ReadWait, MaintenanceWait)
    {
    }

    /// <summary>Test seam: the production bounds are real time, which a deterministic test must not depend on.</summary>
    internal OrderedHistoryRepository(
        IHistoryRepository inner,
        IHistoryWriter writer,
        ILogger<OrderedHistoryRepository> logger,
        TimeSpan readWait,
        TimeSpan maintenanceWait)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(logger);
        _inner = inner;
        _writer = writer;
        _logger = logger;
        _readWait = readWait;
        _maintenanceWait = maintenanceWait;
    }

    public HistoryEntry Add(HistoryEntry entry)
    {
        AwaitAcceptedWrites(_maintenanceWait, nameof(Add));
        return _inner.Add(entry);
    }

    public HistoryEntry Add(HistoryEntry entry, CapturedAudio? audio)
    {
        AwaitAcceptedWrites(_maintenanceWait, nameof(Add));
        return _inner.Add(entry, audio);
    }

    public long AddAudioBlob(CapturedAudio audio) => _inner.AddAudioBlob(audio);

    public IReadOnlyList<HistoryEntry> GetRecent(int limit = 100)
    {
        AwaitAcceptedWrites(_readWait, nameof(GetRecent));
        return _inner.GetRecent(limit);
    }

    public CapturedAudio? GetAudio(long blobId) => _inner.GetAudio(blobId);

    public void SetAiRating(long id, AiRating rating) => _inner.SetAiRating(id, rating);

    public void Delete(long id)
    {
        AwaitAcceptedWrites(_maintenanceWait, nameof(Delete));
        _inner.Delete(id);
    }

    public void Clear()
    {
        AwaitAcceptedWrites(_maintenanceWait, nameof(Clear));
        _inner.Clear();
    }

    public int PruneOlderThan(DateTimeOffset cutoffUtc)
    {
        AwaitAcceptedWrites(_maintenanceWait, nameof(PruneOlderThan));
        return _inner.PruneOlderThan(cutoffUtc);
    }

    private void AwaitAcceptedWrites(TimeSpan bound, string operation)
    {
        if (_writer.WaitForAcceptedWrites(bound))
        {
            return;
        }

        try
        {
            _logger.LogWarning(
                "History {Operation} went ahead after waiting {Ms} ms for earlier dictation history that was still " +
                "being written.",
                operation, (long)bound.TotalMilliseconds);
        }
        catch
        {
            // A failed diagnostic must never fail the history call it describes.
        }
    }
}
