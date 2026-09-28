using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <summary>
/// Stores AI cleanup runtime failures so Settings can surface them, with explicit clear and a
/// rolling prune. Distinct from the general history log; these are diagnostic, user-clearable, and
/// short-lived by design.
/// </summary>
public interface ICleanupFailureLog
{
    /// <summary>Records a failure and returns it with its assigned id.</summary>
    CleanupFailure Add(CleanupFailure failure);

    /// <summary>Most recent failures first, capped at <paramref name="limit"/>.</summary>
    IReadOnlyList<CleanupFailure> GetRecent(int limit = 50);

    /// <summary>
    /// The <paramref name="shown"/> most recent failures and how many there are, counted up to <paramref name="countCap"/>,
    /// from one read: exactly <c>GetRecent(countCap).Take(shown)</c> and <c>GetRecent(countCap).Count</c>, which is what this
    /// default gives. The database's log reads only the rows it shows (BoundedDiagnosticsReads).
    /// </summary>
    CleanupFailurePage GetRecentPage(int shown, int countCap)
    {
        var recent = GetRecent(countCap);
        return new CleanupFailurePage([.. recent.Take(Math.Max(0, shown))], recent.Count);
    }
    /// <summary>Total number of recorded failures.</summary>
    int Count();

    /// <summary>Removes all failures; returns the number of rows deleted.</summary>
    int Clear();

    /// <summary>Removes failures older than <paramref name="cutoffUtc"/>; returns rows deleted.</summary>
    int PruneOlderThan(DateTimeOffset cutoffUtc);
}

/// <summary>A page of the most recent failures and how many there are, up to a cap.</summary>
public sealed record CleanupFailurePage(IReadOnlyList<CleanupFailure> Recent, int Count);
