using Scribe.Core.Hotkeys;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Exercises the real <c>GetLastInputInfo</c> P/Invoke. The idle gate that stops the watchdog
/// probe from keeping the machine awake is only as good as this reading: a wrong
/// <c>cbSize</c>, a bad struct layout, or signed tick arithmetic would all still compile and
/// would silently report a nonsense idle time, which reads as "the user is active" forever and
/// puts the keep-awake bug straight back.
/// </summary>
public sealed class SystemIdleTimeTests
{
    [Fact]
    public void Idle_time_is_readable_on_this_machine()
    {
        Assert.NotNull(NativeMethods.TryGetSystemIdleTime());
    }

    /// <summary>
    /// Signed arithmetic on the 32-bit tick stamp would surface here as a negative span, and the
    /// upper bound catches a rollover that was not handled in unsigned arithmetic (the raw
    /// difference would land near the 49.7-day wrap rather than near zero).
    /// </summary>
    [Fact]
    public void Idle_time_is_never_negative_or_absurd()
    {
        TimeSpan idle = NativeMethods.TryGetSystemIdleTime()!.Value;

        Assert.True(idle >= TimeSpan.Zero, $"Idle time was negative: {idle}.");
        Assert.True(idle < TimeSpan.FromDays(7), $"Idle time was implausible: {idle}.");
    }

    /// <summary>
    /// The gate compares this reading against the watchdog period, so the clock has to actually
    /// advance. A stuck value would pin the answer to whatever it read first.
    /// </summary>
    [Fact]
    public void Idle_time_advances_with_the_clock()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        TimeSpan first = NativeMethods.TryGetSystemIdleTime()!.Value;
        Thread.Sleep(250);
        TimeSpan second = NativeMethods.TryGetSystemIdleTime()!.Value;
        var gap = System.Diagnostics.Stopwatch.GetElapsedTime(started);

        Assert.True(ReadingMoved(first, second, gap), $"Idle time did not advance: {first} then {second}, {gap} apart.");
    }

    /// <summary>
    /// The check above, on readings the test chooses (review round 4 of stream TR): a reading that did not move, or went back
    /// further than input during the gap between the readings explains, fails it, and a late second reading after input
    /// passes it, however long the gap.
    /// </summary>
    [Theory]
    [InlineData(10_000, 10_250, 250, true)] // advanced with the clock
    [InlineData(10_000, 100, 250, true)] // input during the gap reset it
    [InlineData(10_000, 900, 1_000, true)] // input during a gap a stall made long, read late
    [InlineData(10_000, 10_000, 250, false)] // stuck: it did not reset and did not advance
    [InlineData(10_000, 5_000, 250, false)] // went back further than any input during the gap explains
    [InlineData(10_000, 1_000, 250, false)] // likewise, and more than the old fixed 500 ms allowed as well
    public void A_reading_that_neither_advanced_nor_reset_fails_the_check(int firstMs, int secondMs, int gapMs, bool moved)
    {
        Assert.Equal(
            moved,
            ReadingMoved(TimeSpan.FromMilliseconds(firstMs), TimeSpan.FromMilliseconds(secondMs), TimeSpan.FromMilliseconds(gapMs)));
    }

    // Idle time counts from the last input, in GetTickCount's units, whose resolution the documentation gives as the system
    // timer's, "typically in the range of 10 milliseconds to 16 milliseconds".
    private static readonly TimeSpan IdleClockResolution = TimeSpan.FromMilliseconds(16);

    // Real input between the readings resets the idle time rather than advancing it: then the second reading is at most
    // the time since that input, which is at most the measured gap, plus one tick of the idle clock. Anything else must
    // have advanced. The gap is measured from before the first reading to after the second, so a stall of the test thread
    // lengthens it and never turns a reset into a failure.
    private static bool ReadingMoved(TimeSpan first, TimeSpan second, TimeSpan gap) =>
        second > first || second <= gap + IdleClockResolution;
}
