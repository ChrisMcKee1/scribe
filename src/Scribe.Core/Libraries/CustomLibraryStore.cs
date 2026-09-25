using System.Text.Json;
using System.Text.Json.Nodes;
using Scribe.Core.Infrastructure;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Libraries;

/// <summary>One file as the read path found it: its bytes and hash, or why it could not be read.</summary>
/// <param name="Name">Its name, relative to the libraries folder (<c>team-terms.csv</c>, <c>edits/github.json</c>).</param>
/// <param name="Bytes">Its bytes, when they could be read.</param>
/// <param name="Hash">Their hash.</param>
/// <param name="Failure">Why the read failed; <see cref="LibraryIoFailure.SharingViolation"/> is another app holding it open.</param>
/// <param name="AwaitingRelease">The bytes are a pending manifest's redo image whose operation is not done yet.</param>
internal sealed record LibraryFileRead(
    string Name, byte[]? Bytes, LibraryContentHash? Hash, LibraryIoFailure Failure = LibraryIoFailure.None, bool AwaitingRelease = false)
{
    public bool Readable => Bytes is not null;

    public static LibraryFileRead Of(string name, byte[] bytes, bool awaitingRelease = false) =>
        new(name, bytes, LibraryContentHashing.Of(bytes), LibraryIoFailure.None, awaitingRelease);
}

/// <summary>Whether the <see cref="LibrarySettingKeys.FileIds"/> row was found and understood (contract 6.3).</summary>
internal enum LibraryFileIdsHealth
{
    Absent,
    Ok,

    /// <summary>Malformed: the ids are recomputed with the deterministic rule.</summary>
    Unreadable,

    /// <summary>A later version's row: used as far as it reads, never written by this one.</summary>
    Newer,
}

/// <summary>The recorded ids of hand-placed files whose stem is a built-in id, by file name.</summary>
internal sealed record LibraryFileIds(LibraryFileIdsHealth Health, IReadOnlyDictionary<string, string> Files)
{
    public const int CurrentVersion = 1;

    public static LibraryFileIds Absent { get; } =
        new(LibraryFileIdsHealth.Absent, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary><c>{"version":1,"files":{"github.csv":"custom-github"}}</c>, read leniently entry by entry.</summary>
    public static LibraryFileIds Parse(string? value)
    {
        if (value is null)
        {
            return Absent;
        }

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (JsonNode.Parse(value) is not JsonObject root ||
                root["version"] is not JsonValue versionValue || !versionValue.TryGetValue<long>(out var version) ||
                version < CurrentVersion)
            {
                return new LibraryFileIds(LibraryFileIdsHealth.Unreadable, files);
            }

            var health = version > CurrentVersion ? LibraryFileIdsHealth.Newer : LibraryFileIdsHealth.Ok;
            if (root["files"] is not JsonObject entries)
            {
                // Version 1 requires the map; a later version is used as far as it reads, which here is nothing.
                return new LibraryFileIds(health == LibraryFileIdsHealth.Newer ? health : LibraryFileIdsHealth.Unreadable, files);
            }

            foreach (var (fileName, idNode) in entries)
            {
                if (idNode is JsonValue idValue && idValue.TryGetValue<string>(out var id) &&
                    LibraryManifest.IsTopLevelCsv(fileName) && !string.IsNullOrWhiteSpace(id))
                {
                    files.TryAdd(fileName, id.Trim());
                }
            }

            return new LibraryFileIds(health, files);
        }
        catch (JsonException)
        {
            return new LibraryFileIds(LibraryFileIdsHealth.Unreadable, files);
        }
    }

    /// <summary>The row for <paramref name="files"/>, or null to delete it when nothing is remapped.</summary>
    public static string? Write(IReadOnlyDictionary<string, string> files)
    {
        if (files.Count == 0)
        {
            return null;
        }

        var entries = new JsonObject();
        foreach (var (fileName, id) in files.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            entries[fileName] = id;
        }

        return new JsonObject { ["version"] = CurrentVersion, ["files"] = entries }.ToJsonString();
    }
}

/// <summary>
/// The files of the libraries folder as the one read path sees them (contract 3.1.4): custom CSVs at the top level,
/// built-in edits documents, their last good copies, retired built-ins' documents and Recently deleted entries. A
/// pending manifest of the stored generation is applied over it whole, never file by file (6.6.5).
/// </summary>
internal sealed class LibraryFolderSnapshot
{
    public Dictionary<string, LibraryFileRead> Custom { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, LibraryFileRead> Edits { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> PreviousEdits { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, LibraryFileRead> RetiredEdits { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, LibraryFileRead> Deleted { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every file name at the top level of the libraries folder, whatever its extension: names a new id must avoid.</summary>
    public HashSet<string> TopLevelNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Recently deleted entries a pending delete names, by entry name, with the library id it came from.</summary>
    public Dictionary<string, string> DeletedIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files of the pending manifest not in place yet.</summary>
    public int FilesAwaitingRelease { get; set; }

    /// <summary>
    /// Library files not listed because their names are not well-formed UTF-16 (NTFS allows one): never a library, so
    /// such a name never becomes an id, a manifest path or a state entry (contract 3.1.4).
    /// </summary>
    public int SkippedNames { get; set; }
}

/// <summary>
/// Reads the libraries folder for the catalog: every <c>*.csv</c> at the top level (top level only, ordered without
/// case, as 0.4.3's loader reads them), the documents in <c>edits\</c> recognized by suffix in a fixed order, and the
/// ids of hand-placed files remapped away from a built-in id.
/// </summary>
internal sealed class CustomLibraryStore
{
    private const string CsvPattern = "*.csv";
    private const string JsonPattern = "*.json";
    private const string BackupSuffix = ".backup.json";
    private const string PreviousSuffix = ".previous.json";
    private const string JsonSuffix = ".json";
    private const string CsvSuffix = ".csv";

    private readonly ILibraryFileSystem _files;
    private readonly AppPaths _paths;
    private readonly Action<LibraryFileOperation, Exception> _onFailure;
    private readonly HashSet<string> _retiredIds;

    public CustomLibraryStore(
        ILibraryFileSystem files, AppPaths paths, Action<LibraryFileOperation, Exception> onFailure, IEnumerable<string>? retiredIds = null)
    {
        _files = files;
        _paths = paths;
        _onFailure = onFailure;
        _retiredIds = new HashSet<string>(retiredIds ?? LibraryPrecedence.RetiredBuiltInIds, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every id a built-in has ever had in this build's precedence list, shipped or retired, plus the shipped ones.</summary>
    public static IReadOnlySet<string> BuiltInIds { get; } = LibraryPrecedence.BuiltInOrder
        .Concat(LibraryPrecedence.RetiredBuiltInIds)
        .Concat(BuiltInDictionaryLibraries.All.Select(library => library.Id))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The shipped built-in ids.</summary>
    public static IReadOnlySet<string> ShippedIds { get; } =
        BuiltInDictionaryLibraries.All.Select(library => library.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the folder. A file another app holds open is kept with <see cref="LibraryIoFailure.SharingViolation"/> and
    /// no bytes, any other failed read with its failure, so the catalog lists it (paused or awaiting release) rather
    /// than skipping it.
    /// </summary>
    public LibraryFolderSnapshot Read()
    {
        var snapshot = new LibraryFolderSnapshot();
        snapshot.TopLevelNames.UnionWith(List(_paths.LibrariesDir, "*"));
        foreach (var name in List(_paths.LibrariesDir, CsvPattern, snapshot))
        {
            snapshot.Custom[name] = ReadFile(name, Path.Combine(_paths.LibrariesDir, name));
        }

        foreach (var name in List(_paths.LibraryEditsDir, JsonPattern))
        {
            // Recognition by suffix, in this order (review finding G15): every *.backup.json is a set-aside copy, every
            // *.previous.json a last good copy, and only then is <x>.json the edits document of x.
            if (name.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.EndsWith(PreviousSuffix, StringComparison.OrdinalIgnoreCase))
            {
                snapshot.PreviousEdits.Add(name[..^PreviousSuffix.Length]);
                continue;
            }

            var id = name[..^JsonSuffix.Length];
            if (ShippedIds.Contains(id))
            {
                snapshot.Edits[id] = ReadFile(LibraryManifest.EditsPrefix + name, Path.Combine(_paths.LibraryEditsDir, name));
            }
            else if (_retiredIds.Contains(id))
            {
                snapshot.RetiredEdits[id] = ReadFile(LibraryManifest.EditsPrefix + name, Path.Combine(_paths.LibraryEditsDir, name));
            }

            // Any other <x>.json (a newer version's built-in, a stray file) is nobody's: never read, listed or touched.
        }

        foreach (var name in List(_paths.LibraryDeletedDir, CsvPattern, snapshot))
        {
            snapshot.Deleted[name] = ReadFile(LibraryManifest.DeletedPrefix + name, Path.Combine(_paths.LibraryDeletedDir, name));
        }

        return snapshot;
    }

    /// <summary>One file by its name relative to the libraries folder.</summary>
    public LibraryFileRead ReadRelative(string relative) =>
        ReadFile(relative, LibraryManifest.ToAbsolute(_paths.LibrariesDir, relative));

    /// <summary>
    /// The logical id of every custom file (contract 6.3): its stem, except a stem that is a built-in id, which gets the
    /// id recorded for its file name or, when none is usable, <c>custom-&lt;stem&gt;</c> with <c>-2</c>, <c>-3</c> while
    /// taken. Files are taken in name order, so the answer is the same at every load until a commit records it.
    /// </summary>
    public static IReadOnlyDictionary<string, string> AssignIds(
        IEnumerable<string> fileNames, LibraryFileIds recorded, IEnumerable<string> recentlyDeletedIds)
    {
        var names = fileNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        var stems = names.Select(Stem).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(BuiltInIds, StringComparer.OrdinalIgnoreCase);
        taken.UnionWith(stems);
        taken.UnionWith(recentlyDeletedIds);
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var stem = Stem(name);
            if (!BuiltInIds.Contains(stem) && assigned.Add(stem))
            {
                ids[name] = stem;
            }
        }

        foreach (var name in names)
        {
            var stem = Stem(name);
            if (!BuiltInIds.Contains(stem))
            {
                continue;
            }

            // A recorded id stays while its file exists, unless it now collides with a built-in or another file's own id.
            var id = recorded.Files.TryGetValue(name, out var kept) && !BuiltInIds.Contains(kept) && !assigned.Contains(kept) &&
                     !stems.Contains(kept)
                ? kept
                : RemapId(stem, taken);
            taken.Add(id);
            assigned.Add(id);
            ids[name] = id;
        }

        return ids;
    }

    /// <summary>
    /// <c>custom-&lt;stem&gt;</c>, then <c>-2</c>, <c>-3</c> while taken, compared without case; the stem is kept as it is
    /// (not slugged), so a remap is recognizable (contract 3.5.4, <c>LibraryNaming.RemapId</c>'s rule).
    /// </summary>
    public static string RemapId(string stem, IReadOnlySet<string> taken)
    {
        var candidate = "custom-" + stem;
        for (var n = 2; taken.Contains(candidate); n++)
        {
            candidate = "custom-" + stem + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    public static string Stem(string fileName) =>
        fileName.EndsWith(CsvSuffix, StringComparison.OrdinalIgnoreCase) ? fileName[..^CsvSuffix.Length] : Path.GetFileNameWithoutExtension(fileName);

    // Names that are not well-formed UTF-16 are left out of every listing, and counted when the listing is of libraries.
    private List<string> List(string directory, string pattern, LibraryFolderSnapshot? counted = null)
    {
        List<string> paths;
        try
        {
            paths = [.. _files.EnumerateFiles(directory, pattern)];
        }
        catch (Exception ex)
        {
            _onFailure(LibraryFileOperation.Enumerate, ex);
            return [];
        }

        var names = new List<string>();
        foreach (var name in paths.Select(Path.GetFileName).OfType<string>().Where(name => name.Length > 0))
        {
            if (LibraryText.IsWellFormed(name))
            {
                names.Add(name);
            }
            else if (counted is not null)
            {
                counted.SkippedNames++;
            }
        }

        return [.. names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    private LibraryFileRead ReadFile(string name, string path)
    {
        try
        {
            return LibraryFileRead.Of(name, _files.ReadAllBytes(path));
        }
        catch (Exception ex)
        {
            _onFailure(LibraryFileOperation.Read, ex);
            return new LibraryFileRead(name, null, null, LibraryIoFailures.Classify(ex));
        }
    }
}
