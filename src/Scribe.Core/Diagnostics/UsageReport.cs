using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Diagnostics;

/// <summary>Reads what the usage page needs and computes one snapshot for a period.</summary>
public static class UsageReport
{
    /// <summary>Newest retained dictations read for one snapshot.</summary>
    public const int HistoryLimit = 5000;

    /// <param name="Snapshot">Metrics for the period.</param>
    /// <param name="PeriodCapped">
    /// True when the period holds more dictations than <see cref="HistoryLimit"/>, so the page must
    /// say the numbers cover only the newest ones.
    /// </param>
    public sealed record Result(UsageAnalyzer.Snapshot Snapshot, bool PeriodCapped);

    /// <summary>
    /// Reads retained history, the enabled dictionary, and the libraries <paramref name="enabledLibraryIds"/> names,
    /// then computes the snapshot for the last <paramref name="periodDays"/> days, or for all retained history when
    /// null.
    /// </summary>
    /// <remarks>
    /// Cancellation is checked between the steps rather than inside them: Microsoft.Data.Sqlite runs
    /// its calls synchronously and <see cref="UsageAnalyzer.Compute"/> takes no token, so neither can
    /// be interrupted once started. Coverage uses the saved dictionary and the library selection of the settings
    /// dictation runs on, the same vocabulary dictation uses: not unsaved edits in an open Settings window, and not a
    /// fresh read of a stored document that may have turned unreadable.
    /// </remarks>
    public static Result Build(
        IHistoryRepository history,
        IDictionaryRepository dictionary,
        IDictionaryLibraryService libraries,
        IReadOnlyCollection<string> enabledLibraryIds,
        int? periodDays,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(enabledLibraryIds);

        return Build(
            history.GetRecent,
            dictionary.GetEnabled,
            () => libraries.GetEnabledLibraryEntries(enabledLibraryIds),
            periodDays,
            nowUtc,
            cancellationToken);
    }

    internal static Result Build(
        Func<int, IReadOnlyList<HistoryEntry>> readRecentHistory,
        Func<IReadOnlyList<DictionaryEntry>> readEnabledDictionary,
        Func<IReadOnlyList<DictionaryEntry>> readEnabledLibraryEntries,
        int? periodDays,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // One row past the cap reveals whether the period holds more than was read.
        var recent = readRecentHistory(HistoryLimit + 1);
        cancellationToken.ThrowIfCancellationRequested();

        var entries = recent.Take(HistoryLimit).ToList();
        var since = periodDays is { } days
            ? nowUtc.AddDays(-(days - 1))
            : entries.Count == 0
                ? nowUtc
                : entries.Min(entry => entry.TimestampUtc);

        var knownTerms = new List<DictionaryEntry>(readEnabledDictionary());
        cancellationToken.ThrowIfCancellationRequested();
        knownTerms.AddRange(readEnabledLibraryEntries());
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = UsageAnalyzer.Compute(entries, knownTerms, since, nowUtc);
        cancellationToken.ThrowIfCancellationRequested();

        var periodCapped = recent.Count > HistoryLimit &&
            (periodDays is null || recent[HistoryLimit].TimestampUtc >= since);
        return new Result(snapshot, periodCapped);
    }
}
