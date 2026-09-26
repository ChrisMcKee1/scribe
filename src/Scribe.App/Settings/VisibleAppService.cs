using System.Diagnostics;
using System.Runtime.InteropServices;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

internal sealed class VisibleAppService
{
    public IReadOnlyList<AppPickerCandidate> GetVisibleApps()
    {
        var currentProcess = Environment.ProcessId;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<AppPickerCandidate>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsVisibleTopLevelWindow(hwnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0 || processId == currentProcess)
            {
                return true;
            }

            try
            {
                using var process = Process.GetProcessById((int)processId);
                var processName = process.ProcessName;
                if (IsScribeProcess(processName) || !seen.Add(processName))
                {
                    return true;
                }

                apps.Add(new AppPickerCandidate(processName, AppDisplayName.For(processName), IsRunning: true));
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            return true;
        }, IntPtr.Zero);

        return apps;
    }

    private static bool IsVisibleTopLevelWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
        {
            return false;
        }

        return true;
    }

    private static bool IsScribeProcess(string processName) =>
        processName.StartsWith("Scribe", StringComparison.OrdinalIgnoreCase);

    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
