using System.Globalization;
using Scribe.Core.Overlay;

namespace Scribe.Core.Tests;

/// <summary>
/// <see cref="OverlayPipeProtocol.MeterLine"/> formats its line in stack scratch space sized for the longest line an int
/// makes, so the string it returns is its only allocation. These tests show it writes the text the concatenation wrote,
/// for every level the app sends and far beyond, at every length up to the longest, whatever the current culture.
/// </summary>
public sealed class OverlayPipeProtocolMeterLineTests
{
    // The ends of the int range and both sides of every power of ten, positive and negative: every length a line can have.
    private static readonly int[] BoundaryLevels = BuildBoundaryLevels();

    public static TheoryData<int> Boundaries() => [.. BoundaryLevels];

    [Theory]
    [InlineData("")]
    [InlineData("sv-SE")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    public void A_meter_line_is_the_verb_and_the_invariant_digits_whatever_the_culture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var mismatches = Enumerable.Range(-2_000, 5_001).Concat(BoundaryLevels)
                .Where(level => OverlayPipeProtocol.MeterLine(level) != "METER " + level.ToString(CultureInfo.InvariantCulture))
                .Take(10)
                .ToList();

            Assert.Empty(mismatches);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void Every_length_up_to_the_longest_line_is_written_whole(int level)
    {
        var line = OverlayPipeProtocol.MeterLine(level);

        Assert.Equal("METER " + level.ToString(CultureInfo.InvariantCulture), line);
        Assert.InRange(line.Length, "METER 0".Length, "METER -2147483648".Length);
    }

    private static int[] BuildBoundaryLevels()
    {
        var levels = new SortedSet<int> { int.MinValue, int.MinValue + 1, int.MaxValue - 1, int.MaxValue };
        for (var power = 1L; power <= 1_000_000_000L; power *= 10)
        {
            levels.Add((int)power - 1);
            levels.Add((int)power);
            levels.Add(1 - (int)power);
            levels.Add(-(int)power);
        }

        return [.. levels];
    }

    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Theory]
        [InlineData(0)]
        [InlineData(299)]
        [InlineData(300)]
        [InlineData(523)]
        [InlineData(1000)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        [InlineData(int.MaxValue)]
        public void A_meter_line_allocates_only_the_string_it_returns(int level)
        {
            var length = 0;
            for (var i = 0; i < 100; i++)
            {
                length = OverlayPipeProtocol.MeterLine(level).Length;
                _ = new string('x', length);
            }

            _ = RuntimeWork.Now().Since(RuntimeWork.Now());

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var line = OverlayPipeProtocol.MeterLine(level);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            before = GC.GetAllocatedBytesForCurrentThread();
            var same = new string('x', length);
            var oneString = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(length, line.Length);
            Assert.Equal(length, same.Length);
            Assert.True(
                allocated == oneString,
                $"MeterLine({level}) allocated {allocated} bytes; one string of its length is {oneString}. During it: {during}.");
        }
    }
}
