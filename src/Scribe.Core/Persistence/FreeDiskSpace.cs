using System.Runtime.InteropServices;

namespace Scribe.Core.Persistence;

/// <summary>Free space on the volume that holds a directory, for the maintenance VACUUM pre-check.</summary>
internal static partial class FreeDiskSpace
{
    /// <summary>
    /// Bytes available to this user on the volume containing <paramref name="directory"/>, or
    /// <see langword="null"/> when Windows cannot say. GetDiskFreeSpaceExW takes any directory, so
    /// a folder on a mounted volume reports that volume rather than its drive letter's.
    /// </summary>
    internal static long? TryGetAvailableBytes(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        try
        {
            var path = Path.GetFullPath(directory);

            // UNC paths must end in a backslash for this API; a trailing one is harmless otherwise.
            if (!Path.EndsInDirectorySeparator(path))
            {
                path += Path.DirectorySeparatorChar;
            }

            return GetDiskFreeSpaceEx(path, out var available, out _, out _)
                ? (long)Math.Min(available, long.MaxValue)
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);
}
