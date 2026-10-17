using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Infrastructure;

/// <summary>
/// Registers the running process with Windows Application Recovery and Restart so Windows Restart
/// Manager relaunches Scribe in the system tray after a Microsoft Store package update or an OS
/// update restart closes it.
/// </summary>
/// <remarks>
/// <para>
/// Microsoft's packaged-desktop guidance requires calling <c>RegisterApplicationRestart</c> in the
/// main process if the app should restart after an update closes it. Only update and OS-reboot
/// restarts are enabled (<see cref="UpdateRestartFlags"/> sets <c>RESTART_NO_CRASH</c> and
/// <c>RESTART_NO_HANG</c> so unhandled exceptions or hangs never enter a restart loop), and the
/// registered command line is empty so a session originally opened with <c>--settings</c> comes
/// back quietly in the tray.
/// </para>
/// <para>
/// The overlay helper (<c>Scribe.Overlay.exe</c>) deliberately never registers: the main process
/// owns its lifetime and launches it with a per-session pipe name.
/// </para>
/// </remarks>
public static class ApplicationRestartRegistration
{
    internal const uint RestartNoCrash = 0x1;
    internal const uint RestartNoHang = 0x2;

    /// <summary>
    /// Command-line switch passed to Windows Restart Manager so that if both Restart Manager and the
    /// startup task launch Scribe after an OS update reboot, the duplicate instance exits quietly
    /// instead of opening the Settings window or showing the already-running notice.
    /// </summary>
    public const string RestartCommandLine = "--restarted";

    /// <summary>
    /// Restart only when Windows closes the app for a patch/package update or an update reboot.
    /// </summary>
    public const uint UpdateRestartFlags = RestartNoCrash | RestartNoHang;

    /// <summary>True when <paramref name="args"/> carries <see cref="RestartCommandLine"/>.</summary>
    public static bool HasRestartedSwitch(IReadOnlyList<string>? args) =>
        args is not null && args.Any(arg => string.Equals(arg, RestartCommandLine, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Registers the current process to restart after an update unless it is running against an
    /// isolated scratch data folder (<c>SCRIBE_DATA_DIR</c>). Never throws.
    /// </summary>
    public static bool TryRegisterForUpdateRestart(
        bool isIsolatedRoot = false,
        ILogger? log = null,
        Func<string?, uint, int>? registerOverride = null)
    {
        if (isIsolatedRoot)
        {
            log?.LogDebug("Application restart registration skipped: this instance uses an isolated data folder.");
            return false;
        }

        try
        {
            var register = registerOverride ?? RegisterApplicationRestart;
            var hr = register(RestartCommandLine, UpdateRestartFlags);
            if (hr == 0)
            {
                log?.LogDebug("Registered for automatic restart after Windows or Store updates.");
                return true;
            }

            log?.LogDebug("Windows did not register the app for update restart (hr=0x{Hr:X8}).", hr);
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log?.LogDebug("Could not register for update restart ({Failure}).", FailureShape.Describe(ex));
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? pwzCommandline, uint dwFlags);
}
