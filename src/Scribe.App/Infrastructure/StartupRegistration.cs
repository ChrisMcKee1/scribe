using Microsoft.Win32;

namespace Scribe.App.Infrastructure;

/// <summary>
/// Manages "launch at logon" for the current user via the
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> key. This is the standard,
/// dependency-free mechanism for an unpackaged desktop app: it needs no elevation, survives
/// reboots, and is surfaced (and toggleable) by the user in Task Manager's "Startup apps".
/// The stored command points at the running executable so the entry stays valid wherever the
/// app is installed; <see cref="Sync"/> reconciles the registry with the user's preference and
/// self-heals a stale path if the app was moved.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Scribe";

    /// <summary>The path of the running executable (the apphost), used as the Run command.</summary>
    private static string? ExecutablePath => Environment.ProcessPath;

    /// <summary>True when a Run entry for Scribe currently exists.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Adds or removes the Run entry to match <paramref name="enabled"/>. Returns true on success.
    /// Failures (e.g. locked-down registry) are swallowed and reported as false so the caller can
    /// keep the persisted preference in sync without crashing.
    /// </summary>
    public static bool Set(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var exe = ExecutablePath;
                if (string.IsNullOrWhiteSpace(exe))
                {
                    return false;
                }

                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                // Quote the path so a directory with spaces (e.g. "Program Files") still launches.
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reconciles the Run key with the persisted preference at startup. When enabled it also
    /// refreshes the stored path so an entry written by an older/moved copy is corrected.
    /// </summary>
    public static void Sync(bool enabled)
    {
        if (!enabled)
        {
            if (IsEnabled())
            {
                Set(false);
            }

            return;
        }

        if (!IsEnabled() || !PathMatchesCurrentExecutable())
        {
            Set(true);
        }
    }

    private static bool PathMatchesCurrentExecutable()
    {
        try
        {
            var exe = ExecutablePath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                return false;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key?.GetValue(ValueName) is not string stored)
            {
                return false;
            }

            var normalized = stored.Trim().Trim('"');
            return string.Equals(normalized, exe, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
