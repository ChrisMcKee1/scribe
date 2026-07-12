using System.Globalization;
using System.Text.RegularExpressions;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Diagnostics;

/// <summary>Computes descriptive, local-only usage metrics from retained dictation history.</summary>
public static partial class UsageAnalyzer
{
    public sealed record AppUsage(string Name, int Dictations, int Words);

    public sealed record TrendPoint(DateOnly Start, int Dictations, int Words);

    public sealed record TermUsage(string Text, int Dictations, int Occurrences, bool Covered);

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
        TrendGranularity Granularity = TrendGranularity.Daily);

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
        int maxTerms = 16)
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
                entry => string.IsNullOrWhiteSpace(entry.TargetApp) ? "Unknown app" : entry.TargetApp.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new AppUsage(
                string.IsNullOrWhiteSpace(group.First().TargetApp)
                    ? "Unknown app"
                    : group.OrderBy(entry => entry.TargetApp, StringComparer.Ordinal).First().TargetApp!.Trim(),
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
            Terms: ExtractTerms(selected, knownTerms, maxTerms),
            Granularity: granularity);
    }

    /// <summary>Counts Unicode letter/number words without assuming a particular language.</summary>
    public static int CountWords(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : Word().Matches(text).Count;

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

    private static IReadOnlyList<TermUsage> ExtractTerms(
        IReadOnlyList<HistoryEntry> entries,
        IEnumerable<DictionaryEntry> knownTerms,
        int maxTerms)
    {
        var known = knownTerms
            .Where(entry => entry.Enabled && !string.IsNullOrWhiteSpace(entry.Replacement))
            .GroupBy(entry => entry.Replacement.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Canonical = group.First().Replacement.Trim(),
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
        // compiled regex matching.
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
                phraseMatchers.Add(new PhraseMatcher(form, CreatePhraseRegex(form)));
            }
        }

        var coveredForms = new HashSet<string>(
            known.SelectMany(term => term.Forms),
            StringComparer.OrdinalIgnoreCase);
        var termDictations = new int[known.Count];
        var termOccurrences = new int[known.Count];
        var novelForms = new Dictionary<string, (string Surface, int Dictations, int Occurrences)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var formCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var lastTokenMatchEnds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var seenNovelForms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Token().Matches(entry.Text))
            {
                CountSingleTokenForms(match, singleTokenForms, formCounts, lastTokenMatchEnds);

                var token = match.Value.TrimEnd('.', ',', ':', ';', '!', '?');
                if (token.Length < 2 ||
                    coveredForms.Contains(token) ||
                    !DictionarySuggestionMiner.IsJargonShaped(token))
                {
                    continue;
                }

                var current = novelForms.GetValueOrDefault(token);
                novelForms[token] = (
                    string.IsNullOrEmpty(current.Surface) ? token : current.Surface,
                    current.Dictations + (seenNovelForms.Add(token) ? 1 : 0),
                    current.Occurrences + 1);
            }

            foreach (var matcher in phraseMatchers)
            {
                var count = matcher.Pattern.Matches(entry.Text).Count;
                if (count > 0)
                {
                    formCounts[matcher.Text] = count;
                }
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

        var results = new List<TermUsage>();
        for (var i = 0; i < known.Count; i++)
        {
            if (termDictations[i] > 0)
            {
                results.Add(new TermUsage(known[i].Canonical, termDictations[i], termOccurrences[i], Covered: true));
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
        var match = Token().Match(form);
        return match.Success && match.Index == 0 && match.Length == form.Length;
    }

    private static void CountSingleTokenForms(
        Match tokenMatch,
        HashSet<string> singleTokenForms,
        Dictionary<string, int> formCounts,
        Dictionary<string, int> lastMatchEnds)
    {
        var token = tokenMatch.Value;
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

                var candidate = token[start..end];
                if (!singleTokenForms.TryGetValue(candidate, out var form))
                {
                    continue;
                }

                var absoluteStart = tokenMatch.Index + start;
                if (lastMatchEnds.TryGetValue(form, out var lastEnd) && absoluteStart < lastEnd)
                {
                    continue;
                }

                formCounts[form] = formCounts.GetValueOrDefault(form) + 1;
                lastMatchEnds[form] = tokenMatch.Index + end;
            }
        }
    }

    // Compiled because each surviving phrase regex still runs against every history entry.
    private static Regex CreatePhraseRegex(string phrase) => new(
        $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(phrase)}(?![\p{{L}}\p{{N}}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static DateOnly LocalDate(DateTimeOffset timestamp, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, zone).DateTime);

    private static DateOnly StartOfWeek(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-offset);
    }

    private sealed record PhraseMatcher(string Text, Regex Pattern);

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}]+(?:['’\-][\p{L}\p{M}\p{N}]+)*")]
    private static partial Regex Word();

    [GeneratedRegex(@"\.?[\p{L}\p{N}][\p{L}\p{M}\p{N}._#+\-/]*")]
    private static partial Regex Token();
}
