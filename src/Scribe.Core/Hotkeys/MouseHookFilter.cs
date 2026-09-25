using System.Runtime.InteropServices;

namespace Scribe.Core.Hotkeys;

/// <summary>
/// What the <c>WH_MOUSE_LL</c> callback does with one mouse message. The mouse hook is called for every move of the
/// pointer, hundreds of times a second on a gaming mouse, and the pointer waits for each call to return, so everything
/// but the four messages a binding can use (the middle and side buttons going down and up) is handed straight to the
/// next hook after a single comparison, without reading the message's data or touching the engine. The four that
/// remain go to the same <see cref="HotkeyEngine"/> as the keyboard hook, as the button's virtual-key code.
/// </summary>
internal static class MouseHookFilter
{
    internal const int WM_MOUSEMOVE = 0x0200;
    internal const int WM_MBUTTONDOWN = 0x0207;
    internal const int WM_MBUTTONUP = 0x0208;
    internal const int WM_MOUSEWHEEL = 0x020A;
    internal const int WM_XBUTTONDOWN = 0x020B;
    internal const int WM_XBUTTONUP = 0x020C;
    internal const int WM_MOUSEHWHEEL = 0x020E;

    // HIWORD of MSLLHOOKSTRUCT.mouseData for an X button message.
    private const uint XButton1 = 0x0001;
    private const uint XButton2 = 0x0002;

    // The two MSLLHOOKSTRUCT fields a button message needs, at the offsets Windows lays them out at (POINT pt, then DWORD
    // mouseData, DWORD flags, DWORD time, ULONG_PTR dwExtraInfo; the layout test pins both against the struct). Written
    // out rather than taken from Marshal.OffsetOf, whose reflection would allocate in this class's initializer, which the
    // first button callback runs. Direct reads, as the keyboard callback does, because PtrToStructure would marshal the
    // whole struct on a path the pointer waits for.
    internal const int MouseDataOffset = 8;

    internal static readonly int ExtraInfoOffset = IntPtr.Size == 8 ? 24 : 20;

    /// <summary>
    /// The part of <see cref="SyntheticInputMarker"/> a low-level mouse hook can see. Windows keeps only the low 32 bits
    /// of <c>MOUSEINPUT.dwExtraInfo</c> in the <c>MSLLHOOKSTRUCT</c> it hands the hook: measured on CI, on x64 and
    /// Arm64, a 64-bit value sent with SendInput arrived with its high half zeroed. So the mouse hook compares the low
    /// half alone, which still matches if a later Windows keeps the whole value.
    /// </summary>
    internal static readonly uint Marker = unchecked((uint)SyntheticInputMarker.Value);

    /// <summary>Whether a mouse hook message's extra information marks it as Scribe's own injected input.</summary>
    internal static bool IsScribesOwn(nint lParam) => unchecked((uint)Marshal.ReadInt32(lParam, ExtraInfoOffset)) == Marker;

    /// <summary>
    /// True exactly for WM_MBUTTONDOWN, WM_MBUTTONUP, WM_XBUTTONDOWN and WM_XBUTTONUP, in one comparison: those are
    /// 0x207 plus 0, 1, 4 and 5, the only offsets with no bit set outside 0b101, and an offset below zero wraps to a
    /// value with high bits set. Moves (0x200), the wheels (0x20A, 0x20E) and the left and right buttons (0x201 to
    /// 0x205) all fail it.
    /// </summary>
    internal static bool IsButtonMessage(int message) => unchecked(((uint)message - WM_MBUTTONDOWN) & ~5u) == 0;

    /// <summary>Whether a button message is a press rather than a release.</summary>
    internal static bool IsDown(int message) => message is WM_MBUTTONDOWN or WM_XBUTTONDOWN;

    /// <summary>
    /// The virtual-key code of the button a button message is for: <see cref="MouseButtons.Middle"/> for the middle
    /// button, and for an X button whichever one the high word of <paramref name="mouseData"/> names, or 0 when it
    /// names neither or both (injected input can), which the hook then passes on untouched.
    /// </summary>
    internal static uint ButtonOf(int message, uint mouseData)
    {
        if (message is WM_MBUTTONDOWN or WM_MBUTTONUP)
        {
            return MouseButtons.Middle;
        }

        return (mouseData >> 16) switch
        {
            XButton1 => MouseButtons.Back,
            XButton2 => MouseButtons.Forward,
            _ => 0,
        };
    }

    /// <summary>
    /// The hook callback's decision: true to swallow the message (the callback returns 1), false to pass it to the next
    /// hook. Hook thread only. Anything but a button message returns false before <paramref name="lParam"/> is read, so
    /// a test can pass zero for it; a button message carrying <see cref="SyntheticInputMarker"/> is passed on as the
    /// keyboard hook passes Scribe's own input (Scribe injects no mouse input today; the check keeps any it ever does out
    /// of the engine). Like the keyboard callback
    /// this never waits, locks or logs; its only allocation is the one the engine makes when a press or release starts
    /// or ends a dictation.
    /// </summary>
    internal static bool Swallows(
        int nCode, int message, nint lParam, HotkeyEngine engine, HotkeyReconcileSignal? reconcileSignal)
    {
        if (!IsButtonMessage(message) || nCode < 0)
        {
            return false;
        }

        if (IsScribesOwn(lParam))
        {
            return false;
        }

        // Only an X button message carries its button in mouseData; for the middle button the field is not used.
        var mouseData = message is WM_XBUTTONDOWN or WM_XBUTTONUP ? (uint)Marshal.ReadInt32(lParam, MouseDataOffset) : 0u;
        var button = ButtonOf(message, mouseData);
        if (button == 0)
        {
            return false;
        }

        var decision = engine.OnMouseButtonEvent(button, IsDown(message));
        if (decision.RequestReconcile)
        {
            reconcileSignal?.Signal();
        }

        return decision.Suppress;
    }
}
