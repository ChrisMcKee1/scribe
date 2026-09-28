using System.Collections.ObjectModel;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Xunit.Abstractions;

namespace Scribe.Core.Tests;

/// <summary>
/// <see cref="DictionaryLibraryComposer.Merge"/> sizes its set and list once when both inputs know their counts, instead
/// of growing them from empty (ledger LB5). What it keeps is pinned against a copy of the version that grew, over every
/// shape of input its callers pass and a few they do not.
/// </summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class DictionaryLibraryComposerCapacityTests(ITestOutputHelper output)
{
    private static readonly DictionaryEntry[] Personal =
    [
        new(1, "git hub", "GitHub"), new(2, "  Git Hub ", "Mine"), new(3, "kube", "Kubernetes"), null!, new(4, "", "Blank"),
        new(5, "   ", "Spaces"), new(6, null!, "No pattern"), new(7, "jay son", "JSON", Enabled: false), new(8, "helm", "Helm"),
    ];

    private static readonly DictionaryEntry[] Library =
    [
        new(0, "GIT HUB", "Library GitHub"), new(0, "dot net", ".NET"), new(0, "Dot Net ", "Other .NET"), null!,
        new(0, "helm", "Library Helm"), new(0, "see sharp", "C#"), new(0, "\t", "Tab"), new(0, "jay son", "Library JSON"),
    ];

    public static TheoryData<string> Shapes =>
    [
        "array", "list", "read-only collection", "select over a list", "where", "iterator", "empty and array",
        "array and empty", "both empty",
    ];

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Every_shape_of_input_keeps_the_same_entries_in_the_same_order(string shape)
    {
        var (first, second) = Inputs(shape);

        var merged = DictionaryLibraryComposer.Merge(first, second);

        var expected = GrownMerge(Inputs(shape).First, Inputs(shape).Second);
        Assert.Equal(expected.Count, merged.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Same(expected[i], merged[i]);
        }
    }

    [Fact]
    public void Each_input_is_enumerated_once()
    {
        var first = new CountingEnumerable(Personal);
        var second = new CountingEnumerable(Library);

        _ = DictionaryLibraryComposer.Merge(first, second);

        Assert.Equal(1, first.Enumerations);
        Assert.Equal(1, second.Enumerations);
    }

    [Fact]
    public void A_merge_with_nothing_to_drop_keeps_no_spare_room()
    {
        var (personal, library) = Distinct(1_000);

        var merged = DictionaryLibraryComposer.Merge(personal, library);

        Assert.Equal(2_000, merged.Count);
        Assert.Equal(2_000, Assert.IsType<List<DictionaryEntry>>(merged).Capacity);
    }

    [Fact]
    public void Merging_two_thousand_entries_sizes_its_set_and_list_once()
    {
        var (personal, library) = Distinct(1_000);

        var bytes = Measure(() => DictionaryLibraryComposer.Merge(personal, library));
        output.WriteLine($"Merge of 1,000 and 1,000 entries: {bytes} bytes");

        // Grown from empty, the set went through 3, 7, 17, ... 1,931 slots to 4,049 and the list through 4 to 2,048 elements;
        // sized once, the set takes 2,333 slots and the list 2,000 elements. Measured on x64: 187,312 bytes before and
        // 62,952 after. The bound sits halfway, so it fails if either grows from empty again.
        Assert.True(bytes <= 125_000, $"Merge allocated {bytes} bytes; the bound is 125,000.");
    }

    // The version that grew its set and list from empty, as 4d5d89e had it.
    private static List<DictionaryEntry> GrownMerge(IEnumerable<DictionaryEntry> baseEntries, IEnumerable<DictionaryEntry> libraryEntries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DictionaryEntry>();
        foreach (var entry in baseEntries.Concat(libraryEntries))
        {
            if (entry is null)
            {
                continue;
            }

            var key = entry.Pattern?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            if (seen.Add(key))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private static (IEnumerable<DictionaryEntry> First, IEnumerable<DictionaryEntry> Second) Inputs(string shape) => shape switch
    {
        "array" => (Personal, Library),
        "list" => (Personal.ToList(), Library.ToList()),
        "read-only collection" => (new ReadOnlyCollection<DictionaryEntry>(Personal), new ReadOnlyCollection<DictionaryEntry>(Library)),
        "select over a list" => (Personal.ToList().Select(entry => entry), Library.ToList().Select(entry => entry)),
        "where" => (Personal.Where(_ => true), Library.Where(_ => true)),
        "iterator" => (Yield(Personal), Yield(Library)),
        "empty and array" => ([], Library),
        "array and empty" => (Personal, []),
        "both empty" => ([], []),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    private static IEnumerable<DictionaryEntry> Yield(IEnumerable<DictionaryEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
        }
    }

    private static (DictionaryEntry[] Personal, DictionaryEntry[] Library) Distinct(int each) =>
    (
        [.. Enumerable.Range(0, each).Select(i => new DictionaryEntry(i + 1, FormattableString.Invariant($"personal {i}"), "P"))],
        [.. Enumerable.Range(0, each).Select(i => new DictionaryEntry(0, FormattableString.Invariant($"library {i}"), "L"))]
    );

    // The smallest of three measured runs after a warm-up: the first calls JIT the path and load its types, and the
    // smallest run is the one no runtime bookkeeping landed in.
    private static long Measure(Func<object> merge)
    {
        for (var i = 0; i < 3; i++)
        {
            GC.KeepAlive(merge());
        }

        var smallest = long.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = merge();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(result);
            smallest = Math.Min(smallest, bytes);
        }

        return smallest;
    }

    private sealed class CountingEnumerable(IEnumerable<DictionaryEntry> entries) : IEnumerable<DictionaryEntry>
    {
        public int Enumerations { get; private set; }

        public IEnumerator<DictionaryEntry> GetEnumerator()
        {
            Enumerations++;
            return entries.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
