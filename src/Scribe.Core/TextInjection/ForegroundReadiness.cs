using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.TextInjection;

/// <summary>
/// Waits until a window is genuinely ready to receive synthesized keystrokes.
/// </summary>
/// <remarks>
/// <para>
/// Activation on Windows has two stages, and only the first is observable through
/// <c>GetForegroundWindow</c>. <c>SetForegroundWindow</c> posts a request; the window becomes
/// foreground when its thread processes it; and only after that does the thread restore focus to a
/// child control and rebuild its caret and selection. Input delivered between those two points is
/// silently dropped, which is exactly one or two characters at typing speed.
/// </para>
/// <para>
/// A fixed sleep is the wrong instrument for this: too short on a loaded machine, wasted latency on
/// an idle one, and impossible to tune for both. <c>GetGUIThreadInfo</c> exposes the real signal.
/// When it reports a non-zero <c>hwndFocus</c> for the target's thread, that thread has finished
/// restoring focus and its focused control is ready to receive input.
/// </para>
/// <para>
/// The residual sleep after that is small and deliberate. Some frameworks (Chromium, Electron, WPF)
/// set focus on the native window before their own internal focus model has caught up, so a short
/// pause after the OS-level signal covers the gap the OS cannot report.
/// </para>
/// </remarks>
public static class ForegroundReadiness
{
    private const int PollIntervalMs = 10;

    /// <summary>
    /// Time allowed for the window to become foreground AND restore focus to a control. Generous:
    /// the cost of waiting is imperceptible, and the cost of giving up early is losing the start of
    /// the user's text.
    /// </summary>
    private const int DefaultTimeoutMs = 900;

    /// <summary>
    /// Pause after the OS reports focus restored, covering frameworks whose internal focus model
    /// lags the native one.
    /// </summary>
    private const int SettleMs = 60;

    /// <summary>
    /// Blocks until <paramref name="window"/> is foreground and its thread has a focused control,
    /// then settles briefly. Returns false if that never happens within the timeout.
    /// </summary>
    /// <remarks>Never throws. Call from a background thread: this blocks for up to a second.</remarks>
    public static bool WaitForInputReady(nint window, int timeoutMs = DefaultTimeoutMs)
    {
        if (window == 0)
        {
            return false;
        }

        var deadline = Environment.TickCount64 + timeoutMs;
        var sawForeground = false;

        while (Environment.TickCount64 < deadline)
        {
            if (GetForegroundWindow() == window)
            {
                sawForeground = true;

                if (HasFocusedControl(window))
                {
                    // The settle applies on EVERY success path, including the one where the window
                    // was already foreground on entry. Returning early without it was the original
                    // defect: the palette closing usually hands foreground back before this runs, so
                    // the no-settle path was the common one rather than the rare one.
                    Thread.Sleep(SettleMs);
                    return true;
                }
            }

            Thread.Sleep(PollIntervalMs);
        }

        // Foreground arrived but focus never did. Some windows genuinely have no focused child
        // (a canvas-based editor drawing its own caret), so this is still worth attempting rather
        // than refusing outright; the settle gives it the best chance available.
        if (sawForeground)
        {
            Thread.Sleep(SettleMs);
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when the thread owning <paramref name="window"/> reports a focused control, which is the
    /// OS-level signal that focus restoration has finished.
    /// </summary>
    private static bool HasFocusedControl(nint window)
    {
        try
        {
            var threadId = GetWindowThreadProcessId(window, out _);
            if (threadId == 0)
            {
                return false;
            }

            var info = new GUITHREADINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>(),
            };

            return GetGUIThreadInfo(threadId, ref info) && info.hwndFocus != 0;
        }
        catch (Exception)
        {
            // A readiness probe must never be the reason an injection fails. Treat an unreadable
            // thread as not-yet-ready and let the caller's timeout decide.
            return false;
        }
    }
}
