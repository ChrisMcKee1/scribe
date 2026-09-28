using System.Security.AccessControl;

namespace Scribe.Core.Diagnostics;

/// <summary>
/// Opens the shared daily log so that every write lands at its end (DATA-O-02, <c>PerfFlags.AppendOnlyLog</c>).
/// </summary>
/// <remarks>
/// A <see cref="FileMode.Append"/> stream writes at the offset it captured when it opened, so a line the other process
/// appends between this one's open and its write is written over. A handle with FILE_APPEND_DATA and without
/// FILE_WRITE_DATA "will not overwrite existing data" (Win32 File Access Rights Constants), whatever offset FileStream
/// passes, so the app and the overlay can no longer write over each other's lines. Everything else stays as today's
/// writers do it: one open, one write and one close per batch or line, <see cref="FileShare.ReadWrite"/>, and no managed
/// buffer. The overlay compiles this file itself (it has no Scribe.Core reference), as it does PillGeometry.cs.
/// <para>
/// On .NET 10 the returned stream also reports <see cref="Stream.CanRead"/>: FileSystemAclExtensions maps any right to
/// read and write there (its later versions map this one to write only). The handle Windows returns has no read right, so
/// a read is refused; nothing here reads.
/// </para>
/// </remarks>
internal static class AppendOnlyFile
{
    /// <summary>
    /// The overlay launch argument that tells the helper the app appends this way too. The helper follows only this, never
    /// the environment, so a pair never runs two ways.
    /// </summary>
    internal const string LaunchArgument = "--append-only-log";

    /// <summary>
    /// Opens <paramref name="path"/> for appending only, creating it when missing. A buffer of one byte is no buffer, so one
    /// <see cref="Stream.Write(byte[], int, int)"/> call is one write to the file.
    /// </summary>
    internal static FileStream Open(string path) =>
        new FileInfo(path).Create(FileMode.OpenOrCreate, FileSystemRights.AppendData, FileShare.ReadWrite, 1, FileOptions.None, null);
}
