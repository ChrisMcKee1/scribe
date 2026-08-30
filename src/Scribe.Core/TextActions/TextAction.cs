namespace Scribe.Core.TextActions;

/// <summary>
/// Which panel of the palette an action belongs to. Order here is the order groups render in.
/// </summary>
public enum TextActionGroup
{
    /// <summary>Rewrites that change tone, clarity or structure of prose.</summary>
    Rewrite,

    /// <summary>Reshapes the text for a particular destination (Teams, Markdown, JSON).</summary>
    Format,

    /// <summary>Deterministic passes that need no model.</summary>
    Vocabulary,
}

/// <summary>
/// Whether an action needs a language model. Deterministic actions run offline against the user's
/// own dictionary, which is what keeps the palette useful on a default install where AI cleanup is
/// off.
/// </summary>
public enum TextActionKind
{
    /// <summary>Runs locally with no model, using the user's dictionary and libraries.</summary>
    Deterministic,

    /// <summary>Sends the selection to the configured cleanup model.</summary>
    Ai,
}

/// <summary>
/// How much the output length is allowed to differ from the selection. The cleanup pipeline's single
/// ramble bound assumes output length tracks input length, which is true for transcript cleanup and
/// false here: "make it concise" and "rewrite for an agent" move in opposite directions. Each action
/// therefore carries its own band, and <see cref="TextActionSanitizer"/> enforces it.
/// </summary>
public enum TextActionLength
{
    /// <summary>Roughly the same size: tone and grammar edits. 0.4x to 2.0x.</summary>
    Similar,

    /// <summary>Deliberately shorter. 0.1x to 1.1x.</summary>
    Shorter,

    /// <summary>Deliberately longer. 0.8x to 6.0x.</summary>
    Longer,

    /// <summary>
    /// Structure changes where length is not a meaningful signal (code fences, JSON, task lists).
    /// Only the empty and absurd-runaway bounds apply.
    /// </summary>
    Restructure,
}

/// <summary>
/// One entry in the text action palette: what it is called, what it does to the selection, and the
/// instruction fragment that is composed into the system prompt for AI actions.
/// </summary>
/// <param name="Id">
/// Stable identifier used for settings, per-action preferences and telemetry counts. Never
/// localized, never renamed once shipped.
/// </param>
/// <param name="Label">Short name shown on the palette row.</param>
/// <param name="Description">
/// One line of plain English under the label saying what the action does. The palette shows this
/// permanently rather than on hover: the whole point of a palette over a context menu is room to
/// read what an action will do before it rewrites your text.
/// </param>
/// <param name="Instruction">
/// The action-specific portion of the system prompt, composed after the shared preamble. Empty for
/// deterministic actions.
/// </param>
/// <param name="UsesGlossary">
/// Whether the user's vocabulary is rendered into the prompt. False for actions where the glossary
/// is noise or actively harmful: a JSON conversion does not benefit from a product-name list, and
/// spending a small model's context on one crowds out the text being converted.
/// </param>
/// <param name="Advanced">
/// Hidden behind the palette's "More formats" disclosure. Keeps the default surface short without
/// cutting the capability.
/// </param>
/// <param name="Enrichment">
/// How much of <see cref="EnrichmentRules"/> this action receives. Separate from
/// <see cref="Length"/> because they answer different questions: Length is how much the output may
/// grow, Enrichment is whether the model may add structure at all. A proofread and a JSON
/// conversion can share a length band and must never share an enrichment level.
/// </param>
public sealed record TextAction(
    string Id,
    string Label,
    string Description,
    TextActionGroup Group,
    TextActionKind Kind,
    string Instruction,
    bool UsesGlossary = true,
    TextActionLength Length = TextActionLength.Similar,
    bool Advanced = false,
    EnrichmentLevel Enrichment = EnrichmentLevel.PreserveOnly)
{
    /// <summary>True when this action needs a configured, Ready cleanup model to run at all.</summary>
    public bool RequiresModel => Kind == TextActionKind.Ai;
}
