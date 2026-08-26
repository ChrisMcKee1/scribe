using System.Text.Json;
using System.Text.RegularExpressions;
using Scribe.StyleEval.Markup;

namespace Scribe.StyleEval.Checks;

/// <summary>
/// The positive half: did the answer MISS structure the content actually warranted?
/// </summary>
/// <remarks>
/// <para>
/// This is the half a deterministic suite normally skips, and skipping it leaves a hole big enough
/// to drive a regression through. Every negative checker above is satisfied by an answer that does
/// nothing at all: zero bold clears the emphasis ceiling, zero lists clear the list ceiling, zero
/// headings clear the blacklist. A model that reads a deadline, three peer items and a file path and
/// returns flat prose scores a perfect negative sheet while producing a worse result than a careful
/// human editor would.
/// </para>
/// <para>
/// These checkers only fire where the answer could have expressed the construct and where the action
/// was actually given <c>EnrichmentRules.Detection</c>. Everywhere else they return NotApplicable,
/// because a tone rewrite that adds no table is following its instruction, not failing it.
/// </para>
/// </remarks>
internal static partial class PositiveChecks
{
    private const CheckPolarity Pos = CheckPolarity.Positive;

    /// <summary>
    /// The words <c>EnrichmentRules.Restraint</c> names as making a phrase bold-eligible, verbatim.
    /// </summary>
    /// <remarks>
    /// Whole words. A substring test accepts "commonly" as carrying "only" and "mustard" as carrying
    /// "must", which would let any bold at all justify itself.
    /// </remarks>
    [GeneratedRegex(@"\b(important|critical|must|only|never|do not|don['’]t|note that|key thing|blocker)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MarkerWord { get; }

    /// <summary>Every phrase the author's own marker words made bold-eligible is actually bold.</summary>
    public static CheckResult ShouldBold(CheckContext c)
    {
        const string Name = "should-bold";

        if (c.Scenario.ShouldBold.Count == 0)
        {
            return CheckResult.Skip(Name, Pos, "the scenario names no bold-eligible phrase");
        }

        if (!Destinations.DetectionApplies(c.Action))
        {
            return CheckResult.Skip(Name, Pos, $"{c.Action.Id} is not given the Detection rules");
        }

        if (!Destinations.SupportsBold(c.Destination))
        {
            return CheckResult.Skip(Name, Pos, $"{c.Destination} has no emphasis");
        }

        if (c.Markup.ParseError is not null)
        {
            return CheckResult.Skip(Name, Pos, $"unreadable answer: {c.Markup.ParseError}");
        }

        var bold = c.Markup.Bold;

        // Nothing bolded is the failure this half exists to catch.
        if (bold.Count == 0)
        {
            return CheckResult.Fail(Name, Pos,
                $"the author marked {string.Join(", ", c.Scenario.ShouldBold.Select(m => $"'{TextTools.Clip(m, 40)}'"))} " +
                "and the answer bolds nothing at all");
        }

        var missed = c.Scenario.ShouldBold
            .Where(phrase => !bold.Any(b => TextTools.PhraseMatches(b.Text, phrase)))
            .ToList();

        // Partial coverage is a PASS on purpose, and the reason is the rulebook rather than leniency.
        // Restraint caps emphasis at one phrase per paragraph and tells Teams to bold at most one
        // thing in the whole message, so a scenario naming three eligible phrases cannot be satisfied
        // by bolding all three. Choosing one of them is the correct answer.
        if (missed.Count < c.Scenario.ShouldBold.Count)
        {
            return missed.Count == 0
                ? CheckResult.Pass(Name, Pos, $"all {c.Scenario.ShouldBold.Count} marked phrase(s) came back bold")
                : CheckResult.Pass(Name, Pos,
                    $"emphasised {c.Scenario.ShouldBold.Count - missed.Count} of {c.Scenario.ShouldBold.Count} " +
                    $"marked phrase(s); left {string.Join(", ", missed.Select(m => $"'{TextTools.Clip(m, 30)}'"))} unmarked, " +
                    "which the one-bold-per-paragraph ceiling allows");
        }

        // The answer bolded something, but nothing the scenario named. Accept it only when what it
        // did bold carries one of the marker words Restraint actually lists, since the scenario's
        // shouldBold is one editor's choice among the eligible phrases rather than the only one.
        var justified = bold.FirstOrDefault(b => MarkerWord.IsMatch(b.Text));

        if (justified is not null)
        {
            return CheckResult.Pass(Name, Pos,
                $"emphasised '{TextTools.Clip(justified.Text, 40)}', a different phrase the author's own " +
                "marker words made eligible");
        }

        return CheckResult.Fail(Name, Pos,
            $"missed emphasis on {string.Join(", ", c.Scenario.ShouldBold.Select(m => $"'{TextTools.Clip(m, 40)}'"))}; " +
            $"the answer bolds {string.Join(", ", bold.Take(3).Select(b => $"'{TextTools.Clip(b.Text, 30)}'"))} instead, " +
            "which carries no marker word either");
    }

    /// <summary>Three or more genuine peer items became a list.</summary>
    public static CheckResult ShouldList(CheckContext c)
    {
        const string Name = "should-list";

        if (!c.Scenario.ShouldList)
        {
            return CheckResult.Skip(Name, Pos, "the scenario has no run of three peer items");
        }

        if (!Destinations.DetectionApplies(c.Action))
        {
            return CheckResult.Skip(Name, Pos, $"{c.Action.Id} is not given the Detection rules");
        }

        if (c.Destination == Destination.Json)
        {
            if (c.Json is null)
            {
                return CheckResult.Skip(Name, Pos, "the answer does not parse as JSON");
            }

            var biggest = JsonWalker.Arrays(c.Json.RootElement)
                .Select(a => a.GetArrayLength())
                .DefaultIfEmpty(0)
                .Max();

            return biggest >= 3
                ? CheckResult.Pass(Name, Pos, $"three peer items became an array of {biggest}")
                : CheckResult.Fail(Name, Pos,
                    $"three or more peer items in the text, but the largest array in the answer holds {biggest}");
        }

        if (c.Markup.ParseError is not null)
        {
            return CheckResult.Skip(Name, Pos, $"unreadable answer: {c.Markup.ParseError}");
        }

        var best = c.Markup.Lists.Select(l => l.ItemCount).DefaultIfEmpty(0).Max();

        return best >= 3
            ? CheckResult.Pass(Name, Pos, $"three peer items became a list of {best}")
            : CheckResult.Fail(Name, Pos,
                c.Markup.Lists.Count == 0
                    ? "three or more peer items in the text, but the answer contains no list"
                    : $"three or more peer items in the text, but the longest list in the answer holds {best}");
    }

    /// <summary>Records sharing the same fields became one repeated structure.</summary>
    public static CheckResult ShouldTable(CheckContext c)
    {
        const string Name = "should-table";

        if (!c.Scenario.ShouldTable)
        {
            return CheckResult.Skip(Name, Pos, "the scenario has no repeated same-field records");
        }

        if (!Destinations.DetectionApplies(c.Action))
        {
            return CheckResult.Skip(Name, Pos, $"{c.Action.Id} is not given the Detection rules");
        }

        if (!Destinations.SupportsTable(c.Destination))
        {
            return CheckResult.Skip(Name, Pos, $"{c.Destination} has no table construct");
        }

        // Two records are a genuine repeated structure, and in JSON they are an array of two objects.
        // They are NOT a table: Restraint sets the floor at three rows carrying the same fields, and
        // the scenarios that carry exactly two say so in their own notes. Grading their Markdown and
        // HTML cells for a missing table asks the model to break the ceiling it was given.
        if (c.Scenario.RecordCount == 2 && c.Destination is Destination.Markdown or Destination.Html)
        {
            return CheckResult.Skip(Name, Pos,
                "two records is below the three-row table floor, so paired lines are the correct answer here");
        }

        if (c.Destination == Destination.Markdown && !Destinations.IsDocumentDestination(c.Action.Id))
        {
            return CheckResult.Skip(Name, Pos, $"{c.Action.Id} writes a brief, not a document, and is never asked for a table");
        }

        if (c.Destination == Destination.Json)
        {
            if (c.Json is null)
            {
                return CheckResult.Skip(Name, Pos, "the answer does not parse as JSON");
            }

            var uniform = JsonWalker.Arrays(c.Json.RootElement).FirstOrDefault(a => IsUniformObjectArray(a, out _));
            if (uniform.ValueKind == JsonValueKind.Array)
            {
                IsUniformObjectArray(uniform, out var keys);
                return CheckResult.Pass(Name, Pos,
                    $"repeated records became an array of {uniform.GetArrayLength()} objects sharing {keys} key(s)");
            }

            return CheckResult.Fail(Name, Pos,
                "two or more records share the same fields, but the answer holds no array of objects with one shape");
        }

        if (c.Markup.ParseError is not null)
        {
            return CheckResult.Skip(Name, Pos, $"unreadable answer: {c.Markup.ParseError}");
        }

        var table = c.Markup.Tables.FirstOrDefault(t => t.RowCount >= 2 && t.ColumnCount >= 2);
        return table is not null
            ? CheckResult.Pass(Name, Pos, $"repeated records became a {table.RowCount} by {table.ColumnCount} table")
            : CheckResult.Fail(Name, Pos,
                c.Markup.Tables.Count == 0
                    ? "two or more records share the same fields, but the answer contains no table"
                    : "the answer has a table, but not one with two or more rows and columns");
    }

    private static bool IsUniformObjectArray(JsonElement array, out int keyCount)
    {
        keyCount = 0;
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() < 2)
        {
            return false;
        }

        HashSet<string>? shape = null;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var keys = item.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            if (shape is null)
            {
                shape = keys;
                continue;
            }

            if (!shape.SetEquals(keys))
            {
                return false;
            }
        }

        keyCount = shape?.Count ?? 0;
        return keyCount > 0;
    }

    /// <summary>Identifiers, paths, commands, flags and error strings got code formatting.</summary>
    public static CheckResult ShouldCode(CheckContext c)
    {
        const string Name = "should-code";

        if (c.Scenario.ShouldCode.Count == 0)
        {
            return CheckResult.Skip(Name, Pos, "the scenario names no code-eligible token");
        }

        if (!Destinations.DetectionApplies(c.Action))
        {
            return CheckResult.Skip(Name, Pos, $"{c.Action.Id} is not given the Detection rules");
        }

        if (!Destinations.SupportsCode(c.Destination))
        {
            return CheckResult.Skip(Name, Pos, $"{c.Destination} has no code formatting");
        }

        if (c.Markup.ParseError is not null)
        {
            return CheckResult.Skip(Name, Pos, $"unreadable answer: {c.Markup.ParseError}");
        }

        var code = c.Markup.AllCode.ToList();
        var missed = c.Scenario.ShouldCode
            .Where(token => !code.Any(span => span.Contains(token, StringComparison.Ordinal)))
            .ToList();

        if (missed.Count == 0)
        {
            return CheckResult.Pass(Name, Pos, $"all {c.Scenario.ShouldCode.Count} identifier(s) came back in code formatting");
        }

        var detail = code.Count == 0
            ? "the answer uses no code formatting at all"
            : $"{code.Count} code run(s) present, but not around these";

        return CheckResult.Fail(Name, Pos,
            $"left unmarked: {string.Join(", ", missed.Select(m => $"'{TextTools.Clip(m, 40)}'"))}; {detail}");
    }
}
