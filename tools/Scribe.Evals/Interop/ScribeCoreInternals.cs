using Scribe.Core.TextActions;

namespace Scribe.Evals.Interop;

/// <summary>
/// The few <c>internal</c> members of Scribe.Core that a sibling tool needs, re-exposed for
/// tools/Scribe.StyleEval.
/// </summary>
/// <remarks>
/// Scribe.Core grants <c>InternalsVisibleTo</c> to Scribe.Evals and not to Scribe.StyleEval, and
/// internals access is not transitive. Rather than duplicate a shipping constant into the style
/// suite (where it would drift the moment somebody retunes a band in
/// <see cref="TextActionSanitizer"/>), the suite reads it back through here. Nothing in this file
/// ships: neither project is published by build/pack.ps1.
/// </remarks>
internal static class ScribeCoreInternals
{
    /// <summary>
    /// The shipping output-to-input length band for a policy, straight from
    /// <c>TextActionSanitizer.Bounds</c>.
    /// </summary>
    public static (double Min, double Max) LengthBounds(TextActionLength length) =>
        TextActionSanitizer.Bounds(length);
}
