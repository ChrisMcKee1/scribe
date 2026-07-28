using System.Runtime.InteropServices;
using System.Text;

namespace Scribe.InjectionLab;

/// <summary>
/// A real, focusable Win32 target for injection measurements: a top-level window hosting a
/// multiline EDIT (or RichEdit) control. The lab needs a genuine HWND with a running message pump
/// because SendInput posts to the thread input queue; injecting into a control that never pumps
/// measures nothing but SendInput's own return value.
/// </summary>
internal sealed class TargetWindow : IDisposable
{
    private const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CHILD = 0x40000000;
    private const int WS_VSCROLL = 0x00200000;
    private const int ES_MULTILINE = 0x0004;
    private const int ES_AUTOVSCROLL = 0x0040;
    private const int ES_WANTRETURN = 0x1000;
    private const uint WM_SETTEXT = 0x000C;
    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_GETTEXTLENGTH = 0x000E;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_CHAR = 0x0102;
    private const uint WM_KEYDOWN = 0x0100;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_SHIFT = 0x10;
    private const uint PM_REMOVE = 0x0001;

    private readonly IntPtr _host;
    private readonly IntPtr _edit;
    private readonly WndProcDelegate _wndProc;
    private readonly StringBuilder _captured = new();
    private int _plainEnters;
    private int _shiftEnters;

    public IntPtr Handle => _host;

    public string ControlClass { get; }

    /// <summary>True when there is no child edit control, so keystrokes land on the host window.</summary>
    public bool IsCustomTarget => _edit == IntPtr.Zero;

    /// <summary>Enters delivered without Shift held. Non-zero means a chat app would have sent.</summary>
    public int PlainEnters => _plainEnters;

    /// <summary>Enters delivered with Shift held, which is the soft-newline chord.</summary>
    public int ShiftEnters => _shiftEnters;

    public TargetWindow(bool richEdit, bool custom = false)
    {
        if (richEdit && !custom)
        {
            // RichEdit lives in a separate DLL that must be loaded before the class exists.
            _ = LoadLibrary("Msftedit.dll");
        }

        ControlClass = custom ? "(custom WM_CHAR sink)" : richEdit ? "RICHEDIT50W" : "EDIT";

        // A custom sink deliberately is NOT an Edit/RichEdit, so TryInsertIntoStandardEdit declines
        // and the configured injection method is the one actually exercised. This is the only way to
        // measure the typing and paste paths, because every classic edit control short-circuits to
        // the EM_REPLACESEL fast path first.
        _wndProc = custom ? CaptureWndProc : DefWindowProc;
        var className = "ScribeInjectionLab_" + Guid.NewGuid().ToString("N");
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
            hCursor = LoadCursor(IntPtr.Zero, 32512),
        };

        if (RegisterClass(ref wc) == 0)
        {
            throw new InvalidOperationException($"RegisterClass failed: {Marshal.GetLastWin32Error()}");
        }

        _host = CreateWindowEx(
            0, className, "Scribe injection lab", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            120, 120, 900, 500, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_host == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx(host) failed: {Marshal.GetLastWin32Error()}");
        }

        _edit = custom
            ? IntPtr.Zero
            : CreateWindowEx(
                0, ControlClass, string.Empty,
                WS_CHILD | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE | ES_AUTOVSCROLL | ES_WANTRETURN,
                0, 0, 880, 460, _host, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (!custom && _edit == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx({ControlClass}) failed: {Marshal.GetLastWin32Error()}");
        }
    }

    // Accumulates what actually arrived. WM_CHAR carries the printable text; Return is inspected at
    // WM_KEYDOWN because WM_CHAR reports 0x0D for both the plain and shifted chord, and the whole
    // point of the measurement is telling those two apart.
    private IntPtr CaptureWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CHAR:
                var ch = (char)wParam;
                if (ch != '\r' && ch != '\n')
                {
                    lock (_captured)
                    {
                        _captured.Append(ch);
                    }
                }

                return IntPtr.Zero;

            case WM_KEYDOWN when (ushort)wParam == VK_RETURN:
                var shifted = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
                lock (_captured)
                {
                    _captured.Append('\n');
                    if (shifted)
                    {
                        _shiftEnters++;
                    }
                    else
                    {
                        _plainEnters++;
                    }
                }

                return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Brings the window forward. Windows refuses SetForegroundWindow from a process that does not
    /// already own the foreground, so this borrows the current foreground thread's input state
    /// first, which is the documented way to make the call succeed.
    /// </summary>
    public void Focus()
    {
        var foreground = GetForegroundWindow();
        var us = GetCurrentThreadId();
        var them = foreground == IntPtr.Zero ? 0u : GetWindowThreadProcessId(foreground, out _);

        var attached = them != 0 && them != us && AttachThreadInput(us, them, true);
        try
        {
            ShowWindow(_host, SW_SHOW);
            BringWindowToTop(_host);
            SetForegroundWindow(_host);
            SetActiveWindow(_host);
            SetFocus(_edit == IntPtr.Zero ? _host : _edit);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(us, them, false);
            }
        }

        Pump(TimeSpan.FromMilliseconds(120));
    }

    /// <summary>
    /// Polls until this window really is the foreground window. Windows refuses SetForegroundWindow
    /// from a process that does not own the foreground, so a single call can silently do nothing and
    /// the injector then bails out with "focus changed" in ~15 ms, which reads as a fast success in
    /// timings and an empty control in fidelity. Measurements must not start until this is true.
    /// </summary>
    public bool EnsureForeground(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (GetForegroundWindow() == _host)
            {
                SetFocus(_edit == IntPtr.Zero ? _host : _edit);
                Pump(TimeSpan.FromMilliseconds(40));
                return GetForegroundWindow() == _host;
            }

            Focus();
        }

        return false;
    }

    public void Clear()
    {
        lock (_captured)
        {
            _captured.Clear();
            _plainEnters = 0;
            _shiftEnters = 0;
        }

        if (_edit != IntPtr.Zero)
        {
            SendMessage(_edit, WM_SETTEXT, IntPtr.Zero, string.Empty);
        }

        Pump(TimeSpan.FromMilliseconds(30));
    }

    /// <summary>Reads what the control actually contains, which is the fidelity measurement.</summary>
    public string ReadText()
    {
        if (_edit == IntPtr.Zero)
        {
            lock (_captured)
            {
                return _captured.ToString();
            }
        }

        int length = (int)SendMessage(_edit, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 2);
        _ = SendMessageSb(_edit, WM_GETTEXT, (IntPtr)(length + 1), buffer);
        return buffer.ToString();
    }

    /// <summary>
    /// Drains the message queue for the given period. Injection runs on the injector's own thread,
    /// so the lab thread has to keep pumping or the control never renders the keystrokes.
    /// </summary>
    public void Pump(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            Thread.Sleep(1);
        }
    }

    /// <summary>Pumps until <paramref name="done"/> completes, so the target renders during injection.</summary>
    public void PumpUntil(Task done, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!done.IsCompleted && DateTime.UtcNow < until)
        {
            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        if (_host != IntPtr.Zero)
        {
            SendMessage(_host, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Pump(TimeSpan.FromMilliseconds(30));
        }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessageSb(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);
}
