using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests;

/// <summary>
/// The selection-scoped vocabulary pass. These tests exist because reusing
/// <see cref="TextPostProcessor.ProcessDetailed"/> for a selection corrupts the user's document in
/// two specific ways, and both would be invisible in a dictation test.
/// </summary>
public sealed class SelectionVocabularyTests : IDisposable
{
    private readonly ScribeDatabase _db = ScribeDatabase.CreateInMemory();

    public void Dispose() => _db.Dispose();

    private TextPostProcessor Create(
        IEnumerable<(string Pattern, string Replacement)>? entries = null,
        IEnumerable<(string Phrase, string Template)>? snippets = null)
    {
        var dictionary = new DictionaryRepository(_db);
        foreach (var (pattern, replacement) in entries ?? [])
        {
            dictionary.Add(DictionaryEntry.New(pattern, replacement));
        }

        var snippetRepo = new SnippetRepository(_db);
        var toSave = (snippets ?? []).Select(s => Snippet.New(s.Phrase, s.Template)).ToList();
        if (toSave.Count > 0)
        {
            snippetRepo.SaveAll(toSave);
        }

        return new TextPostProcessor(
            dictionary, NullLogger<TextPostProcessor>.Instance, snippetRepo, libraries: null);
    }

    [Fact]
    public void Applies_dictionary_substitutions_to_a_selection()
    {
        var processor = Create([("scribe app", "Scribe")]);

        var result = processor.ProcessSelection("I use the scribe app every day.");

        Assert.Equal("I use the Scribe every day.", result.Text);
        Assert.Single(result.Replacements);
        Assert.False(result.IsNoOp);
    }

    [Fact]
    public void Reports_a_no_op_when_nothing_matches()
    {
        var processor = Create([("scribe app", "Scribe")]);

        var result = processor.ProcessSelection("Nothing in here matches the dictionary.");

        Assert.True(result.IsNoOp);
        Assert.Equal("Nothing in here matches the dictionary.", result.Text);
    }

    [Fact]
    public void Snippets_are_never_expanded_inside_a_selection()
    {
        // The bug this prevents: ProcessDetailed expands snippets FIRST, across the whole text. Run
        // over a colleague's email that happens to contain a snippet trigger, it would paste the
        // user's own template (a home address, a signature, a phone number) into that document.
        var processor = Create(
            entries: [],
            snippets: [("my address", "1 Privacy Lane, Springfield, 555-0100")]);

        const string Selection = "Please confirm my address before the courier arrives.";

        var result = processor.ProcessSelection(Selection);

        Assert.Equal(Selection, result.Text);
        Assert.DoesNotContain("Privacy Lane", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Replacements, r => r.Kind == TextReplacementKind.Snippet);
    }

    [Fact]
    public void Snippets_still_expand_on_the_normal_dictation_path()
    {
        // Guards against "fixing" the leak above by breaking the feature it belongs to.
        var processor = Create(
            entries: [],
            snippets: [("my address", "1 Privacy Lane, Springfield, 555-0100")]);

        var dictated = processor.ProcessDetailed("please send it to my address");

        Assert.Contains("Privacy Lane", dictated.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Indentation_and_blank_lines_survive()
    {
        // ProcessDetailed calls NormalizeWhitespace unconditionally, which collapses runs of spaces
        // and tabs. In a dictation that is invisible. Over a selected code block or an aligned table
        // it destroys the layout, and it produces no replacement span, so the preview diff would
        // show "no changes" while the document silently reflowed.
        var processor = Create([("scribe app", "Scribe")]);

        const string Selection =
            "Item      Qty\n" +
            "Widget      3\n" +
            "\n" +
            "\tIndented note about the scribe app.";

        var result = processor.ProcessSelection(Selection);

        Assert.Contains("Item      Qty", result.Text, StringComparison.Ordinal);
        Assert.Contains("\n\n", result.Text, StringComparison.Ordinal);
        Assert.Contains("\tIndented note", result.Text, StringComparison.Ordinal);
        Assert.Contains("Scribe", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Leading_and_trailing_whitespace_is_preserved()
    {
        // The cleanup chunker trims, which is invisible in dictation and visible the instant text is
        // written back over a live selection: "...done. Next..." would become "...done.Next...".
        var processor = Create([("foo", "Foo")]);

        var result = processor.ProcessSelection("  foo  ");

        Assert.Equal("  Foo  ", result.Text);
    }

    [Theory]
    [InlineData("Run `scribe app --help` for usage.")]
    [InlineData("See https://example.com/scribe app/docs for details.")]
    [InlineData("Open C:\\tools\\scribe app\\readme.txt now.")]
    public void Code_spans_urls_and_paths_are_left_alone(string selection)
    {
        var processor = Create([("scribe app", "Scribe")]);

        var result = processor.ProcessSelection(selection);

        Assert.Equal(selection, result.Text);
    }

    [Fact]
    public void A_term_outside_a_protected_span_still_gets_fixed()
    {
        var processor = Create([("scribe app", "Scribe")]);

        var result = processor.ProcessSelection("The scribe app writes to `scribe app.log` on exit.");

        Assert.Contains("The Scribe writes", result.Text, StringComparison.Ordinal);
        Assert.Contains("`scribe app.log`", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_selection_is_returned_unchanged()
    {
        var processor = Create([("foo", "Foo")]);

        Assert.Equal(string.Empty, processor.ProcessSelection(string.Empty).Text);
    }

    [Fact]
    public void No_dictionary_means_no_change()
    {
        var processor = Create();

        var result = processor.ProcessSelection("Some text with no rules to apply.");

        Assert.True(result.IsNoOp);
    }
}
