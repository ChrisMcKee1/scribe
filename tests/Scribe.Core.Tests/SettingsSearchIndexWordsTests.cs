using System.Globalization;
using System.Text;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// <see cref="SettingsSearchIndex.Search(string?, int)"/> finds the words of its static entries once, on the first search,
/// instead of tokenizing every entry for every term of every keystroke. These tests hold it to the search it replaced,
/// copied here: the same results, in the same order, for every prefix of every word the index holds, for whole labels,
/// keywords and ids, for combinations of terms, and for queries with accents, capitals, punctuation and nothing in them,
/// at every result limit.
/// </summary>
public sealed class SettingsSearchIndexWordsTests
{
    private static readonly int[] Limits = [int.MinValue, -1, 0, 1, 2, 5, 7, 8, 9, 20, int.MaxValue];

    [Fact]
    public void Every_prefix_of_every_word_the_index_holds_finds_what_it_found()
    {
        var mismatches = Queries().Where(query => !SameResults(query, 8) || !SameResults(query, 20)).Take(10).ToList();

        Assert.Empty(mismatches);
    }

    [Fact]
    public void The_default_limit_finds_what_it_found()
    {
        var mismatches = Queries()
            .Where(query => !Same(Before(query, 8), SettingsSearchIndex.Search(query)))
            .Take(10)
            .ToList();

        Assert.Empty(mismatches);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("Cópi")]
    [InlineData("rec ind")]
    [InlineData("zzzz")]
    [InlineData("mícrophone")]
    [InlineData("ÁI")]
    [InlineData("\uFF2Dodel")]
    [InlineData("push-to-talk")]
    [InlineData("ai,cleanup")]
    [InlineData("İdle")]
    [InlineData("model model")]
    [InlineData("foundry model")]
    [InlineData("a b c d e")]
    [InlineData("dictionary words")]
    [InlineData("windows accent color")]
    [InlineData("--")]
    [InlineData("e\u0301")]
    public void A_query_finds_what_it_found_at_every_limit(string? query)
    {
        foreach (var limit in Limits)
        {
            Assert.True(SameResults(query, limit), $"'{query}' with a limit of {limit}");
        }
    }

    [Fact]
    public void The_current_culture_changes_nothing()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        try
        {
            string[] queries = ["idle", "IDLE", "İdle", "ıdle", "trim silence", "Tenant ID", "INDICATOR", "insights"];
            Assert.All(queries, query => Assert.True(SameResults(query, 8), query));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // Every prefix of every word of every label, keyword and page, as typed and in capitals; every label, keyword, page,
    // context, id and control name whole; and seeded pairs and triples of those words and prefixes.
    private static IEnumerable<string> Queries()
    {
        var words = new SortedSet<string>(StringComparer.Ordinal);
        var queries = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in SettingsSearchIndex.Entries)
        {
            string?[] texts =
            [
                entry.Label, entry.DisplayLabel, entry.Context, entry.PageLabel, entry.Id, entry.ControlName,
                .. entry.Keywords ?? [],
            ];
            foreach (var text in texts.OfType<string>())
            {
                queries.Add(text);
                foreach (var word in BeforeWords(text))
                {
                    words.Add(word);
                    for (var length = 1; length <= word.Length; length++)
                    {
                        queries.Add(word[..length]);
                        queries.Add(word[..length].ToUpperInvariant());
                    }
                }
            }
        }

        var pool = words.ToArray();
        var random = new Random(20260917);
        for (var i = 0; i < 1_500; i++)
        {
            var first = pool[random.Next(pool.Length)];
            var second = pool[random.Next(pool.Length)];
            queries.Add($"{first[..random.Next(1, first.Length + 1)]} {second[..random.Next(1, second.Length + 1)]}");
            if (i % 3 == 0)
            {
                var third = pool[random.Next(pool.Length)];
                queries.Add($"{first} {second}, {third[..random.Next(1, third.Length + 1)]}");
            }
        }

        return queries;
    }

    private static bool SameResults(string? query, int limit) => Same(Before(query, limit), SettingsSearchIndex.Search(query, limit));

    private static bool Same(IReadOnlyList<SettingsSearchResult> before, IReadOnlyList<SettingsSearchResult> after) =>
        before.Count == after.Count && before.Zip(after).All(pair =>
            ReferenceEquals(pair.First.Entry, pair.Second.Entry)
            && string.Equals(pair.First.DisplayText, pair.Second.DisplayText, StringComparison.Ordinal));

    // SettingsSearchIndex.Search of 10c9a0b, which tokenized every entry for every term of every search.
    private static IReadOnlyList<SettingsSearchResult> Before(string? query, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
        {
            return [];
        }

        var terms = BeforeWords(query).ToArray();
        if (terms.Length == 0)
        {
            return [];
        }

        return SettingsSearchIndex.Entries
            .Select((entry, index) => new { Entry = entry, Index = index, Rank = BeforeRank(entry, terms) })
            .Where(candidate => candidate.Rank < int.MaxValue)
            .OrderBy(candidate => candidate.Rank)
            .ThenBy(candidate => SettingsNavigation.Items.First(item => item.Page == candidate.Entry.Page).Position)
            .ThenBy(candidate => candidate.Index)
            .Take(Math.Min(maxResults, 8))
            .Select(candidate => new SettingsSearchResult(candidate.Entry, $"{candidate.Entry.DisplayLabel} on {candidate.Entry.PageLabel}"))
            .ToArray();
    }

    private static int BeforeRank(SettingsSearchEntry entry, IReadOnlyList<string> terms)
    {
        if (BeforeAllTermsMatch(terms, entry.DisplayLabel))
        {
            return 0;
        }

        if (entry.Keywords is { Count: > 0 } keywords && BeforeAllTermsMatch(terms, [entry.DisplayLabel, .. keywords]))
        {
            return 2;
        }

        if (BeforeAllTermsMatch(terms, entry.PageLabel))
        {
            return 3;
        }

        return int.MaxValue;
    }

    private static bool BeforeAllTermsMatch(IReadOnlyList<string> terms, params string?[] values) =>
        terms.All(term => values.SelectMany(BeforeWords).Any(word => word.StartsWith(term, StringComparison.Ordinal)));

    private static IEnumerable<string> BeforeWords(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Theory]
        [InlineData("ai")]
        [InlineData("dictionary words")]
        [InlineData("model")]
        public void A_keystroke_tokenizes_only_the_query(string query)
        {
            for (var i = 0; i < 20; i++)
            {
                _ = SettingsSearchIndex.Search(query);
            }

            _ = RuntimeWork.Now().Since(RuntimeWork.Now());

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var results = SettingsSearchIndex.Search(query);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            // A bound, not an exact size: the query's words, the candidates, their sort and at most 8 results with their
            // text. Tokenizing the 51 entries again cost about 128 KB for "ai".
            Assert.NotEmpty(results);
            Assert.True(allocated <= 16 * 1024, $"Search(\"{query}\") allocated {allocated} bytes. During it: {during}.");
        }
    }
}
