using Scribe.Core.Libraries;
using Scribe.Core.Models;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// <see cref="LibraryMetadata"/>: the rules that keep a managed file's name, category and description readable by 0.4.3
/// (review finding A11), checked against 0.4.3's own reader and writer (<see cref="Legacy043LibraryCsv"/>).
/// </summary>
public sealed class LibraryMetadataTests
{
    private static readonly DictionaryEntry[] Rows =
    [
        new(0, "get hub", "GitHub", true, true),
        new(0, "say \"hi\"", "Hello, world", false, true),
        new(0, "kube", "K8s", true, false),
    ];

    [Fact]
    public void Commit_flattens_control_characters_and_trims()
    {
        Assert.Equal("Team terms", LibraryMetadata.Commit("  Team terms\t"));
        Assert.Equal("Team   terms", LibraryMetadata.Commit("Team\r\n\tterms"));
        Assert.Equal("a b", LibraryMetadata.Commit("a\u0085b"));
        Assert.Equal("a b", LibraryMetadata.Commit("a\u0000b"));
        Assert.Equal("a\u2028b", LibraryMetadata.Commit("a\u2028b"));
        Assert.Equal(string.Empty, LibraryMetadata.Commit(null));
        Assert.Equal(string.Empty, LibraryMetadata.Commit(" \r\n "));
    }

    [Fact]
    public void A_typed_value_may_hold_anything_but_a_double_quote()
    {
        Assert.Equal(LibraryMetadataProblem.DoubleQuote, LibraryMetadata.CheckTyped("Team \"Q3\""));
        Assert.Equal(LibraryMetadataProblem.DoubleQuote, LibraryMetadata.CheckTyped("5\" screens"));
        Assert.Equal(LibraryMetadataProblem.None, LibraryMetadata.CheckTyped("Team, terms,"));
        Assert.Equal(LibraryMetadataProblem.None, LibraryMetadata.CheckTyped("#1 team's terms"));
        Assert.Equal(LibraryMetadataProblem.None, LibraryMetadata.CheckTyped(null));
    }

    [Fact]
    public void A_trailing_comma_and_paired_quotes_read_back_in_0_4_3()
    {
        foreach (var (name, category) in new[] { ("Team,", "Custom"), ("Team \"Q3\"", "Custom"), ("a\"b", "c\"d"), ("Team, terms", "Work, notes,") })
        {
            var file = Legacy043LibraryCsv.Parse(Legacy043LibraryCsv.Export(name, category, null, Rows));

            Assert.True(LibraryMetadata.ReadsBackInOlderVersions(name, category), name);
            Assert.Equal(name, file.Name);
            Assert.Equal(category, file.Category);
            Assert.Empty(file.Errors);
            Assert.Equal(Rows.Length, file.Entries.Count);
        }
    }

    [Fact]
    public void An_unpaired_quote_hides_the_rows_from_0_4_3()
    {
        // Astra's counterexample: one quote in the name opens a quoted field that swallows the column header and every
        // row after it, so 0.4.3 finds no rules in the library at all.
        var file = Legacy043LibraryCsv.Parse(Legacy043LibraryCsv.Export("Team \"Notes", "Custom", null, [Rows[0]]));

        Assert.False(LibraryMetadata.ReadsBackInOlderVersions("Team \"Notes", "Custom"));
        Assert.Empty(file.Entries);
        Assert.Single(file.Errors);
    }

    [Fact]
    public void The_quote_rule_is_exactly_what_0_4_3_reads_back()
    {
        // Seeded property test against 0.4.3's own writer and reader: a header reads back, metadata and every row, if and
        // only if ReadsBackInOlderVersions says so. The alphabet leans on the characters that matter to its CSV reader.
        const string alphabet = "ab \"\",,#:\t'";
        var random = new Random(20260925);
        var outcomes = new int[2];
        for (var i = 0; i < 6000; i++)
        {
            var name = LibraryMetadata.Commit(Random(random, alphabet)) is { Length: > 0 } n ? n : "n";
            var category = LibraryMetadata.Commit(Random(random, alphabet)) is { Length: > 0 } c ? c : "c";
            var description = random.Next(3) == 0 ? null : LibraryMetadata.Commit(Random(random, alphabet));
            var written = string.IsNullOrWhiteSpace(description) ? null : description;

            var file = Legacy043LibraryCsv.Parse(Legacy043LibraryCsv.Export(name, category, written, Rows));
            var readsBack =
                file.Errors.Count == 0 &&
                file.Entries.Count == Rows.Length &&
                file.Entries.Zip(Rows).All(pair => pair.First == pair.Second) &&
                file.Name == name &&
                file.Category == category &&
                file.Description == written;

            Assert.True(
                readsBack == LibraryMetadata.ReadsBackInOlderVersions(name, category, written),
                $"case {i}: {name.Length}, {category.Length}, {written?.Length}");
            outcomes[readsBack ? 1 : 0]++;
        }

        Assert.True(outcomes[0] > 500 && outcomes[1] > 500, $"both outcomes reached: {outcomes[0]}, {outcomes[1]}");
    }

    private static string Random(Random random, string alphabet) =>
        new(Enumerable.Range(0, random.Next(0, 12)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
}
