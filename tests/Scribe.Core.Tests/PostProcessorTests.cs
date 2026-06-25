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
    public void DefaultVocabulary_is_valid_and_canonicalizes_terms()
    {
        var (processor, repo, db) = Create();
        using (db)
        {
            var added = repo.SeedIfEmpty(DefaultVocabulary.Entries);
            Assert.Equal(DefaultVocabulary.Entries.Count, added);
            processor.Reload();

            Assert.Equal("Azure Foundry uses ONNX and sherpa-onnx",
                processor.Process("azure foundry uses onnx and sherpa onnx"));
        }
    }
}
