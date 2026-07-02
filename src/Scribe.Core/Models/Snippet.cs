namespace Scribe.Core.Models;

/// <summary>
/// A voice snippet: speaking the trigger <see cref="Phrase"/> expands to the (possibly multi-line)
/// <see cref="Template"/> during post-processing. Distinct from a dictionary entry: templates can
/// be long, are matched as a whole phrase, and are never folded into the AI cleanup glossary.
/// </summary>
public sealed record Snippet(
    long Id,
    string Phrase,
    string Template,
    bool Enabled = true)
{
    /// <summary>A not-yet-persisted snippet (Id 0).</summary>
    public static Snippet New(string phrase, string template) => new(0, phrase, template);
}
