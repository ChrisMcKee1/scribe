namespace Scribe.Core.Libraries;

/// <summary>
/// The rules for a custom library's name, category and description (its metadata), shared by the editor, which
/// commits and validates them, and the CSV codec, which writes them (review finding A11).
/// </summary>
/// <remarks>
/// <para>
/// A managed file in the libraries folder stores metadata as raw comment lines (<c># name: Team terms</c>), exactly as
/// 0.4.3 writes and reads them, because 0.4.3 and 0.4.2 read the same folder. 0.4.3 takes each value from its raw line,
/// but its CSV reader also runs over those lines, and it treats a double quote anywhere on them as the start or end of
/// a quoted field. An odd number of quotes across the header therefore leaves that reader inside a quoted field when
/// the rows begin, and it silently swallows the column header and the rows after it. Every other character is safe:
/// a comma on a comment line only splits a record whose first field starts with "#", which 0.4.3 skips whole, and the
/// value itself still comes from the raw line. So nothing else is refused, a trailing comma included: managed reads
/// take metadata from the raw line and never remove spreadsheet padding, and exports write metadata as quoted CSV
/// fields, so a trailing comma survives both.
/// </para>
/// <para>
/// The editor refuses a double quote in a value the user types (<see cref="CheckTyped"/>), with the reason shown. An
/// untouched value that came from an older file is kept as it is, and a Save that rewrites its library is allowed only
/// while the header still reads back in 0.4.3 (<see cref="ReadsBackInOlderVersions"/>); otherwise the editor asks for
/// that value to be changed before the library can be saved.
/// </para>
/// </remarks>
public static class LibraryMetadata
{
    /// <summary>
    /// The form a typed name, category or description is committed in: every control character, a line break or a tab
    /// included, replaced by a space, then trimmed. 0.4.3's writer flattened control characters the same way, so a
    /// committed value is one raw comment line and reads back unchanged. Never null.
    /// </summary>
    public static string Commit(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
            {
                chars[i] = ' ';
            }
        }

        return new string(chars).Trim();
    }

    /// <summary>Why a value the user typed cannot be committed as a name, category or description, or <see cref="LibraryMetadataProblem.None"/>.</summary>
    public static LibraryMetadataProblem CheckTyped(string? value) =>
        value is not null && value.Contains('"', StringComparison.Ordinal)
            ? LibraryMetadataProblem.DoubleQuote
            : LibraryMetadataProblem.None;

    /// <summary>
    /// Whether a managed header holding <paramref name="values"/> (the name, category, description and based-on id, as
    /// the codec writes them) reads back in 0.4.3 exactly as written, rows included: its lines hold an even number of
    /// double quotes in total. That is exact, not a heuristic: in 0.4.3's reader each quote outside a quoted field opens
    /// one, each quote inside closes it unless it is doubled, and a doubled quote stays inside, so the reader ends the
    /// header outside a quoted field exactly when the count is even. Null values count as absent.
    /// </summary>
    public static bool ReadsBackInOlderVersions(params string?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var quotes = 0;
        foreach (var value in values)
        {
            if (value is null)
            {
                continue;
            }

            foreach (var ch in value)
            {
                if (ch == '"')
                {
                    quotes++;
                }
            }
        }

        return quotes % 2 == 0;
    }
}

/// <summary>Why a typed name, category or description cannot be committed.</summary>
public enum LibraryMetadataProblem
{
    /// <summary>It can be committed.</summary>
    None,

    /// <summary>
    /// It holds a double quote ("), which 0.4.3 would misread in the managed file and which would hide the library's
    /// rows there. The editor says so beside the field.
    /// </summary>
    DoubleQuote,
}
