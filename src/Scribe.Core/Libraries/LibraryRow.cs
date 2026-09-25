namespace Scribe.Core.Libraries;

/// <summary>
/// Where a row's current values come from. Every origin except <see cref="Shipped"/> is authored, the middle tier of
/// Decision 1's precedence (the dictionary, then authored library terms, then shipped terms); the tier rules
/// themselves, and the legacy markers that keep a pre-upgrade result, belong to composition.
/// </summary>
public enum TermOrigin
{
    /// <summary>A row of a custom library. Authored.</summary>
    Custom,

    /// <summary>A built-in row exactly as the running version ships it, with no edit. The only shipped-tier origin.</summary>
    Shipped,

    /// <summary>A built-in row with at least one value the user changed. Authored until Restore built-in values.</summary>
    Edited,

    /// <summary>
    /// A built-in row whose authored values equal what the running version ships (a later version adopted the edit, or
    /// the user chose Use updated values). Authored until Restore built-in values, so adopting it upstream never
    /// demotes it below another library's term (review finding R4).
    /// </summary>
    Pinned,

    /// <summary>A built-in row the user turned off. Authored; contributes no rule and suppresses no other library's copy.</summary>
    Off,

    /// <summary>A row the user added to a built-in library. Authored.</summary>
    Added,

    /// <summary>
    /// An edited or added row of a built-in whose shipped counterpart the running version no longer ships. Its authored
    /// values stay in use, like an added row, and the row says "No longer shipped".
    /// </summary>
    NoLongerShipped,
}

/// <summary>The four stored values of a term, as flags, for saying which of them differ.</summary>
[Flags]
public enum TermFields
{
    None = 0,
    Spoken = 1,
    Written = 2,
    WholeWord = 4,
    Enabled = 8,
}

/// <summary>
/// A built-in row that needs the user's decision after an upgrade: a field the user changed that the new version also
/// changed differently. Term details compares the two field by field; Keep my changes and Use updated values
/// (<see cref="TermReviewChoice"/>) resolve it.
/// </summary>
/// <param name="Yours">The values in use now, the user's.</param>
/// <param name="UpdatedBuiltIn">What the running version ships for this row.</param>
/// <param name="Differing">
/// The fields that differ between the two; never <see cref="TermFields.None"/>. Every differing field, not only the
/// ones both sides changed: a UI that highlights the conflicting fields derives them from the entry's value objects
/// (<see cref="BuiltInTermEdit.Base"/>, <see cref="BuiltInTermEdit.Value"/> and <see cref="BuiltInTermEdit.Acknowledged"/>).
/// </param>
public sealed record TermReview(TermValues Yours, TermValues UpdatedBuiltIn, TermFields Differing);

/// <summary>How the user resolves a <see cref="TermReview"/>.</summary>
public enum TermReviewChoice
{
    /// <summary>Keep the values in use and stop asking about this shipped version (it becomes the acknowledged base).</summary>
    KeepMine,

    /// <summary>Take the updated built-in values; the row stays authored (<see cref="TermOrigin.Pinned"/>).</summary>
    UseUpdated,
}

/// <summary>
/// One effective row of a library as every consumer sees it: its identity, the values in use, where they come from,
/// and for a built-in row the shipped values and the edit behind it. Rows of a library are listed in saved order.
/// </summary>
/// <remarks>
/// <para>
/// Invariants, which the producer of a row keeps (the custom library loader and the editor for custom rows, the
/// built-in overlay for built-in rows):
/// </para>
/// <list type="bullet">
/// <item><see cref="TermOrigin.Custom"/>: <see cref="Key"/> is <c>LibraryTermKey.From(Values.Spoken)</c>, and
/// <see cref="Shipped"/>, <see cref="Edit"/> and <see cref="Review"/> are null.</item>
/// <item><see cref="TermOrigin.Shipped"/>: <see cref="Shipped"/> equals <see cref="Values"/>, and <see cref="Edit"/> and
/// <see cref="Review"/> are null.</item>
/// <item>Every other origin: <see cref="Edit"/> is the document entry the values come from, and <see cref="Key"/> is
/// its key: the key of the original shipped spoken form, kept when the user changes Spoken, or for an added row the
/// key of the spoken form it was added with.</item>
/// <item><see cref="TermOrigin.Off"/>: <see cref="Values"/> has <see cref="TermValues.Enabled"/> false.</item>
/// <item><see cref="TermOrigin.NoLongerShipped"/>: <see cref="Shipped"/> is null.</item>
/// <item><see cref="Review"/> is non-null only on an authored built-in row whose shipped values changed in a field
/// the user also changed, to a different value.</item>
/// </list>
/// <para>
/// Keys are unique within a library once it is saved; a legacy file may still hold a repeated spoken form, which loads
/// faithfully and must be resolved before that library can be saved again. Record equality compares
/// <see cref="Values"/> and the other members by value, which is what undo and change detection rely on.
/// </para>
/// <para>
/// <see cref="Key"/> is identity, not the pattern that competes (review question 2). Composition and the editor's
/// duplicate check compare the spoken form a row writes, <c>LibraryTermKey.From(Values.Spoken)</c>, and a legacy marker
/// is stored under it (while finding the built-in rows it answers by <see cref="PostProcessing.SpokenFormFold"/>);
/// <see cref="Key"/> only says which shipped row an edit belongs to, so upgrades find it. For a custom row the two are
/// the same. A built-in row whose Spoken the user changed from "get hub" to "git hub" competes as "git hub" (authored
/// tier), no longer supplies "get hub", and is still found by the key "get hub" when a later version changes that
/// shipped row.
/// </para>
/// </remarks>
/// <param name="Key">The row's identity within its library.</param>
/// <param name="Values">The values in use.</param>
/// <param name="Origin">Where <paramref name="Values"/> come from.</param>
/// <param name="Shipped">For a built-in row the running version ships: its shipped values. Otherwise null.</param>
/// <param name="Edit">For an authored built-in row: its edits document entry. Otherwise null.</param>
/// <param name="Review">A decision the user owes after an upgrade, or null.</param>
public sealed record LibraryRow(
    LibraryTermKey Key,
    TermValues Values,
    TermOrigin Origin,
    TermValues? Shipped = null,
    BuiltInTermEdit? Edit = null,
    TermReview? Review = null)
{
    /// <summary>A row of a custom library, keyed by its spoken form.</summary>
    public static LibraryRow Custom(TermValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new LibraryRow(LibraryTermKey.From(values.Spoken), values, TermOrigin.Custom);
    }
}
