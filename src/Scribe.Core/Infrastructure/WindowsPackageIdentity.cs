using System.Runtime.InteropServices;
using System.Text;

namespace Scribe.Core.Infrastructure;

/// <summary>
/// Detects whether the current process has Windows package identity. Microsoft Store installs
/// have package identity and must let the Store own updates; Velopack installs do not.
/// </summary>
public static class WindowsPackageIdentity
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    public static bool IsPackaged()
    {
        try
        {
            var length = 0;
            var result = GetCurrentPackageFullName(ref length, null);
            return HasIdentityForProbeResult(result);
        }
        catch
        {
            // Package detection is an update-routing hint. It must never block app startup.
            return false;
        }
    }

    internal static bool HasIdentityForProbeResult(int result) =>
        result is ErrorSuccess or ErrorInsufficientBuffer;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref int packageFullNameLength,
        StringBuilder? packageFullName);
}
