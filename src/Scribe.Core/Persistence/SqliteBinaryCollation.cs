namespace Scribe.Core.Persistence;

/// <summary>
/// SQLite's <c>BINARY</c> collation, the one <c>ORDER BY pattern</c> sorts the dictionary with
/// (<see cref="DictionaryRepository.GetEnabled"/>): memcmp over the stored UTF-8, which is Unicode scalar
/// value order. <see cref="string.CompareOrdinal(string, string)"/> is not the same order: it compares UTF-16
/// code units, which puts U+E000 to U+FFFF after the surrogate pairs of the supplementary planes.
/// </summary>
internal sealed class SqliteBinaryCollation : IComparer<string>
{
    public static SqliteBinaryCollation Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null || y is null)
        {
            return x is null ? -1 : 1;
        }

        // A lone surrogate enumerates as U+FFFD, which is what the UTF-8 encoder stores in its place.
        var left = x.EnumerateRunes();
        var right = y.EnumerateRunes();
        while (true)
        {
            var hasLeft = left.MoveNext();
            var hasRight = right.MoveNext();
            if (!hasLeft || !hasRight)
            {
                return hasLeft ? 1 : hasRight ? -1 : 0;
            }

            var order = left.Current.Value.CompareTo(right.Current.Value);
            if (order != 0)
            {
                return order;
            }
        }
    }
}
