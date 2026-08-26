using Scribe.Core.TextActions;
using Scribe.StyleEval.Corpus;

namespace Scribe.StyleEval.Checks;

/// <summary>Runs every deterministic checker over one cell.</summary>
internal static class CheckSuite
{
    /// <summary>Names of every checker, in report order. Stable, so a results file is comparable.</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "preservation", "house-style", "restraint-bold", "restraint-list", "heading-blacklist",
        "markdown-contract", "html-contract", "json-contract", "teams-contract",
        "length-band", "minimal-diff",
        "should-bold", "should-list", "should-table", "should-code",
    ];

    /// <summary>
    /// Grades one answer.
    /// </summary>
    /// <param name="scenario">The selection that was transformed.</param>
    /// <param name="action">The shipping action that transformed it.</param>
    /// <param name="rawResponse">The model's answer before the sanitizer touched it.</param>
    /// <param name="gradedText">
    /// The text to grade: the sanitized answer when the sanitizer accepted it, otherwise the raw
    /// answer. Grading the raw answer on a rejection is deliberate: the sanitizer hands the user's
    /// own selection back on rejection, so grading its output would score the INPUT and report a
    /// perfect sheet for a cell that failed.
    /// </param>
    /// <param name="sanitizerAccepted">Whether the shipping sanitizer accepted the answer.</param>
    public static IReadOnlyList<CheckResult> Run(
        Scenario scenario,
        TextAction action,
        string rawResponse,
        string gradedText,
        bool sanitizerAccepted)
    {
        var context = new CheckContext(scenario, action, gradedText, sanitizerAccepted);
        try
        {
            return
            [
                NegativeChecks.Preservation(context),
                NegativeChecks.HouseStyle(context),
                NegativeChecks.RestraintBold(context),
                NegativeChecks.RestraintList(context),
                NegativeChecks.HeadingBlacklist(context),
                DestinationChecks.Markdown(context, rawResponse),
                DestinationChecks.Html(context),
                DestinationChecks.Json(context),
                DestinationChecks.Teams(context),
                NegativeChecks.LengthBand(context),
                NegativeChecks.MinimalDiff(context),
                PositiveChecks.ShouldBold(context),
                PositiveChecks.ShouldList(context),
                PositiveChecks.ShouldTable(context),
                PositiveChecks.ShouldCode(context),
            ];
        }
        finally
        {
            context.Dispose();
        }
    }
}
