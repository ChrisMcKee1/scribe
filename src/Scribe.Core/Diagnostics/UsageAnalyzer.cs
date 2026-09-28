using System.Globalization;
using System.Text.RegularExpressions;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Diagnostics;

/// <summary>Computes descriptive, local-only usage metrics from retained dictation history.</summary>
public static partial class UsageAnalyzer
{
    public sealed record AppUsage(string Name, int Dictations, int Words);

    public sealed record TrendPoint(DateOnly Start, int Dictations, int Words);

    public sealed record TermUsage(string Text, int Dictations, int Occurrences, bool Covered)
    {
        /// <summary>
        /// Whether <see cref="Text"/> may leave this PC as a term label in the opt-in AI usage insight
        /// (<see cref="UsageInsight.BuildSummary"/>). True only for a covered term whose every
        /// replacement is vocabulary as the user wrote it, before it was trimmed into this label
        /// (<see cref="CleanupPrompt.IsVocabularyReplacement"/>, the rule the AI cleanup glossary applies), and, in a
        /// usage report, only when every library term behind it is one AI cleanup may carry
        /// (<see cref="Libraries.LibraryVocabulary.AiEntries"/>, review finding A6). False by default, so a term built
        /// anywhere else shares nothing. The local Usage page shows every term either way.
        /// </summary>
        public bool Shareable { get; init; }
    }

    /// <summary>Bucket size of the <see cref="Snapshot.Trend"/> points.</summary>
    public enum TrendGranularity
    {
        Daily,
        Weekly,
    }

    // Granularity defaults to Daily so existing Snapshot constructions stay source-compatible;
    // Compute always overwrites it with the trend builder's actual bucket decision. Consumers
    // must read it instead of guessing from Trend.Count (a 90-day period yields ~13 weekly
    // points, which a count heuristic mislabels as days).
    public sealed record Snapshot(
        int Dictations,
        int Words,
        int ActiveDays,
        TimeSpan Speech,
        double AverageWords,
        IReadOnlyList<AppUsage> TopApps,
        IReadOnlyList<TrendPoint> Trend,
        IReadOnlyList<TermUsage> Terms,
        TrendGranularity Granularity = TrendGranularity.Daily,
        TimeSpan LongestDictation = default);

    /// <summary>
    /// Computes one internally consistent snapshot. Every metric uses entries on or after
    /// <paramref name="sinceUtc"/>; callers own the newest-first read cap and its disclosure.
    /// </summary>
    public static Snapshot Compute(
        IEnumerable<HistoryEntry> entries,
        IEnumerable<DictionaryEntry> knownTerms,
        DateTimeOffset sinceUtc,
        DateTimeOffset nowUtc,
        TimeZoneInfo? timeZone = null,
        int maxApps = 8,
        int maxTerms = 16) =>
        Compute(entries, knownTerms, sinceUtc, nowUtc, mayShare: null, timeZone, maxApps, maxTerms);

    /// <summary>
    /// <see cref="Compute(IEnumerable{HistoryEntry}, IEnumerable{DictionaryEntry}, DateTimeOffset, DateTimeOffset, TimeZoneInfo, int, int)"/>
    /// with a say in which known terms may make their label shareable: a label is shareable only when every term behind it
    /// is vocabulary and <paramref name="mayShare"/> allows it (the usage report allows dictionary entries, and library
    /// entries only when AI cleanup may carry them, review finding A6). Null allows every term.
    /// </summary>
    /// <param name="perfFlags">
    /// Which of 0.5.1's counting changes run (<see cref="UsageCounting.From"/>). Null or none on is the counting 0.5.0
    /// shipped; every combination returns the same snapshot.
    /// </param>
    internal static Snapshot Compute(
        IEnumerable<HistoryEntry> entries,
        IEnumerable<DictionaryEntry> knownTerms,
        DateTimeOffset sinceUtc,
        DateTimeOffset nowUtc,
        Func<DictionaryEntry, bool>? mayShare,
        TimeZoneInfo? timeZone = null,
        int maxApps = 8,
        int maxTerms = 16,
        PerfFlags? perfFlags = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(knownTerms);

        var zone = timeZone ?? TimeZoneInfo.Local;
        var selected = entries
            .Where(entry => entry.TimestampUtc >= sinceUtc && entry.TimestampUtc <= nowUtc)
            .ToList();
        var wordCounts = selected.ToDictionary(entry => entry.Id, entry => CountWords(entry.Text));
        var words = wordCounts.Values.Sum();
        var activeDays = selected
            .Select(entry => LocalDate(entry.TimestampUtc, zone))
            .Distinct()
            .Count();

        var apps = selected
            .GroupBy(
                entry => string.IsNullOrWhiteSpace(entry.TargetApp) ? "Unknown app" : AppDisplayName.For(entry.TargetApp.Trim()),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new AppUsage(
                group.Key,
                group.Count(),
                group.Sum(entry => wordCounts[entry.Id])))
            .OrderByDescending(app => app.Dictations)
            .ThenBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, maxApps))
            .ToList();

        var (trend, granularity) = BuildTrend(selected, wordCounts, sinceUtc, nowUtc, zone);
        return new Snapshot(
            Dictations: selected.Count,
            Words: words,
            ActiveDays: activeDays,
            Speech: TimeSpan.FromMilliseconds(selected.Sum(entry => (long)Math.Max(0, entry.AudioMilliseconds))),
            AverageWords: selected.Count == 0 ? 0 : words / (double)selected.Count,
            TopApps: apps,
            Trend: trend,
            Terms: ExtractTerms(selected, knownTerms, maxTerms, mayShare, UsageCounting.From(perfFlags), out _),
            Granularity: granularity,
            LongestDictation: TimeSpan.FromMilliseconds(selected.Count == 0 ? 0 : selected.Max(entry => Math.Max(0, entry.AudioMilliseconds))));
    }

    /// <summary>Counts Unicode letter/number words without assuming a particular language.</summary>
    public static int CountWords(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : Word().Count(text);

    private static (IReadOnlyList<TrendPoint> Points, TrendGranularity Granularity) BuildTrend(
        IReadOnlyList<HistoryEntry> entries,
        IReadOnlyDictionary<long, int> wordCounts,
        DateTimeOffset sinceUtc,
        DateTimeOffset nowUtc,
        TimeZoneInfo zone)
    {
        var end = LocalDate(nowUtc, zone);
        var requestedStart = LocalDate(sinceUtc, zone);
        var firstEntry = entries.Count == 0
            ? end
            : entries.Min(entry => LocalDate(entry.TimestampUtc, zone));
        var start = requestedStart.Year <= 1 ? firstEntry : requestedStart;
        if (start > end)
        {
            start = end;
        }

        var granularity = end.DayNumber - start.DayNumber > 31 ? TrendGranularity.Weekly : TrendGranularity.Daily;
        var useWeeks = granularity == TrendGranularity.Weekly;
        if (useWeeks)
        {
            start = StartOfWeek(start);
        }

        var grouped = entries
            .GroupBy(entry =>
            {
                var date = LocalDate(entry.TimestampUtc, zone);
                return useWeeks ? StartOfWeek(date) : date;
            })
            .ToDictionary(
                group => group.Key,
                group => (Dictations: group.Count(), Words: group.Sum(entry => wordCounts[entry.Id])));

        var points = new List<TrendPoint>();
        for (var cursor = start; cursor <= end; cursor = cursor.AddDays(useWeeks ? 7 : 1))
        {
            var value = grouped.GetValueOrDefault(cursor);
            points.Add(new TrendPoint(cursor, value.Dictations, value.Words));
        }

        return (points, granularity);
    }

    private const string TrailingPunctuation = ".,:;!?";

    // The options of every phrase regex, the dictionary matcher's own (OrdinalPrefilter's guard is read with them).
    private const RegexOptions PhraseOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// Which of 0.5.1's usage counting changes run, each off by default (off is 0.5.0's counting). Read once per snapshot.
    /// </summary>
    /// <param name="SparseAggregation">
    /// <see cref="PerfFlags.SparseUsageAggregation"/>: a dictation's counts reach its terms through a form-to-term index,
    /// instead of every form of every known term being looked up in every dictation.
    /// </param>
    /// <param name="TermIndex">
    /// <see cref="PerfFlags.UsageTermIndex"/>: an ASCII phrase's regex runs only on dictations where an ordinal ignore-case
    /// search finds the phrase (<see cref="OrdinalPrefilter"/>), and is built only when it first has to run.
    /// </param>
    internal readonly record struct UsageCounting(bool SparseAggregation, bool TermIndex)
    {
        public static UsageCounting From(PerfFlags? flags) =>
            flags is null
                ? default
                : new(flags.IsOn(PerfFlags.SparseUsageAggregation), flags.IsOn(PerfFlags.UsageTermIndex));
    }

    /// <summary>
    /// The term list <see cref="Compute(IEnumerable{HistoryEntry}, IEnumerable{DictionaryEntry}, DateTimeOffset, DateTimeOffset, Func{DictionaryEntry, bool}, TimeZoneInfo, int, int, PerfFlags)"/>
    /// returns for entries already in its period, with how many of the phrase regexes it built (all of them unless
    /// <see cref="PerfFlags.UsageTermIndex"/> is on).
    /// </summary>
    internal static IReadOnlyList<TermUsage> ExtractTermsForTesting(
        IReadOnlyList<HistoryEntry> entries,
        IEnumerable<DictionaryEntry> knownTerms,
        int maxTerms,
        Func<DictionaryEntry, bool>? mayShare,
        PerfFlags? perfFlags,
        out int phraseRegexesBuilt) =>
        ExtractTerms(entries, knownTerms, maxTerms, mayShare, UsageCounting.From(perfFlags), out phraseRegexesBuilt);

    private static IReadOnlyList<TermUsage> ExtractTerms(
        IReadOnlyList<HistoryEntry> entries,
        IEnumerable<DictionaryEntry> knownTerms,
        int maxTerms,
        Func<DictionaryEntry, bool>? mayShare,
        UsageCounting counting,
        out int phraseRegexesBuilt)
    {
        var known = knownTerms
            .Where(entry => entry.Enabled && !string.IsNullOrWhiteSpace(entry.Replacement))
            .GroupBy(entry => entry.Replacement.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Canonical = group.First().Replacement.Trim(),
                // Judged on every replacement behind the label as written: the trim above can hide a
                // trailing line break, or the padding that takes one past the cap, and either one
                // marks a template (a signature, a footer) rather than a term. And every term behind it
                // must be one the caller lets leave the PC: a label also counts the spoken forms behind it.
                Shareable = group.All(entry =>
                    CleanupPrompt.IsVocabularyReplacement(entry.Replacement) && (mayShare?.Invoke(entry) ?? true)),
                Forms = group
                    .SelectMany(entry => new[] { entry.Pattern.Trim(), entry.Replacement.Trim() })
                    .Where(form => form.Length >= 2)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            })
            // A 1-char pattern with a 1-char replacement leaves no usable forms; skipping the
            // group keeps the max-over-forms below from throwing on an empty sequence.
            .Where(term => term.Forms.Count > 0)
            .ToList();

        // Forms represented by Token are counted through one tokenization pass and hash
        // lookups. Only forms Token cannot represent, normally multi-token phrases, retain
        // regex matching.
        var singleTokenForms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var phraseMatchers = new List<PhraseMatcher>();
        foreach (var form in known.SelectMany(term => term.Forms).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsSingleTokenForm(form))
            {
                singleTokenForms.Add(form);
            }
            else
            {
                phraseMatchers.Add(new PhraseMatcher(form, deferred: counting.TermIndex));
            }
        }

        var coveredForms = new HashSet<string>(
            known.SelectMany(term => term.Forms),
            StringComparer.OrdinalIgnoreCase);
        var singleTokenLookup = singleTokenForms.GetAlternateLookup<ReadOnlySpan<char>>();
        var coveredLookup = coveredForms.GetAlternateLookup<ReadOnlySpan<char>>();
        var termDictations = new int[known.Count];
        var termOccurrences = new int[known.Count];
        var novelForms = new Dictionary<string, (string Surface, int Dictations, int Occurrences)>(
            StringComparer.OrdinalIgnoreCase);
        var novelLookup = novelForms.GetAlternateLookup<ReadOnlySpan<char>>();
        var formCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastTokenMatchEnds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenNovelForms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Each form's owners: a form belongs to every term that lists it, each term listing it once (its forms are distinct).
        var sparse = counting.SparseAggregation ? new SparseTermCounts(known.ConvertAll(term => term.Forms)) : null;

        foreach (var entry in entries)
        {
            formCounts.Clear();
            lastTokenMatchEnds.Clear();
            seenNovelForms.Clear();
            var text = entry.Text;

            // Token().Matches(text) threw for a null transcript, naming its parameter; EnumerateMatches takes a span and would not.
            ArgumentNullException.ThrowIfNull(text, "input");
            foreach (var match in Token().EnumerateMatches(text))
            {
                var tokenText = text.AsSpan(match.Index, match.Length);
                CountSingleTokenForms(tokenText, match.Index, singleTokenLookup, formCounts, lastTokenMatchEnds);

                var trimmed = tokenText.TrimEnd(TrailingPunctuation);
                if (trimmed.Length < 2 ||
                    coveredLookup.Contains(trimmed) ||
                    !DictionarySuggestionMiner.IsCandidate(trimmed))
                {
                    continue;
                }

                // A form seen before is counted under the spelling it was first stored with, as the indexer below keeps it.
                var token = novelLookup.TryGetValue(trimmed, out var stored, out _) ? stored : trimmed.ToString();
                var current = novelForms.GetValueOrDefault(token);
                novelForms[token] = (
                    string.IsNullOrEmpty(current.Surface) ? token : current.Surface,
                    current.Dictations + (seenNovelForms.Add(token) ? 1 : 0),
                    current.Occurrences + 1);
            }

            // Once per dictation: whether an ordinal search may rule an ASCII phrase out of it (OrdinalPrefilter).
            var prefilter = counting.TermIndex && OrdinalPrefilter.IsSound(text);
            foreach (var matcher in phraseMatchers)
            {
                if (prefilter && matcher.Ascii && !OrdinalPrefilter.MayMatch(text, matcher.Text))
                {
                    continue;
                }

                var count = matcher.Pattern.Count(text);
                if (count > 0)
                {
                    formCounts[matcher.Text] = count;
                }
            }

            if (sparse is not null)
            {
                sparse.Add(formCounts, termDictations, termOccurrences);
                continue;
            }

            for (var i = 0; i < known.Count; i++)
            {
                // Max across forms, not the sum: pattern and replacement describe the same
                // spoken term, so summing would double count one utterance.
                var count = 0;
                foreach (var form in known[i].Forms)
                {
                    count = Math.Max(count, formCounts.GetValueOrDefault(form));
                }

                if (count > 0)
                {
                    termDictations[i]++;
                    termOccurrences[i] += count;
                }
            }
        }

        phraseRegexesBuilt = 0;
        foreach (var matcher in phraseMatchers)
        {
            if (matcher.IsBuilt)
            {
                phraseRegexesBuilt++;
            }
        }

        var results = new List<TermUsage>();
        for (var i = 0; i < known.Count; i++)
        {
            if (termDictations[i] > 0)
            {
                results.Add(new TermUsage(known[i].Canonical, termDictations[i], termOccurrences[i], Covered: true)
                {
                    Shareable = known[i].Shareable,
                });
            }
        }

        results.AddRange(novelForms.Values
            .Where(value => value.Dictations >= 2)
            .Select(value => new TermUsage(value.Surface, value.Dictations, value.Occurrences, Covered: false)));

        return results
            .OrderByDescending(term => term.Dictations)
            .ThenByDescending(term => term.Occurrences)
            .ThenBy(term => term.Text, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, maxTerms))
            .ToList();
    }

    private static bool IsSingleTokenForm(string form)
    {
        var matches = Token().EnumerateMatches(form);
        return matches.MoveNext() && matches.Current.Index == 0 && matches.Current.Length == form.Length;
    }

    private static void CountSingleTokenForms(
        ReadOnlySpan<char> token,
        int tokenIndex,
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> singleTokenForms,
        Dictionary<string, int> formCounts,
        Dictionary<string, int> lastMatchEnds)
    {
        for (var start = 0; start < token.Length; start++)
        {
            if (start > 0 && char.IsLetterOrDigit(token[start - 1]))
            {
                continue;
            }

            for (var end = start + 2; end <= token.Length; end++)
            {
                if (end < token.Length && char.IsLetterOrDigit(token[end]))
                {
                    continue;
                }

                if (!singleTokenForms.TryGetValue(token[start..end], out var form))
                {
                    continue;
                }

                var absoluteStart = tokenIndex + start;
                if (lastMatchEnds.TryGetValue(form, out var lastEnd) && absoluteStart < lastEnd)
                {
                    continue;
                }

                formCounts[form] = formCounts.GetValueOrDefault(form) + 1;
                lastMatchEnds[form] = tokenIndex + end;
            }
        }
    }

    // Interpreted, not compiled: the phrases are built again on every Usage load, and emitting and jitting a thousand
    // compiled regexes per load (every word pack's phrases) cost more than running them interpreted over the history saves.
    // Same pattern and options, so the same matches (UsageAnalyzerTests.Cheap_path_matches_legacy_terms_for_seeded_histories).
    private static Regex CreatePhraseRegex(string phrase) => new(
        $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(phrase)}(?![\p{{L}}\p{{N}}])",
        PhraseOptions);

    private static DateOnly LocalDate(DateTimeOffset timestamp, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, zone).DateTime);

    private static DateOnly StartOfWeek(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-offset);
    }

    // A phrase and its regex: built at once (0.5.0), or, under UsageTermIndex, the first time a dictation needs it. An
    // escaped literal always compiles, so building it later changes no outcome.
    private sealed class PhraseMatcher
    {
        private Regex? _pattern;

        public PhraseMatcher(string text, bool deferred)
        {
            Text = text;
            if (deferred)
            {
                Ascii = System.Text.Ascii.IsValid(text);
            }
            else
            {
                _pattern = CreatePhraseRegex(text);
            }
        }

        public string Text { get; }

        // Whether OrdinalPrefilter may rule the phrase out; only read under UsageTermIndex.
        public bool Ascii { get; }

        public Regex Pattern => _pattern ??= CreatePhraseRegex(Text);

        public bool IsBuilt => _pattern is not null;
    }

    /// <summary>The options every phrase regex is built with, for the test that holds them to the matcher's.</summary>
    internal static RegexOptions PhraseRegexOptions => PhraseOptions;

    // SparseUsageAggregation: a dictation's form counts reach the terms through each form's owners, giving every term the
    // largest count among its forms, as the dense loop does, without visiting the terms none of its forms reached.
    private sealed class SparseTermCounts
    {
        private readonly Dictionary<string, List<int>> _owners = new(StringComparer.OrdinalIgnoreCase);
        private readonly int[] _dictationMax;
        private readonly List<int> _touched = [];

        public SparseTermCounts(IReadOnlyList<List<string>> formsByTerm)
        {
            for (var i = 0; i < formsByTerm.Count; i++)
            {
                foreach (var form in formsByTerm[i])
                {
                    if (!_owners.TryGetValue(form, out var owners))
                    {
                        _owners[form] = owners = [];
                    }

                    owners.Add(i);
                }
            }

            _dictationMax = new int[formsByTerm.Count];
        }

        public void Add(Dictionary<string, int> formCounts, int[] termDictations, int[] termOccurrences)
        {
            foreach (var (form, count) in formCounts)
            {
                if (count <= 0 || !_owners.TryGetValue(form, out var owners))
                {
                    continue;
                }

                foreach (var i in owners)
                {
                    if (_dictationMax[i] == 0)
                    {
                        _touched.Add(i);
                    }

                    _dictationMax[i] = Math.Max(_dictationMax[i], count);
                }
            }

            foreach (var i in _touched)
            {
                termDictations[i]++;
                termOccurrences[i] += _dictationMax[i];
                _dictationMax[i] = 0;
            }

            _touched.Clear();
        }
    }

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}]+(?:['’\-][\p{L}\p{M}\p{N}]+)*")]
    private static partial Regex Word();

    [GeneratedRegex(@"\.?[\p{L}\p{N}][\p{L}\p{M}\p{N}._#+\-/]*")]
    private static partial Regex Token();
}
