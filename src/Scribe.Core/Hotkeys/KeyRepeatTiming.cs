using System.Runtime.InteropServices;

namespace Scribe.Core.Hotkeys;

/// <summary>
/// How long after Scribe's keyboard hook becomes the newest registration a key-down of a key the engine has not seen may
/// still be the autorepeat of a key held since before (<see cref="HotkeyEngine.OnRegisteredAhead"/>), from the user's own
/// keyboard settings. SystemParametersInfo (Learn) gives the repeat delay as "a value in the range from 0 (approximately
/// 250 ms delay) through 3 (approximately 1 second delay). The actual delay associated with each value may vary depending
/// on the hardware" (SPI_GETKEYBOARDDELAY), and the repeat speed as "a value in the range from 0 (approximately 2.5
/// repetitions per second) through 31 (approximately 30 repetitions per second). The actual repeat rates are
/// hardware-dependent and may vary from a linear scale by as much as 20%" (SPI_GETKEYBOARDSPEED).
/// <para>
/// A key held when the hook moved went down before the move, so it repeats first at most one repeat delay after the move,
/// or, if it was repeating already, within one repeat period: its first repeat after the move arrives within the longer of
/// the two. Each is given a quarter more than its setting says (a rate 20% slower is a period 25% longer; the page bounds
/// no delay, so the delay gets the same allowance), and the window adds <see cref="MarginMs"/> for the clock the event
/// times are on (the tick count, which moves in steps of the system timer) and for the event's way to the hook. For
/// Windows' default settings (delay 1, speed 31) that is 875 ms; at most it is 1.5 s.
/// </para>
/// </summary>
internal static partial class KeyRepeatTiming
{
    /// <summary>What the window adds to the longest repeat interval the settings allow, in milliseconds.</summary>
    public const int MarginMs = 250;

    /// <summary>The window for the slowest settings Windows allows (delay 3, speed 0), used when they cannot be read.</summary>
    public const int WorstCaseWindowMs = 1500;

    private const int SlowestDelay = 3;
    private const int FastestSpeed = 31;

    /// <summary>The uncertainty window for these settings, in milliseconds, each clamped to the range Windows documents.</summary>
    public static int UncertaintyWindowMs(int delaySetting, int speedSetting)
    {
        var delayMs = 250.0 * (Math.Clamp(delaySetting, 0, SlowestDelay) + 1);
        var repeatsPerSecond = 2.5 + ((30.0 - 2.5) * Math.Clamp(speedSetting, 0, FastestSpeed) / FastestSpeed);
        var periodMs = 1000.0 / repeatsPerSecond;
        return (int)Math.Ceiling(Math.Max(delayMs, periodMs) * 1.25) + MarginMs;
    }

    /// <summary>
    /// The uncertainty window for the user's current settings, or <see cref="WorstCaseWindowMs"/> when Windows does not
    /// give them. Off the hook thread only: it asks Windows for two settings.
    /// </summary>
    public static int ReadUncertaintyWindowMs() =>
        SystemParametersInfo(SpiGetKeyboardDelay, 0, out var delay, 0) &&
        SystemParametersInfo(SpiGetKeyboardSpeed, 0, out var speed, 0)
            ? UncertaintyWindowMs(delay, speed)
            : WorstCaseWindowMs;

    private const uint SpiGetKeyboardSpeed = 0x000A;
    private const uint SpiGetKeyboardDelay = 0x0016;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint uiAction, uint uiParam, out int pvParam, uint fWinIni);
}
