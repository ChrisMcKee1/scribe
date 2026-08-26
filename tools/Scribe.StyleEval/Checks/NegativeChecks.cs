using Scribe.Core.TextActions;
using Scribe.Evals.Interop;
using Scribe.StyleEval.Markup;

namespace Scribe.StyleEval.Checks;

/// <summary>
/// The negative half: did the answer BREAK a stated rule? Every one of these is mechanically
/// decidable, cheap and perfectly reliable, which is why they are code rather than a judge prompt.
/// </summary>
internal static class NegativeChecks
{
    private const CheckPolarity Neg = CheckPolarity.Negative;

    /// <summary>Every protected token survives byte-identical.</summary>
    public static CheckResult Preservation(CheckContext c)
    {
        const string Name = "preservation";

        if (c.Scenario.ProtectedTokens.Count == 0)
        {
            return CheckResult.Skip(Name, Neg, "the scenario protects no tokens");
        }

        var surface = c.SearchSurface;
        var missing = c.Scenario.ProtectedTokens
            .Where(t => !surface.Contains(t, StringComparison.Ordinal))
            .ToList();

        return missing.Count == 0
            ? CheckResult.Pass(Name, Neg, $"all {c.Scenario.ProtectedTokens.Count} protected tokens survived verbatim")
            : CheckResult.Fail(Name, Neg, $"dropped or altered: {string.Join(", ", missing.Select(m => $"'{TextTools.Clip(m, 60)}'"))}");
    }

    /// <summary>
    /// Spoken numbers reached digits, and the answer introduced no em or en dash the input did not
    /// have. A dash the AUTHOR wrote must still be there.
    /// </summary>
    public static CheckResult HouseStyle(CheckContext c)
    {
        const string Name = "house-style";

        var problems = new List<string>();
        var notes = new List<string>();

        if (c.Scenario.SpelledOutNumbers.Count > 0)
        {
            var surviving = c.Scenario.SpelledOutNumbers
                .Where(p => Corpus.CorpusLoader.ContainsLoose(c.Output, p))
                .ToList();

            if (surviving.Count > 0)
            {
                problems.Add($"left spelled out: {string.Join(", ", surviving.Select(s => $"'{s}'"))}");
            }
            else
            {
                notes.Add($"{c.Scenario.SpelledOutNumbers.Count} spoken number forms converted");
            }
        }

        var inputDashes = TextTools.CountDashes(c.Scenario.Text);
        var outputDashes = TextTools.CountDashes(c.Output);

        if (outputDashes > inputDashes)
        {
            problems.Add($"added {outputDashes - inputDashes} dash character(s) the selection did not contain");
        }
        else
        {
            notes.Add($"dash count {outputDashes} <= input {inputDashes}");
        }

        // A dash the author wrote is their content. Only graded where the action promises to keep
        // roughly the same text: a deliberate shortening or a structural conversion may legitimately
        // drop the clause the dash was in.
        if (c.Scenario.ContainsDash && c.Action.Length == TextActionLength.Similar && outputDashes == 0)
        {
            problems.Add("removed the author's own em or en dash");
        }

        if (problems.Count == 0 && notes.Count == 0)
        {
            return CheckResult.Skip(Name, Neg, "the scenario carries no house-style expectation");
        }

        return problems.Count == 0
            ? CheckResult.Pass(Name, Neg, string.Join("; ", notes))
            : CheckResult.Fail(Name, Neg, string.Join("; ", problems));
    }

    /// <summary>The emphasis ceilings from <c>EnrichmentRules.Restraint</c>.</summary>
    public static CheckResult RestraintBold(CheckContext c)
    {
        const string Name = "restraint-bold";

        if (!Destinations.SupportsBold(c.Destination))
        {
            return CheckResult.Skip(Name, Neg, $"{c.Destination} has no emphasis");
        }

        if (c.Markup.ParseError is not null)
        {
            return CheckResult.Skip(Name, Neg, $"unreadable answer: {c.Markup.ParseError}");
        }

        // Emphasis the author typed themselves is content, not the model's decision, so it is
        // excluded from the ceilings for the same reason short lists in the selection are.
        var authorBold = c.InputMarkup.ParseError is null
            ? c.InputMarkup.Bold.Select(b => b.Text).ToHashSet(StringComparer.Ordinal)
            : [];

        var bold = c.Markup.Bold.Where(b => !authorBold.Contains(b.Text)).ToList();

        if (c.Scenario.ExpectNoBold)
        {
            // The Teams instruction has its own answer for content that wants a table: one line per
            // row with a bold label at the front of each line. Where the scenario carries records,
            // that bold is the destination telling the model to emit it, not the model deciding a
            // phrase is interesting, so the no-emphasis expectation cannot be applied to it. Without
            // this arm every scenario that is both expectNoBold and shouldTable fails its Teams cell
            // for following the instruction.
            if (c.Destination == Destination.Teams && c.Scenario.ShouldTable)
            {
                return CheckResult.Skip(Name, Neg,
                    "Teams renders a table as one line per row with a bold label, so the bold here is instructed");
            }

            return bold.Count == 0
                ? CheckResult.Pass(Name, Neg, "no emphasis trigger in the text and no bold in the answer")
                : CheckResult.Fail(Name, Neg,
                    $"the text contains no emphasis trigger, but the answer bolds {bold.Count}: " +
                    string.Join(", ", bold.Take(3).Select(b => $"'{TextTools.Clip(b.Text, 40)}'")));
        }

        var problems = new List<string>();

        var crowded = bold.GroupBy(b => b.BlockIndex).Where(g => g.Count() > 1).ToList();
        if (crowded.Count > 0)
        {
            problems.Add($"{crowded.Count} block(s) carry more than one bold phrase");
        }

        var wordy = bold.Where(b => b.WordCount > 4).ToList();
        if (wordy.Count > 0)
        {
            problems.Add($"bold runs longer than four words: {string.Join(", ", wordy.Take(3).Select(b => $"'{TextTools.Clip(b.Text, 40)}'"))}");
        }

        var whole = bold.Where(b => b.IsWholeBlock || b.LooksLikeSentence).ToList();
        if (whole.Count > 0)
        {
            problems.Add($"bold swallowed a whole line or sentence: {string.Join(", ", whole.Take(2).Select(b => $"'{TextTools.Clip(b.Text, 40)}'"))}");
        }

        var adjacent = Adjacent(bold);
        if (adjacent > 0)
        {
            problems.Add($"{adjacent} pair(s) of bold phrases run back to back");
        }

        return problems.Count == 0
            ? CheckResult.Pass(Name, Neg, $"{bold.Count} bold run(s), all inside the ceilings")
            : CheckResult.Fail(Name, Neg, string.Join("; ", problems));
    }

    private static int Adjacent(IReadOnlyList<BoldSpan> bold)
    {
        var pairs = 0;
        foreach (var group in bold.GroupBy(b => b.BlockIndex))
        {
            var ordered = group.Where(b => b.StartInBlock >= 0).OrderBy(b => b.StartInBlock).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var previousEnd = ordered[i - 1].StartInBlock + ordered[i - 1].Text.Length;

                // Two bold runs carrying the SAME text both resolve to the first occurrence of that
                // text in the block, so the second one starts before the first one ends. Slicing
                // that range throws, which would take the whole cell down over an answer that is
                // merely repetitive, so the overlap is skipped rather than guessed at.
                if (previousEnd > ordered[i].StartInBlock || previousEnd > ordered[i].BlockText.Length)
                {
                    continue;
                }

                var gap = ordered[i].BlockText[previousEnd..ordered[i].StartInBlock];
                if (gap.Trim().Length <= 1)
                {
                    pairs++;
                }
            }
        }

        return pairs;
    }

    /// <summary>The list ceilings: nothing at all when the text is one argument, three items otherwise.</summary>
    public static CheckResult RestraintList(CheckContext c)
    {
        const string Name = "restraint-list";

        if (c.Destination == Destination.Json)
        {
            // A JSON array is a shape, not a rendering decision. Grade it in JsonCheck instead.
            return CheckResult.Skip(Name, Neg, "JSON arrays are graded by json-contract");
        }

        if (c.Markup.ParseError is not null)
        {
            return CheckResult.Skip(Name, Neg, $"unreadable answer: {c.Markup.ParseError}");
        }

        var lists = c.Markup.Lists;
        var listsInInput = c.InputMarkup.ParseError is null ? c.InputMarkup.Lists.Count : 0;

        if (c.Scenario.ExpectNoList)
        {
            // Counted against the selection for the same reason the three-item floor is: the ceiling
            // governs structure the model INTRODUCED. A dictated unified diff whose lines open with
            // "-" and "+" parses as a list, and an answer that reproduces the hunk intact must not
            // be read as having built one.
            var introduced = lists.Count - listsInInput;

            return introduced <= 0
                ? CheckResult.Pass(Name, Neg, "one connected argument, and the answer added no list")
                : CheckResult.Fail(Name, Neg,
                    $"the text is one connected argument, but the answer built {introduced} list(s) " +
                    $"({string.Join(", ", lists.Select(l => l.ItemCount + " items"))})");
        }

        // The agent brief is the one action whose own instruction names the lists it wants: a
        // Constraints list and an Acceptance criteria list, however many entries the author gave.
        // TextActionPrompt's tie-break says the task decides structure and the conventions decide
        // spelling, so the three-item floor does not govern a list the task asked for by name.
        if (c.Action.Id == "rewrite-for-ai")
        {
            return CheckResult.Pass(Name, Neg,
                $"{lists.Count} list(s); the brief's own instruction names its Constraints and " +
                "Acceptance criteria lists, so the three-item floor does not apply");
        }

        var thin = lists.Count(l => l.ItemCount is > 0 and < 3);
        var thinInInput = c.InputMarkup.ParseError is null
            ? c.InputMarkup.Lists.Count(l => l.ItemCount is > 0 and < 3)
            : 0;

        if (thin > thinInInput)
        {
            return CheckResult.Fail(Name, Neg,
                $"{thin - thinInInput} list(s) with fewer than three items that the selection did not " +
                "already have; two items stay in a sentence");
        }

        if (thin > 0)
        {
            return CheckResult.Pass(Name, Neg,
                $"{thin} short list(s), all of them already in the selection; keeping them is preservation");
        }

        return lists.Count == 0
            ? CheckResult.Pass(Name, Neg, "no list, which is always an allowed answer")
            : CheckResult.Pass(Name, Neg, $"{lists.Count} list(s), each with three or more items");
    }

    /// <summary>The headings the rulebook names and forbids, because the author did not write them.</summary>
    public static readonly IReadOnlySet<string> BlacklistedHeadings =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "summary", "overview", "background", "context", "details",
            "analysis", "conclusion", "next steps", "key points",
        };

    public static CheckResult HeadingBlacklist(CheckContext c)
    {
        const string Name = "heading-blacklist";

        if (c.Destination == Destination.Json)
        {
            return CheckResult.Skip(Name, Neg, "JSON has no headings");
        }

        if (c.Markup.ParseError is not null)
        {
            return CheckResult.Skip(Name, Neg, $"unreadable answer: {c.Markup.ParseError}");
        }

        // A heading the AUTHOR wrote is content. The rule forbids a heading whose name the model had
        // to invent, and its own wording says why: "the author did not write those words". When they
        // did write them, deleting the heading would be dropping the author's text.
        var authorHeadings = c.InputMarkup.ParseError is null
            ? c.InputMarkup.Headings
                .Select(h => h.Trim().TrimEnd(':', '.'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        var offenders = c.Markup.Headings
            .Select(h => h.Trim().TrimEnd(':', '.'))
            .Where(h => BlacklistedHeadings.Contains(h) && !authorHeadings.Contains(h))
            .ToList();

        if (offenders.Count > 0)
        {
            return CheckResult.Fail(Name, Neg,
                $"invented heading(s) the author never wrote: {string.Join(", ", offenders.Select(o => $"'{o}'"))}");
        }

        return c.Markup.Headings.Count == 0
            ? CheckResult.Pass(Name, Neg, "no headings")
            : CheckResult.Pass(Name, Neg, $"{c.Markup.Headings.Count} heading(s), none from the blacklist");
    }

    /// <summary>The action's own length band, read straight out of the shipping sanitizer.</summary>
    public static CheckResult LengthBand(CheckContext c)
    {
        const string Name = "length-band";

        var original = c.Scenario.Text.Length;
        if (original == 0)
        {
            return CheckResult.Skip(Name, Neg, "empty selection");
        }

        // The sanitizer exempts a short selection from the ratio test for structural conversions,
        // because a correct JSON rendering of fifteen characters legitimately runs past a hundred.
        // Mirror the exemption rather than inventing a second policy.
        if (c.Action.Length == TextActionLength.Restructure && original < 80)
        {
            return CheckResult.Skip(Name, Neg, "structural conversion of a selection under 80 characters");
        }

        var (min, max) = ScribeCoreInternals.LengthBounds(c.Action.Length);
        var ratio = (double)c.Output.Length / original;

        if (ratio < min)
        {
            return CheckResult.Fail(Name, Neg,
                $"{ratio:F2}x the selection, below the {c.Action.Length} floor of {min:F2}x");
        }

        return ratio > max
            ? CheckResult.Fail(Name, Neg, $"{ratio:F2}x the selection, above the {c.Action.Length} ceiling of {max:F2}x")
            : CheckResult.Pass(Name, Neg, $"{ratio:F2}x, inside the {c.Action.Length} band {min:F2}x to {max:F2}x");
    }

    /// <summary>Threshold for how much of a proofread may change and still be a proofread.</summary>
    public const double MinimalDiffThreshold = 0.15;

    /// <summary>
    /// The proofread promises a minimal diff: correct the errors, change nothing else.
    /// </summary>
    public static CheckResult MinimalDiff(CheckContext c)
    {
        const string Name = "minimal-diff";

        if (c.Action.Id != "fix-grammar")
        {
            return CheckResult.Skip(Name, Neg, "only the proofread promises a minimal diff");
        }

        var distance = TextTools.NormalizedEditDistance(c.Scenario.Text, c.Output);
        var problems = new List<string>();

        if (distance > MinimalDiffThreshold)
        {
            problems.Add($"rewrote {distance:P0} of the text, over the {MinimalDiffThreshold:P0} proofreading ceiling");
        }

        var inputSentences = TextTools.CountSentences(c.Scenario.Text);
        var outputSentences = TextTools.CountSentences(c.Output);
        if (inputSentences != outputSentences)
        {
            problems.Add($"sentence count moved from {inputSentences} to {outputSentences}; a proofread merges and splits nothing");
        }

        var inputParagraphs = TextTools.CountParagraphs(c.Scenario.Text);
        var outputParagraphs = TextTools.CountParagraphs(c.Output);
        if (inputParagraphs != outputParagraphs)
        {
            problems.Add($"paragraph count moved from {inputParagraphs} to {outputParagraphs}");
        }

        return problems.Count == 0
            ? CheckResult.Pass(Name, Neg,
                $"{distance:P0} changed, {outputSentences} sentences and {outputParagraphs} paragraphs unchanged")
            : CheckResult.Fail(Name, Neg, string.Join("; ", problems));
    }
}
