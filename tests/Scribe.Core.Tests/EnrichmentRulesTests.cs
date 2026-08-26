using Scribe.Core.TextActions;

namespace Scribe.Core.Tests;

public class EnrichmentRulesTests
{
    private static TextAction Json => TextActionCatalog.Find("format-json")!;

    private static TextAction Markdown => TextActionCatalog.Find("format-markdown")!;

    private static TextAction Grammar => TextActionCatalog.Find("fix-grammar")!;

    private static TextAction Formal => TextActionCatalog.Find("make-formal")!;

    [Fact]
    public void Format_actions_receive_the_full_rulebook()
    {
        foreach (var id in (string[])["format-markdown", "format-html", "format-json", "format-for-teams", "rewrite-for-ai"])
        {
            var action = TextActionCatalog.Find(id)!;

            Assert.Equal(EnrichmentLevel.Full, action.Enrichment);

            var prompt = TextActionPrompt.BuildSystemPrompt(action);
            Assert.Contains("Structure the content already has", prompt, StringComparison.Ordinal);
            Assert.Contains("Restraint.", prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_proofread_receives_no_rulebook_at_all()
    {
        // It promises character-for-character output. A rulebook telling it to add lists and bold
        // would destroy exactly the contract that makes it useful.
        Assert.Equal(EnrichmentLevel.None, Grammar.Enrichment);

        var prompt = TextActionPrompt.BuildSystemPrompt(Grammar);

        Assert.DoesNotContain("Structure the content already has", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Restraint.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(EnrichmentRules.Preservation, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Tone_rewrites_get_preservation_without_structure()
    {
        // A formal email wants prose. It still must not invent, drop or soften anything, which is
        // the half of the rulebook that genuinely applies to every action.
        Assert.Equal(EnrichmentLevel.PreserveOnly, Formal.Enrichment);

        var prompt = TextActionPrompt.BuildSystemPrompt(Formal);

        Assert.Contains(EnrichmentRules.Preservation, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Structure the content already has", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Single_line_targets_never_receive_structural_rules()
    {
        // Without this, Format as Markdown into a terminal would ship a prompt that mandates bulleted
        // lists in one paragraph and forbids them in another.
        var prompt = TextActionPrompt.BuildSystemPrompt(Markdown, requireSingleLine: true);

        Assert.Contains(TextActionPrompt.SingleLineContract, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Structure the content already has", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("one item per line", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_models_get_the_compressed_rulebook()
    {
        var frontier = TextActionPrompt.BuildSystemPrompt(Markdown, localModel: false);
        var local = TextActionPrompt.BuildSystemPrompt(Markdown, localModel: true);

        Assert.Contains(EnrichmentRules.Local, local, StringComparison.Ordinal);
        Assert.DoesNotContain(EnrichmentRules.Detection, local, StringComparison.Ordinal);
        Assert.True(local.Length < frontier.Length, "the local rulebook must be shorter than the frontier one");
    }

    [Fact]
    public void The_rulebook_sits_inside_the_task_section_so_the_tie_break_still_covers_it()
    {
        // The tie-break says "the task above decides the structure". Composing the rulebook after the
        // house style instead would put it outside what that sentence refers to.
        var prompt = TextActionPrompt.BuildSystemPrompt(Markdown, writingStyleOverride: "Write numbers as digits.");

        var task = prompt.IndexOf("Your task:", StringComparison.Ordinal);
        var rules = prompt.IndexOf("Structure the content already has", StringComparison.Ordinal);
        var style = prompt.IndexOf("Formatting conventions", StringComparison.Ordinal);
        var tieBreak = prompt.IndexOf("the task decides", StringComparison.Ordinal);

        Assert.True(task < rules, "the rulebook must come after the task heading");
        Assert.True(rules < style, "the rulebook must come before the house style");
        Assert.True(style < tieBreak, "the tie-break must come last");
    }

    [Fact]
    public void The_sentences_that_blocked_enrichment_are_gone()
    {
        // These three literal sentences were the bug. Markdown and HTML were told not to rewrite,
        // which is exactly the intelligence the feature is supposed to add.
        foreach (var id in (string[])["format-markdown", "format-html"])
        {
            var instruction = TextActionCatalog.Find(id)!.Instruction;

            Assert.DoesNotContain("not a rewrite", instruction, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("do not reword", instruction, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Json_keeps_its_verbatim_string_rule()
    {
        // Scoped to string VALUES, not to the document, so it constrains paraphrasing without
        // blocking schema inference. Deleting it would let the model summarize the user's sentences.
        Assert.Contains(
            "verbatim inside string values", Json.Instruction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Teams_offers_links_and_forbids_html()
    {
        var teams = TextActionCatalog.Find("format-for-teams")!;

        Assert.Contains("[label](https://example.com)", teams.Instruction, StringComparison.Ordinal);
        Assert.Contains("no HTML", teams.Instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not use a heading", teams.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void No_rulebook_block_contains_an_em_or_en_dash()
    {
        foreach (var block in (string[])
            [EnrichmentRules.Detection, EnrichmentRules.Restraint, EnrichmentRules.Preservation, EnrichmentRules.Local])
        {
            Assert.DoesNotContain('—', block);
            Assert.DoesNotContain('–', block);
        }
    }

    [Fact]
    public void Restraint_states_the_countable_ceilings()
    {
        // The bold-everything failure is prevented by making emphasis an eligibility lookup in the
        // author's own marker words, not a judgement about what is interesting. The ceilings that
        // do that work are countable, and each one is pinned here so an edit cannot quietly turn a
        // count into a judgement call.
        Assert.Contains("at most four words", EnrichmentRules.Restraint, StringComparison.Ordinal);
        Assert.Contains("At most one bold phrase per paragraph", EnrichmentRules.Restraint, StringComparison.Ordinal);
        Assert.Contains("Three items is the minimum", EnrichmentRules.Restraint, StringComparison.Ordinal);
        Assert.Contains("Three rows and two columns minimum", EnrichmentRules.Restraint, StringComparison.Ordinal);

        // The list veto: items that need a connective to attach to the previous one are an argument,
        // not a list. This is what stops a reasoned paragraph becoming three unranked bullets.
        Assert.Contains("are one argument and they stay as prose", EnrichmentRules.Restraint, StringComparison.Ordinal);
    }

    [Fact]
    public void Order_never_disqualifies_a_list_it_only_chooses_numbered_over_bulleted()
    {
        // The veto used to ask whether the items "still mean the same thing in any order" and treated
        // a no as proof of an argument. That vetoed the content that most wants a numbered list: an
        // author who writes "the first item is X, the second item is Y" has produced items that
        // reorder badly precisely BECAUSE they are a sequence. Peer-ness decides list versus prose;
        // order only decides numbered versus bulleted.
        Assert.Contains(
            "never disqualifies a list", EnrichmentRules.Restraint, StringComparison.Ordinal);
        Assert.Contains(
            "Peers in a stated order become a numbered list", EnrichmentRules.Restraint, StringComparison.Ordinal);

        // The discredited test must not come back.
        Assert.DoesNotContain(
            "still mean the same thing in any order", EnrichmentRules.Restraint, StringComparison.Ordinal);
    }

    [Fact]
    public void Restraint_names_which_span_to_bold_not_only_how_long_it_may_be()
    {
        // The four-word ceiling on its own told the model how much it could bold and nothing about
        // what to bold, so it took the whole clause around a marked phrase and then tripped the
        // ceiling on a phrase it had picked correctly. The span rule is what makes the count usable.
        Assert.Contains("bold the marked", EnrichmentRules.Restraint, StringComparison.Ordinal);
        Assert.Contains("by July 3", EnrichmentRules.Restraint, StringComparison.Ordinal);
    }

    [Fact]
    public void Restraint_resolves_the_number_contradiction_rather_than_leaving_it()
    {
        // "a deadline, a cost" was eligible and "a number" was not, one sentence apart, and a
        // deadline is a date and a cost is a number. The model resolved that by bolding nothing.
        Assert.DoesNotContain(
            "a proper noun, a number, a heading", EnrichmentRules.Restraint, StringComparison.Ordinal);
        Assert.Contains("the number IS the marked thing", EnrichmentRules.Restraint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_marker_word_test_does_not_govern_the_value_only_half_of_the_eligibility_list()
    {
        // Emphasis is eligible either by a marker word or by a named deadline, blocker, cost or
        // decision. "The word has to be doing the marking" was written to reject "critical CSS",
        // but it measures whether a WORD is marking, which is false for every value-only trigger by
        // construction: "before Friday" carries no marking word at all. Measured over 9,936 graded
        // cells, value-only triggers missed 71.4% against 35.6% for marker-word ones.
        Assert.Contains(
            "A marker word has to be doing the marking rather than sitting inside a name",
            EnrichmentRules.Restraint,
            StringComparison.Ordinal);
        Assert.Contains(
            "is marked by being the thing the reader has to act on, not by any word beside it",
            EnrichmentRules.Restraint,
            StringComparison.Ordinal);

        // The unqualified form is what made the clause fire on the value-only half.
        Assert.DoesNotContain(
            "The word has to be doing the marking", EnrichmentRules.Restraint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_recoverable_signals_list_includes_the_deadline_the_author_named()
    {
        // The "Doing nothing" paragraph is the escape hatch out of the no-markup answer, and it
        // re-listed only the marker-word half, silently closing over the deadline, cost, blocker
        // and decision that the emphasis rule had just made eligible.
        Assert.Contains(
            "named the deadline, the cost, the blocker or the decision themselves",
            EnrichmentRules.Restraint,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_serial_and_of_an_enumeration_never_vetoes_a_list()
    {
        // The peer test names six subordinating connectives; the clause after it generalised to
        // "a connecting word". Every enumeration written inside one sentence ends with a serial
        // "and", which you must drop to put the items on their own lines, so the clause vetoed the
        // exact construction Detection's first bullet exists to catch. 137 of the 205 corpus
        // scenarios that want a list carry one.
        Assert.Contains(
            "drop one of those six words from", EnrichmentRules.Restraint, StringComparison.Ordinal);
        Assert.Contains(
            "dropping that one word is how a sentence full of peers becomes a list",
            EnrichmentRules.Restraint,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "drop a connecting word from", EnrichmentRules.Restraint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_one_list_cap_yields_to_a_task_that_names_its_lists()
    {
        // rewrite-for-ai's own instruction names three lists (steps, Constraints, Acceptance
        // criteria) and Restraint capped the whole text at one, with no third option: folding the
        // constraints into the steps breaks "do not mix a step, a fact and a question", and opening
        // the constraints list breaks the cap. The cap still forbids a second list for leftovers.
        Assert.Contains(
            "unless the task above names the lists it wants", EnrichmentRules.Restraint, StringComparison.Ordinal);
        Assert.Contains(
            "Never open a second list to hold the items that did not fit the first",
            EnrichmentRules.Restraint,
            StringComparison.Ordinal);

        var brief = TextActionCatalog.Find("rewrite-for-ai")!;
        Assert.Contains("Constraints list", brief.Instruction, StringComparison.Ordinal);
        Assert.Contains("Acceptance criteria list", brief.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_row_is_not_demoted_for_reading_conversationally()
    {
        // The Tables paragraph vetoed and mandated the same content two sentences apart: "a label
        // followed by a sentence of prose is a list, not a table", then "three invoice lines each
        // with their days and their rate are a table already". structured-data-001 is literally
        // three invoice lines with their days and their rate, and should-table failed 63.3% of its
        // HTML cells, 67 of 76 misses coming back as paragraphs only.
        Assert.Contains(
            "A label followed by one sentence of prose is a list, not a table",
            EnrichmentRules.Restraint,
            StringComparison.Ordinal);
        Assert.Contains(
            "the same two or three short values every other row carries is a table row",
            EnrichmentRules.Restraint,
            StringComparison.Ordinal);
        Assert.Contains("three invoice lines", EnrichmentRules.Restraint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_record_floor_is_the_same_number_in_both_blocks()
    {
        // Detection admitted repeated structure at two records, Restraint set the table floor at
        // three, and nothing said what a two-record run looks like, so both exits were closed and
        // paragraphs came back. Detection now names the shape a pair of records gets.
        Assert.Contains("Three rows and two columns minimum", EnrichmentRules.Restraint, StringComparison.Ordinal);
        Assert.Contains(
            "Two records are repeated structure but not yet a table",
            EnrichmentRules.Detection,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Emphasis_markup_is_formatting_rather_than_a_change_of_value()
    {
        // SharedPreamble says every number and date must appear exactly as written, and Preservation
        // repeats it, then carves out only two cases: convention spelling and code formatting. Bold
        // was not in the list, so the one thing Restraint most wants bolded (a bare date, price or
        // limit) was also the one thing the preservation rules appeared to freeze.
        Assert.Contains(
            "Wrapping a value in the bold or italic markup the task allows is formatting too",
            EnrichmentRules.Preservation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Code_eligibility_beats_the_plain_word_exclusion_on_a_token_that_is_both()
    {
        // Every member of the exclusion list also appears on the eligibility list one sentence
        // above: HTTP/2 is a version number and a protocol name, Ctrl is a key and an ordinary
        // word, and a message the software prints is made of ordinary English. Those exact tokens
        // are the recurring should-code misses.
        Assert.Contains(
            "unless that same token also appears on the list above",
            EnrichmentRules.Detection,
            StringComparison.Ordinal);
        Assert.Contains("Ctrl is a key", EnrichmentRules.Detection, StringComparison.Ordinal);
    }

    [Fact]
    public void Detection_points_sentence_shape_at_the_task_rather_than_at_the_conventions()
    {
        // Detection handed sentence and paragraph shape to the formatting conventions; the tie-break
        // in TextActionPrompt takes it back for the task, in the clause that lets fix-grammar keep
        // the author's boundaries. Two ownership assignments over one decision naming opposite
        // owners is the arbitrary-resolution case the tie-break exists to prevent.
        Assert.Contains("the task wins wherever the two differ", EnrichmentRules.Detection, StringComparison.Ordinal);
        Assert.DoesNotContain("and they own that", EnrichmentRules.Detection, StringComparison.Ordinal);
    }

    [Fact]
    public void A_signal_is_dropped_for_capability_or_for_the_task_saying_so_and_nothing_else()
    {
        // "Do not approximate it with different markup" and "never because carrying it takes a
        // different form here" are three sentences apart and give opposite answers about the same
        // move, and the Teams instruction forbids a block quote the same paragraph says Teams can
        // render. The permission and the prohibition now describe different things.
        Assert.Contains("Do not substitute a different signal for it", EnrichmentRules.Detection, StringComparison.Ordinal);
        Assert.Contains(
            "or when the task above names that markup and tells you not to use it here",
            EnrichmentRules.Detection,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Do not approximate it with different markup", EnrichmentRules.Detection, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rulebook_says_missing_structure_is_a_failure_too()
    {
        // Every ceiling in Restraint is satisfiable by emitting nothing, and a graded run found the
        // model taking that exit about half the time on text whose author had counted their own
        // items. Both blocks now state the obligation in both directions.
        Assert.Contains("is the same failure from the other side", EnrichmentRules.Detection, StringComparison.Ordinal);
        Assert.Contains(
            "is a failure rather than the safe choice", EnrichmentRules.Restraint, StringComparison.Ordinal);
    }

    [Fact]
    public void Emphasis_eligibility_is_judged_against_the_original_text()
    {
        // Widening Detection to license value-only triggers had a side effect the graded run caught:
        // on rewrite-for-ai the model began bolding prohibitions IT had introduced while rewriting.
        // "so we stop shipping staging credentials" came back as bold "Do not ship", emphasis the
        // author never placed. The existing "point to it in the author's own words" rule did not
        // bite, because after a rewrite every phrase is the model's own.
        Assert.Contains(
            "judge eligibility against the ORIGINAL", EnrichmentRules.Restraint, StringComparison.Ordinal);
        Assert.Contains(
            "the emphasis would be yours and not theirs",
            EnrichmentRules.Restraint,
            StringComparison.Ordinal);
    }
}
