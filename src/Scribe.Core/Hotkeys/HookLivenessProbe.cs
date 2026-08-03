namespace Scribe.Core.Hotkeys;

/// <summary>
/// Decides whether a <c>WH_KEYBOARD_LL</c> hook has been silently removed by Windows.
///
/// Windows unhooks a low-level hook whose callback misses the OS deadline and gives no
/// notification of any kind, so liveness has to be inferred. The watchdog injects an inert
/// keystroke each period; if the hook is installed, the callback runs and this counter advances.
///
/// The decision is kept here, away from Win32, because the previous inline version carried a race
/// that could only be found in production logs. It compared two <c>Environment.TickCount64</c>
/// stamps, and armed the probe with the stamp read AFTER <c>SendInput</c> returned, while the hook
/// callback stamped itself DURING that call (injected input is dispatched into the hook chain
/// before <c>SendInput</c> returns). The callback therefore always looked older than the probe it
/// had just answered, and any advance of the ~15.6 ms tick counter in between read as a dead hook.
/// In 22 days of production logs that fired 3,775 times, on 13.3% of watchdog ticks, every one
/// phase-aligned to the watchdog's own timer grid. Each false positive tore down and reinstalled
/// the hook thread, which also resets chord state and stops any dictation in progress.
///
/// A monotonic counter removes the whole class of problem: it needs no clock, and it does not
/// matter whether the callback runs before or after the send returns. Any callback at all proves
/// the hook is installed, so real typing answers the probe just as well as the probe itself.
/// </summary>
internal sealed class HookLivenessProbe
{
    private long _baseline;
    private bool _armed;

    /// <summary>True when a probe is outstanding and its result has not been judged yet.</summary>
    public bool IsArmed => _armed;

    /// <summary>
    /// Judges the outstanding probe. Returns true only when a probe was armed and not one hook
    /// callback has run since, which means the hook is gone and must be reinstalled.
    /// </summary>
    public bool IsHookDead(long callbackCount) => _armed && callbackCount == _baseline;

    /// <summary>
    /// Records the callback count that the next probe will be judged against. This MUST be called
    /// before the probe keystroke is sent, so a callback raised by the send itself still counts as
    /// an answer.
    /// </summary>
    public void Baseline(long callbackCount) => _baseline = callbackCount;

    /// <summary>
    /// Arms the probe after the send, or disarms it when the send failed. A rejected
    /// <c>SendInput</c> (UIPI, a desktop switch mid-tick) proves nothing about the hook, so the
    /// next tick must not read the absence of a callback as a dead hook.
    /// </summary>
    public void Arm(bool sendSucceeded) => _armed = sendSucceeded;

    /// <summary>
    /// Forgets any outstanding probe. Used when the interactive desktop is unreachable, and after
    /// a reinstall, so a probe armed against the previous hook is never judged against the new one.
    /// </summary>
    public void Disarm() => _armed = false;

    /// <summary>
    /// True when this tick must not send its probe keystroke because nobody is at the keyboard.
    ///
    /// The probe is genuine injected input, so Windows resets the system idle timer for it exactly
    /// as it does for a real keypress. A probe every watchdog period is therefore the same
    /// mechanism a "keep awake" utility uses (Caffeine injects F15 on a timer for precisely this
    /// effect), and it stopped machines from ever reaching their sleep or display timeout for as
    /// long as Scribe was running. Withholding the probe once the system has been idle for a full
    /// period restores normal sleep: the injections stop, the idle timer grows, and the machine
    /// suspends.
    ///
    /// Liveness detection loses nothing that matters. Windows removes a low-level hook whose
    /// callback misses the OS deadline, which is a load-induced failure (a GC pause during ASR
    /// decode), so it happens while the user is dictating rather than while they are away. And a
    /// dead hook only has a victim when somebody is present to press push-to-talk. Real input
    /// re-enables probing on the very next tick, so detection resumes as soon as there is anyone
    /// to detect it for.
    ///
    /// Because the probe resets the idle clock itself, exactly one further probe fires after the
    /// user stops touching the machine; that caps the added wake time at a single watchdog period.
    ///
    /// <paramref name="systemIdle"/> is null when the idle time could not be read. Probe in that
    /// case: an unreadable clock must not silently disable liveness detection.
    /// </summary>
    public static bool ShouldWithholdProbe(TimeSpan? systemIdle, TimeSpan watchdogPeriod) =>
        systemIdle is { } idle && idle >= watchdogPeriod;
}
