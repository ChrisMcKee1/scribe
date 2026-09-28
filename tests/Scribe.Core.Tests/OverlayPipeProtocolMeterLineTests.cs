using System.Globalization;
using Scribe.Core.Overlay;

namespace Scribe.Core.Tests;

/// <summary>
/// <see cref="OverlayPipeProtocol.MeterLine"/> returns a cached line for the levels the app sends (0 to 1,000) and
/// formats every other int with int.TryFormat in stack scratch space sized for the longest line an int makes, so the
/// string it returns is its only allocation, in every tier the JIT moves the code through. These tests show it writes the
/// text the concatenation wrote, for every level the app sends, far beyond it and across the whole int range, at every
/// length up to the longest, whatever the current culture; and that after its calls have had time to move up the JIT's
/// tiers a cached level allocates nothing and any other allocates exactly the string it returns.
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
            var random = new Random(20260927);
            var sample = Enumerable.Range(0, 100_000).Select(_ => random.Next(int.MinValue, int.MaxValue));
            var mismatches = Enumerable.Range(-2_000, 5_001).Concat(BoundaryLevels).Concat(sample)
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
        private const int CallsPerRound = 500;

        // Longer than the runtime's default 100 ms delay before it counts calls for promotion, so the methods MeterLine calls
        // can move up a tier between rounds. In one of those tiers, the one that profiles the code, the interpolated string
        // handler MeterLine used before allocated a box for the int on every call.
        private const int TierUpPauseMs = 150;

        [Theory]
        [InlineData(-1)]
        [InlineData(1001)]
        [InlineData(int.MinValue)]
        [InlineData(int.MaxValue)]
        public void A_meter_line_allocates_only_the_string_it_returns(int level)
        {
            var length = OverlayPipeProtocol.MeterLine(level).Length;
            Assert.Equal(("METER " + level.ToString(CultureInfo.InvariantCulture)).Length, length);

            // Three rounds with a pause between them, and the last is measured: the lines' bytes beyond as many strings of
            // their length, each counted the same way.
            for (var round = 1; round < 3; round++)
            {
                Lines(level);
                Strings(length);
                Thread.Sleep(TierUpPauseMs);
            }

            _ = RuntimeWork.Now().Since(RuntimeWork.Now());

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            Lines(level);
            var lines = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            Strings(length);
            var strings = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            AllocationMeasurement.AssertZero(
                lines - strings,
                during,
                $"{CallsPerRound} meter lines for level {level}, beyond {CallsPerRound} strings of {length} characters ({strings} bytes)",
                () => Lines(level));
        }

        // The levels the app sends (0 to 1,000) come from the protocol's cache of their lines, so they allocate nothing.
        [Theory]
        [InlineData(0)]
        [InlineData(299)]
        [InlineData(300)]
        [InlineData(523)]
        [InlineData(1000)]
        public void A_meter_line_for_a_level_the_app_sends_allocates_nothing(int level)
        {
            Assert.Equal("METER " + level.ToString(CultureInfo.InvariantCulture), OverlayPipeProtocol.MeterLine(level));
            for (var round = 1; round < 3; round++)
            {
                Lines(level);
                Thread.Sleep(TierUpPauseMs);
            }

            _ = RuntimeWork.Now().Since(RuntimeWork.Now());

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            Lines(level);
            var lines = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            AllocationMeasurement.AssertZero(lines, during, $"{CallsPerRound} meter lines for level {level}", () => Lines(level));
        }

        private static void Lines(int level)
        {
            for (var i = 0; i < CallsPerRound; i++)
            {
                _ = OverlayPipeProtocol.MeterLine(level);
            }
        }

        private static void Strings(int length)
        {
            for (var i = 0; i < CallsPerRound; i++)
            {
                _ = new string('x', length);
            }
        }
    }
}
