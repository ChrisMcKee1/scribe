using System.Text.Json;
using System.Text.RegularExpressions;
using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// <see cref="LibraryTermKey"/>, the one key the editor, the built-in overlay and composition share, and the commit form
/// the editor normalizes a typed Spoken value to. The fixture <c>tests/fixtures/libraries/term-keys.json</c> is what the
/// macOS port checks itself against, so it is read here too, and each of its answers is also checked against the
/// definitions (the key: trim, then OrdinalIgnoreCase; the commit form: trim and collapse) so it can never drift from
/// what .NET does.
/// </summary>
public sealed class LibraryTermKeyTests
{
    public static TheoryData<string?, string, string> CommitFormCases() => Cases("commitForm");

    public static TheoryData<string?, string, string> KeyCases() => Cases("key");

    public static TheoryData<string, string, bool, string> SameCases()
    {
        var data = new TheoryData<string, string, bool, string>();
        foreach (var item in Fixture().GetProperty("same").EnumerateArray())
        {
            data.Add(
                item.GetProperty("a").GetString()!,
                item.GetProperty("b").GetString()!,
                item.GetProperty("same").GetBoolean(),
                item.GetProperty("note").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CommitFormCases))]
    public void Commits_as_the_shared_fixture_says(string? input, string expected, string note)
    {
        Assert.True(string.Equals(expected, LibraryTermKey.Normalize(input), StringComparison.Ordinal), note);
        Assert.True(string.Equals(expected, Collapsed(input), StringComparison.Ordinal), $"{note}: the fixture disagrees with the definition");
        Assert.True(LibraryTermKey.IsInCommitForm(expected), note);
        Assert.True(
            LibraryTermKey.IsInCommitForm(input) == string.Equals(input ?? string.Empty, expected, StringComparison.Ordinal),
            $"{note}: IsInCommitForm");
    }

    [Theory]
    [MemberData(nameof(KeyCases))]
    public void Keys_as_the_shared_fixture_says(string? input, string expected, string note)
    {
        Assert.True(string.Equals(expected, LibraryTermKey.From(input).Value, StringComparison.Ordinal), note);
        Assert.True(string.Equals(expected, Trimmed(input), StringComparison.Ordinal), $"{note}: the fixture disagrees with the definition");
    }

    [Theory]
    [MemberData(nameof(SameCases))]
    public void Compares_as_the_shared_fixture_says(string a, string b, bool same, string note)
    {
        Assert.True(same == LibraryTermKey.AreSame(a, b), note);
        Assert.True(same == (LibraryTermKey.From(a) == LibraryTermKey.From(b)), note);
        Assert.True(same != (LibraryTermKey.From(a) != LibraryTermKey.From(b)), note);
        Assert.True(same == LibraryTermKey.SpokenComparer.Equals(a, b), note);
        Assert.True(
            same == string.Equals(Trimmed(a), Trimmed(b), StringComparison.OrdinalIgnoreCase),
            $"{note}: the fixture disagrees with the definition");
        if (same)
        {
            Assert.Equal(LibraryTermKey.From(a).GetHashCode(), LibraryTermKey.From(b).GetHashCode());
            Assert.Equal(LibraryTermKey.SpokenComparer.GetHashCode(a), LibraryTermKey.SpokenComparer.GetHashCode(b));
        }
    }

    [Fact]
    public void The_key_is_the_one_0_4_3_composed_libraries_by()
    {
        // DictionaryLibraryComposer's de-duplication before this version: Pattern.Trim() in an OrdinalIgnoreCase set.
        const string letters = "aAbBgGhHtTuU";
        string[] white = [" ", "  ", "\t", "\u00A0", "\r\n", "\u3000", ""];
        var random = new Random(20260925);
        for (var i = 0; i < 4000; i++)
        {
            var a = Random(random, letters, white);
            var b = random.Next(3) == 0 ? a.ToUpperInvariant() : Random(random, letters, white);
            var legacy = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a.Trim() };

            Assert.Equal(!legacy.Add(b.Trim()), LibraryTermKey.AreSame(a, b));
        }
    }

    [Fact]
    public void A_legacy_row_with_irregular_spacing_never_takes_the_key_of_a_row_that_matches()
    {
        // Review finding A10: library Alpha's "get  hub" (two spaces) never matches dictated text, whose runs of
        // horizontal white space the matcher reduces to one space; Beta's "get hub" does. They must stay two terms, as
        // they were in 0.4.3, or composition would keep Alpha's dead rule and drop the one that works.
        string[] irregular = ["get  hub", "get\thub", "get\u00A0hub", "get\r\nhub", "get \t hub", "get\u3000hub"];
        foreach (var spoken in irregular)
        {
            Assert.False(LibraryTermKey.AreSame(spoken, "get hub"), spoken);
            Assert.False(LibraryTermKey.IsInCommitForm(spoken), spoken);
            Assert.Equal(2, new HashSet<string?>(LibraryTermKey.SpokenComparer) { spoken, "get hub" }.Count);

            // Editing the row commits it in normal form, and then it is the same term as the working row.
            Assert.True(LibraryTermKey.AreSame(LibraryTermKey.Normalize(spoken), "get hub"), spoken);
        }
    }

    [Fact]
    public void Every_shipped_spoken_form_is_in_commit_form()
    {
        // So for every shipped row the key and a collapsing comparison agree, and the key's A10 rule moves no shipped
        // winner: only an older custom file can hold a spoken form with irregular inner white space.
        var irregular = BuiltInDictionaryLibraries.All
            .SelectMany(library => library.Entries
                .Where(entry => !LibraryTermKey.IsInCommitForm(entry.Pattern))
                .Select(entry => $"{library.Id}: a spoken form of {entry.Pattern.Length} characters"))
            .ToList();

        Assert.Empty(irregular);
    }

    [Fact]
    public void The_empty_key_is_the_default_and_the_key_of_nothing()
    {
        Assert.True(LibraryTermKey.Empty.IsEmpty);
        Assert.Equal(string.Empty, default(LibraryTermKey).Value);
        Assert.Equal(LibraryTermKey.Empty, default);
        Assert.Equal(LibraryTermKey.Empty, LibraryTermKey.From(null));
        Assert.Equal(LibraryTermKey.Empty, LibraryTermKey.From(" \t\u00A0\r\n"));
        Assert.NotEqual(LibraryTermKey.Empty, LibraryTermKey.From("a"));
        Assert.True(LibraryTermKey.SpokenComparer.Equals(null, "   "));
        Assert.Equal(LibraryTermKey.SpokenComparer.GetHashCode("   "), LibraryTermKey.SpokenComparer.GetHashCode(string.Empty));
    }

    [Fact]
    public void The_value_keeps_the_spelling_and_the_comparison_ignores_its_case()
    {
        var key = LibraryTermKey.From("  GitHub   Copilot ");

        Assert.Equal("GitHub   Copilot", key.Value);
        Assert.Equal(key, LibraryTermKey.From("github   copilot"));
        Assert.NotEqual(key, LibraryTermKey.From("github copilot"));
        Assert.True(key.Equals((object)LibraryTermKey.From("GITHUB   COPILOT")));
        Assert.False(key.Equals((object)"GitHub   Copilot"));
        Assert.Equal(key.GetHashCode(), LibraryTermKey.From("github   copilot").GetHashCode());
    }

    [Fact]
    public void A_spoken_form_already_in_shape_is_returned_as_it_is()
    {
        var spoken = string.Concat("get", " ", "hub");

        Assert.Same(spoken, LibraryTermKey.Normalize(spoken));
        Assert.Same(spoken, LibraryTermKey.From(spoken).Value);
    }

    [Fact]
    public void ToString_shows_the_shape_and_never_the_words()
    {
        var text = LibraryTermKey.From("north star").ToString();

        Assert.DoesNotContain("north", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("star", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("10", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_set_keyed_by_spoken_form_holds_one_entry_per_term()
    {
        var set = new HashSet<string?>(LibraryTermKey.SpokenComparer)
        {
            "get hub", "Get Hub", " get hub\t", "Get  Hub", "get\thub", "gethub", null, "",
        };

        Assert.Equal(5, set.Count);
        Assert.Contains("GET HUB", set);
        Assert.Contains("GET  HUB", set);
        Assert.Contains("   ", set);
    }

    [Fact]
    public void Committing_matches_the_definition_and_is_idempotent_for_random_spoken_forms()
    {
        // Seeded, so a failure reproduces: letters of both cases, digits, punctuation and every kind of white space
        // the commit form collapses, plus characters that look like white space and are not.
        const string alphabet = "aAbBzZ09-_.'\u00E9\u00C9\u03C3\u03A3 \t\r\n\u00A0\u2003\u3000\u2028\u200B\uFEFF\u180E";
        var random = new Random(20260924);
        for (var i = 0; i < 5000; i++)
        {
            var chars = new char[random.Next(0, 24)];
            for (var j = 0; j < chars.Length; j++)
            {
                chars[j] = alphabet[random.Next(alphabet.Length)];
            }

            var spoken = new string(chars);
            var committed = LibraryTermKey.Normalize(spoken);

            Assert.Equal(Collapsed(spoken), committed);
            Assert.Equal(committed, LibraryTermKey.Normalize(committed));
            Assert.True(LibraryTermKey.IsInCommitForm(committed));
            Assert.Equal(Trimmed(spoken), LibraryTermKey.From(spoken).Value);
            Assert.Equal(string.Equals(spoken, committed, StringComparison.Ordinal), LibraryTermKey.IsInCommitForm(spoken));
        }
    }

    [Fact]
    public void Committed_values_compare_as_a_collapsing_key_would()
    {
        // What the plan's key promised for everything the editor writes: two typed spoken forms that differ only in
        // case or white space become one term once committed, whatever white space they were typed with.
        const string letters = "abcdefghijklmnopqrstuvwxyz";
        string[] runs = [" ", "  ", "\t", " \t ", "\u00A0", "\r\n", "\u3000"];
        var random = new Random(4242);
        for (var i = 0; i < 2000; i++)
        {
            var words = Enumerable.Range(0, random.Next(1, 5))
                .Select(_ => new string(Enumerable.Range(0, random.Next(1, 8)).Select(_ => letters[random.Next(letters.Length)]).ToArray()))
                .ToArray();
            var plain = string.Join(' ', words);
            var varied = runs[random.Next(runs.Length)] +
                string.Join(string.Empty, words.Select((word, index) =>
                    (index == 0 ? string.Empty : runs[random.Next(runs.Length)]) +
                    new string(word.Select(c => random.Next(2) == 0 ? char.ToUpperInvariant(c) : c).ToArray()))) +
                runs[random.Next(runs.Length)];
            var other = Random(random, letters, runs);

            Assert.True(LibraryTermKey.AreSame(plain, LibraryTermKey.Normalize(varied)), $"case {i}");
            Assert.Equal(LibraryTermKey.From(plain).GetHashCode(), LibraryTermKey.From(LibraryTermKey.Normalize(varied)).GetHashCode());
            Assert.Equal(
                string.Equals(Collapsed(varied), Collapsed(other), StringComparison.OrdinalIgnoreCase),
                LibraryTermKey.AreSame(LibraryTermKey.Normalize(varied), LibraryTermKey.Normalize(other)));
        }
    }

    [Fact]
    public void Case_and_edge_white_space_variations_of_a_spoken_form_are_one_term()
    {
        const string letters = "abcdefghijklmnopqrstuvwxyz";
        string[] edges = [string.Empty, " ", "  ", "\t", "\u00A0", "\r\n", "\u3000"];
        var random = new Random(4343);
        for (var i = 0; i < 2000; i++)
        {
            var plain = string.Join(' ', Enumerable.Range(0, random.Next(1, 5))
                .Select(_ => new string(Enumerable.Range(0, random.Next(1, 8)).Select(_ => letters[random.Next(letters.Length)]).ToArray())));
            var varied = edges[random.Next(edges.Length)] +
                new string(plain.Select(c => random.Next(2) == 0 ? char.ToUpperInvariant(c) : c).ToArray()) +
                edges[random.Next(edges.Length)];

            Assert.True(LibraryTermKey.AreSame(plain, varied), $"case {i}");
            Assert.Equal(LibraryTermKey.From(plain).GetHashCode(), LibraryTermKey.From(varied).GetHashCode());
        }
    }

    // The key's definition: string.Trim, which removes exactly the characters char.IsWhiteSpace answers true for.
    private static string Trimmed(string? spoken) => spoken?.Trim() ?? string.Empty;

    // The commit form's definition: trim, then every run of white space to one space. .NET's \s is exactly the set
    // char.IsWhiteSpace answers true for (\f\n\r\t\v, U+0085 and the Unicode separators).
    private static string Collapsed(string? spoken) =>
        spoken is null ? string.Empty : Regex.Replace(spoken.Trim(), @"\s+", " ");

    private static string Random(Random random, string letters, string[] white)
    {
        var parts = Enumerable.Range(0, random.Next(1, 4))
            .Select(_ => new string(Enumerable.Range(0, random.Next(1, 5)).Select(_ => letters[random.Next(letters.Length)]).ToArray()));
        return white[random.Next(white.Length)] +
            string.Join(string.Empty, parts.Select((part, index) => (index == 0 ? string.Empty : white[random.Next(white.Length)]) + part)) +
            white[random.Next(white.Length)];
    }

    private static TheoryData<string?, string, string> Cases(string section)
    {
        var data = new TheoryData<string?, string, string>();
        foreach (var item in Fixture().GetProperty(section).EnumerateArray())
        {
            var input = item.GetProperty("input");
            data.Add(
                input.ValueKind == JsonValueKind.Null ? null : input.GetString(),
                item.GetProperty("expected").GetString()!,
                item.GetProperty("note").GetString()!);
        }

        return data;
    }

    private static JsonElement Fixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var path = Path.Combine(root.FullName, "tests", "fixtures", "libraries", "term-keys.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }
}
