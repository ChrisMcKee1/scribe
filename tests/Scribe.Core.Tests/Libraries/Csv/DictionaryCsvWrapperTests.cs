using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests.Libraries.Csv;

/// <summary>
/// <see cref="DictionaryCsv"/> and <see cref="DictionaryLibraryCsv"/> are thin wrappers over the shared record reader
/// and field writer (pattern P-2, acceptance X-8): their public signatures and their own tests are unchanged, they parse
/// every text exactly as 0.4.3 did, error messages included, and the built-in libraries load as they always have.
/// </summary>
public sealed class DictionaryCsvWrapperTests
{
    [Fact]
    public void DictionaryLibraryCsv_parses_every_text_exactly_as_0_4_3_did()
    {
        var random = new Random(20261001);
        for (var i = 0; i < 3000; i++)
        {
            var text = LibraryCsvManagedTests.RandomLegacyFile(random);

            var actual = DictionaryLibraryCsv.Parse(text);
            var expected = Legacy043LibraryCsv.Parse(text);

            Assert.True(expected.Name == actual.Name, $"case {i}: name");
            Assert.True(expected.Category == actual.Category, $"case {i}: category");
            Assert.True(expected.Description == actual.Description, $"case {i}: description");
            Assert.True(expected.Entries.SequenceEqual(actual.Entries), $"case {i}: entries");
            Assert.True(expected.Errors.SequenceEqual(actual.Errors), $"case {i}: errors");
        }
    }

    [Fact]
    public void DictionaryCsv_parses_every_text_exactly_as_0_4_3_did()
    {
        var random = new Random(20261002);
        for (var i = 0; i < 3000; i++)
        {
            var text = LibraryCsvManagedTests.RandomLegacyFile(random);

            var actual = DictionaryCsv.Parse(text);
            var expected = Legacy043LibraryCsv.Parse(text);

            Assert.True(expected.Entries.SequenceEqual(actual.Entries), $"case {i}: entries");
            Assert.True(expected.Errors.SequenceEqual(actual.Errors), $"case {i}: errors");
        }

        Assert.Empty(DictionaryCsv.Parse(null).Entries);
        Assert.Empty(DictionaryCsv.Parse(" \r\n ").Errors);
    }

    [Fact]
    public void What_the_wrappers_export_reads_back_in_0_4_3_as_0_4_3s_own_exports_did()
    {
        // The shared writer quotes a few more values than 0.4.3's did (a first field starting with "#" or reading as the
        // header, white space at an edge), and 0.4.3's reader cannot tell: it skips and trims those rows exactly as before.
        const string metadata = "ab ,#\u00E9\"";
        var random = new Random(20261003);
        for (var i = 0; i < 2000; i++)
        {
            var name = CsvTestData.NonBlank(random, metadata);
            var category = CsvTestData.NonBlank(random, metadata);
            var description = random.Next(3) == 0 ? null : CsvTestData.Random(random, metadata, 10);
            if (!LibraryMetadata.ReadsBackInOlderVersions(name, category, description))
            {
                category += "\"";
            }

            var entries = Enumerable.Range(0, random.Next(0, 8))
                .Select(_ => CsvTestData.Term(random, CsvTestData.Adversarial).ToEntry())
                .ToList();
            var library = new DictionaryLibrary("custom-test", name, category, description, BuiltIn: false, entries);

            var ours = Legacy043LibraryCsv.Parse(DictionaryLibraryCsv.Export(library));
            var theirs = Legacy043LibraryCsv.Parse(Legacy043LibraryCsv.Export(name, category, description, entries));
            var ourRows = DictionaryCsv.Parse(DictionaryCsv.Export(entries));

            Assert.True(theirs.Name == ours.Name && theirs.Category == ours.Category && theirs.Description == ours.Description, $"case {i}: metadata");
            Assert.True(theirs.Entries.SequenceEqual(ours.Entries), $"case {i}: entries");
            Assert.True(theirs.Errors.SequenceEqual(ours.Errors), $"case {i}: errors");
            Assert.True(theirs.Entries.SequenceEqual(ourRows.Entries), $"case {i}: dictionary rows");
            Assert.Empty(ourRows.Errors);
        }
    }

    [Fact]
    public void The_built_in_libraries_load_exactly_as_0_4_3_loaded_them()
    {
        var byId = BuiltInDictionaryLibraries.All.ToDictionary(l => l.Id, StringComparer.Ordinal);
        foreach (var (resource, bytes) in CsvTestData.BuiltInCsvs())
        {
            var id = resource["Scribe.Core.PostProcessing.Libraries.".Length..^".csv".Length];
            var expected = Legacy043LibraryCsv.Parse(CsvTestData.ReadAllTextOf(bytes));
            var library = byId[id];

            Assert.Equal(expected.Name, library.Name);
            Assert.Equal(expected.Category, library.Category);
            Assert.Equal(expected.Description, library.Description);
            Assert.Equal(expected.Entries, library.Entries);
            Assert.Empty(expected.Errors);
        }

        Assert.Equal(11, byId.Count);
    }

    [Fact]
    public void A_dictionary_export_quotes_values_a_strict_reader_would_misread()
    {
        DictionaryEntry[] entries = [DictionaryEntry.New("#tag", "Tag"), DictionaryEntry.New("pattern", "Pattern"), DictionaryEntry.New("a,b", "#c")];

        Assert.Equal(
            DictionaryCsv.Header + Environment.NewLine + "\"#tag\",Tag,true,true" + Environment.NewLine +
            "\"pattern\",Pattern,true,true" + Environment.NewLine + "\"a,b\",#c,true,true" + Environment.NewLine,
            DictionaryCsv.Export(entries));
        Assert.Equal(["a,b"], DictionaryCsv.Parse(DictionaryCsv.Export(entries)).Entries.Select(e => e.Pattern));
    }
}
