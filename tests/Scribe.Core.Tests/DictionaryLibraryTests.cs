using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class DictionaryLibraryTests
{
    // --- CSV metadata + entries ------------------------------------------------------------

    [Fact]
    public void Parse_reads_metadata_header_and_entries()
    {
        var csv =
            """
            # name: My Library
            # category: Testing
            # description: A sample library
            pattern,replacement,whole_word,enabled
            a p i m,APIM,true,true
            cosmos db,Cosmos DB
            """;

        var file = DictionaryLibraryCsv.Parse(csv);

        Assert.Equal("My Library", file.Name);
        Assert.Equal("Testing", file.Category);
        Assert.Equal("A sample library", file.Description);
        Assert.Equal(2, file.Entries.Count);
        Assert.Equal("a p i m", file.Entries[0].Pattern);
        Assert.Equal("APIM", file.Entries[0].Replacement);
        Assert.Empty(file.Errors);
    }

    [Fact]
    public void Parse_without_header_returns_null_metadata_but_keeps_entries()
    {
        var file = DictionaryLibraryCsv.Parse("azure,Azure\nfoundry,Foundry\n");

        Assert.Null(file.Name);
        Assert.Null(file.Category);
        Assert.Null(file.Description);
        Assert.Equal(2, file.Entries.Count);
    }

    [Fact]
    public void Export_then_parse_round_trips_metadata_and_entries()
    {
        var library = new DictionaryLibrary(
            "test-lib", "Test Lib", "Testing", "A description",
            BuiltIn: false, [DictionaryEntry.New("a p i m", "APIM"), DictionaryEntry.New("aks", "AKS")]);

        var parsed = DictionaryLibraryCsv.Parse(DictionaryLibraryCsv.Export(library));

        Assert.Equal("Test Lib", parsed.Name);
        Assert.Equal("Testing", parsed.Category);
        Assert.Equal("A description", parsed.Description);
        Assert.Equal(2, parsed.Entries.Count);
        Assert.Equal("APIM", parsed.Entries[0].Replacement);
    }

    // --- Composition -----------------------------------------------------------------------

    [Fact]
    public void Merge_lets_the_base_dictionary_win_on_a_conflicting_pattern()
    {
        var baseEntries = new[] { DictionaryEntry.New("azure", "Azure-user") };
        var libraryEntries = new[]
        {
            DictionaryEntry.New("azure", "Azure-library"),
            DictionaryEntry.New("aks", "AKS"),
        };

        var merged = DictionaryLibraryComposer.Merge(baseEntries, libraryEntries);

        Assert.Equal(2, merged.Count);
        Assert.Equal("Azure-user", merged.Single(e => e.Pattern == "azure").Replacement);
        Assert.Contains(merged, e => e.Pattern == "aks");
    }

    [Fact]
    public void ComposeLibraries_dedupes_first_wins_and_skips_disabled_entries()
    {
        var first = new DictionaryLibrary("l1", "L1", "c", null, false,
        [
            DictionaryEntry.New("apim", "APIM"),
            DictionaryEntry.New("aks", "AKS") with { Enabled = false },
        ]);
        var second = new DictionaryLibrary("l2", "L2", "c", null, false,
        [
            DictionaryEntry.New("apim", "APIM-second"),
            DictionaryEntry.New("acr", "ACR"),
        ]);

        var composed = DictionaryLibraryComposer.ComposeLibraries([first, second]);

        Assert.Equal("APIM", composed.Single(e => e.Pattern == "apim").Replacement); // first wins
        Assert.DoesNotContain(composed, e => e.Pattern == "aks"); // disabled dropped
        Assert.Contains(composed, e => e.Pattern == "acr");
    }

    // --- Built-in catalog ------------------------------------------------------------------

    [Fact]
    public void BuiltIn_libraries_load_from_embedded_resources()
    {
        var all = BuiltInDictionaryLibraries.All;

        Assert.Contains(all, l => l.Id == "microsoft-azure");
        Assert.Contains(all, l => l.Id == "microsoft-365");
        Assert.Contains(all, l => l.Id == "software-development");
        Assert.Contains(all, l => l.Id == "data-and-ai");

        var azure = all.Single(l => l.Id == "microsoft-azure");
        Assert.True(azure.BuiltIn);
        Assert.Equal("Microsoft", azure.Category);
        Assert.Contains(azure.Entries, e => e.Replacement == "APIM");
        Assert.Contains(azure.Entries, e => e.Pattern == "cosmos db" && e.Replacement == "Cosmos DB");
    }

    [Fact]
    public void BuiltIn_libraries_have_no_duplicate_patterns()
    {
        foreach (var library in BuiltInDictionaryLibraries.All)
        {
            var duplicates = library.Entries
                .GroupBy(e => e.Pattern.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.True(duplicates.Count == 0,
                $"Library '{library.Id}' has duplicate patterns: {string.Join(", ", duplicates)}");
        }
    }

    [Fact]
    public void BuiltIn_libraries_avoid_multiword_no_op_entries()
    {
        // A multi-word phrase mapped to itself (e.g. "resource group" -> "resource group") adds no
        // value and, running after AI cleanup, would strip a legitimate sentence-start capital.
        // Single lowercase tokens mapped to themselves are fine: they normalize casing (NPM -> npm).
        foreach (var library in BuiltInDictionaryLibraries.All)
        {
            foreach (var entry in library.Entries)
            {
                var isSelfMap = string.Equals(
                    entry.Pattern.Trim(), entry.Replacement.Trim(), StringComparison.Ordinal);
                Assert.False(isSelfMap && entry.Pattern.Contains(' '),
                    $"Library '{library.Id}' maps the phrase '{entry.Pattern}' to itself.");
            }
        }
    }
}
