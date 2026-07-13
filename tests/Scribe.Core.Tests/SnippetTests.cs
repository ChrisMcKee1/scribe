using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class SnippetTests
{
    private static (TextPostProcessor Processor, SnippetRepository Snippets, ScribeDatabase Db) Create(
        params Snippet[] seed)
    {
        var db = ScribeDatabase.CreateInMemory();
        var dictionary = new DictionaryRepository(db);
        var snippets = new SnippetRepository(db);
        if (seed.Length > 0)
        {
            snippets.SaveAll(seed);
        }

        var processor = new TextPostProcessor(dictionary, NullLogger<TextPostProcessor>.Instance, snippets);
        return (processor, snippets, db);
    }

    [Fact]
    public void Repository_save_all_round_trips_and_replaces()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SnippetRepository(db);

        repo.SaveAll([Snippet.New("standup", "Yesterday:\nToday:\nBlockers:")]);
        var first = Assert.Single(repo.GetAll());
        Assert.True(first.Id > 0);
        Assert.Equal("Yesterday:\nToday:\nBlockers:", first.Template);

        // Update in place + add + implicit delete semantics, all in one save.
        repo.SaveAll([first with { Template = "changed" }, Snippet.New("sig", "Regards,\nChris")]);
        var all = repo.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Equal("changed", all.Single(s => s.Id == first.Id).Template);

        repo.SaveAll([]);
        Assert.Empty(repo.GetAll());
    }

    [Fact]
    public void Spoken_trigger_expands_to_the_template()
    {
        var (processor, _, db) = Create(Snippet.New("insert my standup update", "Yesterday: -\nToday: -\nBlockers: none"));
        using (db)
        {
            var result = processor.Process("insert my standup update");
            Assert.Equal("Yesterday: -\nToday: -\nBlockers: none", result);
        }
    }

    [Fact]
    public void Process_detailed_reports_the_expanded_snippet()
    {
        var (processor, _, db) = Create(Snippet.New("insert my signature", "Regards,\nChris"));
        using (db)
        {
            var result = processor.ProcessDetailed("insert my signature");
            var replacement = Assert.Single(result.Replacements);

            Assert.Equal(TextReplacementKind.Snippet, replacement.Kind);
            Assert.Equal("insert my signature", replacement.Pattern);
            Assert.Equal(result.Text, result.Text.Substring(replacement.Start, replacement.Length));
        }
    }

    [Fact]
    public void Template_preserves_tabs_indentation_and_repeated_spaces()
    {
        const string template = "Header:\n\tfirst  column\n    indented";
        var (processor, _, db) = Create(Snippet.New("formatted block", template));
        using (db)
        {
            Assert.Equal(template, processor.Process("formatted block"));
        }
    }

    [Fact]
    public void Trigger_matches_case_insensitively_and_swallows_trailing_punctuation()
    {
        // AI cleanup capitalizes and adds a period; the trigger must still fire, and the period
        // must not survive as a stray character after the template.
        var (processor, _, db) = Create(Snippet.New("insert my signature", "Regards,\nChris"));
        using (db)
        {
            var result = processor.Process("Insert my signature.");
            Assert.Equal("Regards,\nChris", result);
        }
    }

    [Fact]
    public void Trigger_expands_inline_within_a_longer_dictation()
    {
        var (processor, _, db) = Create(Snippet.New("insert my signature", "Regards, Chris"));
        using (db)
        {
            var result = processor.Process("Sounds good. insert my signature");
            Assert.Equal("Sounds good. Regards, Chris", result);
        }
    }

    [Fact]
    public void Disabled_snippets_do_not_expand()
    {
        var (processor, _, db) = Create(Snippet.New("magic words", "EXPANDED") with { Enabled = false });
        using (db)
        {
            Assert.Equal("say the magic words now", processor.Process("say the magic words now"));
        }
    }

    [Fact]
    public void Template_dollar_signs_are_literal_not_regex_substitutions()
    {
        var (processor, _, db) = Create(Snippet.New("price line", "Total: $1 (final)"));
        using (db)
        {
            Assert.Equal("Total: $1 (final)", processor.Process("price line"));
        }
    }

    [Fact]
    public void Template_text_is_canonicalized_by_dictionary_rules()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var dictionary = new DictionaryRepository(db);
        dictionary.SeedIfEmpty([DictionaryEntry.New("azure", "Azure")]);
        var snippets = new SnippetRepository(db);
        snippets.SaveAll([Snippet.New("cloud check", "verify the azure deployment")]);
        var processor = new TextPostProcessor(dictionary, NullLogger<TextPostProcessor>.Instance, snippets);

        // Snippets expand first, so the dictionary then fixes casing inside the template.
        Assert.Equal("verify the Azure deployment", processor.Process("cloud check"));
    }

    [Fact]
    public void Reload_picks_up_newly_saved_snippets()
    {
        var (processor, repo, db) = Create();
        using (db)
        {
            Assert.Equal("hello brb", processor.Process("hello brb"));

            repo.SaveAll([Snippet.New("brb", "be right back")]);
            processor.Reload();

            Assert.Equal("hello be right back", processor.Process("hello brb"));
        }
    }

    [Fact]
    public void Partial_phrase_does_not_trigger()
    {
        var (processor, _, db) = Create(Snippet.New("insert my standup update", "TEMPLATE"));
        using (db)
        {
            Assert.Equal("insert my standup", processor.Process("insert my standup"));
        }
    }
}
