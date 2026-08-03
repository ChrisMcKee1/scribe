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
        TimeSpan first = NativeMethods.TryGetSystemIdleTime()!.Value;
        Thread.Sleep(250);
        TimeSpan second = NativeMethods.TryGetSystemIdleTime()!.Value;

        // Real input during the sleep resets the reading rather than advancing it; either way it
        // must have moved, and it must never have gone backwards without dropping near zero.
        Assert.True(
            second > first || second < TimeSpan.FromMilliseconds(500),
            $"Idle time did not advance: {first} then {second}.");
    }
}
