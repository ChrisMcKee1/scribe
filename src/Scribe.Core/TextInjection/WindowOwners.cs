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
    // and Access Rights), which is all QueryFullProcessImageName needs.
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>The window in front now, or zero.</summary>
    public static nint Foreground() => InjectionNativeMethods.GetForegroundWindow();

    /// <summary>
    /// The name of the process that owns <paramref name="window"/>, as <c>Process.ProcessName</c> gives it (no path, no
    /// extension), or null when the window, its process or its image name cannot be had.
    /// </summary>
    public static string? ProcessNameOf(nint window)
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
            var path = new char[1024];
            var length = (uint)path.Length;
            return QueryFullProcessImageName(process, 0, path, ref length)
                ? Path.GetFileNameWithoutExtension(new string(path, 0, (int)length))
                : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint hProcess, uint dwFlags, char[] lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
