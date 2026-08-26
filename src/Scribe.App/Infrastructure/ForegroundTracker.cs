using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Scribe.App.Infrastructure;

/// <summary>
/// Remembers the most recent foreground window that did NOT belong to Scribe.
/// </summary>
/// <remarks>
/// Needed because two of the three ways to reach the text action palette destroy the very thing the
/// palette needs. Clicking the tray menu makes the tray the foreground window, and opening any
/// Scribe window does the same, so by the time the handler runs <c>GetForegroundWindow</c> returns
/// Scribe and the selection is unreachable. A global <c>EVENT_SYSTEM_FOREGROUND</c> hook records
/// the last window that was genuinely someone else's, and the controller restores it before reading.
/// <para>
/// This is an out-of-context WinEvent hook (<c>WINEVENT_OUTOFCONTEXT</c>), so the callback is
/// delivered to this process's message queue rather than being injected into other processes. It
/// carries none of the deadline risk of a low-level input hook: a slow callback here delays nothing
/// system-wide.
/// </para>
/// </remarks>
public sealed class ForegroundTracker : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private delegate void WinEventProc(
        nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint threadId, uint time);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin, uint eventMax, nint module, WinEventProc callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    // Held as a field: SetWinEventHook stores the native function pointer, so letting the delegate
    // be collected would leave the OS calling into freed memory.
    private readonly WinEventProc _callback;
    private readonly ILogger _logger;
    private readonly uint _ownProcessId;
    private nint _hook;
    private nint _lastForeign;
    private bool _disposed;

    public ForegroundTracker(ILogger logger)
    {
        _logger = logger;
        _callback = OnForegroundChanged;
        _ownProcessId = (uint)Environment.ProcessId;
    }

    /// <summary>
    /// The last foreground window that was not one of Scribe's, or 0 if none has been seen. Falls
    /// back to the live foreground window when that is already someone else's, so the very first
    /// invocation after startup still works.
    /// </summary>
    public nint LastForeignWindow
    {
        get
        {
            var current = GetForegroundWindow();
            return IsForeign(current) ? current : _lastForeign;
        }
    }

    /// <summary>Installs the hook. Safe to call twice; the second call does nothing.</summary>
    public void Start()
    {
        if (_hook != 0 || _disposed)
        {
            return;
        }

        // WINEVENT_SKIPOWNPROCESS means Scribe's own windows never even raise the event, so the
        // remembered target cannot be overwritten by the palette or the settings window opening.
        _hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            nint.Zero,
            _callback,
            0,
            0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (_hook == 0)
        {
            _logger.LogWarning("Could not install the foreground tracker; the tray path will be less reliable.");
            return;
        }

        // Seed it, so a user who invokes before ever switching windows still gets a target.
        var current = GetForegroundWindow();
        if (IsForeign(current))
        {
            _lastForeign = current;
        }
    }

    private void OnForegroundChanged(
        nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint threadId, uint time)
    {
        // idObject OBJID_WINDOW is 0; anything else is a child object we do not care about.
        if (hwnd == 0 || idObject != 0)
        {
            return;
        }

        if (IsForeign(hwnd))
        {
            _lastForeign = hwnd;
        }
    }

    private bool IsForeign(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        return processId != 0 && processId != _ownProcessId;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hook != 0)
        {
            _ = UnhookWinEvent(_hook);
            _hook = 0;
        }
    }
}
