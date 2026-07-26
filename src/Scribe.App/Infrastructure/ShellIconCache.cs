using System;
using System.Runtime.InteropServices;

namespace Scribe.App.Infrastructure;

/// <summary>Notifies Windows when an install or update replaces Scribe's embedded icon.</summary>
internal static class ShellIconCache
{
    private const uint AssociationChanged = 0x08000000;
    private const uint IdList = 0x0000;

    /// <summary>
    /// Invalidates cached shortcut and search-result artwork. This runs inside time-limited
    /// Velopack lifecycle hooks, so it must stay synchronous and fully non-throwing.
    /// </summary>
    public static void Refresh()
    {
        try
        {
            SHChangeNotify(AssociationChanged, IdList, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Icon-cache refresh is cosmetic and must never interfere with install or startup.
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        IntPtr item1,
        IntPtr item2);
}
