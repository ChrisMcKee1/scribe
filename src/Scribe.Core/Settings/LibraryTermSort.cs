using System.Globalization;

namespace Scribe.Core.Settings;

/// <summary>The orders the terms grid's Sort menu offers (plan 3.1, UX-08).</summary>
public enum LibraryTermSortOrder
{
    /// <summary>The library's saved order, which is the order its rules compete in.</summary>
    SavedOrder,

    SpokenAscending,

    SpokenDescending,

    WrittenAscending,

    WrittenDescending,
}

/// <summary>
/// The terms grid's Sort menu: rebuilds the row list once in the chosen order, never a live view sort that would move a
/// row the moment an edit commits. View state only: sorting marks nothing unsaved and never changes a winner, which
/// the saved order decides.
/// </summary>
/// <remarks>
/// Text compares the way <see cref="Libraries.LibraryOrdering"/> compares names: the captured culture's rules, case
/// ignored, digits as numbers. Ties go to the other column, then to ordinal text, so the result is the same every time;
/// Z to A reverses all of that, and rows that still tie keep their saved order in both directions. Rows with nothing in
/// the sorted column (a removal rule when sorting by Written, a blank placeholder) go last in both directions.
/// </remarks>
public sealed class LibraryTermSort
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.NumericOrdering;

    private readonly CompareInfo _compareInfo;

    private LibraryTermSort(CultureInfo culture)
    {
        Culture = culture;
        _compareInfo = culture.CompareInfo;
    }

    /// <summary>The culture whose sort rules this instance applies.</summary>
    public CultureInfo Culture { get; }

    /// <summary>A sort for the culture the current thread uses, fixed from now on.</summary>
    public static LibraryTermSort ForCurrentCulture() => new(CultureInfo.CurrentCulture);

    /// <summary>A sort for <paramref name="culture"/>.</summary>
    public static LibraryTermSort For(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return new LibraryTermSort(culture);
    }

    /// <summary><paramref name="rows"/> (in saved order) in <paramref name="order"/>, as a new list.</summary>
    public IReadOnlyList<DraftTermRow> Sort(IReadOnlyList<DraftTermRow> rows, LibraryTermSortOrder order)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (order == LibraryTermSortOrder.SavedOrder)
        {
            return [.. rows];
        }

        var bySpoken = order is LibraryTermSortOrder.SpokenAscending or LibraryTermSortOrder.SpokenDescending;
        var descending = order is LibraryTermSortOrder.SpokenDescending or LibraryTermSortOrder.WrittenDescending;
        var indexed = rows.Select((row, index) => (Row: row, Index: index)).ToList();
        indexed.Sort((a, b) =>
        {
            var first = bySpoken ? a.Row.Row.Values.Spoken : a.Row.Row.Values.Written;
            var second = bySpoken ? b.Row.Row.Values.Spoken : b.Row.Row.Values.Written;
            if (first.Length == 0 || second.Length == 0)
            {
                // Empty values last whatever the direction; between two empty ones, saved order.
                return first.Length == 0 && second.Length == 0 ? a.Index.CompareTo(b.Index) : first.Length == 0 ? 1 : -1;
            }

            var byColumn = _compareInfo.Compare(first, second, Options);
            var otherFirst = bySpoken ? a.Row.Row.Values.Written : a.Row.Row.Values.Spoken;
            var otherSecond = bySpoken ? b.Row.Row.Values.Written : b.Row.Row.Values.Spoken;
            if (byColumn == 0)
            {
                byColumn = _compareInfo.Compare(otherFirst, otherSecond, Options);
            }

            if (byColumn == 0)
            {
                byColumn = string.CompareOrdinal(first, second);
            }

            if (byColumn == 0)
            {
                byColumn = string.CompareOrdinal(otherFirst, otherSecond);
            }

            if (descending)
            {
                byColumn = -byColumn;
            }

            return byColumn != 0 ? byColumn : a.Index.CompareTo(b.Index);
        });
        return indexed.Select(item => item.Row).ToList();
    }
}
