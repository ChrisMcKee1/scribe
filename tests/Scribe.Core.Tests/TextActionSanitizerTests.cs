using Scribe.Core.TextActions;

namespace Scribe.Core.Tests;

public class TextActionSanitizerTests
{
    private static TextAction Similar => TextActionCatalog.Find("improve-writing")!;

    private static TextAction Shorter => TextActionCatalog.Find("make-concise")!;

    private static TextAction Grammar => TextActionCatalog.Find("fix-grammar")!;

    private static TextAction Restructure => TextActionCatalog.Find("rewrite-for-ai")!;

    [Fact]
    public void Accepts_a_reasonable_rewrite()
    {
        var result = TextActionSanitizer.Sanitize(
            "We shipped the build on Thursday.", "we shipped the build thursday", Similar);

        Assert.True(result.Accepted);
        Assert.Equal("We shipped the build on Thursday.", result.Text);
    }

    [Fact]
    public void Empty_answer_is_rejected_and_returns_the_original()
    {
        var result = TextActionSanitizer.Sanitize("   ", "the original text", Similar);

        Assert.False(result.Accepted);
        Assert.Equal("the original text", result.Text);
        Assert.Equal(TextActionSanitizer.RejectionReason.Empty, result.Reason);
    }

    [Fact]
    public void A_refusal_is_rejected_rather_than_written_into_the_document()
    {
        var result = TextActionSanitizer.Sanitize(
            "I'm sorry, but I cannot assist with that request.",
            "Some perfectly ordinary paragraph of text that the user selected on screen.",
            Similar);

        Assert.False(result.Accepted);
        Assert.Equal(TextActionSanitizer.RejectionReason.Refused, result.Reason);
    }

    [Fact]
    public void Answering_the_selection_instead_of_rewriting_it_trips_the_length_floor()
    {
        // The classic small-model failure: the selection is a question, the model answers it.
        var original = "Could you please make sure the deployment pipeline is green before you merge this?";

        var result = TextActionSanitizer.Sanitize("Yes.", original, Similar);

        Assert.False(result.Accepted);
        Assert.Equal(TextActionSanitizer.RejectionReason.TooShort, result.Reason);
        Assert.Equal(original, result.Text);
    }

    [Fact]
    public void Ramble_is_rejected()
    {
        var result = TextActionSanitizer.Sanitize(
            new string('x', 400), "short input", Similar);

        Assert.False(result.Accepted);
        Assert.Equal(TextActionSanitizer.RejectionReason.TooLong, result.Reason);
    }

    [Fact]
    public void Shorter_action_allows_a_genuinely_short_answer()
    {
        // The cleanup pipeline's single band would have accepted this too, but the point is that the
        // SAME answer must be rejected for a same-length action and accepted for a shortening one.
        var original = new string('a', 400);
        var candidate = new string('b', 60);

        Assert.True(TextActionSanitizer.Sanitize(candidate, original, Shorter).Accepted);
        Assert.False(TextActionSanitizer.Sanitize(candidate, original, Similar).Accepted);
    }

    [Fact]
    public void Restructure_action_allows_a_large_expansion()
    {
        var original = "fix the login bug and add a test";
        var candidate = new string('y', original.Length * 5);

        Assert.True(TextActionSanitizer.Sanitize(candidate, original, Restructure).Accepted);
        Assert.False(TextActionSanitizer.Sanitize(candidate, original, Similar).Accepted);
    }

    [Fact]
    public void Runaway_generation_is_rejected_even_for_restructure()
    {
        var original = "short";
        var candidate = new string('z', 5000);

        var result = TextActionSanitizer.Sanitize(candidate, original, Restructure);

        Assert.False(result.Accepted);
        Assert.Equal(TextActionSanitizer.RejectionReason.TooLong, result.Reason);
    }

    [Fact]
    public void Dashes_the_user_already_had_are_preserved()
    {
        // This is the whole reason the cleanup sanitizer cannot be reused. DashNormalizer rewrites a
        // dash between digits into the word "to", so running it over the user's own document would
        // silently change "pages 3-7" (written with an en dash) into "pages 3 to 7".
        const string Original = "See pages 3–7 of the specification for the full table.";
        const string Candidate = "See pages 3–7 of the spec for the full table.";

        var result = TextActionSanitizer.Sanitize(Candidate, Original, Grammar);

        Assert.True(result.Accepted);
        Assert.Contains('–', result.Text);
        Assert.DoesNotContain(" to ", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Dashes_the_model_invented_are_normalized_away()
    {
        const string Original = "we shipped it on thursday and everyone was happy";
        const string Candidate = "We shipped it on Thursday — and everyone was happy.";

        var result = TextActionSanitizer.Sanitize(Candidate, Original, Similar);

        Assert.True(result.Accepted);
        Assert.DoesNotContain('—', result.Text);
    }

    [Fact]
    public void A_model_that_adds_one_dash_to_text_that_already_had_one_is_normalized()
    {
        var original = "First point — second point.";
        var candidate = "First point — second point — third point.";

        var result = TextActionSanitizer.Sanitize(candidate, original, Similar);

        Assert.True(result.Accepted);
        Assert.True(
            result.Text.Count(c => c == '—') <= original.Count(c => c == '—'),
            "the answer must not carry more dashes than the selection did");
    }

    [Fact]
    public void A_wrapping_code_fence_is_stripped()
    {
        var result = TextActionSanitizer.Sanitize(
            "```\nWe shipped the build on Thursday.\n```",
            "we shipped the build thursday",
            Similar);

        Assert.True(result.Accepted);
        Assert.Equal("We shipped the build on Thursday.", result.Text);
    }

    [Fact]
    public void Inner_code_fences_survive_for_a_markdown_conversion()
    {
        var markdown = TextActionCatalog.Find("format-markdown")!;
        var candidate = "Run this:\n\n```bash\ndotnet build\n```\n\nThen check the log.";

        var result = TextActionSanitizer.Sanitize(candidate, "run dotnet build then check the log", markdown);

        Assert.True(result.Accepted);
        Assert.Contains("```bash", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Echoed_delimiters_are_stripped()
    {
        var result = TextActionSanitizer.Sanitize(
            "<text>\nWe shipped the build on Thursday.\n</text>",
            "we shipped the build thursday",
            Similar);

        Assert.True(result.Accepted);
        Assert.DoesNotContain("<text>", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</text>", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wrapping_quotes_are_stripped_unless_the_original_was_quoted()
    {
        var unquoted = TextActionSanitizer.Sanitize(
            "\"We shipped it on Thursday.\"", "we shipped it thursday", Similar);
        Assert.Equal("We shipped it on Thursday.", unquoted.Text);

        var quoted = TextActionSanitizer.Sanitize(
            "\"We shipped it on Thursday.\"", "\"we shipped it thursday\"", Similar);
        Assert.StartsWith("\"", quoted.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_rejection_reason_has_a_user_facing_sentence_without_dashes()
    {
        foreach (var reason in Enum.GetValues<TextActionSanitizer.RejectionReason>())
        {
            var text = TextActionSanitizer.Describe(reason);
            if (reason == TextActionSanitizer.RejectionReason.None)
            {
                Assert.Equal(string.Empty, text);
                continue;
            }

            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain('—', text);
            Assert.DoesNotContain('–', text);
        }
    }

    [Fact]
    public void A_rejected_answer_never_returns_model_text()
    {
        // Belt and braces: a caller that ignores Accepted must still be unable to corrupt the document.
        var result = TextActionSanitizer.Sanitize("Yes.", new string('a', 300), Similar);

        Assert.Equal(new string('a', 300), result.Text);
    }

    private static TextAction Json => TextActionCatalog.Find("format-json")!;

    private static TextAction Markdown => TextActionCatalog.Find("format-markdown")!;

    [Fact]
    public void A_json_wrapper_fence_is_stripped_even_when_values_contain_backticks()
    {
        // The data-corruption path this replaces. The old logic decided wrapper versus content by
        // asking whether the body contained a fence, so a json-tagged wrapper whose string values
        // held backticks survived untouched. With no JSON validation the unparseable text was
        // returned as a success, and the palette makes Replace the primary button, so the next Enter
        // committed it into the user's document.
        var candidate = """
            ```json
            {"cmd": "run `build` first"}
            ```
            """;

        var result = TextActionSanitizer.Sanitize(candidate, "run build first, the command is build", Json);

        Assert.True(result.Accepted);
        Assert.DoesNotContain("```", result.Text, StringComparison.Ordinal);
        Assert.StartsWith("{", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tagged_code_fence_is_content_and_survives()
    {
        // The other direction the old heuristic got wrong: an answer that is legitimately one fenced
        // code block had its opening fence and language tag deleted.
        var candidate = """
            ```bash
            dotnet build
            dotnet test
            ```
            """;

        var result = TextActionSanitizer.Sanitize(candidate, "run dotnet build then dotnet test", Markdown);

        Assert.True(result.Accepted);
        Assert.StartsWith("```bash", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_json_is_rejected_rather_than_offered_for_replacement()
    {
        var result = TextActionSanitizer.Sanitize("{ not valid json ", "some text to convert here", Json);

        Assert.False(result.Accepted);
        Assert.Equal(TextActionSanitizer.RejectionReason.InvalidJson, result.Reason);
        Assert.Equal("some text to convert here", result.Text);
    }

    [Fact]
    public void Valid_json_passes()
    {
        var result = TextActionSanitizer.Sanitize(
            """{"attendees":[{"name":"Bob","time":"15:00"}]}""", "bob 3pm", Json);

        Assert.True(result.Accepted);
    }

    [Fact]
    public void A_short_selection_is_not_rejected_for_growing()
    {
        // "bob 3pm sue 4pm" is 15 characters. A correct JSON conversion with inferred keys, quoting
        // and explicit nulls runs past 100, and the old ratio test called that a ramble.
        var candidate =
            """[{"name":"Bob","time":"15:00","note":null},{"name":"Sue","time":"16:00","note":null}]""";

        var result = TextActionSanitizer.Sanitize(candidate, "bob 3pm sue 4pm", Json);

        Assert.True(
            result.Accepted,
            $"a {candidate.Length} character answer to a 15 character selection must pass, got {result.Reason}");
    }

    [Fact]
    public void A_genuine_runaway_on_a_short_selection_is_still_caught()
    {
        var result = TextActionSanitizer.Sanitize(new string('x', 5000), "bob 3pm", Json);

        Assert.False(result.Accepted);
        Assert.Equal(TextActionSanitizer.RejectionReason.TooLong, result.Reason);
    }

    [Theory]
    [InlineData("Ship the build on Thursday and tell Bob about the delay.")]
    [InlineData("1. First step here, then the second one follows after it.")]
    [InlineData("- A bulleted line that starts with punctuation rather than a letter.")]
    [InlineData("**Bold opening** followed by ordinary prose that continues on.")]
    [InlineData("\"A quoted opening that is not a wrapper,\" said the author here.")]
    [InlineData("`code span` opening the line, then some explanatory prose after.")]
    [InlineData("# A heading line that opens the answer for a markdown conversion")]
    [InlineData("<p>An HTML fragment opening with a tag rather than a letter.</p>")]
    public void The_first_character_always_survives_sanitizing(string candidate)
    {
        // A user reported the opening character going missing from every rewrite. The cause was an
        // activation race in the write-back rather than anything here, but the sanitizer strips
        // fences, tags and quotes from the front of the answer, so it is exactly the kind of code
        // that could eat a leading character silently. This pins that it does not.
        var original = "ship the build thursday and tell bob about the delay he needs to know";

        foreach (var action in TextActionCatalog.All.Where(a => a.RequiresModel))
        {
            // The JSON action is excluded: these fixtures are deliberately not JSON, so it correctly
            // rejects them. Its own parse behaviour is covered above.
            if (action.Id == "format-json")
            {
                continue;
            }

            var result = TextActionSanitizer.Sanitize(candidate, original, action);

            Assert.True(result.Accepted, $"{action.Id} rejected the fixture: {result.Reason}");
            Assert.Equal(candidate[0], result.Text[0]);
            Assert.StartsWith(candidate[..8], result.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_quoted_answer_keeps_its_opening_quote_when_the_original_was_quoted()
    {
        // The one place a leading character is removed on purpose, and the guard that scopes it.
        var result = TextActionSanitizer.Sanitize(
            "\"We shipped it Thursday.\"", "\"we shipped it thursday\"", Similar);

        Assert.Equal('"', result.Text[0]);
    }
}
