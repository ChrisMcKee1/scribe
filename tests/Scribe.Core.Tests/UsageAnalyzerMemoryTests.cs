using System.Text.RegularExpressions;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using AppUsage = Scribe.Core.Diagnostics.UsageAnalyzer.AppUsage;
using Snapshot = Scribe.Core.Diagnostics.UsageAnalyzer.Snapshot;
using TermUsage = Scribe.Core.Diagnostics.UsageAnalyzer.TermUsage;
using TrendGranularity = Scribe.Core.Diagnostics.UsageAnalyzer.TrendGranularity;
using TrendPoint = Scribe.Core.Diagnostics.UsageAnalyzer.TrendPoint;

namespace Scribe.Core.Tests;

public partial class UsageAnalyzerMemoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 15, 0, 0, TimeSpan.Zero);

    private static readonly string[] Plain =
    [
        "the", "and", "to", "of", "a", "in", "that", "is", "for", "it", "with", "as", "was", "on", "be", "at", "we", "please",
        "send", "email", "thanks", "tomorrow", "meeting", "project", "review", "update", "budget", "report", "draft",
    ];

    // Tokens where trimming, case, marks, surrogates, jargon shapes and the stoplist decide what counts.
    private static readonly string[] Tricky =
    [
        "OpenAI", "OPENAI", "openai", "OpenAI.", "GitHub", "github", "K8s", "K8s,", "GPT4", "GPT4?!", "ReBAC", "S3", "net10",
        "AKS", "AKS:", "TODO", "PM", "OK", "C#", "C#;", ".NET", ".NET:", "e.g.", "node.js", "state-of-the-art", "don't",
        "don\u2019t", "\u00C9t\u00E9", "\u0131i", "\u212Aelvin", "x/y", "a+b", "#hash", "under_score", "--", "...", "C++",
        "\uD835\uDCB3yz", "e\u0301te", "Visual", "Studio", "visual", "studio", "code", "WinUI3", "iOS", "?!", "..NET",
    ];

    private static readonly string?[] Apps = ["WINWORD", "ms-teams", "OUTLOOK.EXE", "Code", null, "  ", "slack", "olk .exe"];

    private static readonly DictionaryEntry[] CustomTerms =
    [
        new(1, "visual studio", "Visual Studio"),
        new(2, "visual studio code", "Visual Studio Code"),
        new(3, "dot net", ".NET"),
        new(4, "c sharp", "C#"),
        new(5, "k eights", "K8s"),
        new(6, "open ai", "OpenAI"),
        new(7, "Open AI", "openai"),
        new(8, "gpt four", "GPT4"),
        new(9, "x", "y"),
        new(10, "e g", "e.g."),
        new(11, "node js", "node.js"),
        new(12, "sig", "Best,\nChris"),
        new(13, "disabled", "Disabled Term", Enabled: false),
        new(14, "blank", " "),
        new(15, "state of the art", "state-of-the-art"),
        new(16, "rebac", "ReBAC"),
        new(17, "kelvin", "\u212Aelvin"),
        new(18, "ete", "\u00C9t\u00E9"),
    ];

    public static TheoryData<int, int, string, bool, int> Cases => new()
    {
        { 1, 300, "custom", false, 16 },
        { 2, 300, "custom", true, 200 },
        { 3, 120, "custom", false, 0 },
        { 4, 300, "default packs", false, 16 },
        { 5, 200, "default packs", true, 200 },
        { 6, 200, "custom and default packs", true, 40 },
        { 7, 0, "custom", false, 16 },
        { 8, 60, "none", false, 16 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Compute_returns_exactly_what_the_previous_implementation_returned(
        int seed, int count, string terms, bool restrictSharing, int maxTerms)
    {
        var known = Terms(terms);
        var entries = Corpus(seed, count, known);
        Func<DictionaryEntry, bool>? mayShare = restrictSharing ? entry => entry.Pattern.Length % 2 == 0 : null;
        var since = Now.AddDays(-89);

        var expected = Oracle.Compute(entries, known, since, Now, mayShare, TimeZoneInfo.Utc, maxApps: 8, maxTerms);
        var actual = UsageAnalyzer.Compute(entries, known, since, Now, mayShare, TimeZoneInfo.Utc, maxApps: 8, maxTerms);

        AssertSame(expected, actual);
        if (maxTerms >= 200)
        {
            // The corpus reaches both kinds of term, so the comparison is not of two empty lists.
            Assert.Contains(expected.Terms, term => term.Covered);
            Assert.Contains(expected.Terms, term => !term.Covered);
        }

        if (!restrictSharing)
        {
            AssertSame(expected, UsageAnalyzer.Compute(entries, known, since, Now, TimeZoneInfo.Utc, maxApps: 8, maxTerms));
        }
    }

    [Fact]
    public void Words_are_counted_as_the_previous_implementation_counted_them()
    {
        string?[] edges = [null, string.Empty, " ", "\t\n", "don't", "don\u2019t", "a-b-c", "-a-", "'", "\u2019", "12 34", "e\u0301te",
            "\uD835\uDCB3yz \uD835\uDCB3", "state-of-the-art", "a--b", "a''b", "x'", "'x", "\u0661\u0662\u0663", "K8s, .NET!"];
        foreach (var text in edges.Concat(Corpus(9, 200, CustomTerms).Select(entry => entry.Text)))
        {
            Assert.Equal(Oracle.CountWords(text), UsageAnalyzer.CountWords(text));
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("custom")]
    public void A_null_transcript_fails_as_it_did(string terms)
    {
        var known = Terms(terms);
        var entries = Corpus(10, 20, known).Append(new HistoryEntry(1_000, Now.AddHours(-1), null!, 1_000, 100)).ToList();

        var expected = Assert.Throws<ArgumentNullException>(
            () => Oracle.Compute(entries, known, Now.AddDays(-89), Now, null, TimeZoneInfo.Utc));
        var actual = Assert.Throws<ArgumentNullException>(
            () => UsageAnalyzer.Compute(entries, known, Now.AddDays(-89), Now, null, TimeZoneInfo.Utc));

        Assert.Equal(expected.ParamName, actual.ParamName);
        Assert.Equal(expected.Message, actual.Message);
    }

    // In the collection that runs alone: no other test allocates on this thread while it measures.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Fact]
        public void A_report_no_longer_builds_match_objects_and_substrings_for_every_word()
        {
            var entries = Corpus(11, 600, CustomTerms);
            var since = Now.AddDays(-89);
            _ = UsageAnalyzer.Compute(entries, CustomTerms, since, Now, null, TimeZoneInfo.Utc);
            _ = Oracle.Compute(entries, CustomTerms, since, Now, null, TimeZoneInfo.Utc);

            var oracleBefore = GC.GetAllocatedBytesForCurrentThread();
            _ = Oracle.Compute(entries, CustomTerms, since, Now, null, TimeZoneInfo.Utc);
            var oracleBytes = GC.GetAllocatedBytesForCurrentThread() - oracleBefore;

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = UsageAnalyzer.Compute(entries, CustomTerms, since, Now, null, TimeZoneInfo.Utc);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            // A ratio, not a size: both still build a compiled regex per phrase and the snapshot's records, and a new novel
            // term still needs its string. What went is a Match per word and per token, the substrings of every token, a
            // MatchCollection per phrase per dictation, and three collections per dictation.
            Assert.True(
                allocated * 5 < oracleBytes,
                $"{allocated} bytes against the previous implementation's {oracleBytes} for 600 dictations. During it: {during}.");
        }
    }

    private static IReadOnlyList<DictionaryEntry> Terms(string set) => set switch
    {
        "none" => [],
        "custom" => CustomTerms,
        "default packs" => DefaultPackTerms(),
        "custom and default packs" => [.. CustomTerms, .. DefaultPackTerms()],
        _ => throw new ArgumentOutOfRangeException(nameof(set)),
    };

    private static List<DictionaryEntry> DefaultPackTerms() =>
        BuiltInDictionaryLibraries.All
            .Where(library => AppSettings.DefaultLibraryIds.Contains(library.Id))
            .SelectMany(library => library.Entries)
            .ToList();

    private static List<HistoryEntry> Corpus(int seed, int count, IReadOnlyList<DictionaryEntry> terms)
    {
        var random = new Random(seed);
        var entries = new List<HistoryEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var words = new List<string>();
            for (var w = 8 + random.Next(33); w > 0; w--)
            {
                var roll = random.Next(100);
                string word;
                if (roll < 55)
                {
                    word = Plain[random.Next(Plain.Length)];
                }
                else if (roll < 85 || terms.Count == 0)
                {
                    word = Tricky[random.Next(Tricky.Length)];
                }
                else
                {
                    word = TermForm(terms[random.Next(terms.Count)], random);
                }

                words.Add(word);
                words.Add(random.Next(12) switch { 0 => ", ", 1 => ". ", 2 => "\n", 3 => "  ", _ => " " });
            }

            var minutes = random.Next(8) == 0 ? -random.Next(1, 600) : random.Next(120 * 24 * 60);
            entries.Add(new HistoryEntry(
                i + 1,
                Now.AddMinutes(-minutes),
                string.Concat(words).TrimEnd(),
                random.Next(10) == 0 ? -random.Next(100) : random.Next(40_000),
                100 + random.Next(2_000),
                TargetApp: Apps[random.Next(Apps.Length)]));
        }

        return entries;
    }

    private static string TermForm(DictionaryEntry term, Random random)
    {
        var form = random.Next(2) == 0 ? term.Pattern : term.Replacement;
        return random.Next(4) switch
        {
            0 => form.ToUpperInvariant(),
            1 => form.ToLowerInvariant(),
            _ => form,
        };
    }

    private static void AssertSame(Snapshot expected, Snapshot actual)
    {
        Assert.Equal(expected.Dictations, actual.Dictations);
        Assert.Equal(expected.Words, actual.Words);
        Assert.Equal(expected.ActiveDays, actual.ActiveDays);
        Assert.Equal(expected.Speech, actual.Speech);
        Assert.Equal(expected.AverageWords, actual.AverageWords);
        Assert.Equal(expected.TopApps, actual.TopApps);
        Assert.Equal(expected.Trend, actual.Trend);
        Assert.Equal(expected.Terms, actual.Terms);
        Assert.Equal(expected.Terms.Select(term => term.Shareable), actual.Terms.Select(term => term.Shareable));
        Assert.Equal(expected.Granularity, actual.Granularity);
        Assert.Equal(expected.LongestDictation, actual.LongestDictation);
    }

    // UsageAnalyzer's computation as it was before the allocation change, copied from 10c9a0b verbatim apart from its XML
    // documentation, so every snapshot above is compared with what the previous implementation returned for the same input.
    private static partial class Oracle
    {
        internal static Snapshot Compute(
            IEnumerable<HistoryEntry> entries,
            IEnumerable<DictionaryEntry> knownTerms,
            DateTimeOffset sinceUtc,
            DateTimeOffset nowUtc,
            Func<DictionaryEntry, bool>? mayShare,
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
                Terms: ExtractTerms(selected, knownTerms, maxTerms, mayShare),
                Granularity: granularity,
                LongestDictation: TimeSpan.FromMilliseconds(selected.Count == 0 ? 0 : selected.Max(entry => Math.Max(0, entry.AudioMilliseconds))));
        }

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
            int maxTerms,
            Func<DictionaryEntry, bool>? mayShare)
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
                        !DictionarySuggestionMiner.IsCandidate(token))
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
}
