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
        Assert.Contains(all, l => l.Id == "modern-developer-stack");
        Assert.Contains(all, l => l.Id == "data-and-ai");
        Assert.Contains(all, l => l.Id == "dotnet-development");
        Assert.Contains(all, l => l.Id == "data-engineering");
        Assert.Contains(all, l => l.Id == "data-science-machine-learning");
        Assert.Equal(9, all.Count);

        var azure = all.Single(l => l.Id == "microsoft-azure");
        Assert.True(azure.BuiltIn);
        Assert.Equal("Microsoft", azure.Category);
        Assert.Contains(azure.Entries, e => e.Replacement == "APIM");
        Assert.Contains(azure.Entries, e => e.Pattern == "cosmos db" && e.Replacement == "Cosmos DB");
    }

    [Fact]
    public void BuiltIn_libraries_have_complete_metadata_and_meaningful_depth()
    {
        var minimumEntries = new Dictionary<string, int>
        {
            ["data-and-ai"] = 125,
            ["data-engineering"] = 90,
            ["data-science-machine-learning"] = 85,
            ["dotnet-development"] = 85,
            ["github"] = 50,
            ["microsoft-365"] = 95,
            ["microsoft-azure"] = 125,
            ["modern-developer-stack"] = 140,
            ["software-development"] = 165,
        };

        foreach (var library in BuiltInDictionaryLibraries.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(library.Name));
            Assert.False(string.IsNullOrWhiteSpace(library.Category));
            Assert.False(string.IsNullOrWhiteSpace(library.Description));
            Assert.True(library.Entries.Count >= minimumEntries[library.Id],
                $"Library '{library.Id}' has only {library.Entries.Count} entries.");
        }
    }

    [Fact]
    public void BuiltIn_library_resources_parse_without_errors()
    {
        var assembly = typeof(BuiltInDictionaryLibraries).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".PostProcessing.Libraries.", StringComparison.Ordinal) &&
                           name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(9, resources.Count);
        foreach (var resource in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            var parsed = DictionaryLibraryCsv.Parse(reader.ReadToEnd());

            Assert.True(parsed.Errors.Count == 0,
                $"Resource '{resource}' has CSV errors: {string.Join("; ", parsed.Errors)}");
        }
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
    public void BuiltIn_libraries_use_consistent_replacements_for_overlapping_patterns()
    {
        var conflicts = BuiltInDictionaryLibraries.All
            .SelectMany(library => library.Entries.Select(entry => (library.Id, Entry: entry)))
            .GroupBy(item => item.Entry.Pattern.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group
                .Select(item => item.Entry.Replacement.Trim())
                .Distinct(StringComparer.Ordinal)
                .Count() > 1)
            .Select(group =>
                $"{group.Key}: {string.Join(", ", group.Select(item => $"{item.Id}={item.Entry.Replacement}"))}")
            .ToList();

        Assert.True(conflicts.Count == 0,
            $"Overlapping library patterns disagree: {string.Join("; ", conflicts)}");
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

    [Fact]
    public void Specialized_libraries_avoid_ambiguous_bare_english_patterns()
    {
        var forbiddenByLibrary = new Dictionary<string, string[]>
        {
            ["dotnet-development"] = ["maui", "razor", "polly", "rider"],
            ["data-engineering"] = ["airflow", "spark", "beam", "iceberg", "prefect", "athena", "redshift"],
            ["data-science-machine-learning"] = ["lime", "lightning", "ray", "r", "torch"],
        };

        foreach (var (libraryId, forbiddenPatterns) in forbiddenByLibrary)
        {
            var library = BuiltInDictionaryLibraries.All.Single(l => l.Id == libraryId);
            foreach (var pattern in forbiddenPatterns)
            {
                Assert.DoesNotContain(library.Entries, entry =>
                    string.Equals(entry.Pattern, pattern, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public void Modern_developer_stack_avoids_ambiguous_bare_english_patterns()
    {
        var library = BuiltInDictionaryLibraries.All.Single(l => l.Id == "modern-developer-stack");
        var forbidden = new[]
        {
            "go", "react", "rust", "swift", "bun", "cursor", "warp", "render", "railway",
            "postman", "playwright", "prettier",
        };

        foreach (var pattern in forbidden)
        {
            Assert.DoesNotContain(library.Entries, entry =>
                string.Equals(entry.Pattern, pattern, StringComparison.OrdinalIgnoreCase));
        }
    }
}
