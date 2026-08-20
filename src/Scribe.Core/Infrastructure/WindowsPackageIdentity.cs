using System.Runtime.InteropServices;
using System.Text;

namespace Scribe.Core.Infrastructure;

/// <summary>
/// Detects whether the current process has Windows package identity. Microsoft Store installs
/// have package identity and must let the Store own updates; Velopack installs do not.
/// <para>
/// Package identity also decides <b>where the app's files actually land</b>. A packaged desktop
/// app runs with AppData write virtualization on by default: a folder it creates under
/// <c>%LOCALAPPDATA%</c> is written to
/// <c>%LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\Local\</c> and does not exist at the path
/// the app itself sees. That is why Store users could not find
/// <c>%LOCALAPPDATA%\ScribeData\logs</c>: it was never there. The package manifest now excludes
/// <c>ScribeData</c> from virtualization, and <see cref="AppPaths"/> uses
/// <see cref="TryGetVirtualizedLocalAppData"/> to migrate data written by builds that shipped
/// before that fix.
/// </para>
/// </summary>
public static class WindowsPackageIdentity
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>Folder Windows redirects virtualized <c>%LOCALAPPDATA%</c> writes into.</summary>
    private const string PackagesFolderName = "Packages";

    /// <summary>Per-package subtree holding the redirected Local/Roaming AppData trees.</summary>
    private const string LocalCacheLocalRelativePath = @"LocalCache\Local";

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

    /// <summary>
    /// The current package family name (e.g. <c>53984VeteranApps.ScribeAI_e3jkm6dfkwwbm</c>), or
    /// <see langword="null"/> when this process has no package identity.
    /// </summary>
    public static string? TryGetPackageFamilyName()
    {
        try
        {
            var length = 0;
            if (GetCurrentPackageFamilyName(ref length, null) != ErrorInsufficientBuffer || length <= 0)
            {
                return null;
            }

            var buffer = new StringBuilder(length);
            return GetCurrentPackageFamilyName(ref length, buffer) == ErrorSuccess
                ? buffer.ToString()
                : null;
        }
        catch
        {
            // Same contract as IsPackaged: identity is a hint, never a startup blocker.
            return null;
        }
    }

    /// <summary>
    /// The real on-disk folder that virtualized <c>%LOCALAPPDATA%</c> writes are redirected into
    /// for this package, or <see langword="null"/> when the process is unpackaged. The folder is
    /// not required to exist: callers probe it.
    /// </summary>
    public static string? TryGetVirtualizedLocalAppData(string? localAppData = null)
    {
        var family = TryGetPackageFamilyName();
        if (family is null)
        {
            return null;
        }

        try
        {
            var root = localAppData
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(root)
                ? null
                : Path.Combine(root, PackagesFolderName, family, LocalCacheLocalRelativePath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Composes the redirected path for a package family without querying the current process.
    /// Exposed for tests, which have no package identity of their own.
    /// </summary>
    internal static string ComposeVirtualizedLocalAppData(string localAppData, string packageFamilyName) =>
        Path.Combine(localAppData, PackagesFolderName, packageFamilyName, LocalCacheLocalRelativePath);

    internal static bool HasIdentityForProbeResult(int result) =>
        result is ErrorSuccess or ErrorInsufficientBuffer;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref int packageFullNameLength,
        StringBuilder? packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(
        ref int packageFamilyNameLength,
        StringBuilder? packageFamilyName);
}
