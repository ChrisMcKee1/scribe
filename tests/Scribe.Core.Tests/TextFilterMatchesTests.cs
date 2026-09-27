using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// <see cref="TextFilter.Matches(string?, string?[])"/> loops over its fields instead of calling Enumerable.Any, gains an
/// overload for two fields without a params array, and keeps a query's normalized form for as long as the query string
/// lives, keyed by the instance, because a list filter asks once per row with the same string. These tests hold both
/// overloads to the filter they replaced, copied here, over queries and fields in many scripts, forms and spacings,
/// exceptions included; show that a query seen before costs nothing to ask again; and show that a query string is not
/// kept alive by the filter.
/// </summary>
public sealed partial class TextFilterMatchesTests
{
    private static readonly string?[] Texts =
    [
        null, "", " ", "\t", "\u00A0", "resume", "Résumé for Contoso", "RÉSUMÉ", "re sume", "re  sume", "re\u00A0sume",
        "Microsoft Teams", "TEAMS", "teams", "slack", "e\u0301", "\u00E9", "E", "stra\u00DFe", "STRASSE", "\u0130stanbul",
        "\u0131", "i", "\u01C5", "\uFB01", "fi", "\uFF21", "a", "\u1100\u1161", "\uAC00", "\u00C5ngstr\u00F6m", "A\u030Angstrom",
        "\u212B", "\u03AC", "\u0391", "\uD83D\uDE00 smile", "x\u200By", "\u2028", " trimmed ", "\uD800", "a\uDC00b",
        "Ω", "\u2126", "ﬀ", "1", "١", "C#", "c#", "Line\r\nbreak", "tab\tbed",
    ];

    [Fact]
    public void Every_query_and_pair_of_fields_matches_as_it_matched()
    {
        var mismatches = new List<string>();
        var threw = 0;
        foreach (var query in Texts)
        {
            foreach (var first in Texts)
            {
                foreach (var second in Texts)
                {
                    var before = Outcome(() => Before(query, first, second));
                    var pair = Outcome(() => TextFilter.Matches(query, first, second));
                    var array = Outcome(() => TextFilter.Matches(query, [first, second]));
                    if (before != pair || before != array)
                    {
                        mismatches.Add($"'{Escaped(query)}' in '{Escaped(first)}', '{Escaped(second)}': {before}, {pair}, {array}");
                    }

                    threw += before.StartsWith("threw", StringComparison.Ordinal) ? 1 : 0;
                }
            }
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} differ: {string.Join("; ", mismatches.Take(10))}");
        Assert.True(threw > 0, "No text that is not well formed reached the filter.");
    }

    [Fact]
    public void Any_number_of_fields_matches_as_it_matched()
    {
        var random = new Random(20260917);
        for (var i = 0; i < 5_000; i++)
        {
            var query = Texts[random.Next(Texts.Length)];
            var fields = Enumerable.Range(0, random.Next(5)).Select(_ => Texts[random.Next(Texts.Length)]).ToArray();
            Assert.Equal(Outcome(() => Before(query, fields)), Outcome(() => TextFilter.Matches(query, fields)));
        }
    }

    // A fact, not a theory: xUnit hands a theory's string rows through UTF-8, which turns a lone surrogate into U+FFFD.
    [Fact]
    public void A_null_array_throws_what_it_threw()
    {
        string?[] queries = [null, "", "  ", "teams", "\uD800"];
        foreach (var query in queries)
        {
            Assert.Equal(Outcome(() => Before(query, null!)), Outcome(() => TextFilter.Matches(query, (string?[])null!)));
        }

        Assert.StartsWith(
            "threw System.ArgumentNullException", Outcome(() => TextFilter.Matches("teams", (string?[])null!)), StringComparison.Ordinal);
        Assert.StartsWith(
            "threw System.ArgumentException", Outcome(() => TextFilter.Matches("\uD800", (string?[])null!)), StringComparison.Ordinal);
    }

    [Fact]
    public void An_equal_query_in_another_string_matches_as_the_first_did()
    {
        var first = string.Concat("r\u00E9", "sum\u00E9");
        var second = new string(first.AsSpan());

        Assert.NotSame(first, second);
        Assert.True(TextFilter.Matches(first, "Resume for Contoso", null));
        Assert.True(TextFilter.Matches(second, "Resume for Contoso", null));
        Assert.False(TextFilter.Matches(second, "Microsoft Teams", null));
    }

    [Fact]
    public void A_query_string_is_not_kept_alive_by_the_filter()
    {
        var query = Ask();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(query.TryGetTarget(out _));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<string> Ask()
    {
        var query = string.Concat("private ", "search ", Guid.NewGuid().ToString("N"));
        Assert.False(TextFilter.Matches(query, "Microsoft Teams", "Resume"));
        Assert.False(TextFilter.Matches(query, ["Microsoft Teams", "Resume"]));
        return new WeakReference<string>(query);
    }

    private static string Outcome(Func<bool> matches)
    {
        try
        {
            return matches().ToString();
        }
        catch (ArgumentException failure)
        {
            return $"threw {failure.GetType().FullName}: {failure.Message}";
        }
    }

    private static string Escaped(string? text) =>
        text is null ? "(null)" : string.Concat(text.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:X4}"));

    // TextFilter.Matches of 10c9a0b.
    private static bool Before(string? query, params string?[] fields)
    {
        var normalizedQuery = BeforeNormalize(query);
        if (normalizedQuery.Length == 0)
        {
            return true;
        }

        return fields.Any(field => BeforeNormalize(field).Contains(normalizedQuery, StringComparison.Ordinal));
    }

    private static string BeforeNormalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var form = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(form.Length);
        foreach (var ch in form)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToUpperInvariant(ch));
            }
        }

        return BeforeSpaceRuns().Replace(builder.ToString().Normalize(NormalizationForm.FormC), " ");
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex BeforeSpaceRuns();

    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Fact]
        public void A_query_seen_before_is_asked_again_without_allocating()
        {
            var query = string.Concat("r\u00E9", "sum\u00E9 ", "tea");
            string?[] fields = [null, null];
            var matches = 0;
            for (var i = 0; i < 100; i++)
            {
                matches += Ask(query, fields);
            }

            _ = RuntimeWork.Now().Since(RuntimeWork.Now());

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            matches += Ask(query, fields);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            AllocationMeasurement.AssertZero(allocated, during, "1,000 rows filtered with a query seen before", () => Ask(query, fields));
            Assert.Equal(0, matches);
        }

        // 1,000 rows through both overloads, with fields that normalize to nothing, so only the query's own cost remains.
        private static int Ask(string query, string?[] fields)
        {
            var matches = 0;
            for (var row = 0; row < 500; row++)
            {
                matches += TextFilter.Matches(query, fields[0], fields[1]) ? 1 : 0;
                matches += TextFilter.Matches(query, fields) ? 1 : 0;
            }

            return matches;
        }
    }
}
