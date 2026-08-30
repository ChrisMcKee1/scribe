using Scribe.Core.TextActions;

namespace Scribe.StyleEval.Markup;

/// <summary>Which markup vocabulary an action's answer is written in.</summary>
internal enum Destination
{
    /// <summary>Plain prose. The tone rewrites, the shortener and the proofread.</summary>
    Prose,

    /// <summary>The Teams compose box: a Markdown subset with no heading, no table, no HTML.</summary>
    Teams,

    /// <summary>CommonMark.</summary>
    Markdown,

    /// <summary>An HTML fragment.</summary>
    Html,

    /// <summary>A single JSON value.</summary>
    Json,
}

/// <summary>
/// Maps each action to its destination and to what that destination can express.
/// </summary>
/// <remarks>
/// <para>
/// The positive checkers exist to catch a model that formats nothing. They are only meaningful
/// where the destination can express the construct AND the action was actually handed the
/// Detection rules. Both gates are read off the shipping catalog rather than hardcoded: an action
/// whose <see cref="TextAction.Enrichment"/> is not <see cref="EnrichmentLevel.Full"/> never
/// receives <c>EnrichmentRules.Detection</c>, so a missing list in its answer is not a missed
/// opportunity, it is the prompt working as designed. Those cells report NotApplicable.
/// </para>
/// <para>
/// <c>rewrite-for-ai</c> is the one judgement call. Its answer is Markdown-flavoured (it asks for an
/// ordered list of steps, a Constraints list and an Acceptance criteria list), so bold, lists and
/// code apply. Headings and tables do not: nothing in its instruction asks for either, and grading a
/// brief for a missing table would manufacture failures.
/// </para>
/// </remarks>
internal static class Destinations
{
    public static Destination For(string actionId) => actionId switch
    {
        "format-for-teams" => Destination.Teams,
        "format-markdown" => Destination.Markdown,
        "format-html" => Destination.Html,
        "format-json" => Destination.Json,
        "rewrite-for-ai" => Destination.Markdown,
        _ => Destination.Prose,
    };

    /// <summary>True when the destination renders emphasis at all.</summary>
    public static bool SupportsBold(Destination d) => d is not Destination.Json;

    /// <summary>True when the destination has a list construct.</summary>
    public static bool SupportsList(Destination d) => true;

    /// <summary>True when the destination has a heading construct. Teams deliberately does not.</summary>
    public static bool SupportsHeading(Destination d) => d is Destination.Markdown or Destination.Html;

    /// <summary>True when the destination has a table or an equivalent repeated structure.</summary>
    public static bool SupportsTable(Destination d) =>
        d is Destination.Markdown or Destination.Html or Destination.Json;

    /// <summary>True when the destination can mark a token as code.</summary>
    public static bool SupportsCode(Destination d) => d is not Destination.Json;

    /// <summary>
    /// True when the positive checkers should grade this action at all: it received the Detection
    /// rules, so structure the content warranted is genuinely expected of it.
    /// </summary>
    public static bool DetectionApplies(TextAction action) => action.Enrichment == EnrichmentLevel.Full;

    /// <summary>
    /// <c>rewrite-for-ai</c> writes a brief, not a document. Grading it for a missing heading or a
    /// missing table would invent failures its instruction never asked for.
    /// </summary>
    public static bool IsDocumentDestination(string actionId) =>
        actionId is "format-markdown" or "format-html";
}
