using System.Diagnostics;
using System.Text.Json;
using Scribe.Core.Infrastructure;

namespace Scribe.Core.Cleanup;

/// <summary>
/// Where Scribe's own Foundry Local data lives, plus the marker that remembers a pending
/// "keep only the selected model" pass across restarts.
/// </summary>
/// <remarks>
/// <para>
/// One directory, resolved once by <see cref="ResolveAppDataDir(AppPaths)"/>, is both what the SDK is
/// configured with (<c>Configuration.AppDataDir</c>) and the only root reclaim may delete under, so
/// the two can never disagree. For the normal profile it is the SDK's own default for
/// <see cref="AppName"/>: the configuration documents the application data directory as
/// <c>{home}/.{AppName}</c>, the model cache as <c>{appdata}/cache/models</c>, and logs as
/// <c>{appdata}/logs</c> (Configuration.cs in microsoft/Foundry-Local, the commit the pinned 1.2.4
/// package was built from). That default is where every existing install's downloads already are,
/// so it must not move. An isolated profile (tests, <c>SCRIBE_DATA_DIR</c>) keeps it inside its own
/// root instead, so it can never download into, or delete from, the real user's directory.
/// </para>
/// <para>
/// Execution-provider downloads land in <c>{appdata}/ep</c>; the SDK does not document that path, it
/// was observed on a real install (<c>ep\cuda-ep</c>, <c>ep\webgpu-ep</c>), so the janitor treats it
/// with the same refusals as everything else rather than trusting it. This is never the Foundry
/// Local CLI's own <c>%USERPROFILE%\.foundry</c>, and never another application's directory.
/// </para>
/// </remarks>
internal sealed class FoundryLocalStorage
{
    /// <summary>The Foundry Local application name Scribe registers with, and so its directory name.</summary>
    public const string AppName = "Scribe";

    /// <summary>The folder inside an isolated data root that stands in for the SDK's default directory.</summary>
    internal const string IsolatedFolderName = "foundry";

    internal const string MarkerFileName = "foundry-local-storage.json";

    public FoundryLocalStorage(string appDataDir, string markerPath, FoundryStorageJanitor janitor)
    {
        if (string.IsNullOrWhiteSpace(appDataDir) || !Path.IsPathFullyQualified(appDataDir))
        {
            throw new ArgumentException("The Foundry Local data directory must be a full path.", nameof(appDataDir));
        }

        AppDataDir = Path.GetFullPath(appDataDir);
        ModelCacheDir = Path.Combine(AppDataDir, "cache", "models");
        ExecutionProviderDir = Path.Combine(AppDataDir, "ep");
        MarkerPath = markerPath;
        Janitor = janitor;
    }

    /// <summary>The SDK's application data directory for <see cref="AppName"/>.</summary>
    public string AppDataDir { get; }

    /// <summary>The SDK's default model cache directory.</summary>
    public string ModelCacheDir { get; }

    /// <summary>Where execution-provider downloads were observed to land.</summary>
    public string ExecutionProviderDir { get; }

    /// <summary>Persisted pending-reclaim state, beside Scribe's other settings files.</summary>
    public string MarkerPath { get; }

    public FoundryStorageJanitor Janitor { get; }

    /// <summary>
    /// The Foundry Local application data directory for this profile, or null when it cannot be
    /// resolved to a full path (then the SDK keeps its own default and reclaim stays disarmed).
    /// </summary>
    public static string? ResolveAppDataDir(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return ResolveAppDataDir(
            paths.IsIsolatedRoot, paths.RootDir, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    internal static string? ResolveAppDataDir(bool isolatedRoot, string rootDir, string? userProfile)
    {
        if (isolatedRoot)
        {
            // Always inside the isolated root, resolved to a full path, and never the user's own.
            return string.IsNullOrWhiteSpace(rootDir)
                ? null
                : Path.GetFullPath(Path.Combine(rootDir, IsolatedFolderName));
        }

        // The SDK default. Our process and the in-process SDK resolve the profile folder the same way,
        // and a relative answer would otherwise resolve against the current directory.
        return !string.IsNullOrWhiteSpace(userProfile) && Path.IsPathFullyQualified(userProfile)
            ? Path.Combine(userProfile, "." + AppName)
            : null;
    }

    /// <summary>
    /// The layout for this profile, or null when its directory cannot be resolved. Null disarms
    /// reclaim: nothing outside the SDK's own directory may ever be deleted.
    /// </summary>
    public static FoundryLocalStorage? For(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return ResolveAppDataDir(paths) is { } appDataDir
            ? new FoundryLocalStorage(
                appDataDir,
                Path.Combine(paths.RootDir, MarkerFileName),
                new FoundryStorageJanitor(new PhysicalFoundryStorageFileSystem()))
            : null;
    }

    /// <summary>
    /// True when a model switch is waiting for the new model to be in use before the others are
    /// deleted. Never throws; an unreadable marker reads as false, which deletes nothing.
    /// </summary>
    public bool ReadKeepOnlySelected()
    {
        try
        {
            if (!File.Exists(MarkerPath))
            {
                return false;
            }

            // Delete sharing, so a reader can never be the reason a concurrent clear fails.
            using var stream = new FileStream(
                MarkerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var marker = JsonSerializer.Deserialize<Marker>(stream);
            return marker?.KeepOnlySelected == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Persists the pending state. Returns false when it could not be written.</summary>
    public bool WriteKeepOnlySelected(bool value)
    {
        try
        {
            if (!value)
            {
                if (File.Exists(MarkerPath))
                {
                    File.Delete(MarkerPath);
                }

                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);

            // Staged and moved into place so a crash mid-write can never leave a torn marker.
            var staging = MarkerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(staging, JsonSerializer.Serialize(new Marker { KeepOnlySelected = true }));
                File.Move(staging, MarkerPath, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(staging))
                    {
                        File.Delete(staging);
                    }
                }
                catch (Exception)
                {
                    // A uniquely named staging file cannot block a later write.
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// <paramref name="path"/> with the user profile folded to <c>%USERPROFILE%</c>, so a log line can
    /// say which directory was reclaimed without recording the user name.
    /// </summary>
    public static string DisplayPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) &&
            path.StartsWith(home, StringComparison.OrdinalIgnoreCase) &&
            (path.Length == home.Length || path[home.Length] is '\\' or '/'))
        {
            return "%USERPROFILE%" + path[home.Length..];
        }

        return "(outside the user profile)";
    }

    private sealed class Marker
    {
        public bool KeepOnlySelected { get; set; }
    }
}

/// <summary>What the janitor did. Sizes and counts only.</summary>
internal readonly record struct FoundryStorageReclaimResult(
    long BytesDeleted,
    int FilesDeleted,
    int FilesDeferred,
    int ReparsePointsSkipped,
    bool Refused)
{
    public static FoundryStorageReclaimResult RefusedResult { get; } = new(0, 0, 0, 0, Refused: true);

    public FoundryStorageReclaimResult Add(FoundryStorageReclaimResult other) => new(
        BytesDeleted + other.BytesDeleted,
        FilesDeleted + other.FilesDeleted,
        FilesDeferred + other.FilesDeferred,
        ReparsePointsSkipped + other.ReparsePointsSkipped,
        Refused || other.Refused);
}

/// <summary>One directory entry as the janitor needs to see it.</summary>
internal readonly record struct FoundryStorageEntry(
    string Path, bool IsDirectory, bool IsReparsePoint, long Length, bool IsReadOnly);

/// <summary>The file operations the janitor performs, so its refusals can be tested against fakes.</summary>
internal interface IFoundryStorageFileSystem
{
    /// <summary>The entry at <paramref name="path"/>, or null when nothing is there.</summary>
    FoundryStorageEntry? GetEntry(string path);

    /// <summary>The direct children of a directory. Never follows a reparse point.</summary>
    IEnumerable<FoundryStorageEntry> EnumerateEntries(string directory);

    void DeleteFile(string path, bool clearReadOnly);

    /// <summary>Deletes a directory that is already empty; throws when it is not.</summary>
    void DeleteEmptyDirectory(string path);

    /// <summary>
    /// Renames a directory within its parent. Throws when anything inside is open, so the directory
    /// moves as a whole or not at all.
    /// </summary>
    void MoveDirectory(string source, string destination);

    /// <summary>The files loaded as modules in this process, or null when they cannot be listed.</summary>
    IReadOnlyList<string>? LoadedModulePaths();
}

/// <summary>
/// Deletes directories inside Scribe's Foundry Local directory, refusing anything that would reach
/// outside it.
/// </summary>
/// <remarks>
/// <see cref="ReclaimDirectory"/> is all or nothing against a file in use: the directory is first
/// renamed to a tombstone beside it, which Windows refuses while any file inside is open ("The
/// directory or a file within it is being used by another process", the documented
/// <c>Directory.Move</c> failure), and only the tombstone is then deleted. A loaded native library
/// does not block that rename, since a mapped image holds no open handle (measured: the parent of a
/// loaded DLL renames, the DLL itself then refuses deletion), so a directory holding a module loaded
/// in this process is left whole instead. Refusals, each of which is a test:
/// <list type="bullet">
/// <item>The target must be strictly inside the root after full-path normalization.</item>
/// <item>The root, and every directory from the root down to the target, must be a real directory
/// rather than a junction or symbolic link. A user who moved the cache to another drive with a
/// junction must not have that drive's contents deleted.</item>
/// <item>Any reparse point met while walking is skipped: not followed, and not deleted.</item>
/// <item>A directory with a file in use, or a module loaded in this process, is left whole for a
/// later attempt and counted as deferred. Nothing is forced.</item>
/// <item>A tombstone an earlier pass could not finish (interrupted, or holding a link it would not
/// delete) is deleted by the next pass under the same refusals.</item>
/// </list>
/// It never throws; a failure part-way reports what it managed.
/// </remarks>
internal sealed class FoundryStorageJanitor(IFoundryStorageFileSystem fileSystem)
{
    // Deep enough for any model or execution-provider layout; a bound so a pathological tree cannot
    // turn a cleanup into a stack overflow.
    private const int MaxDepth = 32;

    // A cache holding this many entries holds a model; the bound keeps a startup check cheap.
    private const int MaxInspectedEntries = 100_000;

    /// <summary>What a tombstone's name carries between the directory's name and a GUID.</summary>
    internal const string TombstoneMarker = ".reclaim-";

    private static readonly FoundryStorageReclaimResult DeferredWhole = new(0, 0, FilesDeferred: 1, 0, Refused: false);

    /// <summary>
    /// Deletes one directory as a unit, after deleting any tombstones an earlier pass left for it.
    /// </summary>
    public FoundryStorageReclaimResult ReclaimDirectory(string root, string target, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryResolve(root, target, out var fullRoot, out var fullTarget))
            {
                return FoundryStorageReclaimResult.RefusedResult;
            }

            if (CheckParents(fullRoot, fullTarget) is { } stopped)
            {
                return stopped;
            }

            var result = DeleteLeftoverTombstones(fullRoot, fullTarget, cancellationToken);
            if (CheckTarget(fullTarget) is { } targetStopped)
            {
                return result.Add(targetStopped);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return result;
            }

            if (HoldsLoadedModule(fullTarget))
            {
                return result.Add(DeferredWhole);
            }

            var tombstone = Path.Combine(Path.GetDirectoryName(fullTarget)!, NewTombstoneName(Path.GetFileName(fullTarget)));
            try
            {
                fileSystem.MoveDirectory(fullTarget, tombstone);
            }
            catch (Exception)
            {
                // Something inside is open. Nothing was deleted; all of it is retried later.
                return result.Add(DeferredWhole);
            }

            // Re-validated as a path of its own before anything in it is deleted.
            return result.Add(DeleteContents(fullRoot, tombstone, cancellationToken));
        }
        catch (Exception)
        {
            return FoundryStorageReclaimResult.RefusedResult;
        }
    }

    /// <summary>
    /// Deletes everything under <paramref name="target"/> file by file, leaving whatever is in use.
    /// Reclaim reaches it only through <see cref="ReclaimDirectory"/>, on a tombstone nothing has open.
    /// </summary>
    public FoundryStorageReclaimResult DeleteContents(string root, string target, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryResolve(root, target, out var fullRoot, out var fullTarget))
            {
                return FoundryStorageReclaimResult.RefusedResult;
            }

            if (CheckParents(fullRoot, fullTarget) is { } stopped)
            {
                return stopped;
            }

            return CheckTarget(fullTarget) is { } targetStopped
                ? targetStopped
                : DeleteTree(fullRoot, fullTarget, cancellationToken);
        }
        catch (Exception)
        {
            return FoundryStorageReclaimResult.RefusedResult;
        }
    }

    /// <summary>
    /// Whether the model cache holds a model. Loose files at its top are the SDK's catalog index
    /// (<c>foundry.modelinfo.json</c>, present on a PC that never downloaded a model), so a model is a
    /// real file inside one of its folders. Anything unreadable, linked or too large to walk cheaply
    /// is <see cref="FoundryModelCache.Unknown"/>, which callers treat as holding models.
    /// </summary>
    public FoundryModelCache InspectModelCache(string root, string modelCacheDir)
    {
        try
        {
            if (!TryResolve(root, modelCacheDir, out var fullRoot, out var fullCache))
            {
                return FoundryModelCache.Unknown;
            }

            if (CheckParents(fullRoot, fullCache) is { } stopped)
            {
                // Missing on the way is an empty cache; a link or refusal is not something to reason past.
                return stopped == default ? FoundryModelCache.Empty : FoundryModelCache.Unknown;
            }

            var cache = fileSystem.GetEntry(fullCache);
            if (cache is null)
            {
                return FoundryModelCache.Empty;
            }

            if (cache is not { IsDirectory: true, IsReparsePoint: false })
            {
                return FoundryModelCache.Unknown;
            }

            var budget = MaxInspectedEntries;
            foreach (var entry in fileSystem.EnumerateEntries(fullCache))
            {
                if (--budget < 0 || entry.IsReparsePoint || !IsStrictlyInside(Path.GetFullPath(entry.Path), fullRoot))
                {
                    return FoundryModelCache.Unknown;
                }

                if (!entry.IsDirectory)
                {
                    continue;
                }

                var inner = FindFile(fullRoot, entry.Path, depth: 1, ref budget);
                if (inner != FoundryModelCache.Empty)
                {
                    return inner;
                }
            }

            return FoundryModelCache.Empty;
        }
        catch (Exception)
        {
            return FoundryModelCache.Unknown;
        }
    }

    /// <summary>
    /// The total size of the files at or under <paramref name="path"/>, read-only, for reporting.
    /// Zero when the path is outside the root, is a link, or cannot be read.
    /// </summary>
    public long MeasureBytes(string root, string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(path))
            {
                return 0;
            }

            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!IsStrictlyInside(fullPath, fullRoot) ||
                fileSystem.GetEntry(fullPath) is not { IsReparsePoint: false } entry)
            {
                return 0;
            }

            return entry.IsDirectory ? SumDirectory(fullRoot, fullPath, depth: 0) : Math.Max(0, entry.Length);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private long SumDirectory(string root, string directory, int depth)
    {
        if (depth >= MaxDepth)
        {
            return 0;
        }

        long total = 0;
        foreach (var entry in fileSystem.EnumerateEntries(directory))
        {
            if (entry.IsReparsePoint || !IsStrictlyInside(Path.GetFullPath(entry.Path), root))
            {
                continue;
            }

            total += entry.IsDirectory ? SumDirectory(root, entry.Path, depth + 1) : Math.Max(0, entry.Length);
        }

        return total;
    }

    private FoundryStorageReclaimResult DeleteChildren(
        string root, string directory, int depth, CancellationToken cancellationToken)
    {
        var result = default(FoundryStorageReclaimResult);
        if (depth >= MaxDepth)
        {
            return result with { FilesDeferred = 1 };
        }

        List<FoundryStorageEntry> entries;
        try
        {
            entries = fileSystem.EnumerateEntries(directory).ToList();
        }
        catch (Exception)
        {
            return result with { FilesDeferred = 1 };
        }

        foreach (var entry in entries)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Defence in depth: an enumerator must only ever return children of the directory.
            if (!IsStrictlyInside(Path.GetFullPath(entry.Path), root))
            {
                result = result.Add(FoundryStorageReclaimResult.RefusedResult);
                continue;
            }

            if (entry.IsReparsePoint)
            {
                result = result with { ReparsePointsSkipped = result.ReparsePointsSkipped + 1 };
                continue;
            }

            if (entry.IsDirectory)
            {
                result = result.Add(DeleteChildren(root, entry.Path, depth + 1, cancellationToken));
                TryDeleteEmptyDirectory(entry.Path);
                continue;
            }

            try
            {
                fileSystem.DeleteFile(entry.Path, clearReadOnly: entry.IsReadOnly);
                result = result with
                {
                    BytesDeleted = result.BytesDeleted + Math.Max(0, entry.Length),
                    FilesDeleted = result.FilesDeleted + 1,
                };
            }
            catch (Exception)
            {
                // Loaded, open or protected. Left for the next attempt rather than forced.
                result = result with { FilesDeferred = result.FilesDeferred + 1 };
            }
        }

        return result;
    }

    private void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            fileSystem.DeleteEmptyDirectory(directory);
        }
        catch (Exception)
        {
            // Not empty because something was deferred or skipped; that is the correct outcome.
        }
    }

    // A relative path would resolve against the current directory, which is not a place this janitor
    // may ever reach, and the target must be strictly inside the root.
    private static bool TryResolve(string root, string target, out string fullRoot, out string fullTarget)
    {
        fullRoot = string.Empty;
        fullTarget = string.Empty;
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(target))
        {
            return false;
        }

        fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        fullTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        return IsStrictlyInside(fullTarget, fullRoot);
    }

    // The root and every directory between it and the target must be real directories. Returns null
    // when the pass may go on; otherwise what to report: nothing (something on the way is simply
    // missing, the common healthy case of a PC that never used Foundry Local) or a refusal.
    private FoundryStorageReclaimResult? CheckParents(string fullRoot, string fullTarget)
    {
        var rootEntry = fileSystem.GetEntry(fullRoot);
        if (rootEntry is not { IsDirectory: true, IsReparsePoint: false })
        {
            return rootEntry is null ? default(FoundryStorageReclaimResult) : FoundryStorageReclaimResult.RefusedResult;
        }

        var parent = Path.GetDirectoryName(fullTarget)!;
        var current = fullRoot;
        foreach (var segment in Path.GetRelativePath(fullRoot, parent).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            current = Path.Combine(current, segment);
            if (CheckDirectory(current) is { } stopped)
            {
                return stopped;
            }
        }

        return null;
    }

    // The target itself: missing is nothing to do, anything but a real directory is refused.
    private FoundryStorageReclaimResult? CheckTarget(string fullTarget) => CheckDirectory(fullTarget);

    private FoundryStorageReclaimResult? CheckDirectory(string path)
    {
        var entry = fileSystem.GetEntry(path);
        if (entry is null)
        {
            return default(FoundryStorageReclaimResult);
        }

        return entry.Value is { IsDirectory: true, IsReparsePoint: false }
            ? null
            : FoundryStorageReclaimResult.RefusedResult with { ReparsePointsSkipped = entry.Value.IsReparsePoint ? 1 : 0 };
    }

    private FoundryStorageReclaimResult DeleteTree(string root, string directory, CancellationToken cancellationToken)
    {
        var result = DeleteChildren(root, directory, depth: 0, cancellationToken);
        TryDeleteEmptyDirectory(directory);
        return result;
    }

    // Tombstones of this target that an earlier pass renamed but could not finish deleting.
    private FoundryStorageReclaimResult DeleteLeftoverTombstones(string root, string target, CancellationToken cancellationToken)
    {
        var result = default(FoundryStorageReclaimResult);
        var prefix = "." + Path.GetFileName(target) + TombstoneMarker;
        List<FoundryStorageEntry> siblings;
        try
        {
            siblings = fileSystem.EnumerateEntries(Path.GetDirectoryName(target)!).ToList();
        }
        catch (Exception)
        {
            return result;
        }

        foreach (var entry in siblings)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!entry.IsDirectory || !IsTombstoneName(Path.GetFileName(entry.Path), prefix))
            {
                continue;
            }

            if (!IsStrictlyInside(Path.GetFullPath(entry.Path), root))
            {
                result = result.Add(FoundryStorageReclaimResult.RefusedResult);
                continue;
            }

            result = entry.IsReparsePoint
                ? result with { ReparsePointsSkipped = result.ReparsePointsSkipped + 1 }
                : result.Add(DeleteContents(root, entry.Path, cancellationToken));
        }

        return result;
    }

    internal static string NewTombstoneName(string directoryName) =>
        "." + directoryName + TombstoneMarker + Guid.NewGuid().ToString("N");

    // Exactly ".{name}.reclaim-{32 hex digits}", so nothing the SDK or a user named is ever mistaken for one.
    internal static bool IsTombstoneName(string name, string prefix) =>
        name.Length == prefix.Length + 32 &&
        name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParseExact(name.AsSpan(prefix.Length), "N", out _);

    private bool HoldsLoadedModule(string fullTarget)
    {
        // Unlisted is not a reason to keep gigabytes forever: reclaim only reaches these directories
        // when no Foundry Local runtime was created in this process, so nothing of Scribe's is loaded
        // from them. The check exists for the case that assumption is ever wrong.
        if (fileSystem.LoadedModulePaths() is not { } modules)
        {
            return false;
        }

        foreach (var module in modules)
        {
            if (!string.IsNullOrEmpty(module) && Path.IsPathFullyQualified(module) &&
                IsStrictlyInside(Path.GetFullPath(module), fullTarget))
            {
                return true;
            }
        }

        return false;
    }

    // Model when any real file is at or under the directory; Unknown on a link, an escaping entry, or
    // past the depth or entry bound; Empty otherwise.
    private FoundryModelCache FindFile(string root, string directory, int depth, ref int budget)
    {
        if (depth >= MaxDepth)
        {
            return FoundryModelCache.Unknown;
        }

        foreach (var entry in fileSystem.EnumerateEntries(directory))
        {
            if (--budget < 0 || entry.IsReparsePoint || !IsStrictlyInside(Path.GetFullPath(entry.Path), root))
            {
                return FoundryModelCache.Unknown;
            }

            if (!entry.IsDirectory)
            {
                return FoundryModelCache.HasModels;
            }

            var inner = FindFile(root, entry.Path, depth + 1, ref budget);
            if (inner != FoundryModelCache.Empty)
            {
                return inner;
            }
        }

        return FoundryModelCache.Empty;
    }

    private static bool IsStrictlyInside(string candidate, string root)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(candidate);
        return trimmed.Length > root.Length &&
            trimmed.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
            trimmed[root.Length] == Path.DirectorySeparatorChar;
    }
}

/// <summary>The real file system.</summary>
internal sealed class PhysicalFoundryStorageFileSystem : IFoundryStorageFileSystem
{
    public FoundryStorageEntry? GetEntry(string path)
    {
        // DirectoryInfo and FileInfo report on the link itself, not its target, so a junction is seen
        // as a reparse point here rather than as the directory it points to.
        var directory = new DirectoryInfo(path);
        if (directory.Exists)
        {
            return ToEntry(directory);
        }

        var file = new FileInfo(path);
        return file.Exists ? ToEntry(file) : null;
    }

    public IEnumerable<FoundryStorageEntry> EnumerateEntries(string directory)
    {
        // Top level only: the janitor recurses itself so it can refuse reparse points on the way.
        foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            yield return ToEntry(info);
        }
    }

    public void DeleteFile(string path, bool clearReadOnly)
    {
        if (clearReadOnly)
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        File.Delete(path);
    }

    public void DeleteEmptyDirectory(string path) => Directory.Delete(path, recursive: false);

    // Same parent, so always a rename rather than a copy; Directory.Move throws IOException when "the
    // directory or a file within it is being used by another process".
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);

    public IReadOnlyList<string>? LoadedModulePaths()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var paths = new List<string>();
            foreach (ProcessModule module in process.Modules)
            {
                using (module)
                {
                    if (!string.IsNullOrEmpty(module.FileName))
                    {
                        paths.Add(module.FileName);
                    }
                }
            }

            return paths;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static FoundryStorageEntry ToEntry(FileSystemInfo info)
    {
        var attributes = info.Attributes;
        var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);
        return new FoundryStorageEntry(
            info.FullName,
            IsDirectory: attributes.HasFlag(FileAttributes.Directory),
            IsReparsePoint: isReparsePoint,
            Length: info is FileInfo file && !isReparsePoint ? SafeLength(file) : 0,
            IsReadOnly: attributes.HasFlag(FileAttributes.ReadOnly));
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception)
        {
            // Only feeds the "reclaimed N MB" log line; a file whose size cannot be read is still
            // deleted or deferred on its own merits.
            return 0;
        }
    }
}
