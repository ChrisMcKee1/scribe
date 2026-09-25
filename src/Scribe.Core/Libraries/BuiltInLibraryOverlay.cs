using Scribe.Core.PostProcessing;

namespace Scribe.Core.Libraries;

/// <summary>
/// The built-in overlay (<see cref="IBuiltInLibraryOverlay"/>): a built-in library's shipped rows, with the user's edits
/// document applied, and every change the editor makes to a built-in row. The shipped library is never copied or
/// changed; what the user did lives in one versioned document per built-in, as an intent per row (edited, added, pinned
/// or off) that lasts until the user restores the shipped values, whatever later versions ship (review finding R4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Applying an entry to the shipped row S</b> it names (by key), with the entry's base B (the shipped values the user
/// edited), the user's values U and the acknowledged values A:
/// </para>
/// <list type="bullet">
/// <item>Edited: each field merges by itself. A field the user left at B takes S, so a correction a later version ships
/// reaches the row; a field the user changed keeps U; a field both changed, to different values, keeps U and asks
/// (<see cref="LibraryRow.Review"/>) unless A already holds S there. When the merged values equal S the row is
/// <see cref="TermOrigin.Pinned"/>, still authored.</item>
/// <item>Pinned: U applies whole, and any field where S differs from U and from A asks. The row is
/// <see cref="TermOrigin.Pinned"/> while U equals S and <see cref="TermOrigin.Edited"/> otherwise.</item>
/// <item>Off: S, turned off. With no S the entry shows no row, stays in the document, and applies again when S returns.</item>
/// <item>Added: U. When a later version ships the key the row stays <see cref="TermOrigin.Added"/>, authored, with its
/// shipped values set, and is <see cref="TermOrigin.Pinned"/> while U equals S.</item>
/// <item>Edited or pinned with no S: <see cref="TermOrigin.NoLongerShipped"/> with U.</item>
/// </list>
/// <para>
/// B never moves on an upgrade, so a merge always compares a new shipped version with what the user saw. Field values
/// compare ordinally, like every stored value (storage is faithful). WholeWord and Enabled can never ask: with two values,
/// a field the user and a later version both changed was changed to the same value.
/// </para>
/// <para>
/// <b>Per-field authorship</b> (the coordinator's decision in round 2, A3; plan 3.3). Each field of an edited entry is
/// either inherited, when the user's value equals the base, so the shipped value applies, or authored, when it differs,
/// so the user's value applies. An off entry authors only the check box (off); a pinned or added entry authors every
/// field. An edit (<see cref="Edit"/>, and <see cref="SetEnabled"/> on an authored row) changes the user's value only for
/// the fields the user changed relative to the displayed row: a field it leaves alone keeps its value and its base, so an
/// inherited field keeps inheriting (a later shipped change still reaches it) and an authored one stays authored. A
/// field it changes takes the typed value, and in an edited entry the shipped value in use as its base, so the row shows
/// exactly what was typed and nothing asks about that field until a later version changes it; a field typed back to the
/// shipped value inherits again.
/// </para>
/// <para>
/// <see cref="TermReview.Differing"/> is every field in which <see cref="TermReview.Yours"/> and
/// <see cref="TermReview.UpdatedBuiltIn"/> differ, because Use updated values replaces all of them; a review exists only
/// while at least one field asks (the coordinator's decision in round 2, A4, following the surface's
/// <see cref="TermReview"/>). Which fields conflict can be read from the row's entry and shipped values.
/// </para>
/// <para>
/// <b>Rows are canonical.</b> Every row this class returns is exactly what <see cref="Apply"/> gives for its entry against
/// its shipped values, so writing the entries of any rows and applying them again reproduces the rows. Row methods and
/// <see cref="Collect"/> refuse a row that is not (an <see cref="ArgumentException"/>): an authored row whose values do
/// not come from its entry would otherwise be saved as something other than what the user saw.
/// </para>
/// <para>
/// Pure and stateless: no I/O, no clock, no logging, safe to share across threads. Row methods never change the row they
/// are given (rows are immutable records).
/// </para>
/// </remarks>
public sealed class BuiltInLibraryOverlay : IBuiltInLibraryOverlay
{
    private const string NotText = "A term's spoken and written values are text: never null, and every surrogate in a pair.";

    private static readonly TermFields[] Fields = [TermFields.Spoken, TermFields.Written, TermFields.WholeWord, TermFields.Enabled];

    private BuiltInLibraryOverlay()
    {
    }

    /// <summary>The one instance; the overlay holds no state.</summary>
    public static BuiltInLibraryOverlay Instance { get; } = new();

    /// <inheritdoc />
    /// <remarks>Throws <see cref="ArgumentException"/> for a library id that is blank or not text (an unpaired surrogate).</remarks>
    public BuiltInEditsReadResult ReadEdits(string libraryId, ReadOnlySpan<byte> bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryId);
        if (!IsText(libraryId))
        {
            throw new ArgumentException("A library id is text.", nameof(libraryId));
        }

        return BuiltInLibraryEditsJson.Read(libraryId, bytes);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Throws <see cref="ArgumentException"/> for a document that would not read back as written (a blank library id, an
    /// empty or repeated key, an entry without the values its intent needs, a null value string, or an id, key or value
    /// that is not text, holding an unpaired surrogate): that is a bug upstream, and writing it would pause the library at
    /// the next start.
    /// </remarks>
    public byte[] WriteEdits(BuiltInLibraryEdits edits)
    {
        EnsureValid(edits, nameof(edits));
        return BuiltInLibraryEditsJson.Write(edits);
    }

    /// <inheritdoc />
    public IReadOnlyList<LibraryRow> Apply(DictionaryLibrary shipped, BuiltInLibraryEdits? edits)
    {
        var shippedRows = ShippedValues(shipped);
        if (edits is null)
        {
            return [.. shippedRows.Select(ShippedRow)];
        }

        EnsureValid(edits, nameof(edits));
        EnsureDocumentOf(shipped, edits, nameof(edits));

        var entries = edits.Terms.ToDictionary(term => term.Key);
        var matched = new HashSet<LibraryTermKey>();
        var rows = new List<LibraryRow>(shippedRows.Count + edits.Terms.Count);
        foreach (var values in shippedRows)
        {
            var key = LibraryTermKey.From(values.Spoken);
            rows.Add(entries.TryGetValue(key, out var entry) && matched.Add(key)
                ? RowOf(values, entry)!
                : ShippedRow(values));
        }

        foreach (var entry in edits.Terms)
        {
            if (!matched.Contains(entry.Key) && RowOf(null, entry) is { } row)
            {
                rows.Add(row);
            }
        }

        return [.. rows];
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Per-field authorship (see the class remarks): the edit changes the user's value only for the fields that differ
    /// from the displayed row. A field it leaves alone keeps its value and base: inherited, it keeps following the shipped
    /// value; authored, it stays the user's, and a question open about it stays open. A field it changes becomes the typed
    /// value against the shipped value in use as its base, so the returned row's values are exactly
    /// <paramref name="values"/>, the edit asks nothing about that field, and typing the shipped value makes it inherit
    /// again. Editing a shipped row starts an edited entry based on the shipped values. Editing a turned-off row keeps the
    /// check box authored (off, against a base that is on) unless the edit turns it on, even while the version in use ships
    /// the row off. A pinned entry keeps its base, which only Use updated values moves; a field the edit changes is
    /// acknowledged against the shipped value in use, so it asks again only when a later version changes it. Replacing the
    /// user's values whole against the original base would instead show the new shipped value where the user typed the
    /// old one, author shipped values the user never touched, and ask about a field the user had just decided.
    /// </para>
    /// <para>
    /// A value typed back to the shipped values stays authored (<see cref="TermOrigin.Pinned"/>): authorship ends only at
    /// Restore built-in values. An edit that changes nothing returns the row as it is, and one that changes only
    /// <see cref="TermValues.Enabled"/> is <see cref="SetEnabled"/>. Values that are not text (an unpaired surrogate) are an
    /// <see cref="ArgumentException"/>.
    /// </para>
    /// </remarks>
    public LibraryRow Edit(LibraryRow row, TermValues values)
    {
        EnsureCanonical(row, nameof(row));
        EnsureValues(values, nameof(values));
        if (row.Values == values)
        {
            return row;
        }

        var current = row.Values;
        if (current.Spoken == values.Spoken && current.Written == values.Written && current.WholeWord == values.WholeWord)
        {
            return SetEnabled(row, values.Enabled);
        }

        return EditValues(row, values);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Turning a shipped row off records an off intent with the shipped values as its base; turning an off row on
    /// removes the intent, so the row is shipped again. On any other row the flag is the user's value like the other
    /// three, changed as by <see cref="Edit"/>.
    /// </remarks>
    public LibraryRow SetEnabled(LibraryRow row, bool enabled)
    {
        EnsureCanonical(row, nameof(row));
        if (row.Values.Enabled == enabled)
        {
            return row;
        }

        if (row.Origin == TermOrigin.Shipped && !enabled)
        {
            return RowOf(row.Shipped, new BuiltInTermEdit(row.Key, BuiltInTermIntent.Off, row.Shipped, null))!;
        }

        if (row.Edit?.Intent == BuiltInTermIntent.Off && row.Shipped!.Enabled)
        {
            return ShippedRow(row.Shipped);
        }

        return EditValues(row, row.Values with { Enabled = enabled });
    }

    /// <inheritdoc />
    public LibraryRow? RestoreShipped(LibraryRow row)
    {
        EnsureCanonical(row, nameof(row));
        if (row.Shipped is null)
        {
            return null;
        }

        return row.Origin == TermOrigin.Shipped ? row : ShippedRow(row.Shipped);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The row's key is its spoken form's, which must not be blank, and the values must be text (no unpaired surrogate).
    /// The overlay cannot see the library, so the caller checks that no row of it already has that key (a shipped row, or
    /// an edited one whose Spoken the user changed, which keeps its original key): keys are unique within a built-in, and
    /// <see cref="Collect"/> refuses a repeat.
    /// </remarks>
    public LibraryRow Add(TermValues values)
    {
        EnsureValues(values, nameof(values));
        var key = LibraryTermKey.From(values.Spoken);
        if (key.IsEmpty)
        {
            throw new ArgumentException("A term added to a built-in library needs a spoken form.", nameof(values));
        }

        return RowOf(null, new BuiltInTermEdit(key, BuiltInTermIntent.Added, null, values))!;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Keep my changes records the shipped values in use as acknowledged, so the same shipped change asks once. Use
    /// updated values takes them whole, pinned: its base and values become the shipped values, and an earlier
    /// acknowledgment is dropped, since it was made for values the user has now let go.
    /// </remarks>
    public LibraryRow ResolveReview(LibraryRow row, TermReviewChoice choice)
    {
        EnsureCanonical(row, nameof(row));
        if (row.Review is null)
        {
            return row;
        }

        var shipped = row.Shipped!;
        var entry = row.Edit!;
        var resolved = choice switch
        {
            TermReviewChoice.KeepMine => entry with { Acknowledged = shipped },
            TermReviewChoice.UseUpdated => new BuiltInTermEdit(entry.Key, BuiltInTermIntent.Pinned, shipped, shipped),
            _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, "Not a review choice."),
        };

        return RowOf(shipped, resolved)!;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The entries come in one order whatever the history, so a document Scribe wrote is collected back unchanged from
    /// the rows it gives: first the entries of shipped rows, in shipped order (the row's entry when <paramref name="rows"/>
    /// has the row, the committed one when it does not); then the entries of rows this version does not ship (added and
    /// no-longer-shipped), in the order of <paramref name="rows"/>, which <see cref="Apply"/> keeps; then committed off
    /// entries whose shipped row is not in this version, in their committed order.
    /// </para>
    /// <para>
    /// <paramref name="rows"/> must be rows of this built-in as this overlay gives them, with unique keys, in the order
    /// <see cref="Apply"/> gives them with new rows appended; an <see cref="ArgumentException"/> otherwise.
    /// </para>
    /// </remarks>
    public BuiltInLibraryEdits? Collect(DictionaryLibrary shipped, BuiltInLibraryEdits? committed, IReadOnlyList<LibraryRow> rows)
    {
        var shippedRows = ShippedValues(shipped);
        ArgumentNullException.ThrowIfNull(rows);
        if (committed is not null)
        {
            EnsureValid(committed, nameof(committed));
            EnsureDocumentOf(shipped, committed, nameof(committed));
        }

        var shippedByKey = new Dictionary<LibraryTermKey, TermValues>();
        foreach (var values in shippedRows)
        {
            shippedByKey.TryAdd(LibraryTermKey.From(values.Spoken), values);
        }

        var rowsByKey = new Dictionary<LibraryTermKey, LibraryRow>();
        foreach (var row in rows)
        {
            if (row is null)
            {
                throw new ArgumentException("A row is missing.", nameof(rows));
            }

            EnsureBuiltInRow(row, nameof(rows));
            if (!rowsByKey.TryAdd(row.Key, row))
            {
                throw new ArgumentException("Two rows of the built-in library have the same key.", nameof(rows));
            }

            var expected = row.Edit is null
                ? shippedByKey.TryGetValue(row.Key, out var values) ? ShippedRow(values) : null
                : RowOf(shippedByKey.GetValueOrDefault(row.Key), row.Edit);
            if (expected != row)
            {
                throw new ArgumentException("A row is not one this overlay gives for this built-in library.", nameof(rows));
            }
        }

        var committedByKey = committed?.Terms.ToDictionary(term => term.Key) ?? new Dictionary<LibraryTermKey, BuiltInTermEdit>();
        var terms = new List<BuiltInTermEdit>();
        var placed = new HashSet<LibraryTermKey>();
        foreach (var values in shippedRows)
        {
            var key = LibraryTermKey.From(values.Spoken);
            if (!placed.Add(key))
            {
                continue;
            }

            if (rowsByKey.TryGetValue(key, out var row))
            {
                if (row.Edit is not null)
                {
                    terms.Add(row.Edit);
                }
            }
            else if (committedByKey.TryGetValue(key, out var kept) && kept.Intent != BuiltInTermIntent.Added)
            {
                // A shipped row the caller left out keeps its intent: shipped rows are turned off, never deleted.
                terms.Add(kept);
            }
        }

        foreach (var row in rows)
        {
            if (row.Edit is not null && !shippedByKey.ContainsKey(row.Key))
            {
                terms.Add(row.Edit);
            }
        }

        foreach (var entry in committed?.Terms ?? [])
        {
            // Added and no-longer-shipped entries are rows the user can delete, so their absence deletes them. An off
            // entry has no row while its shipped row is gone, and applies again when a later version ships it.
            if (entry.Intent == BuiltInTermIntent.Off &&
                !shippedByKey.ContainsKey(entry.Key) &&
                !rowsByKey.ContainsKey(entry.Key))
            {
                terms.Add(entry);
            }
        }

        if (terms.Count == 0)
        {
            return null;
        }

        IReadOnlyList<BuiltInTermEdit> collected = [.. terms];
        return new BuiltInLibraryEdits(shipped.Id, collected);
    }

    /// <inheritdoc />
    public IReadOnlyList<TermValues> AuthoredTerms(BuiltInLibraryEdits edits)
    {
        EnsureValid(edits, nameof(edits));
        return [.. edits.Terms.Where(term => term.Intent != BuiltInTermIntent.Off).Select(term => term.Value!)];
    }

    /// <summary>
    /// Whether an entry holds exactly the values its intent needs: edited and pinned a base and values, off a base and no
    /// values, added values and no base. The document reader and every writer of entries share this one rule.
    /// </summary>
    internal static bool HasTheValuesItsIntentNeeds(BuiltInTermIntent intent, TermValues? @base, TermValues? value) => intent switch
    {
        BuiltInTermIntent.Edited or BuiltInTermIntent.Pinned => @base is not null && value is not null,
        BuiltInTermIntent.Off => @base is not null && value is null,
        BuiltInTermIntent.Added => @base is null && value is not null,
        _ => false,
    };

    // The row an entry gives against the shipped values of its key (null when this version does not ship the key), or
    // null for an off entry whose shipped row is not here. The one definition of a row: Apply, every row method and
    // Collect's check all come through it, which is what makes rows reproducible from their entries.
    private static LibraryRow? RowOf(TermValues? shipped, BuiltInTermEdit entry)
    {
        switch (entry.Intent)
        {
            case BuiltInTermIntent.Off:
                return shipped is null
                    ? null
                    : new LibraryRow(entry.Key, shipped with { Enabled = false }, TermOrigin.Off, shipped, entry);

            case BuiltInTermIntent.Added:
                var added = entry.Value!;
                var addedOrigin = shipped is not null && added == shipped ? TermOrigin.Pinned : TermOrigin.Added;
                return new LibraryRow(entry.Key, added, addedOrigin, shipped, entry);

            case BuiltInTermIntent.Edited when shipped is not null:
                var merged = Merge(entry.Base!, entry.Value!, shipped);
                var editedQuestions = EditedQuestions(entry.Base!, entry.Value!, shipped, entry.Acknowledged);
                var editedOrigin = merged == shipped ? TermOrigin.Pinned : TermOrigin.Edited;
                return new LibraryRow(entry.Key, merged, editedOrigin, shipped, entry, Review(merged, shipped, editedQuestions));

            case BuiltInTermIntent.Pinned when shipped is not null:
                var pinned = entry.Value!;
                var pinnedQuestions = PinnedQuestions(pinned, shipped, entry.Acknowledged);
                var pinnedOrigin = pinned == shipped ? TermOrigin.Pinned : TermOrigin.Edited;
                return new LibraryRow(entry.Key, pinned, pinnedOrigin, shipped, entry, Review(pinned, shipped, pinnedQuestions));

            default:
                return new LibraryRow(entry.Key, entry.Value!, TermOrigin.NoLongerShipped, null, entry);
        }
    }

    // The user's new values on an authored row, or the first edit of a shipped or turned-off one; see Edit's remarks.
    private static LibraryRow EditValues(LibraryRow row, TermValues values)
    {
        var shipped = row.Shipped;
        var entry = row.Edit;
        BuiltInTermEdit edited;
        if (entry is null)
        {
            // A shipped row inherits every field; the ones the edit changes become the user's.
            edited = new BuiltInTermEdit(row.Key, BuiltInTermIntent.Edited, shipped, values);
        }
        else if (entry.Intent == BuiltInTermIntent.Off)
        {
            // A turned-off row: the check box is authored (off) and every other field inherited, and an edit that leaves
            // the check box alone keeps it authored, so its base is the value it was turned off from, on, even while the
            // version in use ships the row off. Inherited from that version instead, the off would follow the next one
            // that ships the row on, and the row would come back on unasked (round 2, A1).
            var @base = values.Enabled == row.Values.Enabled ? shipped! with { Enabled = true } : shipped!;
            edited = new BuiltInTermEdit(row.Key, BuiltInTermIntent.Edited, @base, values);
        }
        else if (entry.Intent == BuiltInTermIntent.Edited && shipped is not null)
        {
            var changed = Differences(row.Values, values);
            edited = entry with
            {
                Base = Take(changed, shipped, entry.Base!),
                Value = Take(changed, values, entry.Value!),
            };
        }
        else if (entry.Intent == BuiltInTermIntent.Pinned && shipped is not null)
        {
            var changed = Differences(row.Values, values);

            // Where the user never acknowledged anything, the user's own value stands in: a question about a field then
            // arises exactly when the shipped value differs from the user's, as with no acknowledgment at all.
            var acknowledged = Take(changed, shipped, entry.Acknowledged ?? entry.Value!);
            edited = entry with { Value = values, Acknowledged = acknowledged == values ? null : acknowledged };
        }
        else
        {
            // An added row, or an edit whose shipped row is gone: nothing to merge against.
            edited = entry with { Value = values };
        }

        return RowOf(shipped, edited)!;
    }

    private static TermValues Merge(TermValues @base, TermValues value, TermValues shipped) => new(
        value.Spoken == @base.Spoken ? shipped.Spoken : value.Spoken,
        value.Written == @base.Written ? shipped.Written : value.Written,
        value.WholeWord == @base.WholeWord ? shipped.WholeWord : value.WholeWord,
        value.Enabled == @base.Enabled ? shipped.Enabled : value.Enabled);

    // Fields the user changed that the shipped version also changed, to something else, and that the user has not
    // already kept against this very shipped value.
    private static TermFields EditedQuestions(TermValues @base, TermValues value, TermValues shipped, TermValues? acknowledged)
    {
        var questions = TermFields.None;
        foreach (var field in Fields)
        {
            if (!Same(field, value, @base) &&
                !Same(field, shipped, @base) &&
                !Same(field, value, shipped) &&
                (acknowledged is null || !Same(field, acknowledged, shipped)))
            {
                questions |= field;
            }
        }

        return questions;
    }

    private static TermFields PinnedQuestions(TermValues value, TermValues shipped, TermValues? acknowledged)
    {
        var questions = TermFields.None;
        foreach (var field in Fields)
        {
            if (!Same(field, shipped, value) && (acknowledged is null || !Same(field, shipped, acknowledged)))
            {
                questions |= field;
            }
        }

        return questions;
    }

    private static TermReview? Review(TermValues yours, TermValues shipped, TermFields questions) =>
        questions == TermFields.None ? null : new TermReview(yours, shipped, Differences(yours, shipped));

    private static TermFields Differences(TermValues before, TermValues after)
    {
        var changed = TermFields.None;
        foreach (var field in Fields)
        {
            if (!Same(field, before, after))
            {
                changed |= field;
            }
        }

        return changed;
    }

    // Each field from `changed` where the flag is set, otherwise from `kept`.
    private static TermValues Take(TermFields fields, TermValues changed, TermValues kept) => new(
        fields.HasFlag(TermFields.Spoken) ? changed.Spoken : kept.Spoken,
        fields.HasFlag(TermFields.Written) ? changed.Written : kept.Written,
        fields.HasFlag(TermFields.WholeWord) ? changed.WholeWord : kept.WholeWord,
        fields.HasFlag(TermFields.Enabled) ? changed.Enabled : kept.Enabled);

    private static bool Same(TermFields field, TermValues left, TermValues right) => field switch
    {
        TermFields.Spoken => string.Equals(left.Spoken, right.Spoken, StringComparison.Ordinal),
        TermFields.Written => string.Equals(left.Written, right.Written, StringComparison.Ordinal),
        TermFields.WholeWord => left.WholeWord == right.WholeWord,
        TermFields.Enabled => left.Enabled == right.Enabled,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not one field."),
    };

    private static LibraryRow ShippedRow(TermValues shipped) =>
        new(LibraryTermKey.From(shipped.Spoken), shipped, TermOrigin.Shipped, shipped);

    private static IReadOnlyList<TermValues> ShippedValues(DictionaryLibrary shipped)
    {
        ArgumentNullException.ThrowIfNull(shipped);
        if (!shipped.BuiltIn)
        {
            throw new ArgumentException("Only a built-in library has an edits document.", nameof(shipped));
        }

        return [.. (shipped.Entries ?? []).Where(entry => entry is not null).Select(TermValues.FromEntry)];
    }

    private static void EnsureDocumentOf(DictionaryLibrary shipped, BuiltInLibraryEdits edits, string parameter)
    {
        if (!string.Equals(shipped.Id, edits.LibraryId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The edits document belongs to another library.", parameter);
        }
    }

    private static void EnsureValid(BuiltInLibraryEdits edits, string parameter)
    {
        ArgumentNullException.ThrowIfNull(edits, parameter);
        if (string.IsNullOrWhiteSpace(edits.LibraryId) || !IsText(edits.LibraryId))
        {
            throw new ArgumentException("An edits document needs its library's id, as text.", parameter);
        }

        if (edits.Terms is null)
        {
            throw new ArgumentException("An edits document needs its list of entries.", parameter);
        }

        var keys = new HashSet<LibraryTermKey>();
        foreach (var term in edits.Terms)
        {
            EnsureValidEntry(term, parameter);
            if (!keys.Add(term.Key))
            {
                throw new ArgumentException("Two entries of an edits document have the same key.", parameter);
            }
        }
    }

    private static void EnsureValidEntry(BuiltInTermEdit? entry, string parameter)
    {
        if (entry is null)
        {
            throw new ArgumentException("An entry of an edits document is missing.", parameter);
        }

        if (entry.Key.IsEmpty || !IsText(entry.Key.Value))
        {
            throw new ArgumentException("An entry of an edits document needs a key, as text.", parameter);
        }

        if (!Enum.IsDefined(entry.Intent) || !HasTheValuesItsIntentNeeds(entry.Intent, entry.Base, entry.Value))
        {
            throw new ArgumentException("An entry of an edits document lacks the values its intent needs.", parameter);
        }

        if (!HoldsText(entry.Base) || !HoldsText(entry.Value) || !HoldsText(entry.Acknowledged))
        {
            throw new ArgumentException(NotText, parameter);
        }
    }

    private static bool HoldsText(TermValues? values) => values is null || (IsText(values.Spoken) && IsText(values.Written));

    // Text is well-formed UTF-16: never null, and every surrogate in a pair. Anything else is refused wherever an id, a
    // key or a value is taken in: written out, an unpaired surrogate becomes U+FFFD, so two different keys could be written
    // alike and the document read back with a repeated key, pausing the library (round 2, A2).
    internal static bool IsText(string? value)
    {
        if (value is null)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureValues(TermValues values, string parameter)
    {
        ArgumentNullException.ThrowIfNull(values, parameter);
        if (!HoldsText(values))
        {
            throw new ArgumentException(NotText, parameter);
        }
    }

    private static void EnsureBuiltInRow(LibraryRow row, string parameter)
    {
        ArgumentNullException.ThrowIfNull(row, parameter);
        if (row.Origin == TermOrigin.Custom || !Enum.IsDefined(row.Origin))
        {
            throw new ArgumentException("The built-in overlay changes rows of built-in libraries only.", parameter);
        }

        if (row.Edit is not null)
        {
            EnsureValidEntry(row.Edit, parameter);
        }
    }

    // A row the overlay gave: rebuilt from its own shipped values and entry, it comes out the same. Everything the row
    // methods compute rests on that.
    private static void EnsureCanonical(LibraryRow row, string parameter)
    {
        EnsureBuiltInRow(row, parameter);
        var expected = row.Edit is null
            ? row.Shipped is null ? null : ShippedRow(row.Shipped)
            : RowOf(row.Shipped, row.Edit);
        if (expected != row)
        {
            throw new ArgumentException("The row is not one the built-in overlay gives.", parameter);
        }
    }
}
