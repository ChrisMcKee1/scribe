using Scribe.Core.TextActions;
using Scribe.StyleEval.Checks;
using Scribe.StyleEval.Markup;

namespace Scribe.StyleEval.Tests;

/// <summary>
/// Calibration fixtures for the missed-opportunity half of the suite.
/// </summary>
/// <remarks>
/// The failure mode this half exists to catch is an answer that formats nothing, which clears every
/// negative ceiling by doing no work. The failure mode it must never invent is asking an action for
/// structure its prompt never mentioned, so the two abstention gates (can the destination express
/// this, and was the action actually given the Detection rules) carry as many fixtures here as the
/// pass and fail cases do.
/// </remarks>
public class PositiveCheckTests
{
    /// <summary>Every action that receives <c>EnrichmentRules.Detection</c>, read off the catalog.</summary>
    public static TheoryData<string> FullEnrichmentActions()
    {
        var data = new TheoryData<string>();
        foreach (var action in TextActionCatalog.All.Where(a => a.Enrichment == EnrichmentLevel.Full))
        {
            data.Add(action.Id);
        }

        return data;
    }

    /// <summary>Every action that does not, which the positive half must never grade.</summary>
    public static TheoryData<string> PartialEnrichmentActions()
    {
        var data = new TheoryData<string>();
        foreach (var action in TextActionCatalog.All.Where(a =>
                     a.Kind == TextActionKind.Ai && a.Enrichment != EnrichmentLevel.Full))
        {
            data.Add(action.Id);
        }

        return data;
    }

    // ---------------------------------------------------------------- should-bold

    [Fact]
    public void ShouldBold_passes_when_the_marked_phrase_came_back_bold()
    {
        var scenario = Fixture.Selection(
            "The contract must be signed by Friday or the pilot slips.",
            shouldBold: ["must be signed by Friday"]);

        var result = PositiveChecks.ShouldBold(Fixture.Cell(
            "format-markdown", scenario,
            "The contract **must be signed by Friday** or the pilot slips."));

        Expect.Pass(result);
    }

    [Fact]
    public void ShouldBold_passes_a_different_phrase_carrying_one_of_the_marker_words()
    {
        // The scenario's shouldBold is one editor's choice among the eligible phrases, not the only
        // one. Restraint caps emphasis at one phrase per paragraph, so an answer that picked a
        // different phrase the author's own marker words made eligible is a correct answer.
        var scenario = Fixture.Selection(
            "The contract must be signed by Friday, and you must never send it unsigned.",
            shouldBold: ["signed by Friday"]);

        var result = PositiveChecks.ShouldBold(Fixture.Cell(
            "format-markdown", scenario,
            "The contract is due Friday, and you **must never** send it unsigned."));

        Expect.Pass(result);
    }

    [Fact]
    public void ShouldBold_does_not_accept_a_marker_word_found_inside_another_word()
    {
        // The justification arm has to match whole words. "commonly" carries the letters of "only"
        // and "mustard" carries the letters of "must", so a substring test lets any bold at all
        // excuse itself and the positive half stops catching anything.
        var scenario = Fixture.Selection(
            "You must clear the cache before the release, or the old bundle is served.",
            shouldBold: ["must clear the cache"]);

        var result = PositiveChecks.ShouldBold(Fixture.Cell(
            "format-markdown", scenario,
            "The old bundle is **commonly** served when the cache is stale."));

        Expect.Fail(result, "no marker word");
    }

    [Fact]
    public void ShouldBold_fails_an_answer_that_bolds_nothing_at_all()
    {
        var scenario = Fixture.Selection(
            "The contract must be signed by Friday or the pilot slips.",
            shouldBold: ["must be signed by Friday"]);

        var result = PositiveChecks.ShouldBold(Fixture.Cell(
            "format-markdown", scenario,
            "The contract has to be signed by Friday or the pilot slips."));

        Expect.Fail(result, "bolds nothing at all");
    }

    [Fact]
    public void ShouldBold_is_not_applicable_to_json_which_has_no_emphasis()
    {
        var scenario = Fixture.Selection(
            "The contract must be signed by Friday or the pilot slips.",
            shouldBold: ["must be signed by Friday"]);

        var result = PositiveChecks.ShouldBold(Fixture.Cell(
            "format-json", scenario, "{\n  \"signBy\": \"Friday\"\n}"));

        Expect.NotApplicable(result);
    }

    [Fact]
    public void ShouldBold_is_not_applicable_when_the_scenario_marks_no_phrase()
    {
        var scenario = Fixture.Selection("The report went out on Tuesday.");

        Expect.NotApplicable(PositiveChecks.ShouldBold(Fixture.Cell(
            "format-markdown", scenario, "The report went out on Tuesday.")));
    }

    // ---------------------------------------------------------------- should-list

    [Fact]
    public void ShouldList_passes_when_three_peer_items_became_a_list()
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

        Expect.Pass(PositiveChecks.ShouldList(Fixture.Cell("format-markdown", scenario, output)));
    }

    [Fact]
    public void ShouldList_passes_when_three_peer_items_became_a_json_array()
    {
        var scenario = Fixture.Selection(
            "We still need the schema review, the load test and the rollback plan.",
            shouldList: true);

        var output =
            "{\n" +
            "  \"outstanding\": [\n" +
            "    \"schema review\",\n" +
            "    \"load test\",\n" +
            "    \"rollback plan\"\n" +
            "  ]\n" +
            "}";

        Expect.Pass(PositiveChecks.ShouldList(Fixture.Cell("format-json", scenario, output)));
    }

    [Fact]
    public void ShouldList_fails_an_answer_that_left_three_peer_items_in_prose()
    {
        var scenario = Fixture.Selection(
            "We still need the schema review, the load test and the rollback plan.",
            shouldList: true);

        var result = PositiveChecks.ShouldList(Fixture.Cell(
            "format-markdown", scenario,
            "We still need the schema review, the load test and the rollback plan."));

        Expect.Fail(result, "no list");
    }

    [Fact]
    public void ShouldList_is_not_applicable_when_the_scenario_has_no_run_of_peer_items()
    {
        var scenario = Fixture.Selection("We cannot ship until the migration finishes.");

        Expect.NotApplicable(PositiveChecks.ShouldList(Fixture.Cell(
            "format-markdown", scenario, "We cannot ship until the migration finishes.")));
    }

    // ---------------------------------------------------------------- should-table

    [Fact]
    public void ShouldTable_passes_when_repeated_records_became_a_table()
    {
        var scenario = Fixture.Selection(
            "Migration is owned by Ana and due Tuesday. Rollout is owned by Sam and due Thursday.",
            shouldTable: true);

        var output =
            "| Item | Owner | Due |\n" +
            "| --- | --- | --- |\n" +
            "| Migration | Ana | Tuesday |\n" +
            "| Rollout | Sam | Thursday |\n";

        Expect.Pass(PositiveChecks.ShouldTable(Fixture.Cell("format-markdown", scenario, output)));
    }

    [Fact]
    public void ShouldTable_passes_when_repeated_records_became_a_uniform_json_array()
    {
        var scenario = Fixture.Selection(
            "Migration is owned by Ana and due Tuesday. Rollout is owned by Sam and due Thursday.",
            shouldTable: true);

        var output =
            "[\n" +
            "  { \"item\": \"Migration\", \"owner\": \"Ana\", \"due\": \"Tuesday\" },\n" +
            "  { \"item\": \"Rollout\", \"owner\": \"Sam\", \"due\": \"Thursday\" }\n" +
            "]";

        Expect.Pass(PositiveChecks.ShouldTable(Fixture.Cell("format-json", scenario, output)));
    }

    [Fact]
    public void ShouldTable_fails_an_answer_that_wrote_the_same_shape_out_longhand()
    {
        var scenario = Fixture.Selection(
            "Migration is owned by Ana and due Tuesday. Rollout is owned by Sam and due Thursday.",
            shouldTable: true);

        var result = PositiveChecks.ShouldTable(Fixture.Cell(
            "format-markdown", scenario,
            "Migration is owned by Ana and due Tuesday.\n\nRollout is owned by Sam and due Thursday."));

        Expect.Fail(result, "no table");
    }

    [Fact]
    public void ShouldTable_is_not_applicable_to_two_records_in_a_document_destination()
    {
        // Restraint puts the table floor at three rows carrying the same fields. Two records are a
        // real repeated structure and the JSON answer is an array of two objects, but the Markdown
        // answer is paired lines, so grading it for a missing table asks the model to break the
        // ceiling it was handed.
        var scenario = Fixture.Selection(
            "Both laptops came back from the loan pool. The Dell has 16 GB and a 512 GB drive, " +
            "the Lenovo has 32 GB and a 1 TB drive.",
            shouldTable: true,
            recordCount: 2);

        var result = PositiveChecks.ShouldTable(Fixture.Cell(
            "format-markdown", scenario,
            "**Dell:** 16 GB, 512 GB drive\n\n**Lenovo:** 32 GB, 1 TB drive\n"));

        Expect.NotApplicable(result);
    }

    [Fact]
    public void ShouldTable_still_grades_two_records_in_json()
    {
        // The floor is a rendering rule for a table, not a claim that the records stopped being
        // records. JSON has no rows, so two records are still an array of two objects there.
        var scenario = Fixture.Selection(
            "Both laptops came back from the loan pool. The Dell has 16 GB and a 512 GB drive, " +
            "the Lenovo has 32 GB and a 1 TB drive.",
            shouldTable: true,
            recordCount: 2);

        var result = PositiveChecks.ShouldTable(Fixture.Cell(
            "format-json", scenario,
            "{\n  \"laptops\": \"the Dell has 16 GB and the Lenovo has 32 GB\"\n}"));

        Expect.Fail(result, "no array of objects");
    }

    [Fact]
    public void ShouldTable_is_not_applicable_to_teams_which_has_no_table()
    {
        // Teams shows a pipe table as literal pipes. Asking for one and then failing the answer for
        // not having it would manufacture a failure out of the destination's own limitation.
        var scenario = Fixture.Selection(
            "Migration is owned by Ana and due Tuesday. Rollout is owned by Sam and due Thursday.",
            shouldTable: true);

        var result = PositiveChecks.ShouldTable(Fixture.Cell(
            "format-for-teams", scenario,
            "**Migration** Ana, due Tuesday\n**Rollout** Sam, due Thursday"));

        Expect.NotApplicable(result);
        Assert.Equal(Destination.Teams, Fixture.Cell("format-for-teams", scenario, "x").Destination);
    }

    [Fact]
    public void ShouldTable_is_not_applicable_to_the_agent_brief()
    {
        // rewrite-for-ai writes a brief, not a document. Nothing in its instruction asks for a
        // table, so grading it for a missing one would invent a failure.
        var scenario = Fixture.Selection(
            "Migration is owned by Ana and due Tuesday. Rollout is owned by Sam and due Thursday.",
            shouldTable: true);

        var result = PositiveChecks.ShouldTable(Fixture.Cell(
            "rewrite-for-ai", scenario,
            "Finish the migration and the rollout.\n\n1. Ana finishes the migration by Tuesday.\n" +
            "2. Sam runs the rollout by Thursday.\n"));

        Expect.NotApplicable(result);
    }

    // ---------------------------------------------------------------- should-code

    [Fact]
    public void ShouldCode_passes_when_every_identifier_came_back_in_code_formatting()
    {
        var scenario = Fixture.Selection(
            "Set retryCount in src/config.json before you run the migration.",
            shouldCode: ["retryCount", "src/config.json"]);

        var result = PositiveChecks.ShouldCode(Fixture.Cell(
            "format-markdown", scenario,
            "Set `retryCount` in `src/config.json` before you run the migration."));

        Expect.Pass(result);
    }

    [Fact]
    public void ShouldCode_fails_an_identifier_left_as_plain_prose()
    {
        var scenario = Fixture.Selection(
            "Set retryCount in src/config.json before you run the migration.",
            shouldCode: ["retryCount", "src/config.json"]);

        var result = PositiveChecks.ShouldCode(Fixture.Cell(
            "format-markdown", scenario,
            "Set `retryCount` in src/config.json before you run the migration."));

        Expect.Fail(result, "src/config.json");
    }

    [Fact]
    public void ShouldCode_is_not_applicable_to_json_which_has_no_code_formatting()
    {
        var scenario = Fixture.Selection(
            "Set retryCount in src/config.json before you run the migration.",
            shouldCode: ["retryCount", "src/config.json"]);

        var result = PositiveChecks.ShouldCode(Fixture.Cell(
            "format-json", scenario,
            "{\n  \"setting\": \"retryCount\",\n  \"file\": \"src/config.json\"\n}"));

        Expect.NotApplicable(result);
    }

    [Fact]
    public void ShouldCode_is_not_applicable_to_a_plain_tone_rewrite()
    {
        // Worth pinning precisely because the abstention comes from the enrichment gate rather than
        // the destination gate: Destinations.SupportsCode is true for prose, so if the Detection
        // gate were ever removed, a formal-tone rewrite would start being failed for not sprinkling
        // backticks through an email it was told to keep as prose.
        var scenario = Fixture.Selection(
            "Set retryCount in src/config.json before you run the migration.",
            shouldCode: ["retryCount", "src/config.json"]);

        var context = Fixture.Cell("make-formal", scenario,
            "Please set retryCount in src/config.json before running the migration.");

        Assert.True(Destinations.SupportsCode(context.Destination));
        Expect.NotApplicable(PositiveChecks.ShouldCode(context));
    }

    // ---------------------------------------------------------------- the enrichment gate

    [Theory]
    [MemberData(nameof(PartialEnrichmentActions))]
    public void Every_positive_check_abstains_for_an_action_without_the_detection_rules(string actionId)
    {
        // An action never given EnrichmentRules.Detection cannot be said to have missed structure:
        // its prompt never mentioned any. The proofread is the sharpest case, since it promises
        // character-for-character output and would be destroyed by adding a list.
        var action = Fixture.Action(actionId);
        Assert.NotEqual(EnrichmentLevel.Full, action.Enrichment);

        var scenario = Fixture.Selection(
            "We still need the schema review, the load test and the rollback plan, and it must be " +
            "signed off by Friday. Set retryCount in src/config.json first.",
            shouldBold: ["must be signed off by Friday"],
            shouldList: true,
            shouldTable: true,
            shouldCode: ["retryCount", "src/config.json"]);

        // Flat prose: nothing bolded, no list, no table, no code. Every positive checker would have
        // something to say if the gate were not there.
        var context = Fixture.Cell(
            actionId, scenario,
            "We still need the schema review, the load test and the rollback plan, and it must be " +
            "signed off by Friday. Set retryCount in src/config.json first.");

        Expect.NotApplicable(PositiveChecks.ShouldBold(context));
        Expect.NotApplicable(PositiveChecks.ShouldList(context));
        Expect.NotApplicable(PositiveChecks.ShouldTable(context));
        Expect.NotApplicable(PositiveChecks.ShouldCode(context));
    }

    [Theory]
    [MemberData(nameof(FullEnrichmentActions))]
    public void A_full_enrichment_action_is_never_silently_passed_for_formatting_nothing(string actionId)
    {
        // The mirror image, and the reason the positive half exists. For an action that DID receive
        // the Detection rules, flat prose must never come back Pass: either the checker fails it or
        // it abstains for a stated reason, but it never says the answer was fine.
        var scenario = Fixture.Selection(
            "We still need the schema review, the load test and the rollback plan, and it must be " +
            "signed off by Friday. Set retryCount in src/config.json first.",
            shouldBold: ["must be signed off by Friday"],
            shouldList: true,
            shouldTable: true,
            shouldCode: ["retryCount", "src/config.json"]);

        var context = Fixture.Cell(
            actionId, scenario,
            "We still need the schema review, the load test and the rollback plan, and it must be " +
            "signed off by Friday. Set retryCount in src/config.json first.");

        CheckResult[] results =
        [
            PositiveChecks.ShouldBold(context),
            PositiveChecks.ShouldList(context),
            PositiveChecks.ShouldTable(context),
            PositiveChecks.ShouldCode(context),
        ];

        foreach (var result in results)
        {
            Assert.True(result.Status != CheckStatus.Pass,
                $"{actionId}/{result.Check} passed an answer that formatted nothing: {result.Reason}");
        }
    }
}
