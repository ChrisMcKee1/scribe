namespace Scribe.Core.Libraries;

/// <summary>How library terms of different origins rank against each other when two libraries supply one spoken form.</summary>
public enum LibraryPrecedenceRule
{
    /// <summary>
    /// Decision 1 as recommended: authored library terms (custom rows, and edited, added or pinned built-in rows) beat
    /// shipped terms, and a legacy custom row that contradicted a built-in at the upgrade keeps the built-in's result
    /// while that built-in is on, until the user chooses Use my spelling. Nothing changes on upgrade.
    /// </summary>
    AuthoredFirstWithLegacyMarkers,

    /// <summary>
    /// Veto (b): authored terms beat shipped terms everywhere at once, legacy markers ignored, so a custom row that
    /// contradicted a built-in wins from the upgrade on.
    /// </summary>
    AuthoredFirstEverywhere,

    /// <summary>
    /// Veto (c): shipped terms beat authored terms, so an edit can lose to another library's shipped copy. With no edits
    /// this is exactly 0.4.3's order: every built-in row, then every custom row.
    /// </summary>
    ShippedFirst,
}

/// <summary>
/// The two maintainer decisions the library model rests on, each behind one named policy point so a veto is a change to
/// this file and its tests alone (W1b contracts 0 rule 6, 3.3.1). Composition reads <see cref="Precedence"/>; adoption,
/// the AI permission policy and the workspace (as a delegate) read <see cref="DefaultAiPermission"/>. Nothing else
/// encodes either decision.
/// </summary>
public static class LibraryDecisions
{
    /// <summary>
    /// Decision 1: authored library terms beat shipped terms; rows that contradicted a built-in at the upgrade keep the
    /// built-in's result until the user chooses. Veto (b) is <see cref="LibraryPrecedenceRule.AuthoredFirstEverywhere"/>,
    /// veto (c) is <see cref="LibraryPrecedenceRule.ShippedFirst"/>.
    /// </summary>
    public const LibraryPrecedenceRule Precedence = LibraryPrecedenceRule.AuthoredFirstWithLegacyMarkers;

    /// <summary>
    /// Decision 2: whether a library is sent to AI cleanup as vocabulary when nobody has chosen. Built-ins on; created,
    /// imported, restored, changed-outside and discovered libraries off; a duplicate or a retired built-in's kept rows
    /// inherit their source's permission; libraries existing at the upgrade (<see cref="LibraryOrigin.Existing"/>, which
    /// only adoption records) stay on.
    /// </summary>
    /// <param name="origin">How the library came to be.</param>
    /// <param name="builtIn">Whether it ships with Scribe.</param>
    /// <param name="sourcePermission">
    /// For <see cref="LibraryOrigin.Duplicated"/> and <see cref="LibraryOrigin.RetiredBuiltIn"/>: the permission of the
    /// library it came from, as the draft shows it. Null when that is unknown, which inherits nothing and fails closed.
    /// Ignored for every other origin.
    /// </param>
    /// <remarks>
    /// The veto alternatives are one expression each: (b) "on everywhere, as today" returns true; (c) "off for every custom
    /// library, existing ones included" returns <paramref name="builtIn"/>, with the upgrade notice listing what changed.
    /// </remarks>
    public static bool DefaultAiPermission(LibraryOrigin origin, bool builtIn, bool? sourcePermission)
    {
        if (builtIn)
        {
            return true;
        }

        return origin switch
        {
            LibraryOrigin.Existing => true,
            LibraryOrigin.Duplicated or LibraryOrigin.RetiredBuiltIn => sourcePermission ?? false,
            _ => false,
        };
    }
}
