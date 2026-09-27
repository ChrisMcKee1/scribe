using Scribe.Core.Libraries;
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
    public sealed record Result(UsageAnalyzer.Snapshot Snapshot, bool PeriodCapped)
    {
        /// <summary>
        /// What the report's shareable library labels may be sent under (review finding A6): the scope of the vocabulary
        /// snapshot the report was built from, restricted to the libraries whose labels it marks shareable, each with the
        /// content its permission covered (A12). The usage insight is handed over only through
        /// <see cref="ILibraryVocabularySource.TryHandOff"/> with this scope, at the first attempt and at every retry, so
        /// a label is never sent once its library's permission narrowed or its content changed, even when the new content
        /// is permitted. <see cref="AiVocabularyScope.None"/> when no library label is shareable, so a report whose labels
        /// all come from the dictionary is never held up.
        /// </summary>
        public AiVocabularyScope LibraryScope { get; init; } = AiVocabularyScope.None;
    }

    /// <summary>
    /// Reads retained history, the enabled dictionary and the library vocabulary dictation uses, then computes the
    /// snapshot for the last <paramref name="periodDays"/> days, or for all retained history when null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cancellation is checked between the steps rather than inside them: Microsoft.Data.Sqlite runs
    /// its calls synchronously and <see cref="UsageAnalyzer.Compute"/> takes no token, so neither can
    /// be interrupted once started. Coverage uses the saved dictionary and the library vocabulary dictation uses: not
    /// unsaved edits in an open Settings window, and not a fresh read of a stored document that may have turned
    /// unreadable.
    /// </para>
    /// <para>
    /// With a library service that is an <see cref="ILibraryVocabularySource"/>, the library entries and which of their
    /// labels may be shared both come from one <see cref="ILibraryVocabularySource.Current"/> snapshot, and
    /// <paramref name="enabledLibraryIds"/> (after W1b only the downgrade-safe projection of the enabled libraries) is not
    /// used: a library label is shareable only when its entries are in <see cref="LibraryVocabulary.AiEntries"/>. Any
    /// other service is read through the ids, as release 0.4.4 did, and no library label is shareable, because nothing
    /// then says the user lets AI cleanup have it (fail closed). Dictionary labels are shareable exactly as before: when
    /// every replacement behind a label is vocabulary (<see cref="Cleanup.CleanupPrompt.IsVocabularyReplacement"/>).
    /// </para>
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

        return libraries is ILibraryVocabularySource source
            ? Build(history.GetRecent, dictionary.GetEnabled, () => source.Current, periodDays, nowUtc, cancellationToken)
            : Build(
                history.GetRecent,
                dictionary.GetEnabled,
                () => libraries.GetEnabledLibraryEntries(enabledLibraryIds),
                periodDays,
                nowUtc,
                cancellationToken);
    }

    // Release 0.4.4's path, for a service that is not a vocabulary source: every library entry counts toward coverage,
    // and none of their labels is shareable.
    internal static Result Build(
        Func<int, IReadOnlyList<HistoryEntry>> readRecentHistory,
        Func<IReadOnlyList<DictionaryEntry>> readEnabledDictionary,
        Func<IReadOnlyList<DictionaryEntry>> readEnabledLibraryEntries,
        int? periodDays,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken) =>
        Build(
            readRecentHistory,
            readEnabledDictionary,
            () => (readEnabledLibraryEntries(), (LibraryVocabulary?)null),
            periodDays,
            nowUtc,
            cancellationToken);

    // The vocabulary source's path: one snapshot gives the library entries and the shareable subset.
    internal static Result Build(
        Func<int, IReadOnlyList<HistoryEntry>> readRecentHistory,
        Func<IReadOnlyList<DictionaryEntry>> readEnabledDictionary,
        Func<LibraryVocabulary> readVocabulary,
        int? periodDays,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken) =>
        Build(
            readRecentHistory,
            readEnabledDictionary,
            () =>
            {
                var vocabulary = readVocabulary();
                return (vocabulary.Entries, (LibraryVocabulary?)vocabulary);
            },
            periodDays,
            nowUtc,
            cancellationToken);

    private static Result Build(
        Func<int, IReadOnlyList<HistoryEntry>> readRecentHistory,
        Func<IReadOnlyList<DictionaryEntry>> readEnabledDictionary,
        Func<(IReadOnlyList<DictionaryEntry> Entries, LibraryVocabulary? Vocabulary)> readLibraries,
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

        var dictionaryEntries = readEnabledDictionary();
        var knownTerms = new List<DictionaryEntry>(dictionaryEntries);
        cancellationToken.ThrowIfCancellationRequested();
        var (libraryEntries, vocabulary) = readLibraries();
        knownTerms.AddRange(libraryEntries);
        cancellationToken.ThrowIfCancellationRequested();

        var shareable = new ShareableTerms(dictionaryEntries, vocabulary);
        var snapshot = UsageAnalyzer.Compute(entries, knownTerms, since, nowUtc, shareable.MayShare);
        cancellationToken.ThrowIfCancellationRequested();

        var periodCapped = recent.Count > HistoryLimit &&
            (periodDays is null || recent[HistoryLimit].TimestampUtc >= since);
        return new Result(snapshot, periodCapped) { LibraryScope = shareable.ScopeFor(snapshot) };
    }

    /// <summary>Which known terms may make their label shareable, and the scope the report's shareable labels bind to.</summary>
    private sealed class ShareableTerms
    {
        private readonly HashSet<DictionaryEntry> _dictionary;
        private readonly HashSet<DictionaryEntry> _permitted = new(ReferenceEqualityComparer.Instance);
        private readonly LibraryVocabulary? _vocabulary;
        private readonly IReadOnlyDictionary<DictionaryEntry, string>? _origins;

        public ShareableTerms(IReadOnlyList<DictionaryEntry> dictionary, LibraryVocabulary? vocabulary)
        {
            _dictionary = new HashSet<DictionaryEntry>(dictionary, ReferenceEqualityComparer.Instance);
            _vocabulary = vocabulary;
            if (vocabulary is null)
            {
                return;
            }

            if (LibraryVocabularyOrigins.TryGet(vocabulary, out var origins))
            {
                _origins = origins;
            }

            // A library entry may share its label only when AI cleanup may carry it and, where the vocabulary says which
            // library supplied it, only while that library is in the vocabulary's own scope.
            foreach (var entry in vocabulary.AiEntries)
            {
                if (_origins is null ||
                    (_origins.TryGetValue(entry, out var id) && vocabulary.AiScope.PermittedContent.ContainsKey(id)))
                {
                    _permitted.Add(entry);
                }
            }
        }

        /// <summary>A dictionary entry may share (release 0.4.4's rule then decides); a library entry only when permitted.</summary>
        public bool MayShare(DictionaryEntry entry) => _dictionary.Contains(entry) || _permitted.Contains(entry);

        /// <summary>
        /// The vocabulary's scope, restricted to the libraries behind the labels <paramref name="snapshot"/> marks
        /// shareable. A label is the trimmed written form the known terms behind it share, compared without case, the way
        /// the analyzer groups them. Without the vocabulary's origins, the whole scope, which only ever refuses more.
        /// </summary>
        public AiVocabularyScope ScopeFor(UsageAnalyzer.Snapshot snapshot)
        {
            if (_vocabulary is null || _permitted.Count == 0)
            {
                return AiVocabularyScope.None;
            }

            var labels = new HashSet<string>(
                snapshot.Terms.Where(term => term is { Covered: true, Shareable: true }).Select(term => term.Text),
                StringComparer.OrdinalIgnoreCase);
            var behind = _permitted
                .Where(entry => entry.Enabled && !string.IsNullOrWhiteSpace(entry.Replacement) &&
                    labels.Contains(entry.Replacement.Trim()))
                .ToList();
            if (behind.Count == 0)
            {
                return AiVocabularyScope.None;
            }

            if (_origins is null)
            {
                return _vocabulary.AiScope;
            }

            var ids = new HashSet<string>(behind.Select(entry => _origins[entry]), StringComparer.OrdinalIgnoreCase);
            return new AiVocabularyScope(
                _vocabulary.AiScope.Generation,
                _vocabulary.AiScope.PermittedContent.Where(pair => ids.Contains(pair.Key)));
        }
    }
}
