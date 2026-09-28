using System.Text;
using System.Text.RegularExpressions;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// CachedRowSearchText: Your words rows keep their fields' normalized search text, and Find a word matches through
/// <see cref="TextFilter.MatchesCached"/>. Today's <see cref="TextFilter.Matches(string?, string?, string?)"/> is the
/// oracle for the answer, for any exception a field's or the query's text raises, and for which fields are looked at: the
/// query first (a blank or normalized-empty query matches before any field), then the first field, then the second only
/// when the first does not match. Normalizing throws for ill-formed UTF-16, which the personal dictionary's editor does
/// not refuse, so lone surrogates are part of the oracle here.
/// </summary>
public sealed class CachedRowSearchTextTests
{
    private static readonly string LoneHigh = new((char)0xD800, 1);
    private static readonly string LoneLow = new((char)0xDC00, 1);

    private static readonly string[] Pieces =
    [
        "Azure", "azure", "AZURE", "Kubernetes", "caf\u00e9", "cafe\u0301", "CAF\u00c9", "na\u00efve", "\u00c5ngstr\u00f6m",
        "Stra\u00dfe", "stras", "\u0130stanbul", "istanbul", "\u03a3\u03af\u03c3\u03c5\u03c6\u03bf\u03c2", "\ud83d\ude00",
        "C#", ".NET", "x86-64", "  ", "\t", "\u00a0", "\u2003", "\r\n", "", "e\u0301\u0301", "\u1e9b\u0323", "\ufb01",
        "ffi", "\u212b", "\u2126", "k", "\u212a", "\u0000", "\u0301", "\u0301\u0300", LoneHigh, LoneLow,
    ];

    // The old filter's outcome, or the new one's: the answer, or the exception's type.
    private static string Outcome(Func<bool> match)
    {
        try
        {
            return match() ? "true" : "false";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    private static string Old(string? query, string? first, string? second) =>
        Outcome(() => TextFilter.Matches(query, first, second));

    [Fact]
    public void Seeded_rows_and_queries_answer_and_fail_exactly_as_the_old_filter()
    {
        // 400 rows, each asked 40 queries with the same two caches, as a row keeps them, with edits in between.
        var random = new Random(20260928);
        var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var r = 0; r < 400; r++)
        {
            var pattern = Compose(random);
            var replacement = Compose(random);
            var patternCache = default(CachedSearchText);
            var replacementCache = default(CachedSearchText);
            for (var q = 0; q < 40; q++)
            {
                var query = random.Next(9) switch
                {
                    0 => null,
                    1 => string.Empty,
                    2 => "   ",
                    3 => "\u0301",
                    4 => Slice(random, pattern),
                    5 => Slice(random, replacement),
                    _ => Compose(random),
                };

                // Now and then the row is edited, sometimes to an equal string in a new instance.
                switch (random.Next(12))
                {
                    case 0:
                        pattern = new string(pattern.AsSpan());
                        break;
                    case 1:
                        pattern = Compose(random);
                        break;
                    case 2:
                        replacement = Compose(random);
                        break;
                }

                var expected = Old(query, pattern, replacement);
                var actual = Outcome(() => TextFilter.MatchesCached(query, ref patternCache, pattern, ref replacementCache, replacement));
                Assert.True(expected == actual, $"row {r} query {q}: old {expected}, cached {actual}");
                outcomes[expected] = outcomes.GetValueOrDefault(expected) + 1;
            }
        }

        // The sequence exercised matches, misses and failures alike.
        Assert.True(outcomes.GetValueOrDefault("true") > 1_000, string.Join(", ", outcomes));
        Assert.True(outcomes.GetValueOrDefault("false") > 1_000, string.Join(", ", outcomes));
        Assert.True(outcomes.GetValueOrDefault(nameof(ArgumentException)) > 100, string.Join(", ", outcomes));
    }

    [Theory]
    [InlineData(0)] // null
    [InlineData(1)] // empty
    [InlineData(2)] // white space
    [InlineData(3)] // combining marks only: normalizes to nothing
    public void A_blank_or_normalized_empty_query_matches_before_either_field_is_looked_at(int kind)
    {
        string? query = kind switch { 0 => null, 1 => string.Empty, 2 => "   ", _ => "\u0301\u0300" };
        var first = default(CachedSearchText);
        var second = default(CachedSearchText);

        Assert.Equal("true", Old(query, LoneHigh, LoneLow));
        Assert.Equal("true", Outcome(() => TextFilter.MatchesCached(query, ref first, LoneHigh, ref second, LoneLow)));
        Assert.False(first.Holds(LoneHigh));
        Assert.False(second.Holds(LoneLow));
    }

    [Fact]
    public void A_matching_first_field_leaves_the_second_unread()
    {
        var first = default(CachedSearchText);
        var second = default(CachedSearchText);

        Assert.Equal("true", Old("alpha", "Alpha beta", LoneHigh));
        Assert.Equal("true", Outcome(() => TextFilter.MatchesCached("alpha", ref first, "Alpha beta", ref second, LoneHigh)));
        Assert.True(first.Holds("Alpha beta"));
        Assert.False(second.Holds(LoneHigh));
    }

    [Fact]
    public void A_first_field_that_does_not_match_reads_the_second_and_fails_as_the_old_filter_does()
    {
        var first = default(CachedSearchText);
        var second = default(CachedSearchText);

        Assert.Equal("ArgumentException", Old("gamma", "Alpha beta", LoneHigh));
        Assert.Equal("ArgumentException", Outcome(() => TextFilter.MatchesCached("gamma", ref first, "Alpha beta", ref second, LoneHigh)));
        Assert.True(first.Holds("Alpha beta"));
        Assert.False(second.Holds(LoneHigh));

        // With a second field that normalizes, both are read and kept.
        Assert.Equal("true", Outcome(() => TextFilter.MatchesCached("gamma", ref first, "Alpha beta", ref second, "Gamma")));
        Assert.True(second.Holds("Gamma"));
    }

    [Fact]
    public void A_query_that_cannot_be_normalized_fails_before_any_field_is_read()
    {
        var first = default(CachedSearchText);
        var second = default(CachedSearchText);

        Assert.Equal("ArgumentException", Old("a" + LoneHigh, "alpha", "beta"));
        Assert.Equal("ArgumentException", Outcome(() => TextFilter.MatchesCached("a" + LoneHigh, ref first, "alpha", ref second, "beta")));
        Assert.False(first.Holds("alpha"));
        Assert.False(second.Holds("beta"));
    }

    [Fact]
    public void A_field_that_fails_to_normalize_never_takes_the_text_kept_for_another()
    {
        // UI-IR-01: a populated cache, a failed replacement, a repeated failure, then later successes, each against
        // normalizing afresh.
        var cache = default(CachedSearchText);
        var malformed = "caf\u00e9" + LoneHigh;

        Assert.Equal(TextFilter.NormalizeField("alpha"), cache.For("alpha"));
        Assert.Equal(Normalized(malformed), Kept(ref cache, malformed));
        Assert.Equal("ArgumentException", Kept(ref cache, malformed));
        Assert.Equal("ArgumentException", Kept(ref cache, malformed));
        Assert.True(cache.Holds("alpha"));
        Assert.False(cache.Holds(malformed));

        Assert.Equal(TextFilter.NormalizeField("alpha"), cache.For("alpha"));
        Assert.Equal(TextFilter.NormalizeField("Beta gamma"), cache.For("Beta gamma"));
        Assert.Equal("ArgumentException", Kept(ref cache, malformed));
        Assert.True(cache.Holds("Beta gamma"));
        Assert.Equal(TextFilter.NormalizeField(null), cache.For(null));
        Assert.True(cache.Holds(null));
    }

    [Fact]
    public void A_fresh_cache_fails_as_normalizing_does_and_keeps_nothing()
    {
        var cache = default(CachedSearchText);

        Assert.Equal("ArgumentException", Kept(ref cache, LoneLow));
        Assert.False(cache.Holds(LoneLow));
        Assert.False(cache.Holds(null));
        Assert.Equal(string.Empty, cache.For("   "));
    }

    [Fact]
    public void The_kept_form_is_the_same_string_until_the_field_is_another_string()
    {
        var cache = default(CachedSearchText);
        var field = "Caf\u00e9 au lait";

        var first = cache.For(field);
        Assert.Same(first, cache.For(field));
        Assert.Equal(TextFilter.NormalizeField(field), first);

        var edited = field + "s";
        Assert.Equal(TextFilter.NormalizeField(edited), cache.For(edited));
        Assert.Equal(string.Empty, cache.For(null));
        Assert.Equal(string.Empty, cache.For("   "));
    }

    [Fact]
    public void The_Your_words_filter_uses_the_row_cache_only_with_the_flag_on_and_in_the_old_order()
    {
        var root = RepositoryRoot();
        var yourWords = File.ReadAllText(Path.Combine(root, "src", "Scribe.App", "Settings", "SettingsWindow.YourWords.cs"));
        var filter = yourWords[yourWords.IndexOf("private bool DictionaryFilter(object item)", StringComparison.Ordinal)..];
        filter = filter[..filter.IndexOf("private void UpdateDictionaryViewState()", StringComparison.Ordinal)];

        Assert.Contains("if (_perfFlags.IsOn(PerfFlags.CachedRowSearchText))", filter, StringComparison.Ordinal);
        Assert.Contains("return row.MatchesSearch(DictionarySearchBox?.Text);", filter, StringComparison.Ordinal);
        Assert.Contains("return TextFilter.Matches(DictionarySearchBox?.Text, row.Pattern, row.Replacement);", filter, StringComparison.Ordinal);

        // The row hands its caches and fields over as they are; the helper decides what is read, and when.
        var window = File.ReadAllText(Path.Combine(root, "src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs"));
        Assert.Contains(
            "TextFilter.MatchesCached(query, ref _patternSearch, Pattern, ref _replacementSearch, Replacement);",
            window,
            StringComparison.Ordinal);
        Assert.Single(Regex.Matches(window, @"public bool MatchesSearch\(string\? query\)"));
        Assert.DoesNotContain("_patternSearch.For(", window, StringComparison.Ordinal);
        Assert.DoesNotContain("_replacementSearch.For(", window, StringComparison.Ordinal);
    }

    private static string Normalized(string? value) => Outcome(() => TextFilter.NormalizeField(value).Length >= 0);

    private static string Kept(ref CachedSearchText cache, string? value)
    {
        try
        {
            return cache.For(value).Length >= 0 ? "true" : "false";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    private static string Compose(Random random)
    {
        var text = new StringBuilder();
        var parts = random.Next(0, 4);
        for (var i = 0; i < parts; i++)
        {
            if (i > 0 && random.Next(3) == 0)
            {
                text.Append(' ');
            }

            text.Append(Pieces[random.Next(Pieces.Length)]);
        }

        return text.ToString();
    }

    // Any piece of the text, surrogate pairs cut through included: ill-formed queries are part of the oracle too.
    private static string Slice(Random random, string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var start = random.Next(text.Length);
        return text.Substring(start, random.Next(1, text.Length - start + 1));
    }

    private static string RepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Scribe.slnx")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
