using System.Runtime.InteropServices;
using System.Text;
using Scribe.Core.Hotkeys;

namespace Scribe.Core.Tests;

/// <summary>
/// The query a desktop-switch notice is checked with (see HotkeyEngine.OnDesktopSwitchNotice). Read-only: it opens
/// nothing but a handle to the input desktop, closed again, and raises no event.
/// </summary>
public sealed class InputDesktopQueryTests
{
    [Fact]
    public void The_input_desktop_query_agrees_with_OpenInputDesktop()
    {
        var answer = NativeMethods.ThreadDesktopReceivesInput();

        // OpenInputDesktop says which desktop has the input without UOI_IO, so it can judge the answer: true on the
        // desktop a user is at, false on one created for a test run and never switched to. It cannot judge outside the
        // interactive window station, in a session that is not connected (there it names the desktop the session will
        // get back), or while a secure desktop has the input, and then the test asserts nothing more.
        if (InputDesktopIsThisThreadsDesktop() is { } expected)
        {
            Assert.Equal(expected, answer);
        }
    }

    [Fact]
    public void The_input_desktop_query_reports_a_failed_check_as_unknown()
    {
        // No desktop (what GetThreadDesktop returns when it fails), and a handle that is not a desktop, for which
        // GetUserObjectInformation with UOI_IO fails: neither may read as a desktop that lost the input.
        Assert.Null(NativeMethods.DesktopReceivesInput(0));
        using var notADesktop = new ManualResetEvent(false);
        Assert.Null(NativeMethods.DesktopReceivesInput(notADesktop.SafeWaitHandle.DangerousGetHandle()));
    }

    private const int UoiName = 2;
    private const uint DesktopReadObjects = 0x0001;
    private const uint CurrentSession = 0xFFFFFFFF;
    private const int WtsConnectState = 8;
    private const int WtsActive = 0;

    private static bool? InputDesktopIsThisThreadsDesktop()
    {
        if (!string.Equals(ObjectName(GetProcessWindowStation()), "WinSta0", StringComparison.OrdinalIgnoreCase)
            || !SessionIsActive())
        {
            return null;
        }

        var input = OpenInputDesktop(0, false, DesktopReadObjects);
        if (input == 0)
        {
            return null;
        }

        try
        {
            var inputName = ObjectName(input);
            var ownName = ObjectName(GetThreadDesktop(NativeMethods.GetCurrentThreadId()));
            return inputName is null || ownName is null
                ? null
                : string.Equals(inputName, ownName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(input);
        }
    }

    private static string? ObjectName(nint handle)
    {
        var buffer = new byte[512];
        return GetUserObjectInformationW(handle, UoiName, buffer, buffer.Length, out var needed)
            ? Encoding.Unicode.GetString(buffer, 0, Math.Min(needed, buffer.Length)).TrimEnd('\0')
            : null;
    }

    private static bool SessionIsActive()
    {
        if (!WTSQuerySessionInformationW(0, CurrentSession, WtsConnectState, out var buffer, out var bytes))
        {
            return false;
        }

        try
        {
            return bytes >= sizeof(int) && Marshal.ReadInt32(buffer) == WtsActive;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(
        nint server, uint sessionId, int infoClass, out nint buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);

    [DllImport("user32.dll")]
    private static extern nint GetProcessWindowStation();

    [DllImport("user32.dll")]
    private static extern nint GetThreadDesktop(uint threadId);

    [DllImport("user32.dll")]
    private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(nint handle, int index, byte[] info, int length, out int needed);
}
