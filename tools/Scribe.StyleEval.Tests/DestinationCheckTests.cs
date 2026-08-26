using Scribe.StyleEval.Checks;

namespace Scribe.StyleEval.Tests;

/// <summary>
/// Calibration fixtures for the four per-destination output contracts.
/// </summary>
/// <remarks>
/// Each of these checkers grades exactly one action and abstains on every other, which is the
/// property most worth pinning: an off-by-one in that gate would quietly grade a Teams message
/// against the HTML contract and report nine thousand failures.
/// </remarks>
public class DestinationCheckTests
{
    // ---------------------------------------------------------------- markdown-contract

    /// <summary>
    /// A correct answer with one of everything the contract has an opinion about: a tagged fence,
    /// a list, a heading, and a blank line before every block.
    /// </summary>
    private const string CleanMarkdown =
        "## Rollout plan\n" +
        "\n" +
        "We ship on Tuesday once the migration lands.\n" +
        "\n" +
        "- Update the config\n" +
        "- Restart the workers\n" +
        "- Verify the queue drains\n" +
        "\n" +
        "```bash\n" +
        "scribe --verify\n" +
        "```\n";

    [Fact]
    public void MarkdownContract_passes_a_clean_commonmark_answer()
    {
        var scenario = Fixture.Selection("Rollout plan: update the config, restart the workers, verify the queue.");

        Expect.Pass(DestinationChecks.Markdown(
            Fixture.Cell("format-markdown", scenario, CleanMarkdown), CleanMarkdown));
    }

    [Fact]
    public void MarkdownContract_fails_an_answer_wrapped_in_a_presentation_fence()
    {
        // Checked against the RAW response on purpose. TextActionSanitizer strips exactly this
        // wrapper, so grading only the sanitized text would hide the defect entirely.
        var scenario = Fixture.Selection("Rollout plan: we ship on Tuesday.");
        var raw = "```markdown\n## Rollout plan\n\nWe ship on Tuesday.\n```";
        var sanitized = "## Rollout plan\n\nWe ship on Tuesday.";

        Expect.Fail(DestinationChecks.Markdown(
            Fixture.Cell("format-markdown", scenario, sanitized), raw), "wrapped in a");
    }

    [Fact]
    public void MarkdownContract_fails_an_untagged_fence()
    {
        var scenario = Fixture.Selection("Run scribe --verify after the deploy.");
        var output = "Run the verifier after the deploy.\n\n```\nscribe --verify\n```\n";

        Expect.Fail(DestinationChecks.Markdown(
            Fixture.Cell("format-markdown", scenario, output), output), "language tag");
    }

    [Fact]
    public void MarkdownContract_fails_raw_html()
    {
        var scenario = Fixture.Selection("The migration must finish before Tuesday.");
        var output = "The migration <b>must finish</b> before Tuesday.";

        Expect.Fail(DestinationChecks.Markdown(
            Fixture.Cell("format-markdown", scenario, output), output), "raw HTML");
    }

    [Fact]
    public void MarkdownContract_allows_a_tag_the_author_wrote_when_it_is_in_a_code_span()
    {
        // A selection about HTML legitimately contains tags, and the correct Markdown answer puts
        // them in code spans, where Markdig reports CodeInline rather than HtmlInline. A regex over
        // the raw text would fail this answer for doing the right thing.
        var scenario = Fixture.Selection("Wrap the label in a <strong> tag before you paste it.");
        var output = "Wrap the label in a `<strong>` tag before you paste it.";

        Expect.Pass(DestinationChecks.Markdown(
            Fixture.Cell("format-markdown", scenario, output), output));
    }

    [Fact]
    public void MarkdownContract_passes_a_shell_glob_inside_a_fenced_block()
    {
        // The regression this file was written to catch. MarkdownReader appends fence CONTENT to
        // PlainText, so a stray-delimiter arm that searches PlainText reads the glob's "**" as an
        // emphasis run that never closed and fails an answer that did exactly the right thing.
        // Every technical selection that carries a command hits this, which is where fences live.
        var scenario = Fixture.Selection("Clear the temp files out of the build directory before deploying.");
        var output =
            "Clear the temp files before you deploy.\n" +
            "\n" +
            "```bash\n" +
            "rm -rf ./build/**/*.tmp\n" +
            "```\n";

        Expect.Pass(DestinationChecks.Markdown(Fixture.Cell("format-markdown", scenario, output), output));
    }

    [Fact]
    public void MarkdownContract_passes_a_delimiter_inside_an_inline_code_span()
    {
        // The same defect one level down: CodeInline content is appended to PlainText too, so a
        // wildcard shown inline has to be excluded for the same reason a fenced one is.
        var scenario = Fixture.Selection("Delete every tmp file under build.");
        var output = "Run `del build\\**\\*.tmp` before you deploy.";

        Expect.Pass(DestinationChecks.Markdown(Fixture.Cell("format-markdown", scenario, output), output));
    }

    [Fact]
    public void MarkdownContract_still_fails_an_emphasis_run_that_never_closed()
    {
        // The arm has to keep working on prose, which is the only place it was ever meaningful.
        var scenario = Fixture.Selection("This one is important, do not skip it.");
        var output = "This one is **important, do not skip it.";

        Expect.Fail(
            DestinationChecks.Markdown(Fixture.Cell("format-markdown", scenario, output), output),
            "did not close");
    }

    [Fact]
    public void MarkdownContract_is_not_applicable_to_another_destination()
    {
        var scenario = Fixture.Selection("Rollout plan: we ship on Tuesday.");

        Expect.NotApplicable(DestinationChecks.Markdown(
            Fixture.Cell("format-for-teams", scenario, "We ship on Tuesday."), "We ship on Tuesday."));
    }

    // ---------------------------------------------------------------- html-contract

    [Fact]
    public void HtmlContract_passes_a_clean_semantic_fragment()
    {
        var scenario = Fixture.Selection("Read the release notes at https://example.com/notes before Tuesday.");
        var output =
            "<p>Read the <a href=\"https://example.com/notes\">release notes</a> before Tuesday.</p>";

        Expect.Pass(DestinationChecks.Html(Fixture.Cell("format-html", scenario, output)));
    }

    [Fact]
    public void HtmlContract_fails_an_attribute_whose_name_begins_with_on()
    {
        var scenario = Fixture.Selection("Click the link to open the notes.");
        var output = "<p onclick=\"openNotes()\">Click to open the notes.</p>";

        Expect.Fail(DestinationChecks.Html(Fixture.Cell("format-html", scenario, output)), "on");
    }

    [Fact]
    public void HtmlContract_passes_an_event_handler_the_author_quoted_as_content()
    {
        // A selection about web security legitimately quotes an inline handler. The correct fragment
        // shows it as text inside a code element, where it is content and not an attribute at all,
        // and a raw scan for "on...=" reads that correct answer as having emitted one.
        var scenario = Fixture.Selection(
            "The avatar fallback is implemented with onerror=\"this.src='/img/default.png'\" inline on the img element.");
        var output =
            "<p>The avatar fallback is implemented with " +
            "<code>onerror=\"this.src='/img/default.png'\"</code> inline on the img element.</p>";

        Expect.Pass(DestinationChecks.Html(Fixture.Cell("format-html", scenario, output)));
    }

    [Fact]
    public void HtmlContract_passes_an_escaped_tag_carrying_an_event_handler()
    {
        // The other correct rendering of the same content: angle brackets escaped, so the whole
        // hostile tag arrives as text. Nothing here is markup, so nothing here is an attribute.
        var scenario = Fixture.Selection(
            "QA found that typing <img src=x onerror=alert(1)> into the display name gets stored fine.");
        var output =
            "<p>QA found that typing &lt;img src=x onerror=alert(1)&gt; into the display name gets " +
            "stored fine.</p>";

        Expect.Pass(DestinationChecks.Html(Fixture.Cell("format-html", scenario, output)));
    }

    [Fact]
    public void HtmlContract_fails_a_javascript_href()
    {
        var scenario = Fixture.Selection("Click the link to open the notes.");
        var output = "<p><a href=\"javascript:openNotes()\">Open the notes</a></p>";

        Expect.Fail(DestinationChecks.Html(Fixture.Cell("format-html", scenario, output)), "href scheme");
    }

    [Fact]
    public void HtmlContract_fails_a_script_element()
    {
        var scenario = Fixture.Selection("The report is ready.");
        var output = "<p>The report is ready.</p><script>track('ready')</script>";

        Expect.Fail(DestinationChecks.Html(Fixture.Cell("format-html", scenario, output)), "script");
    }

    [Fact]
    public void HtmlContract_fails_an_unbalanced_fragment()
    {
        // Parsed strictly, as XML. A forgiving HTML parser would repair this and hand back a clean
        // bill of health for a fragment that breaks the page it is pasted into.
        var scenario = Fixture.Selection("The report is ready.");
        var output = "<p>The report is <strong>ready.</p>";

        Expect.Fail(DestinationChecks.Html(Fixture.Cell("format-html", scenario, output)));
    }

    [Fact]
    public void HtmlContract_fails_an_h1_that_would_break_the_host_page_outline()
    {
        var scenario = Fixture.Selection("Rollout plan. We ship on Tuesday.");
        var output = "<h1>Rollout plan</h1><p>We ship on Tuesday.</p>";

        Expect.Fail(DestinationChecks.Html(Fixture.Cell("format-html", scenario, output)), "h1");
    }

    [Fact]
    public void HtmlContract_is_not_applicable_to_another_destination()
    {
        var scenario = Fixture.Selection("The report is ready.");

        Expect.NotApplicable(DestinationChecks.Html(
            Fixture.Cell("format-markdown", scenario, "The report is ready.")));
    }

    // ---------------------------------------------------------------- json-contract

    [Fact]
    public void JsonContract_passes_a_valid_document()
    {
        var scenario = Fixture.Selection("Fifteen seats, live in two weeks, owned by Ana.");
        var output =
            "{\n" +
            "  \"seats\": 15,\n" +
            "  \"owner\": \"Ana\",\n" +
            "  \"liveIn\": \"2 weeks\"\n" +
            "}";

        Expect.Pass(DestinationChecks.Json(Fixture.Cell("format-json", scenario, output)));
    }

    [Fact]
    public void JsonContract_fails_an_answer_that_does_not_parse()
    {
        var scenario = Fixture.Selection("Fifteen seats, owned by Ana.");

        Expect.Fail(DestinationChecks.Json(
            Fixture.Cell("format-json", scenario, "{\n  \"seats\": 15,\n  \"owner\":\n}")),
            "does not parse");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\n  \"tags\": [],\n  \"owner\": null\n}")]
    public void JsonContract_accepts_a_legitimately_empty_array_or_object(string output)
    {
        // The instruction says an empty object is {} and an empty array is []. A contract that
        // treated emptiness as a defect would fail the one answer the instruction asks for when
        // the author named a field and left it empty.
        var scenario = Fixture.Selection("No tags yet and no owner assigned.");

        Expect.Pass(DestinationChecks.Json(Fixture.Cell("format-json", scenario, output)));
    }

    [Fact]
    public void JsonContract_fails_a_key_that_is_not_lower_camel_case()
    {
        var scenario = Fixture.Selection("Fifteen seats, owned by Ana.");
        var output = "{\n  \"Seats\": 15,\n  \"owner_name\": \"Ana\"\n}";

        Expect.Fail(DestinationChecks.Json(Fixture.Cell("format-json", scenario, output)), "lowerCamelCase");
    }

    [Fact]
    public void JsonContract_fails_an_invented_wrapper_key()
    {
        var scenario = Fixture.Selection("Fifteen seats, owned by Ana.");
        var output = "{\n  \"data\": {\n    \"seats\": 15,\n    \"owner\": \"Ana\"\n  }\n}";

        Expect.Fail(DestinationChecks.Json(Fixture.Cell("format-json", scenario, output)), "unjustified 'data'");
    }

    [Fact]
    public void JsonContract_fails_a_version_number_demoted_to_a_json_number()
    {
        // "Keep as strings any value whose written form carries meaning." A protected token that
        // arrived as a bare number lost its written form, and no other checker can see it: to
        // preservation, searching the decoded string surface, 2.1 is still present.
        var scenario = Fixture.Selection(
            "Pin the package at 2.1 or the build breaks.",
            protectedTokens: ["2.1"]);
        var output = "{\n  \"pinnedVersion\": 2.1\n}";

        Expect.Fail(DestinationChecks.Json(Fixture.Cell("format-json", scenario, output)), "JSON numbers");
    }

    [Fact]
    public void JsonContract_fails_a_written_out_absolute_date()
    {
        var scenario = Fixture.Selection("The contract was signed on July 3rd, 2026.");
        var output = "{\n  \"signedOn\": \"July 3rd, 2026\"\n}";

        Expect.Fail(DestinationChecks.Json(Fixture.Cell("format-json", scenario, output)), "ISO 8601");
    }

    [Fact]
    public void JsonContract_passes_a_version_number_that_looks_like_a_dotted_date()
    {
        // "3.0.13" is OpenSSL, not the third of October. The same instruction that asks for ISO
        // dates says a version number stays a string exactly as written, so a date check that
        // fires on it fails the answer for obeying the rule beside it.
        var scenario = Fixture.Selection(
            "Pins we agreed for the LTS branch. Node 20.11.1. Python 3.12.4. OpenSSL 3.0.13.",
            protectedTokens: ["20.11.1", "3.12.4", "3.0.13"]);
        var output = "{\n  \"node\": \"20.11.1\",\n  \"python\": \"3.12.4\",\n  \"openssl\": \"3.0.13\"\n}";

        Expect.Pass(DestinationChecks.Json(Fixture.Cell("format-json", scenario, output)));
    }

    [Fact]
    public void JsonContract_passes_a_written_date_inside_a_kept_sentence()
    {
        // The ISO rule governs a date the author gave as a VALUE. The instruction also says to keep
        // the author's own sentences verbatim inside string values, so rewriting the date inside a
        // note would break the rule this check is enforcing.
        var scenario = Fixture.Selection(
            "Renewal is 2026-07-03. Legal noted that we agreed the terms on March 3, 2026 and have not revisited them.");
        var output =
            "{\n" +
            "  \"renewal\": \"2026-07-03\",\n" +
            "  \"note\": \"Legal noted that we agreed the terms on March 3, 2026 and have not revisited them.\"\n" +
            "}";

        Expect.Pass(DestinationChecks.Json(Fixture.Cell("format-json", scenario, output)));
    }

    [Fact]
    public void JsonContract_is_not_applicable_to_another_destination()
    {
        var scenario = Fixture.Selection("Fifteen seats, owned by Ana.");

        Expect.NotApplicable(DestinationChecks.Json(
            Fixture.Cell("format-markdown", scenario, "- 15 seats\n- Owner: Ana\n")));
    }

    // ---------------------------------------------------------------- teams-contract

    [Fact]
    public void TeamsContract_passes_a_compose_box_message()
    {
        var scenario = Fixture.Selection(
            "Deploy is blocked until the migration finishes. New window is Tuesday at 9am. Can you confirm?");
        var output =
            "Deploy is blocked until the migration finishes.\n" +
            "\n" +
            "**Tuesday 9am** is the new window.\n" +
            "\n" +
            "Can you confirm the team is free then?";

        Expect.Pass(DestinationChecks.Teams(Fixture.Cell("format-for-teams", scenario, output)));
    }

    [Fact]
    public void TeamsContract_fails_a_heading()
    {
        var scenario = Fixture.Selection("Deploy is blocked until the migration finishes.");
        var output = "# Deploy update\n\nDeploy is blocked until the migration finishes.";

        Expect.Fail(DestinationChecks.Teams(Fixture.Cell("format-for-teams", scenario, output)), "heading");
    }

    [Fact]
    public void TeamsContract_fails_single_asterisk_emphasis()
    {
        // Teams renders one asterisk as bold and every other renderer reads it as italic, so a
        // message later quoted elsewhere comes back inverted.
        var scenario = Fixture.Selection("The deadline is Tuesday.");
        var output = "The deadline is *Tuesday* and nothing has moved yet.";

        Expect.Fail(DestinationChecks.Teams(Fixture.Cell("format-for-teams", scenario, output)), "single-asterisk");
    }

    [Fact]
    public void TeamsContract_fails_a_pipe_table()
    {
        var scenario = Fixture.Selection("Migration is owned by Ana, rollout by Sam.");
        var output =
            "| Item | Owner |\n" +
            "| --- | --- |\n" +
            "| Migration | Ana |\n" +
            "| Rollout | Sam |\n";

        Expect.Fail(DestinationChecks.Teams(Fixture.Cell("format-for-teams", scenario, output)), "table");
    }

    [Fact]
    public void TeamsContract_passes_a_code_block_carrying_compose_box_lookalikes()
    {
        // The Teams instruction explicitly offers "three backticks alone on a line to open and close
        // a code block", and what goes inside one is the author's material shown verbatim. A shell
        // comment opens with "# ", a pasted transcript line opens with "> ", and a pipeline is full
        // of pipe characters. None of them is a heading, a quote or a table.
        var scenario = Fixture.Selection(
            "Run the packaging script with the log filter before you tag the build.");
        var output =
            "Here is the command that reproduces it:\n" +
            "\n" +
            "```\n" +
            "# clear the old artefacts first\n" +
            "rm -rf ./build/**/*.tmp\n" +
            "grep -r 'level=error' artifacts/logs/*.log | head -50 | sort -u\n" +
            "> restarting in 3s | attempt 2 | giving up\n" +
            "```\n" +
            "\n" +
            "Does that match what you saw?";

        Expect.Pass(DestinationChecks.Teams(Fixture.Cell("format-for-teams", scenario, output)));
    }

    [Fact]
    public void TeamsContract_still_fails_a_heading_outside_a_code_block()
    {
        // The exemption is scoped to the code block. A heading in the message itself is still the
        // construct that converts the whole line and forces the rest of the message inside it.
        var scenario = Fixture.Selection("Deploy is blocked until the migration finishes.");
        var output =
            "## Deploy status\n" +
            "\n" +
            "```\n" +
            "# this comment is fine\n" +
            "```\n";

        Expect.Fail(DestinationChecks.Teams(Fixture.Cell("format-for-teams", scenario, output)), "heading");
    }

    [Fact]
    public void TeamsContract_is_not_applicable_to_another_destination()
    {
        var scenario = Fixture.Selection("Deploy is blocked until the migration finishes.");

        Expect.NotApplicable(DestinationChecks.Teams(
            Fixture.Cell("format-markdown", scenario, "Deploy is blocked until the migration finishes.")));
    }
}
