using System.Collections.Immutable;
using System.Globalization;
using Scribe.Core.Libraries;
using Scribe.Core.Settings;
using Xunit.Abstractions;
using static Scribe.Core.Tests.Libraries.Deciders.DeciderFixture;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// The Sort menu sorts the rows' saved positions over one copy of the rows, instead of a list of (row, position) pairs grown
/// by doubling (ledger LB8). The order is pinned against a copy of the version that sorted pairs, for every order, several
/// cultures and both kinds of list the sort is handed.
/// </summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class LibraryTermSortAllocationTests(ITestOutputHelper output)
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.NumericOrdering;

    // Ties, case and accent variants, digits compared as numbers, the letters Turkish, Swedish and German sort their own way,
    // and empty values, which go last.
    private static readonly string[] Pool =
    [
        "apple", "Apple", "APPLE", "äpple", "Äpfel", "apfel", "item 2", "item 10", "item 02", "Item 1", "ırmak", "Irmak",
        "irmak", "İstanbul", "istanbul", "ångström", "angstrom", "Zebra", "zebra", "straße", "strasse", "résumé", "resume",
        "æble", "øre", "", "", "b", "B", "a1", "a01", "a 1",
    ];

    public static TheoryData<string, LibraryTermSortOrder> Cases()
    {
        var cases = new TheoryData<string, LibraryTermSortOrder>();
        foreach (var culture in new[] { "", "en-US", "tr-TR", "sv-SE", "de-DE", "ja-JP" })
        {
            foreach (var order in Enum.GetValues<LibraryTermSortOrder>())
            {
                cases.Add(culture, order);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_order_is_the_one_the_sort_of_pairs_gave(string cultureName, LibraryTermSortOrder order)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var random = new Random(20_260_927);
        var rows = Enumerable.Range(0, 600)
            .Select(i => new DraftTermRow(
                i + 1,
                LibraryRow.Custom(new TermValues(Pool[random.Next(Pool.Length)], Pool[random.Next(Pool.Length)])),
                RemovalIntent: false,
                LegacyEmpty: false))
            .ToList();
        var sort = LibraryTermSort.For(culture);

        foreach (IReadOnlyList<DraftTermRow> given in new IReadOnlyList<DraftTermRow>[] { rows, rows.ToImmutableList() })
        {
            var sorted = sort.Sort(given, order);

            var expected = PairSort(given, order, culture.CompareInfo);
            Assert.Equal(expected.Count, sorted.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Same(expected[i], sorted[i]);
            }

            Assert.NotSame(given, sorted);
            if (order != LibraryTermSortOrder.SavedOrder)
            {
                Assert.IsType<List<DraftTermRow>>(sorted);
            }
        }
    }

    [Fact]
    public void Sorting_ten_thousand_rows_copies_them_once_and_sorts_positions()
    {
        var terms = Enumerable.Range(0, 10_000).Select(i => new TermValues($"term {9_999 - i}", $"Term {i}"));
        var rows = Workspace(Catalog([Custom("big", "Big", terms)], ["big"])).RowsOf("big");
        var sort = LibraryTermSort.For(CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal("term 0", sort.Sort(rows, LibraryTermSortOrder.SpokenAscending)[0].Row.Values.Spoken);

        var bytes = Measure(() => sort.Sort(rows, LibraryTermSortOrder.SpokenAscending));
        output.WriteLine($"Sort of 10,000 rows by Spoken: {bytes} bytes");

        // Sorting (row, position) pairs grew a list of 16-byte pairs by doubling to 16,384 (262 KB on the large object heap,
        // with the 8,192 before it) and projected the result; now one copy of the rows, one array of positions and the
        // result at its size. Measured on x64: 604,960 bytes before and 200,208 after; the bound sits halfway.
        Assert.True(bytes <= 402_000, $"Sorting 10,000 rows allocated {bytes} bytes; the bound is 402,000.");
    }

    // The version that sorted (row, position) pairs, as 4d5d89e had it.
    private static List<DraftTermRow> PairSort(IReadOnlyList<DraftTermRow> rows, LibraryTermSortOrder order, CompareInfo compareInfo)
    {
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
                return first.Length == 0 && second.Length == 0 ? a.Index.CompareTo(b.Index) : first.Length == 0 ? 1 : -1;
            }

            var byColumn = compareInfo.Compare(first, second, Options);
            var otherFirst = bySpoken ? a.Row.Row.Values.Written : a.Row.Row.Values.Spoken;
            var otherSecond = bySpoken ? b.Row.Row.Values.Written : b.Row.Row.Values.Spoken;
            if (byColumn == 0)
            {
                byColumn = compareInfo.Compare(otherFirst, otherSecond, Options);
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

    // The smallest of three measured runs after a warm-up: the first calls JIT the path and load its types, and the
    // smallest run is the one no runtime bookkeeping landed in.
    private static long Measure(Func<object> sort)
    {
        for (var i = 0; i < 3; i++)
        {
            GC.KeepAlive(sort());
        }

        var smallest = long.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = sort();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(result);
            smallest = Math.Min(smallest, bytes);
        }

        return smallest;
    }
}
