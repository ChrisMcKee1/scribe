using System.Text.Json;
using System.Text.RegularExpressions;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// <see cref="LibraryTermKey"/>, the one key the editor, the built-in overlay and composition share. The fixture
/// <c>tests/fixtures/libraries/term-keys.json</c> is what the macOS port checks itself against, so it is read here too,
/// and each of its answers is also checked against the definition (trim, collapse, OrdinalIgnoreCase) so it can never
/// drift from what .NET does.
/// </summary>
public sealed class LibraryTermKeyTests
{
    public static TheoryData<string?, string, string> NormalizeCases()
    {
        var data = new TheoryData<string?, string, string>();
        foreach (var item in Fixture().GetProperty("normalize").EnumerateArray())
        {
            var input = item.GetProperty("input");
            data.Add(
                input.ValueKind == JsonValueKind.Null ? null : input.GetString(),
                item.GetProperty("expected").GetString()!,
                item.GetProperty("note").GetString()!);
        }

        return data;
    }

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
    [MemberData(nameof(NormalizeCases))]
    public void Normalizes_as_the_shared_fixture_says(string? input, string expected, string note)
    {
        Assert.True(string.Equals(expected, LibraryTermKey.Normalize(input), StringComparison.Ordinal), note);
        Assert.True(string.Equals(expected, LibraryTermKey.From(input).Value, StringComparison.Ordinal), note);
        Assert.True(string.Equals(expected, Reference(input), StringComparison.Ordinal), $"{note}: the fixture disagrees with the definition");
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
            same == string.Equals(Reference(a), Reference(b), StringComparison.OrdinalIgnoreCase),
            $"{note}: the fixture disagrees with OrdinalIgnoreCase");
        if (same)
        {
            Assert.Equal(LibraryTermKey.From(a).GetHashCode(), LibraryTermKey.From(b).GetHashCode());
            Assert.Equal(LibraryTermKey.SpokenComparer.GetHashCode(a), LibraryTermKey.SpokenComparer.GetHashCode(b));
        }
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

        Assert.Equal("GitHub Copilot", key.Value);
        Assert.Equal(key, LibraryTermKey.From("github copilot"));
        Assert.True(key.Equals((object)LibraryTermKey.From("GITHUB\tCOPILOT")));
        Assert.False(key.Equals((object)"GitHub Copilot"));
        Assert.Equal(key.GetHashCode(), LibraryTermKey.From("github copilot").GetHashCode());
    }

    [Fact]
    public void A_spoken_form_already_in_key_form_is_returned_as_it_is()
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
        var set = new HashSet<string?>(LibraryTermKey.SpokenComparer) { "get hub", "Get  Hub", " get\thub ", "gethub", null, "" };

        Assert.Equal(3, set.Count);
        Assert.Contains("GET HUB", set);
        Assert.Contains("   ", set);
    }

    [Fact]
    public void Normalizing_matches_the_definition_and_is_idempotent_for_random_spoken_forms()
    {
        // Seeded, so a failure reproduces: letters of both cases, digits, punctuation and every kind of white space
        // the key collapses, plus characters that look like white space and are not.
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
            var normalized = LibraryTermKey.Normalize(spoken);

            Assert.Equal(Reference(spoken), normalized);
            Assert.Equal(normalized, LibraryTermKey.Normalize(normalized));
            Assert.Equal(LibraryTermKey.From(spoken), LibraryTermKey.From(normalized));
        }
    }

    [Fact]
    public void Case_and_white_space_variations_of_a_spoken_form_are_one_term()
    {
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

            Assert.True(LibraryTermKey.AreSame(plain, varied), $"case {i}");
            Assert.Equal(LibraryTermKey.From(plain).GetHashCode(), LibraryTermKey.From(varied).GetHashCode());
        }
    }

    // The definition the key implements: trim, then every run of white space to one space. .NET's \s is exactly the
    // set char.IsWhiteSpace answers true for (\f\n\r\t\v, U+0085 and the Unicode separators), and string.Trim uses it.
    private static string Reference(string? spoken) =>
        spoken is null ? string.Empty : Regex.Replace(spoken.Trim(), @"\s+", " ");

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
