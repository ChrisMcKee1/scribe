using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class PostProcessorTests
{
    private static (TextPostProcessor processor, DictionaryRepository repo, ScribeDatabase db) Create(
        params DictionaryEntry[] seed)
    {
        var db = ScribeDatabase.CreateInMemory();
        var repo = new DictionaryRepository(db);
        if (seed.Length > 0) repo.SeedIfEmpty(seed);
        var processor = new TextPostProcessor(repo, NullLogger<TextPostProcessor>.Instance);
        return (processor, repo, db);
    }

    // Minimal library service that yields a fixed set of enabled entries, for the merge test below.
    private sealed class StubLibraries(params DictionaryEntry[] entries) : IDictionaryLibraryService
    {
        public IReadOnlyList<DictionaryLibrary> GetLibraries() => [];
        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries() => entries;
        public DictionaryLibrary Import(string csv, string? suggestedName) => throw new NotSupportedException();
        public void Remove(string id) => throw new NotSupportedException();
    }

    [Fact]
    public void Process_applies_enabled_library_entries_on_top_of_the_base_dictionary()
    {
        var db = ScribeDatabase.CreateInMemory();
        var repo = new DictionaryRepository(db);
        repo.SeedIfEmpty([DictionaryEntry.New("azure", "Azure")]);
        var libraries = new StubLibraries(
            DictionaryEntry.New("a p i m", "APIM"),
            DictionaryEntry.New("azure", "AZURE-from-library"));
        var processor = new TextPostProcessor(
            repo, NullLogger<TextPostProcessor>.Instance, snippets: null, libraries: libraries);

        using (db)
        {
            // The library term applies, and the base dictionary wins over the library's 'azure' entry.
            Assert.Equal("deploy APIM to Azure", processor.Process("deploy a p i m to azure"));
        }
    }

    [Fact]
    public void Process_applies_whole_word_casing_case_insensitively()
    {
        var (processor, _, db) = Create(
            DictionaryEntry.New("azure", "Azure"),
            DictionaryEntry.New("api", "API"));
        using (db)
        {
            var result = processor.Process("i deployed to azure using the API and azure CLI");
            Assert.Equal("i deployed to Azure using the API and Azure CLI", result);
        }
    }

    [Fact]
    public void Process_whole_word_does_not_match_inside_other_words()
    {
        var (processor, _, db) = Create(DictionaryEntry.New("api", "API"));
        using (db)
        {
            // "rapid" and "therapist" contain "api" but must not be altered.
            var result = processor.Process("the therapist was rapid");
            Assert.Equal("the therapist was rapid", result);
        }
    }

    [Fact]
    public void Process_substitutes_multi_word_phrases()
    {
        var (processor, _, db) = Create(DictionaryEntry.New("re back", "ReBAC"));
        using (db)
        {
            var result = processor.Process("configure re back rules now");
            Assert.Equal("configure ReBAC rules now", result);
        }
    }

    [Fact]
    public void Process_non_whole_word_rule_matches_inside_words()
    {
        var (processor, _, db) = Create(DictionaryEntry.New("foo", "bar", wholeWord: false));
        using (db)
        {
            Assert.Equal("barbaz", processor.Process("foobaz"));
        }
    }

    [Fact]
    public void Process_treats_replacement_as_literal_text()
    {
        // A replacement containing "$" must not be interpreted as a regex substitution group.
        var (processor, _, db) = Create(DictionaryEntry.New("price", "$5"));
        using (db)
        {
            Assert.Equal("the $5", processor.Process("the price"));
        }
    }

    [Fact]
    public void Process_normalizes_whitespace_and_punctuation_spacing()
    {
        var (processor, _, db) = Create();
        using (db)
        {
            var result = processor.Process("  hello   world  ,  done .  ");
            Assert.Equal("hello world, done.", result);
        }
    }

    [Fact]
    public void Process_returns_empty_for_blank_input()
    {
        var (processor, _, db) = Create();
        using (db)
        {
            Assert.Equal(string.Empty, processor.Process("   "));
            Assert.Equal(string.Empty, processor.Process(null!));
        }
    }

    [Fact]
    public void Process_ignores_disabled_entries()
    {
        var (processor, repo, db) = Create();
        using (db)
        {
            repo.Add(DictionaryEntry.New("azure", "Azure") with { Enabled = false });
            processor.Reload();
            Assert.Equal("azure", processor.Process("azure"));
        }
    }

    [Fact]
    public void Reload_picks_up_newly_added_entries()
    {
        var (processor, repo, db) = Create();
        using (db)
        {
            Assert.Equal("foundry", processor.Process("foundry")); // no rules yet

            repo.Add(DictionaryEntry.New("foundry", "Foundry"));
            processor.Reload();

            Assert.Equal("Foundry", processor.Process("foundry"));
        }
    }

    [Fact]
    public void Process_does_not_double_expand_already_canonical_text()
    {
        // Regression: when AI cleanup is enabled the glossary biases the model to emit "New York",
        // then this deterministic stage runs last. An expansion whose replacement embeds its own
        // pattern ("york" -> "New York") must not fire again and produce "New New York".
        var (processor, _, db) = Create(DictionaryEntry.New("york", "New York"));
        using (db)
        {
            Assert.Equal("I love New York", processor.Process("I love New York"));
        }
    }

    [Fact]
    public void Process_still_expands_raw_pattern_when_replacement_embeds_pattern()
    {
        var (processor, _, db) = Create(DictionaryEntry.New("york", "New York"));
        using (db)
        {
            Assert.Equal("I love New York", processor.Process("I love york"));
        }
    }

    [Fact]
    public void Process_expands_only_the_raw_occurrence_in_mixed_text()
    {
        // A canonical occurrence and a raw one in the same input: leave the canonical alone, expand
        // the raw one — never compounding to "New New York".
        var (processor, _, db) = Create(DictionaryEntry.New("york", "New York"));
        using (db)
        {
            Assert.Equal("New York and New York", processor.Process("New York and york"));
        }
    }

    [Fact]
    public void Process_expands_each_independent_raw_occurrence()
    {
        var (processor, _, db) = Create(DictionaryEntry.New("york", "New York"));
        using (db)
        {
            Assert.Equal("New York and New York", processor.Process("york and york"));
        }
    }
}
