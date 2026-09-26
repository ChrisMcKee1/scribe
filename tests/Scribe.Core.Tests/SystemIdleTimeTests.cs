using System.Runtime.InteropServices;
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
        var inputBefore = LastInputTick();
        TimeSpan first = NativeMethods.TryGetSystemIdleTime()!.Value;
        Thread.Sleep(250);
        TimeSpan second = NativeMethods.TryGetSystemIdleTime()!.Value;
        var inputAfter = LastInputTick();
        var gap = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        var inputCame = inputBefore is { } before && inputAfter is { } after && before != after;

        Assert.True(
            ReadingMoved(first, second, gap, inputCame),
            $"Idle time did not advance: {first} then {second}, {gap} apart, input between them: {inputCame}.");
    }

    /// <summary>
    /// The check above, on readings the test chooses (review rounds 4 and 5 of stream TR): a reading that did not move, or
    /// went back further than input during the gap between the readings explains, fails it, and a late second reading after
    /// input passes it, however long the gap. Equal readings never count as an advance, and count as a reset only when the
    /// native last-input tick shows input came between them: a counter stuck at any value fails, whatever the gap (Astra's
    /// counterexamples, which the round 4 rule passed once the gap exceeded the stuck value).
    /// </summary>
    [Theory]
    [InlineData(10_000, 10_250, 250, false, true)] // advanced with the clock
    [InlineData(10_000, 100, 250, false, true)] // input during the gap reset it: the reading went down
    [InlineData(10_000, 900, 1_000, false, true)] // input during a gap a stall made long, read late
    [InlineData(250, 250, 260, true, true)] // input during the gap, read back at the value it had: the native tick moved
    [InlineData(10_000, 10_000, 250, false, false)] // stuck: it did not reset and did not advance
    [InlineData(10_000, 10_000, 11_000, false, false)] // stuck at 10 s, with a gap above it
    [InlineData(1_000, 1_000, 5_000, false, false)] // stuck at 1 s, with a gap above it
    [InlineData(100, 100, 250, false, false)] // stuck at 100 ms, with a gap above it
    [InlineData(0, 0, 250, false, false)] // stuck at nothing, with a gap above it
    [InlineData(250, 250, 260, false, false)] // equal, and no input came between the readings
    [InlineData(10_000, 5_000, 250, false, false)] // went back further than any input during the gap explains
    [InlineData(10_000, 5_000, 250, true, false)] // likewise, even with input between the readings
    [InlineData(10_000, 1_000, 250, false, false)] // likewise, and more than the old fixed 500 ms allowed as well
    public void A_reading_that_neither_advanced_nor_reset_fails_the_check(
        int firstMs, int secondMs, int gapMs, bool inputCame, bool moved)
    {
        Assert.Equal(
            moved,
            ReadingMoved(
                TimeSpan.FromMilliseconds(firstMs), TimeSpan.FromMilliseconds(secondMs), TimeSpan.FromMilliseconds(gapMs), inputCame));
    }

    // Idle time counts from the last input, in GetTickCount's units, whose resolution the documentation gives as the system
    // timer's, "typically in the range of 10 milliseconds to 16 milliseconds".
    private static readonly TimeSpan IdleClockResolution = TimeSpan.FromMilliseconds(16);

    // An advance is strictly greater. A reset by real input between the readings is seen as the idle time going down, or,
    // should it come back at the value it had, as the native last-input tick moving; and then the second reading is at most
    // the time since that input, which is at most the measured gap, plus one tick of the idle clock. The gap is measured
    // from before the first reading to after the second, so a stall of the test thread lengthens it and never turns a reset
    // into a failure. Equal readings with no input between them are a stuck counter, whatever the gap.
    private static bool ReadingMoved(TimeSpan first, TimeSpan second, TimeSpan gap, bool inputCame) =>
        second > first || ((second < first || inputCame) && second <= gap + IdleClockResolution);

    // The native last-input tick (GetLastInputInfo's dwTime), read on its own: a change between two readings means input came
    // between them. Null when Windows declines to report it.
    private static uint? LastInputTick()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.Time : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}