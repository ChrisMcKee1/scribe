using System.Globalization;

namespace Scribe.Core.Libraries;

/// <summary>
/// The order the Libraries list shows: built-in and custom libraries in one A to Z list, compared the way the user's
/// regional format sorts text (case ignored, and digits compared as numbers so "Release 9" comes before "Release 10"),
/// then by ordinal name and then by ordinal id so that no two different libraries compare equal.
/// </summary>
/// <remarks>
/// <para>
/// This is display order only. Which library supplies a spoken form is decided by <see cref="LibraryPrecedence"/>,
/// which never reads a name or a culture, so sorting, renaming or changing the regional format cannot change what
/// dictation writes.
/// </para>
/// <para>
/// A view captures one instance when it loads (<see cref="ForCurrentCulture"/>), so a row it places later, an import
/// for example, is compared by the same rules as the rows already shown even if the regional format changes while it
/// is open. Enabled state is not an input, so turning a library on or off never moves it. The macOS port sorts with
/// <c>localizedStandardCompare</c> and the same two tie-breaks.
/// </para>
/// </remarks>
public sealed class LibraryOrdering
{
    private const CompareOptions NameOptions = CompareOptions.IgnoreCase | CompareOptions.NumericOrdering;

    private readonly CompareInfo _compareInfo;

    private LibraryOrdering(CultureInfo culture)
    {
        Culture = culture;
        _compareInfo = culture.CompareInfo;
    }

    /// <summary>The culture whose sort rules this instance applies.</summary>
    public CultureInfo Culture { get; }

    /// <summary>An ordering for the culture the current thread formats and sorts text with, fixed from now on.</summary>
    public static LibraryOrdering ForCurrentCulture() => new(CultureInfo.CurrentCulture);

    /// <summary>An ordering for <paramref name="culture"/>.</summary>
    public static LibraryOrdering For(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return new LibraryOrdering(culture);
    }

    /// <summary>
    /// Compares two libraries by display name, then by ordinal name, then by ordinal id. A missing name or id counts as
    /// empty.
    /// </summary>
    public int Compare(string? name, string? id, string? otherName, string? otherId)
    {
        var byName = _compareInfo.Compare(name ?? string.Empty, otherName ?? string.Empty, NameOptions);
        if (byName != 0)
        {
            return byName;
        }

        // Names the culture treats as equal ("github" and "GitHub", or "Release 010" and "Release 10") still need a
        // fixed order, or the list would show them in whatever order they arrived.
        var byOrdinalName = string.CompareOrdinal(name ?? string.Empty, otherName ?? string.Empty);
        return byOrdinalName != 0 ? byOrdinalName : string.CompareOrdinal(id ?? string.Empty, otherId ?? string.Empty);
    }

    /// <summary>
    /// The items in display order, as a new list. The sort is stable, so items that match on name and id (only possible
    /// for a hand-placed file that reuses a built-in's id) keep the order they were given in.
    /// </summary>
    public IReadOnlyList<T> Sort<T>(IEnumerable<T> items, Func<T, string?> name, Func<T, string?> id)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(id);
        return items.OrderBy(item => item, Comparer<T>.Create((a, b) => Compare(name(a), id(a), name(b), id(b)))).ToList();
    }

    /// <summary>
    /// Where <paramref name="item"/> belongs in <paramref name="sorted"/>, a list already in this order: before the first
    /// item that sorts after it, so it lands after any item it ties with.
    /// </summary>
    public int InsertionIndex<T>(IReadOnlyList<T> sorted, T item, Func<T, string?> name, Func<T, string?> id)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(id);

        // A linear scan rather than a binary search: the list holds tens of libraries, and a scan cannot misplace a row
        // if the list it is given was not quite in this order.
        for (var i = 0; i < sorted.Count; i++)
        {
            if (Compare(name(item), id(item), name(sorted[i]), id(sorted[i])) < 0)
            {
                return i;
            }
        }

        return sorted.Count;
    }
}
