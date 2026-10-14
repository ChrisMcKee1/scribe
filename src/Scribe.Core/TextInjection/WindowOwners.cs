using System.Runtime.InteropServices;

namespace Scribe.Core.TextInjection;

/// <summary>
/// The window in front and the process that owns a window, by name: what tells a Remote Desktop or virtual machine client
/// is in front (<see cref="RemoteClientProcesses"/>). Off the hook callbacks and off the hook thread only: opening a process
/// can run drivers' callbacks, and nothing on the hook's path may wait for them.
/// </summary>
internal static class WindowOwners
{
    // "PROCESS_QUERY_LIMITED_INFORMATION ... Required to retrieve certain information about a process" (Process Security
    // and Access Rights), which is all QueryFullProcessImageName and OpenProcessToken(TOKEN_QUERY) need.
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int ErrorAccessDenied = 5;

    // The buffer every build has used: an image path that does not fit fails the query and names no process, as before.
    private const int ImagePathCapacity = 1024;
    private const int IntegrityBufferCapacity = 64;

    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;
    private static uint? s_ownIntegrityRid;

    /// <summary>The window in front now, or zero.</summary>
    public static nint Foreground() => InjectionNativeMethods.GetForegroundWindow();

    /// <summary>
    /// True when <paramref name="window"/> belongs to a process at a higher mandatory integrity level than this process
    /// (or one whose process/token open is denied by Windows). While such a window is in front, Windows UIPI prevents a
    /// lower-integrity <c>SendInput</c> probe from reaching this process's <c>WH_KEYBOARD_LL</c> hook.
    /// </summary>
    public static bool IsHigherIntegrityProcess(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        _ = InjectionNativeMethods.GetWindowThreadProcessId(window, out var processId);
        return IsHigherIntegrityProcessId(processId);
    }

    /// <summary>
    /// True when <paramref name="processId"/> runs at a higher mandatory integrity level than this process, or denies
    /// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> / <c>TOKEN_QUERY</c> with <c>ERROR_ACCESS_DENIED</c>. Returns false for
    /// PID 0, this process, an exited process, or when this process's own integrity level cannot be read.
    /// </summary>
    public static bool IsHigherIntegrityProcessId(uint processId)
    {
        if (processId == 0 || processId == OwnProcessId)
        {
            return false;
        }

        if (GetOwnIntegrityRid() is not { } ownRid)
        {
            return false;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return Marshal.GetLastWin32Error() == ErrorAccessDenied;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token) || token == 0)
            {
                return Marshal.GetLastWin32Error() == ErrorAccessDenied;
            }

            try
            {
                return TryReadTokenIntegrityRid(token) is { } targetRid && targetRid > ownRid;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    internal static uint? TryReadProcessIntegrityRid(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token) || token == 0)
            {
                return null;
            }

            try
            {
                return TryReadTokenIntegrityRid(token);
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static uint? GetOwnIntegrityRid()
    {
        if (s_ownIntegrityRid is { } cached)
        {
            return cached;
        }

        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token) || token == 0)
        {
            return null;
        }

        try
        {
            var rid = TryReadTokenIntegrityRid(token);
            if (rid is { } valid)
            {
                s_ownIntegrityRid = valid;
            }

            return rid;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static unsafe uint? TryReadTokenIntegrityRid(nint token)
    {
        Span<byte> buffer = stackalloc byte[IntegrityBufferCapacity];
        fixed (byte* ptr = buffer)
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, ptr, (uint)buffer.Length, out var needed) ||
                needed < (uint)sizeof(nint))
            {
                return null;
            }

            var sid = *(nint*)ptr;
            if (sid == 0)
            {
                return null;
            }

            var countPtr = GetSidSubAuthorityCount(sid);
            if (countPtr == null || *countPtr == 0)
            {
                return null;
            }

            var ridPtr = GetSidSubAuthority(sid, (uint)(*countPtr - 1));
            return ridPtr == null ? null : *ridPtr;
        }
    }

    /// <summary>
    /// The name of the process that owns <paramref name="window"/>, as <c>Process.ProcessName</c> gives it (no path, no
    /// extension), or null when the window, its process or its image name cannot be had.
    /// </summary>
    public static unsafe string? ProcessNameOf(nint window)
    {
        if (window == 0)
        {
            return null;
        }

        _ = InjectionNativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return null;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            Span<char> path = stackalloc char[ImagePathCapacity];
            var length = (uint)path.Length;
            fixed (char* buffer = path)
            {
                if (!QueryFullProcessImageName(process, 0, buffer, ref length))
                {
                    return null;
                }
            }

            return Path.GetFileNameWithoutExtension(path[..(int)length]).ToString();
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool GetTokenInformation(
        nint tokenHandle, int tokenInformationClass, byte* tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern unsafe byte* GetSidSubAuthorityCount(nint pSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern unsafe uint* GetSidSubAuthority(nint pSid, uint nSubAuthority);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool QueryFullProcessImageName(nint hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
