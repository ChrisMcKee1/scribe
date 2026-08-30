using Scribe.Core.TextActions;
using Scribe.Evals.Interop;
using Scribe.StyleEval.Checks;

namespace Scribe.StyleEval.Tests;

/// <summary>
/// Calibration fixtures for the rule-violation half of the suite.
/// </summary>
public class NegativeCheckTests
{
    /// <summary>
    /// The two dash characters the house style forbids introducing. They are named constants
    /// rather than inline literals so that a repo-wide search for a stray dash lands here, on the
    /// one file that is supposed to contain them, instead of on a fixture string it has to read.
    /// </summary>
    private const string EmDash = "—";

    private const string EnDash = "–";

    // ---------------------------------------------------------------- preservation

    [Fact]
    public void Preservation_passes_when_every_protected_token_survives()
    {
        var scenario = Fixture.Selection(
            "Read https://example.com/docs/v2 before you touch config.yaml.",
            protectedTokens: ["https://example.com/docs/v2", "config.yaml"]);

        var result = NegativeChecks.Preservation(Fixture.Cell(
            "format-markdown", scenario,
            "Read https://example.com/docs/v2 before you touch `config.yaml`."));

        Expect.Pass(result);
    }

    [Fact]
    public void Preservation_passes_when_a_bare_url_was_wrapped_as_a_markdown_link()
    {
        // The single most common false failure a naive check produces. Turning a bare URL into
        // [label](url) is exactly what the Detection rules ask for, and the URL is still there
        // byte-identical inside the parentheses, so the token survived.
        var scenario = Fixture.Selection(
            "The runbook is at https://example.com/runbook/deploy and it is out of date.",
            protectedTokens: ["https://example.com/runbook/deploy"]);

        var result = NegativeChecks.Preservation(Fixture.Cell(
            "format-markdown", scenario,
            "The [runbook](https://example.com/runbook/deploy) is out of date."));

        Expect.Pass(result);
    }

    [Fact]
    public void Preservation_passes_when_an_html_answer_escaped_the_ampersand_it_had_to()
    {
        // A URL kept byte-identical inside an HTML fragment arrives as &amp; where the author
        // wrote &. That is correct escaping rather than a dropped token, so the decoded attribute
        // value is searched as well as the raw answer.
        var scenario = Fixture.Selection(
            "Grab https://example.com/report?a=1&b=2 before the meeting.",
            protectedTokens: ["https://example.com/report?a=1&b=2"]);

        var result = NegativeChecks.Preservation(Fixture.Cell(
            "format-html", scenario,
            "<p>Grab <a href=\"https://example.com/report?a=1&amp;b=2\">the report</a> before the meeting.</p>"));

        Expect.Pass(result);
    }

    [Fact]
    public void Preservation_fails_when_a_protected_token_was_altered()
    {
        var scenario = Fixture.Selection(
            "Pin the package at v2.1.0 or the build breaks.",
            protectedTokens: ["v2.1.0"]);

        var result = NegativeChecks.Preservation(Fixture.Cell(
            "format-markdown", scenario,
            "Pin the package at version 2.1 or the build breaks."));

        Expect.Fail(result, "v2.1.0");
    }

    [Fact]
    public void Preservation_is_not_applicable_when_the_scenario_protects_nothing()
    {
        var scenario = Fixture.Selection("A short note with nothing worth protecting.");

        var result = NegativeChecks.Preservation(Fixture.Cell(
            "improve-writing", scenario, "A short note with nothing worth protecting."));

        Expect.NotApplicable(result);
    }

    // ---------------------------------------------------------------- house-style

    [Fact]
    public void HouseStyle_passes_when_spoken_numbers_reached_digits()
    {
        var scenario = Fixture.Selection(
            "We need about fifteen seats and it has to be live in two weeks.",
            spelledOutNumbers: ["fifteen seats", "two weeks"]);

        var result = NegativeChecks.HouseStyle(Fixture.Cell(
            "improve-writing", scenario,
            "We need about 15 seats and it has to be live in 2 weeks."));

        Expect.Pass(result);
    }

    [Fact]
    public void HouseStyle_does_not_demand_an_iso_date_the_json_action_was_told_not_to_guess()
    {
        // format-json's own instruction says that if the text gives a relative time such as
        // "next Friday", keep the author's own words as the string rather than guessing a date.
        // The spoken quantity still has to reach digits; the relative date staying as written is
        // the action obeying its instruction, and house-style must not read that as a failure.
        var scenario = Fixture.Selection(
            "We need about fifteen seats and the contract has to be signed by next Friday.",
            spelledOutNumbers: ["fifteen seats"]);

        var result = NegativeChecks.HouseStyle(Fixture.Cell(
            "format-json", scenario,
            "{\n  \"seats\": 15,\n  \"signBy\": \"next Friday\"\n}"));

        Expect.Pass(result);
    }

    [Fact]
    public void HouseStyle_fails_when_a_spoken_number_was_left_spelled_out()
    {
        var scenario = Fixture.Selection(
            "It took about forty minutes to drain the queue.",
            spelledOutNumbers: ["forty minutes"]);

        var result = NegativeChecks.HouseStyle(Fixture.Cell(
            "improve-writing", scenario,
            "Draining the queue took about forty minutes."));

        Expect.Fail(result, "forty minutes");
    }

    [Fact]
    public void HouseStyle_fails_when_the_answer_invented_a_dash_the_selection_never_had()
    {
        // Deliberate dash fixture, one of the two places in this project where a dash character is
        // allowed to exist at all.
        var scenario = Fixture.Selection("The build is red and nobody has looked at it since Monday.");

        var result = NegativeChecks.HouseStyle(Fixture.Cell(
            "improve-writing", scenario,
            "The build is red " + EmDash + " nobody has looked at it since Monday."));

        Expect.Fail(result, "dash");
    }

    [Fact]
    public void HouseStyle_fails_when_a_similar_length_rewrite_dropped_the_author_s_own_dash()
    {
        var scenario = Fixture.Selection(
            "The build is red " + EnDash + " nobody has looked at it since Monday.",
            containsDash: true);

        var result = NegativeChecks.HouseStyle(Fixture.Cell(
            "improve-writing", scenario,
            "The build is red, and nobody has looked at it since Monday."));

        Expect.Fail(result, "removed the author");
    }

    [Fact]
    public void HouseStyle_never_reports_not_applicable_even_with_nothing_to_say()
    {
        // Pinned as a known wart rather than as a desirable behaviour. The NotApplicable arm needs
        // both the problem list and the note list to be empty, but the dash arm always pushes onto
        // one of the two, so the arm is unreachable: a scenario carrying no house-style expectation
        // still scores a Pass, which inflates the house-style pass rate across a whole run. If that
        // arm is ever made reachable, this test is what will say so.
        var scenario = Fixture.Selection("Nothing here asserts anything about numbers or punctuation.");

        var result = NegativeChecks.HouseStyle(Fixture.Cell(
            "improve-writing", scenario, "Nothing here asserts anything about numbers or punctuation."));

        Expect.Pass(result);
        Assert.Contains("dash count", result.Reason, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- restraint-bold

    [Fact]
    public void RestraintBold_passes_a_single_short_marked_phrase()
    {
        var scenario = Fixture.Selection("The migration must finish before the Tuesday release.");

        var result = NegativeChecks.RestraintBold(Fixture.Cell(
            "format-markdown", scenario,
            "The migration **must finish** before the Tuesday release."));

        Expect.Pass(result);
    }

    [Fact]
    public void RestraintBold_ignores_asterisks_inside_a_fenced_code_block()
    {
        // A shell snippet containing a glob is not emphasis, and a checker that counted raw
        // asterisks would fail every correct Markdown answer that shows a command. Markdig reads
        // fence content as literal text, which is the whole reason the suite parses rather than
        // pattern-matches.
        var scenario = Fixture.Selection(
            "Before deploying, clear the temp files out of the build directory.",
            expectNoBold: true);

        var output =
            "Clear the temp files before you deploy.\n" +
            "\n" +
            "```bash\n" +
            "rm -rf ./build/**/*.tmp\n" +
            "```\n";

        var context = Fixture.Cell("format-markdown", scenario, output);

        Assert.Empty(context.Markup.Bold);
        Expect.Pass(NegativeChecks.RestraintBold(context));
    }

    [Fact]
    public void RestraintBold_fails_when_the_text_carried_no_emphasis_trigger()
    {
        var scenario = Fixture.Selection(
            "The report went out on Tuesday and the numbers matched.",
            expectNoBold: true);

        var result = NegativeChecks.RestraintBold(Fixture.Cell(
            "format-markdown", scenario,
            "The **report** went out on Tuesday and the numbers matched."));

        Expect.Fail(result, "no emphasis trigger");
    }

    [Fact]
    public void RestraintBold_fails_a_bold_run_longer_than_four_words()
    {
        var scenario = Fixture.Selection("The migration must finish before the Tuesday release goes out.");

        var result = NegativeChecks.RestraintBold(Fixture.Cell(
            "format-markdown", scenario,
            "**The migration must finish before Tuesday** and nobody has started it."));

        Expect.Fail(result, "four words");
    }

    [Fact]
    public void RestraintBold_is_not_applicable_to_the_json_destination()
    {
        var scenario = Fixture.Selection("The migration must finish before the Tuesday release.");

        var result = NegativeChecks.RestraintBold(Fixture.Cell(
            "format-json", scenario, "{\n  \"deadline\": \"Tuesday\"\n}"));

        Expect.NotApplicable(result);
    }

    [Fact]
    public void RestraintBold_does_not_apply_expect_no_bold_to_a_teams_table()
    {
        // The Teams instruction says content that wants a table becomes one line per row with a
        // bold label at the front of each line. A scenario that carries records and no emphasis
        // trigger therefore has bold in its correct Teams answer, and reading that as
        // over-formatting fails every one of those cells for obeying the instruction.
        var scenario = Fixture.Selection(
            "SCRIBE_SIGN_CERT is the pfx path. SCRIBE_SIGN_PASS is the pfx password. " +
            "SCRIBE_FEED_URL is the update feed.",
            expectNoBold: true,
            shouldTable: true);

        var result = NegativeChecks.RestraintBold(Fixture.Cell(
            "format-for-teams",
            scenario,
            "**SCRIBE_SIGN_CERT:** the pfx path\n" +
            "**SCRIBE_SIGN_PASS:** the pfx password\n" +
            "**SCRIBE_FEED_URL:** the update feed"));

        Expect.NotApplicable(result);
    }

    [Fact]
    public void RestraintBold_still_fails_bold_in_a_teams_answer_with_no_records()
    {
        // The exemption above is scoped to the table case. Without records there is no instructed
        // bold, so a Teams answer that emphasises anything is still over-formatting.
        var scenario = Fixture.Selection(
            "The pipeline went green again once the cache was cleared.",
            expectNoBold: true);

        var result = NegativeChecks.RestraintBold(Fixture.Cell(
            "format-for-teams", scenario, "The pipeline is **green again** after the cache clear."));

        Expect.Fail(result, "no emphasis trigger");
    }

    [Fact]
    public void RestraintBold_survives_two_identical_bold_runs_in_one_line()
    {
        // Both runs resolve to the first occurrence of that text in the block, so the second one
        // starts before the first one ends. Slicing that range used to throw, which loses the whole
        // cell over an answer that is only repetitive.
        var scenario = Fixture.Selection("we must ship on tuesday and we must tell support");

        var result = NegativeChecks.RestraintBold(Fixture.Cell(
            "improve-writing", scenario, "We **must** ship on Tuesday and we **must** tell support."));

        Expect.Fail(result, "more than one bold phrase");
    }

    // ---------------------------------------------------------------- restraint-list

    [Fact]
    public void RestraintList_passes_a_list_of_three_peer_items()
    {
        var scenario = Fixture.Selection(
            "We still need the schema review, the load test and the rollback plan.",
            shouldList: true);

        var output =
            "Three things are still outstanding.\n" +
            "\n" +
            "- Schema review\n" +
            "- Load test\n" +
            "- Rollback plan\n";

        Expect.Pass(NegativeChecks.RestraintList(Fixture.Cell("format-markdown", scenario, output)));
    }

    [Fact]
    public void RestraintList_ignores_hyphen_lines_inside_a_fenced_code_block()
    {
        // The same trap as the bold one and worse: an answer that shows a YAML file or a diff
        // inside a fence has hyphen-prefixed lines everywhere, and none of them are list items.
        var scenario = Fixture.Selection(
            "The order matters here because each step depends on the one before it.",
            expectNoList: true);

        var output =
            "The order matters, because each step depends on the one before it.\n" +
            "\n" +
            "```text\n" +
            "- stop the workers\n" +
            "- drain the queue\n" +
            "- restart the workers\n" +
            "```\n";

        var context = Fixture.Cell("format-markdown", scenario, output);

        Assert.Empty(context.Markup.Lists);
        Expect.Pass(NegativeChecks.RestraintList(context));
    }

    [Fact]
    public void RestraintList_fails_when_one_connected_argument_became_a_list()
    {
        var scenario = Fixture.Selection(
            "We cannot ship on Friday because the migration is unfinished, so the reports would be wrong.",
            expectNoList: true);

        var output =
            "We cannot ship on Friday.\n" +
            "\n" +
            "- The migration is unfinished\n" +
            "- The reports would be wrong\n" +
            "- The rollback is untested\n";

        Expect.Fail(NegativeChecks.RestraintList(Fixture.Cell("format-markdown", scenario, output)),
            "one connected argument");
    }

    [Fact]
    public void RestraintList_fails_a_two_item_list_the_selection_did_not_already_have()
    {
        var scenario = Fixture.Selection("We still need the load test and the rollback plan before Friday.");

        var output =
            "Two things are outstanding before Friday.\n" +
            "\n" +
            "- Load test\n" +
            "- Rollback plan\n";

        Expect.Fail(NegativeChecks.RestraintList(Fixture.Cell("format-markdown", scenario, output)),
            "fewer than three items");
    }

    [Fact]
    public void RestraintList_passes_a_short_list_the_selection_already_contained()
    {
        // Preservation beats the ceiling. The author wrote a two-item list, handing it back is the
        // right answer, and the ceiling governs structure the model introduced.
        var input =
            "Two things are outstanding.\n" +
            "\n" +
            "- Load test\n" +
            "- Rollback plan\n";

        var scenario = Fixture.Selection(input);

        Expect.Pass(NegativeChecks.RestraintList(Fixture.Cell("format-markdown", scenario, input)));
    }

    [Fact]
    public void RestraintList_ignores_a_list_the_selection_already_parsed_as()
    {
        // A unified diff hunk is one connected piece of content, and its lines open with "-" and
        // "+", which CommonMark reads as list items. An answer that reproduces the hunk character
        // for character must not be reported as having built a list out of one connected argument.
        var input =
            "@@ -12,7 +12,7 @@ public static string BuildSystemPrompt(\n" +
            "-    var level = action.Enrichment;\n" +
            "+    var level = requireSingleLine ? EnrichmentLevel.None : action.Enrichment;\n";

        var scenario = Fixture.Selection(input, expectNoList: true);

        Expect.Pass(NegativeChecks.RestraintList(Fixture.Cell("improve-writing", scenario, input)));
    }

    [Fact]
    public void RestraintList_still_fails_a_list_added_beside_one_the_selection_had()
    {
        // The subtraction is a count, not a blanket exemption: an answer that keeps the author's
        // list and opens a second one has still introduced structure the text did not ask for.
        var input =
            "Two things are outstanding.\n" +
            "\n" +
            "- Load test\n" +
            "- Rollback plan\n";

        var output =
            input +
            "\n" +
            "Reasons:\n" +
            "\n" +
            "1. The migration is unfinished\n" +
            "2. The reports would be wrong\n";

        var scenario = Fixture.Selection(input, expectNoList: true);

        Expect.Fail(NegativeChecks.RestraintList(Fixture.Cell("format-markdown", scenario, output)),
            "one connected argument");
    }

    [Fact]
    public void RestraintList_is_not_applicable_to_the_json_destination()
    {
        var scenario = Fixture.Selection(
            "We still need the schema review, the load test and the rollback plan.",
            shouldList: true);

        var result = NegativeChecks.RestraintList(Fixture.Cell(
            "format-json", scenario, "{\n  \"outstanding\": [\"schema review\", \"load test\"]\n}"));

        Expect.NotApplicable(result);
    }

    // ---------------------------------------------------------------- heading-blacklist

    [Fact]
    public void HeadingBlacklist_passes_a_heading_taken_from_the_author_s_own_words()
    {
        var scenario = Fixture.Selection("Rollout plan. We ship on Tuesday once the migration lands.");

        var result = NegativeChecks.HeadingBlacklist(Fixture.Cell(
            "format-markdown", scenario,
            "## Rollout plan\n\nWe ship on Tuesday once the migration lands."));

        Expect.Pass(result);
    }

    [Theory]
    [InlineData("## Summary")]
    [InlineData("## Next steps")]
    [InlineData("### Key points:")]
    public void HeadingBlacklist_fails_a_heading_the_author_never_wrote(string heading)
    {
        var scenario = Fixture.Selection("We ship on Tuesday once the migration lands.");

        var result = NegativeChecks.HeadingBlacklist(Fixture.Cell(
            "format-markdown", scenario,
            heading + "\n\nWe ship on Tuesday once the migration lands."));

        Expect.Fail(result, "invented heading");
    }

    [Fact]
    public void HeadingBlacklist_allows_a_blacklisted_heading_the_author_wrote_themselves()
    {
        // The rule's own wording is "the author did not write those words". When they did, deleting
        // the heading would be dropping the author's text, which Preservation forbids outright.
        var scenario = Fixture.Selection(
            "## Summary\n\nThe migration finished at 02:10 and the backfill is still running.");

        var result = NegativeChecks.HeadingBlacklist(Fixture.Cell(
            "format-markdown",
            scenario,
            "## Summary\n\nThe migration finished at 02:10, and the backfill is still running.\n"));

        Expect.Pass(result);
    }

    [Fact]
    public void HeadingBlacklist_is_not_applicable_to_the_json_destination()
    {
        var scenario = Fixture.Selection("We ship on Tuesday once the migration lands.");

        var result = NegativeChecks.HeadingBlacklist(Fixture.Cell(
            "format-json", scenario, "{\n  \"ship\": \"Tuesday\"\n}"));

        Expect.NotApplicable(result);
    }

    // ---------------------------------------------------------------- length-band

    [Fact]
    public void LengthBand_passes_a_rewrite_of_roughly_the_same_size()
    {
        var scenario = Fixture.Selection("we shipped the migration tuesday and the reports came back clean");

        var result = NegativeChecks.LengthBand(Fixture.Cell(
            "improve-writing", scenario,
            "We shipped the migration on Tuesday, and the reports came back clean."));

        Expect.Pass(result);
    }

    [Fact]
    public void LengthBand_fails_an_answer_that_fell_through_the_floor()
    {
        var scenario = Fixture.Selection(
            "We shipped the migration on Tuesday and the reports came back clean, so the rollout can start.");

        var result = NegativeChecks.LengthBand(Fixture.Cell("improve-writing", scenario, "Yes."));

        Expect.Fail(result, "floor");
    }

    [Theory]
    [InlineData(TextActionLength.Similar)]
    [InlineData(TextActionLength.Shorter)]
    [InlineData(TextActionLength.Longer)]
    public void LengthBand_reads_its_bounds_from_the_shipping_sanitizer(TextActionLength length)
    {
        // Nothing here hardcodes a ratio. The bounds come from TextActionSanitizer through the same
        // shim the checker uses, so retuning a band in the shipping sanitizer moves the fixture with
        // it, while a private copy inside the checker would break this test the moment the two
        // drifted apart.
        var action = new TextAction(
            "fixture-band", "Fixture band", "Length band probe only.",
            TextActionGroup.Rewrite, TextActionKind.Ai, Instruction: string.Empty, Length: length);

        var (min, max) = ScribeCoreInternals.LengthBounds(length);
        var scenario = Fixture.Selection(new string('a', 200));

        Expect.Pass(NegativeChecks.LengthBand(
            Fixture.Cell(action, scenario, new string('b', (int)(min * 200)))));
        Expect.Fail(NegativeChecks.LengthBand(
            Fixture.Cell(action, scenario, new string('b', (int)(min * 200) - 1))), "floor");

        Expect.Pass(NegativeChecks.LengthBand(
            Fixture.Cell(action, scenario, new string('b', (int)(max * 200)))));
        Expect.Fail(NegativeChecks.LengthBand(
            Fixture.Cell(action, scenario, new string('b', (int)(max * 200) + 1))), "ceiling");
    }

    [Fact]
    public void LengthBand_is_not_applicable_to_a_short_structural_conversion()
    {
        // Mirrors the sanitizer's own exemption: a correct JSON rendering of fifteen characters
        // legitimately runs past a hundred, so the ratio says nothing.
        var scenario = Fixture.Selection("ship it tuesday");

        var result = NegativeChecks.LengthBand(Fixture.Cell(
            "format-json", scenario, "{\n  \"action\": \"ship it\",\n  \"when\": \"Tuesday\"\n}"));

        Expect.NotApplicable(result);
    }

    [Fact]
    public void LengthBand_is_not_applicable_to_an_empty_selection()
    {
        var result = NegativeChecks.LengthBand(Fixture.Cell(
            "improve-writing", Fixture.Selection(string.Empty), "anything at all"));

        Expect.NotApplicable(result);
    }

    // ---------------------------------------------------------------- minimal-diff

    /// <summary>
    /// A dictated paragraph with thirteen genuine errors in it. Deliberately dense: the risk in a
    /// proofreading ceiling is that it is set tight enough to fail a real proofread of real
    /// dictation, which is the only kind of text this action ever sees.
    /// </summary>
    private const string DenseTypos =
        "teh deploymnet went out on tuesday but the databse migration didnt finsh untill wendesday " +
        "morning. i think we shoud tell the stakholders that the reprots will be delayed by a day, " +
        "and that the invoicing job needs to be re run afterwards.";

    private const string DenseTyposProofread =
        "The deployment went out on Tuesday but the database migration didn't finish until Wednesday " +
        "morning. I think we should tell the stakeholders that the reports will be delayed by a day, " +
        "and that the invoicing job needs to be re-run afterwards.";

    [Fact]
    public void MinimalDiff_passes_a_genuine_proofread_of_densely_misspelled_text()
    {
        var scenario = Fixture.Selection(DenseTypos);

        var result = NegativeChecks.MinimalDiff(Fixture.Cell("fix-grammar", scenario, DenseTyposProofread));

        Expect.Pass(result);
    }

    [Fact]
    public void MinimalDiff_keeps_real_headroom_over_a_dense_proofread()
    {
        // Not a duplicate of the test above. That one asks whether the fixture passes, this one
        // asks by how much, so a future retune that halves the ceiling fails here with a number
        // rather than silently starting to reject correct proofreads across the whole corpus.
        var distance = TextTools.NormalizedEditDistance(DenseTypos, DenseTyposProofread);

        // Measured: thirteen corrections in two sentences move a shade under 8 percent, so the
        // 15 percent ceiling has a little over half of itself left. That is the honest margin, and
        // it is worth knowing: a proofread roughly twice this dense would start tripping the
        // ceiling, so the headroom is real but not generous.
        Assert.True(distance < NegativeChecks.MinimalDiffThreshold * 0.7,
            $"thirteen corrections moved {distance:P1} of the text against a " +
            $"{NegativeChecks.MinimalDiffThreshold:P0} ceiling");
    }

    [Fact]
    public void MinimalDiff_fails_a_restructure_dressed_up_as_a_proofread()
    {
        var scenario = Fixture.Selection(DenseTypos);

        var output =
            "## Deployment status\n" +
            "\n" +
            "- The deployment shipped on Tuesday.\n" +
            "- The database migration ran late and finished on Wednesday morning.\n" +
            "- Reports slip by one day.\n" +
            "\n" +
            "We should let the stakeholders know, and the invoicing job has to be re-run.\n";

        Expect.Fail(NegativeChecks.MinimalDiff(Fixture.Cell("fix-grammar", scenario, output)));
    }

    [Fact]
    public void MinimalDiff_fails_a_proofread_that_merged_two_sentences_into_one()
    {
        // The distance arm alone would let this through. A proofread merges and splits nothing, so
        // sentence count is checked separately from how many characters moved.
        var scenario = Fixture.Selection("The build is red. Nobody has looked at it since Monday.");

        var result = NegativeChecks.MinimalDiff(Fixture.Cell(
            "fix-grammar", scenario, "The build is red and nobody has looked at it since Monday."));

        Expect.Fail(result, "sentence count");
    }

    [Fact]
    public void MinimalDiff_is_not_applicable_to_any_action_but_the_proofread()
    {
        var scenario = Fixture.Selection(DenseTypos);

        Expect.NotApplicable(NegativeChecks.MinimalDiff(
            Fixture.Cell("improve-writing", scenario, DenseTyposProofread)));
        Expect.NotApplicable(NegativeChecks.MinimalDiff(
            Fixture.Cell("format-markdown", scenario, DenseTyposProofread)));
    }
}
