namespace Scribe.Core.Libraries;

/// <summary>
/// Every file operation the library storage performs, as one seam. The journal's procedures (<see cref="LibraryInstaller"/>)
/// are written once over it, so the fault-injecting fake the tests use and <see cref="PhysicalLibraryFileSystem"/> run the
/// very same code: the checked move the fault suite proves is the one the installed app and the Store package run.
/// </summary>
/// <remarks>
/// Paths are absolute. Only <see cref="Replace"/> and a <see cref="Move"/> with <c>overwrite</c> ever replace a file, and
/// the journal calls those only where its rule R1 allows. Enumeration is top level only, like every loader of this and
/// older builds.
/// </remarks>
internal interface ILibraryFileSystem
{
    /// <summary>Whether a file is at <paramref name="path"/>: false only when nothing is there; a check that fails throws.</summary>
    bool Exists(string path);

    /// <summary>Whether a folder is at <paramref name="path"/>: false only when nothing is there; a check that fails throws.</summary>
    bool DirectoryExists(string path);

    byte[] ReadAllBytes(string path);

    /// <summary>Creates <paramref name="path"/> (never over an existing file), writes through and flushes to disk.</summary>
    void WriteAllBytesDurably(string path, ReadOnlySpan<byte> bytes);

    /// <summary><c>File.Replace(installCopy, target, backup, ignoreMetadataErrors: true)</c>.</summary>
    void Replace(string installCopy, string target, string backup);

    /// <summary><c>File.Move</c>: a rename within one volume.</summary>
    void Move(string source, string destination, bool overwrite);

    /// <summary>Deletes a file; no error when it is absent.</summary>
    void Delete(string path);

    /// <summary>
    /// Files directly in <paramref name="directory"/> whose names match <paramref name="pattern"/>; none when the folder
    /// is not there. A folder that cannot be listed throws: it may hold files, so it is never reported empty.
    /// </summary>
    IEnumerable<string> EnumerateFiles(string directory, string pattern);

    /// <summary>Folders directly in <paramref name="directory"/>; none when it is not there, and a failed listing throws.</summary>
    IEnumerable<string> EnumerateDirectories(string directory);

    void CreateDirectory(string path);

    /// <summary>Deletes a spent redo folder with everything in it; no error when it is absent.</summary>
    void DeleteDirectory(string path);
}

/// <summary>The real file system, for the app and for tests that drive the journal over real files.</summary>
/// <remarks>
/// Only a genuine not-found is an empty listing or an absent file. <see cref="Directory.Exists"/> and
/// <see cref="File.Exists"/> answer false on any error, access denied included, so a check made with them would turn a
/// folder Windows refuses to list into an empty, successful listing, and a file it refuses to show into a missing one:
/// every question here goes to the operation itself, and every other failure reaches the caller.
/// </remarks>
internal sealed class PhysicalLibraryFileSystem : ILibraryFileSystem
{
    public static PhysicalLibraryFileSystem Instance { get; } = new();

    private PhysicalLibraryFileSystem()
    {
    }

    public bool Exists(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) == 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    public bool DirectoryExists(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public void WriteAllBytesDurably(string path, ReadOnlySpan<byte> bytes)
    {
        // CreateNew: a redo image, a manifest, an install copy and the witness are only ever created, never written
        // over, so an unexpected file at the name is a failure the caller sees rather than bytes silently replaced.
        using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    public void Replace(string installCopy, string target, string backup) =>
        File.Replace(installCopy, target, backup, ignoreMetadataErrors: true);

    public void Move(string source, string destination, bool overwrite)
    {
        try
        {
            File.Move(source, destination, overwrite);
        }
        catch (UnauthorizedAccessException) when (overwrite && ClearReadOnly(destination))
        {
            File.Move(source, destination, overwrite);
        }
    }

    // The journal deletes only what its rules already let go (a spare backup holding P or S, a spent install copy, an
    // entry the user purged, an expired journal file). A read-only attribute, which a file moved aside from a read-only
    // library keeps, must not strand the Save that moved it there.
    public void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException) when (ClearReadOnly(path))
        {
            File.Delete(path);
        }
    }

    public IEnumerable<string> EnumerateFiles(string directory, string pattern)
    {
        try
        {
            return Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        try
        {
            return Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone.
        }
    }

    private static bool ClearReadOnly(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || !info.Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            return false;
        }

        info.Attributes &= ~FileAttributes.ReadOnly;
        return true;
    }
}
