using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// The quick "Add to dictionary" popup reached from the tray. The value of this feature is that the
/// spoken form comes from the recognizer's own output rather than being retyped from memory: a rule
/// whose pattern has a typo silently never fires, and the user has no feedback telling them why. So
/// the selection logic here is load-bearing, not cosmetic.
/// </summary>
public sealed class QuickDictionaryAddTests
{
    private static readonly DictionaryEntry[] Empty = [];

    [Fact]
    public void Tokenize_returns_nothing_for_blank_input()
    {
        Assert.Empty(QuickDictionaryAdd.Tokenize(null));
        Assert.Empty(QuickDictionaryAdd.Tokenize("   \r\n "));
    }

    [Fact]
    public void Tokenize_keeps_punctuation_attached_so_chips_read_like_the_dictation()
    {
        var tokens = QuickDictionaryAdd.Tokenize("Open cloud pilot, then run it.");

        Assert.Equal(
            ["Open", "cloud", "pilot,", "then", "run", "it."],
            tokens.Select(t => t.Text));
    }

    [Fact]
    public void Tokenize_spans_point_back_at_the_original_text()
    {
        const string transcript = "alpha beta";
        var tokens = QuickDictionaryAdd.Tokenize(transcript);

        foreach (var token in tokens)
        {
            Assert.Equal(token.Text, transcript.Substring(token.Start, token.Length));
        }
    }

    [Fact]
    public void Selecting_one_chip_strips_trailing_sentence_punctuation()
    {
        const string transcript = "Open cloud pilot, then run it.";
        var tokens = QuickDictionaryAdd.Tokenize(transcript);

        Assert.Equal("pilot", QuickDictionaryAdd.Select(transcript, tokens, 2, 2));
    }

    /// <summary>
    /// The whole point of dragging across chips: the misrecognition is usually a phrase, and the
    /// punctuation *between* the words has to survive even though the outer punctuation does not.
    /// </summary>
    [Fact]
    public void Selecting_a_range_keeps_inner_punctuation_and_drops_the_outer()
    {
        const string transcript = "It said \"cloud pilot, apparently\" again.";
        var tokens = QuickDictionaryAdd.Tokenize(transcript);

        Assert.Equal("cloud pilot, apparently", QuickDictionaryAdd.Select(transcript, tokens, 2, 4));
    }

    [Fact]
    public void Selecting_right_to_left_gives_the_same_text()
    {
        const string transcript = "one two three";
        var tokens = QuickDictionaryAdd.Tokenize(transcript);

        Assert.Equal(
            QuickDictionaryAdd.Select(transcript, tokens, 0, 2),
            QuickDictionaryAdd.Select(transcript, tokens, 2, 0));
    }

    /// <summary>
    /// A pattern containing a newline can never match: the post-processor collapses only horizontal
    /// whitespace and keeps CR/LF, so the break is still there in its input. The selection therefore
    /// has to preserve the break rather than flatten it, so Build can reject the rule outright
    /// instead of saving one that is dead on arrival.
    /// </summary>
    [Fact]
    public void Selection_spanning_a_line_break_keeps_the_break_so_it_can_be_rejected()
    {
        const string transcript = "first line\r\n\r\nsecond line";
        var tokens = QuickDictionaryAdd.Tokenize(transcript);

        var selected = QuickDictionaryAdd.Select(transcript, tokens, 0, 3);

        Assert.Contains('\n', selected);

        var plan = QuickDictionaryAdd.Build(selected, "anything", wholeWord: true, Empty);

        Assert.Equal(QuickDictionaryAdd.PlanKind.Invalid, plan.Kind);
        Assert.Contains("line break", plan.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Horizontal runs still collapse, matching TextPostProcessor.NormalizeWhitespace exactly, so a
    /// pattern taken from a transcript is a literal substring of what the matcher sees.
    /// </summary>
    [Fact]
    public void Selection_collapses_horizontal_whitespace_runs()
    {
        const string transcript = "cloud   pilot writes";
        var tokens = QuickDictionaryAdd.Tokenize(transcript);

        Assert.Equal("cloud pilot", QuickDictionaryAdd.Select(transcript, tokens, 0, 1));
    }

    /// <summary>
    /// The dictionary runs one pass over the original transcript, so a pattern that is some other
    /// enabled rule's replacement is unreachable. The popup shows finished text, which is precisely
    /// where those replacements appear, so this is easy to walk into and must be refused loudly.
    /// </summary>
    [Fact]
    public void A_pattern_another_rule_already_produces_is_rejected_as_unreachable()
    {
        var existing = new[] { new DictionaryEntry(7, "teams", "Microsoft Teams") };

        var plan = QuickDictionaryAdd.Build("Microsoft Teams", "Teams", wholeWord: true, existing);

        Assert.Equal(QuickDictionaryAdd.PlanKind.Invalid, plan.Kind);
        Assert.Contains("never apply", plan.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("teams", plan.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A disabled rule produces nothing, so it cannot make a pattern unreachable.</summary>
    [Fact]
    public void A_disabled_rules_replacement_does_not_block_a_new_pattern()
    {
        var existing = new[]
        {
            new DictionaryEntry(7, "teams", "Microsoft Teams", WholeWord: true, Enabled: false),
        };

        var plan = QuickDictionaryAdd.Build("Microsoft Teams", "Teams", wholeWord: true, existing);

        Assert.Equal(QuickDictionaryAdd.PlanKind.Create, plan.Kind);
    }

    /// <summary>
    /// A rule whose replacement equals its own pattern (a casing fix like "github" to "GitHub" seen
    /// case-insensitively) must not report itself as the blocking producer.
    /// </summary>
    [Fact]
    public void A_rule_is_not_treated_as_blocking_its_own_pattern()
    {
        var existing = new[] { new DictionaryEntry(3, "github", "GitHub") };

        var plan = QuickDictionaryAdd.Build("GitHub", "GitHub Enterprise", wholeWord: true, existing);

        Assert.Equal(QuickDictionaryAdd.PlanKind.Update, plan.Kind);
    }

    [Fact]
    public void Internal_apostrophes_and_symbol_words_survive_trimming()
    {
        const string transcript = "don't ship C++ or #tags.";
        var tokens = QuickDictionaryAdd.Tokenize(transcript);

        Assert.Equal("don't", QuickDictionaryAdd.Select(transcript, tokens, 0, 0));
        Assert.Equal("C++", QuickDictionaryAdd.Select(transcript, tokens, 2, 2));
        Assert.Equal("#tags", QuickDictionaryAdd.Select(transcript, tokens, 4, 4));
    }

    [Fact]
    public void Selecting_pure_punctuation_returns_it_rather_than_clearing_the_box()
    {
        const string transcript = "well ... maybe";
        var tokens = QuickDictionaryAdd.Tokenize(transcript);

        Assert.Equal("...", QuickDictionaryAdd.Select(transcript, tokens, 1, 1));
    }

    [Fact]
    public void Out_of_range_indices_are_clamped_rather_than_throwing()
    {
        const string transcript = "one two";
        var tokens = QuickDictionaryAdd.Tokenize(transcript);

        Assert.Equal("one two", QuickDictionaryAdd.Select(transcript, tokens, -5, 99));
        Assert.Equal(string.Empty, QuickDictionaryAdd.Select(transcript, [], 0, 0));
        Assert.Equal(string.Empty, QuickDictionaryAdd.Select(null, tokens, 0, 0));
    }

    [Fact]
    public void Blank_spoken_form_cannot_be_saved()
    {
        var plan = QuickDictionaryAdd.Build("   ", "Copilot", wholeWord: true, Empty);

        Assert.Equal(QuickDictionaryAdd.PlanKind.Invalid, plan.Kind);
        Assert.False(plan.CanSave);
        Assert.Null(plan.Entry);
    }

    [Fact]
    public void A_rule_that_rewrites_text_to_itself_cannot_be_saved()
    {
        var plan = QuickDictionaryAdd.Build("Copilot", " Copilot ", wholeWord: true, Empty);

        Assert.Equal(QuickDictionaryAdd.PlanKind.Invalid, plan.Kind);
    }

    /// <summary>
    /// Capitalisation fixes are the single most common real rule, so a case-only difference has to
    /// stay saveable even though the trimmed strings look equal case-insensitively.
    /// </summary>
    [Fact]
    public void A_case_only_correction_is_a_real_rule()
    {
        var plan = QuickDictionaryAdd.Build("copilot", "Copilot", wholeWord: true, Empty);

        Assert.Equal(QuickDictionaryAdd.PlanKind.Create, plan.Kind);
        Assert.Equal("copilot", plan.Entry!.Pattern);
        Assert.Equal("Copilot", plan.Entry.Replacement);
    }

    [Fact]
    public void New_spoken_form_creates_an_unsaved_entry()
    {
        var plan = QuickDictionaryAdd.Build("cloud pilot", "Copilot", wholeWord: false, Empty);

        Assert.Equal(QuickDictionaryAdd.PlanKind.Create, plan.Kind);
        Assert.True(plan.CanSave);
        Assert.Equal(0, plan.Entry!.Id);
        Assert.False(plan.Entry.WholeWord);
        Assert.True(plan.Entry.Enabled);
    }

    [Fact]
    public void An_empty_replacement_is_allowed_and_described_as_a_removal()
    {
        var plan = QuickDictionaryAdd.Build("um", "", wholeWord: true, Empty);

        Assert.Equal(QuickDictionaryAdd.PlanKind.Create, plan.Kind);
        Assert.Equal(string.Empty, plan.Entry!.Replacement);

        // The message has to say what will happen without using "delete", which testers read as
        // deleting something they already have rather than dropping a word from future dictations.
        Assert.Contains("leave \"um\" out", plan.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete", plan.Message, StringComparison.OrdinalIgnoreCase);

        // And it must not claim the rule is already stored: this message is shown while the user is
        // still typing, so past tense would let them walk away from an unsaved rule.
        Assert.DoesNotContain("saved", plan.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_pending_plan_never_claims_the_rule_is_already_saved()
    {
        var create = QuickDictionaryAdd.Build("cloud pilot", "Copilot", wholeWord: true, Empty);
        var update = QuickDictionaryAdd.Build(
            "cloud pilot", "Copilot", wholeWord: true, [new DictionaryEntry(1, "cloud pilot", "CoPilot")]);

        Assert.Equal(QuickDictionaryAdd.PlanKind.Create, create.Kind);
        Assert.Equal(QuickDictionaryAdd.PlanKind.Update, update.Kind);
        Assert.DoesNotContain("saved", create.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("saved", update.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Matching the settings grid's case-insensitive duplicate rule matters: creating a second row
    /// for the same spoken form would produce a dictionary the grid refuses to save.
    /// </summary>
    [Fact]
    public void An_existing_spoken_form_updates_in_place_regardless_of_case()
    {
        DictionaryEntry[] existing = [new(7, "Cloud Pilot", "Copilot")];

        var plan = QuickDictionaryAdd.Build("cloud pilot", "GitHub Copilot", wholeWord: true, existing);

        Assert.Equal(QuickDictionaryAdd.PlanKind.Update, plan.Kind);
        Assert.Equal(7, plan.Entry!.Id);
        Assert.Equal("cloud pilot", plan.Entry.Pattern);
        Assert.Equal("GitHub Copilot", plan.Entry.Replacement);
    }

    [Fact]
    public void Updating_re_enables_a_disabled_rule()
    {
        DictionaryEntry[] existing = [new(7, "cloud pilot", "Copilot", WholeWord: true, Enabled: false)];

        var plan = QuickDictionaryAdd.Build("cloud pilot", "Copilot", wholeWord: true, existing);

        Assert.Equal(QuickDictionaryAdd.PlanKind.Update, plan.Kind);
        Assert.True(plan.Entry!.Enabled);
    }

    [Fact]
    public void An_identical_existing_rule_reports_no_change()
    {
        DictionaryEntry[] existing = [new(7, "cloud pilot", "Copilot")];

        var plan = QuickDictionaryAdd.Build("cloud pilot", "Copilot", wholeWord: true, existing);

        Assert.Equal(QuickDictionaryAdd.PlanKind.NoChange, plan.Kind);
        Assert.False(plan.CanSave);
    }

    [Fact]
    public void Changing_only_the_whole_word_flag_still_counts_as_an_update()
    {
        DictionaryEntry[] existing = [new(7, "cloud pilot", "Copilot", WholeWord: true)];

        var plan = QuickDictionaryAdd.Build("cloud pilot", "Copilot", wholeWord: false, existing);

        Assert.Equal(QuickDictionaryAdd.PlanKind.Update, plan.Kind);
        Assert.False(plan.Entry!.WholeWord);
    }

    /// <summary>End to end: the chips a user clicks produce a saveable rule.</summary>
    [Fact]
    public void Chip_selection_feeds_straight_into_a_saveable_plan()
    {
        const string transcript = "I opened cloud pilot, and it worked.";
        var tokens = QuickDictionaryAdd.Tokenize(transcript);

        var spoken = QuickDictionaryAdd.Select(transcript, tokens, 2, 3);
        var plan = QuickDictionaryAdd.Build(spoken, "Copilot", wholeWord: true, Empty);

        Assert.Equal("cloud pilot", spoken);
        Assert.Equal(QuickDictionaryAdd.PlanKind.Create, plan.Kind);
    }

    // Applying the saved rule back over the transcript it came from. The whole point is that the
    // repaired copy equals what the user's very next dictation would produce, so these tests assert
    // the LIVE pipeline's behaviour, not a tidier version of it.

    private static DictionaryEntry Rule(string pattern, string replacement, bool wholeWord = true)
        => new(0, pattern, replacement, wholeWord);

    [Fact]
    public void Apply_replaces_every_occurrence_not_just_the_first()
    {
        var result = QuickDictionaryAdd.Apply(
            "aspire runs it, then aspire hosts it, and aspire wins.",
            Rule("aspire", "Aspire"));

        Assert.Equal("Aspire runs it, then Aspire hosts it, and Aspire wins.", result);
    }

    [Fact]
    public void Apply_honours_whole_word_exactly_like_the_matcher()
    {
        Assert.Equal(
            "Aspire and aspires",
            QuickDictionaryAdd.Apply("aspire and aspires", Rule("aspire", "Aspire")));

        Assert.Equal(
            "Aspire and Aspires",
            QuickDictionaryAdd.Apply("aspire and aspires", Rule("aspire", "Aspire", wholeWord: false)));
    }

    [Fact]
    public void Apply_matches_case_insensitively_like_the_matcher()
    {
        Assert.Equal(
            "ASP.NET and ASP.NET",
            QuickDictionaryAdd.Apply("asp.net and ASP.NET", Rule("asp.net", "ASP.NET", wholeWord: false)));
    }

    /// <summary>
    /// The guard that stops an expansion firing on text that already reads the canonical way. Without
    /// it, saving "york" to "New York" from a transcript that already contains "New York" rewrites it
    /// to "New New York", corrupting a sentence the user never complained about.
    /// </summary>
    [Fact]
    public void Apply_does_not_re_expand_text_that_is_already_canonical()
    {
        Assert.Equal(
            "New York is different from New York",
            QuickDictionaryAdd.Apply("York is different from New York", Rule("York", "New York")));
    }

    /// <summary>
    /// A tight-punctuation replacement absorbs the space in front of it, so "hello comma" becomes
    /// "hello," and not "hello ,". This lives in the live single pass, not in whitespace tidy-up, and
    /// it is the reason this method delegates instead of reimplementing.
    /// </summary>
    [Fact]
    public void Apply_absorbs_the_space_before_a_punctuation_replacement()
    {
        Assert.Equal("hello, world", QuickDictionaryAdd.Apply("hello comma world", Rule("comma", ",")));
    }

    [Fact]
    public void Apply_preserves_line_breaks()
    {
        Assert.Equal(
            "Aspire\r\nsecond line",
            QuickDictionaryAdd.Apply("aspire\r\nsecond line", Rule("aspire", "Aspire")));
    }

    /// <summary>
    /// "$" is a substitution token to Regex.Replace, so a naive implementation writes a capture group
    /// instead of the literal text the user typed. The live matcher avoids it with a MatchEvaluator.
    /// </summary>
    [Fact]
    public void Apply_treats_a_dollar_sign_in_the_replacement_as_literal_text()
    {
        Assert.Equal(
            "costs $5 today",
            QuickDictionaryAdd.Apply("costs five dollars today", Rule("five dollars", "$5")));

        Assert.Equal(
            "$& here",
            QuickDictionaryAdd.Apply("ampersand here", Rule("ampersand", "$&")));
    }

    // Regex metacharacters are common in dictated text ("C++", "3.5", "what?"), and the pattern comes
    // straight from the recognizer's output, so escaping is not optional.
    [Fact]
    public void Apply_escapes_regex_metacharacters_in_the_spoken_form()
    {
        Assert.Equal(
            "we use C# here",
            QuickDictionaryAdd.Apply("we use c++ here", Rule("c++", "C#")));

        Assert.Equal("matched", QuickDictionaryAdd.Apply("a.c", Rule("a.c", "matched")));
        Assert.Equal("a.c", QuickDictionaryAdd.Apply("a.c", Rule("abc", "matched")));
    }

    [Fact]
    public void Apply_returns_the_transcript_unchanged_when_the_rule_never_fires()
    {
        Assert.Equal(
            "nothing to change here",
            QuickDictionaryAdd.Apply("nothing to change here", Rule("absent", "present")));
    }

    [Fact]
    public void Apply_handles_missing_inputs_without_throwing()
    {
        Assert.Equal(string.Empty, QuickDictionaryAdd.Apply(null, Rule("a", "b")));
        Assert.Equal(string.Empty, QuickDictionaryAdd.Apply("", Rule("a", "b")));
        Assert.Equal("text", QuickDictionaryAdd.Apply("text", null));
        Assert.Equal("text", QuickDictionaryAdd.Apply("text", Rule("   ", "b")));
    }

    /// <summary>
    /// The load-bearing guarantee: repairing a transcript must produce exactly what the live pipeline
    /// would have produced from the same input. Anything else hands the user a "corrected" transcript
    /// that disagrees with their next dictation. Asserted against the real processor, not a copy.
    /// </summary>
    [Theory]
    [InlineData("so um it works", "um", "")]
    [InlineData("it works, um obviously.", "um", "")]
    [InlineData("um it works", "um", "")]
    [InlineData("hello comma world", "comma", ",")]
    [InlineData("York is different from New York", "York", "New York")]
    [InlineData("aspire and aspires", "aspire", "Aspire")]
    [InlineData("  padded   text  ", "padded", "Padded")]
    public void Apply_agrees_with_the_live_post_processor(string transcript, string pattern, string replacement)
    {
        var entry = new DictionaryEntry(1, pattern, replacement);

        using var db = ScribeDatabase.CreateInMemory();
        var repo = new DictionaryRepository(db);
        repo.SeedIfEmpty([entry]);
        var processor = new TextPostProcessor(repo, NullLogger<TextPostProcessor>.Instance);

        var live = processor.Process(transcript);
        var repaired = QuickDictionaryAdd.Apply(transcript, repo.GetAll().Single());

        Assert.Equal(live, repaired);
    }}
