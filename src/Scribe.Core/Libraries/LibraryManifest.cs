using System.Buffers;
using System.Text.Json;

namespace Scribe.Core.Libraries;

/// <summary>The six things one journal operation can do to one target (contract 6.6.2).</summary>
internal enum LibraryOperationKind
{
    /// <summary>An existing custom CSV or edits document gets new content.</summary>
    Write,

    /// <summary>A new custom CSV or edits document; never written over another file (review finding G3).</summary>
    Create,

    /// <summary>A built-in's edits document is removed (every row back to its shipped values).</summary>
    Remove,

    /// <summary>A custom library moves into Recently deleted.</summary>
    Delete,

    /// <summary>A Recently deleted entry comes back, with the draft's edits if any.</summary>
    Restore,

    /// <summary>A Recently deleted entry is deleted for good.</summary>
    Purge,
}

/// <summary>What becomes of a replaced edits document that still holds its pre-image.</summary>
internal enum LibraryEditsReplacement
{
    None,

    /// <summary>It becomes <c>&lt;id&gt;.previous.json</c>, the last good copy (an ordinary Save).</summary>
    Previous,

    /// <summary>It goes to its set-aside name, kept and never read again (the two recoveries of a paused document).</summary>
    SetAside,
}

/// <summary>
/// One operation of a manifest. Paths are relative to the libraries folder with <c>/</c>, in the top level,
/// <c>edits/</c> or <c>deleted/</c> only (the parse refuses anything else, so a tampered manifest cannot reach outside
/// the folder). Every destination a later step may need is recorded here at prepare, so a resume is deterministic.
/// </summary>
/// <param name="Number">Its place in the manifest, which names its redo image, install copy and backups.</param>
/// <param name="Kind">What it does.</param>
/// <param name="Target">
/// The file it writes, creates, removes, deletes or restores to, or for a purge the Recently deleted entry. A custom
/// target is the library's physical file name (<c>github.csv</c> for a remapped <c>custom-github</c>).
/// </param>
/// <param name="LibraryId">The library the operation belongs to, which a kept version is reported against.</param>
/// <param name="PreImage">P: the bytes the target held at prepare (write, remove, delete), or a purged entry's hash.</param>
/// <param name="Staged">S: the committed bytes of a write, create or restore, which its redo image holds.</param>
/// <param name="KeepAs">A custom write's, create's or restore's preservation destination, the first of its R7 series.</param>
/// <param name="Replaced">For an edits write or remove: what becomes of the replaced document.</param>
/// <param name="SetAsideStem">For an edits write, create or remove: the stem of its set-aside series.</param>
/// <param name="To">For a delete: its Recently deleted entry.</param>
/// <param name="From">For a restore: the Recently deleted entry it comes back from.</param>
/// <param name="FromHash">For a restore: that entry's hash as prepared.</param>
internal sealed record LibraryManifestOperation(
    int Number,
    LibraryOperationKind Kind,
    string Target,
    string? LibraryId = null,
    LibraryContentHash? PreImage = null,
    LibraryContentHash? Staged = null,
    string? KeepAs = null,
    LibraryEditsReplacement Replaced = LibraryEditsReplacement.None,
    string? SetAsideStem = null,
    string? To = null,
    string? From = null,
    LibraryContentHash? FromHash = null)
{
    /// <summary>Whether the operation has committed bytes, and so a redo image.</summary>
    public bool HasPostImage => Kind is LibraryOperationKind.Write or LibraryOperationKind.Create or LibraryOperationKind.Restore;

    /// <summary>Whether the target is a built-in's edits document.</summary>
    public bool IsEditsTarget => Target.StartsWith(LibraryManifest.EditsPrefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What reading a manifest found.</summary>
internal enum LibraryManifestReadStatus
{
    Read,

    /// <summary>Not a version 1 manifest this build can trust: malformed, a broken operation, or a path outside the rules.</summary>
    Unreadable,

    /// <summary>A version above 1.</summary>
    Newer,
}

/// <summary>The result of reading a manifest's bytes.</summary>
internal sealed record LibraryManifestRead(LibraryManifestReadStatus Status, LibraryManifest? Manifest);

/// <summary>
/// A journal manifest (contract 6.6.2): the generation a Save commits and every operation that brings the files to it.
/// Written once at prepare, durably, then renamed into place, and never changed afterwards: completion, recovery and
/// the quarantine read it, and only the name changes when it is set aside.
/// </summary>
internal sealed class LibraryManifest
{
    public const int CurrentVersion = 1;
    public const string EditsPrefix = "edits/";
    public const string DeletedPrefix = "deleted/";
    private const string CsvExtension = ".csv";
    private const string JsonExtension = ".json";

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    public LibraryManifest(
        string id, long generation, long baseGeneration, DateTimeOffset prepared, IReadOnlyList<LibraryManifestOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (!LibraryJournalNames.IsManifestId(id))
        {
            throw new ArgumentException("A manifest id is 32 hexadecimal digits.", nameof(id));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        Id = id;
        Generation = generation;
        BaseGeneration = baseGeneration;
        Prepared = prepared;
        Operations = [.. operations];
    }

    public string Id { get; }

    public long Generation { get; }

    public long BaseGeneration { get; }

    public DateTimeOffset Prepared { get; }

    public IReadOnlyList<LibraryManifestOperation> Operations { get; }

    /// <summary>A path of the manifest as an absolute path under <paramref name="librariesDir"/>.</summary>
    public static string ToAbsolute(string librariesDir, string relative) =>
        Path.Combine(librariesDir, relative.Replace('/', Path.DirectorySeparatorChar));

    public byte[] ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", CurrentVersion);
            writer.WriteString("id", Id);
            writer.WriteNumber("generation", Generation);
            writer.WriteNumber("baseGeneration", BaseGeneration);
            writer.WriteString("prepared", LibraryJournalNames.FormatStamp(Prepared));
            writer.WriteStartArray("operations");
            foreach (var operation in Operations)
            {
                writer.WriteStartObject();
                writer.WriteNumber("n", operation.Number);
                writer.WriteString("op", KindName(operation.Kind));
                if (operation.LibraryId is { } library)
                {
                    writer.WriteString("library", library);
                }

                writer.WriteString("target", operation.Target);
                WriteHash(writer, "preImage", operation.PreImage);
                WriteHash(writer, "staged", operation.Staged);
                WriteOptional(writer, "keepAs", operation.KeepAs);
                if (operation.Replaced != LibraryEditsReplacement.None)
                {
                    writer.WriteString("replaced", operation.Replaced == LibraryEditsReplacement.Previous ? "previous" : "setAside");
                }

                WriteOptional(writer, "setAsideStem", operation.SetAsideStem);
                WriteOptional(writer, "to", operation.To);
                WriteOptional(writer, "from", operation.From);
                WriteHash(writer, "fromHash", operation.FromHash);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Reads a manifest, refusing anything this build cannot trust whole: a version other than 1 (above it is
    /// <see cref="LibraryManifestReadStatus.Newer"/>), a missing or mistyped member, an operation whose members do not fit
    /// its kind, a path outside the rules of contract 6.6.1, two operations on one file, or operation numbers out of order.
    /// </summary>
    public static LibraryManifestRead Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            return ParseDocument(document.RootElement);
        }
        catch (JsonException)
        {
            return new LibraryManifestRead(LibraryManifestReadStatus.Unreadable, null);
        }
    }

    private static LibraryManifestRead ParseDocument(JsonElement root)
    {
        var unreadable = new LibraryManifestRead(LibraryManifestReadStatus.Unreadable, null);
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt64(out var versionNumber))
        {
            return unreadable;
        }

        if (versionNumber > CurrentVersion)
        {
            return new LibraryManifestRead(LibraryManifestReadStatus.Newer, null);
        }

        if (versionNumber != CurrentVersion ||
            !TryGetString(root, "id", out var id) || !LibraryJournalNames.IsManifestId(id) ||
            !TryGetLong(root, "generation", out var generation) || generation < 1 ||
            !TryGetLong(root, "baseGeneration", out var baseGeneration) || baseGeneration != generation - 1 ||
            !TryGetString(root, "prepared", out var preparedText) ||
            !LibraryJournalNames.TryParseStamp(preparedText, out var prepared) ||
            !root.TryGetProperty("operations", out var operationsElement) || operationsElement.ValueKind != JsonValueKind.Array)
        {
            return unreadable;
        }

        var operations = new List<LibraryManifestOperation>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in operationsElement.EnumerateArray())
        {
            if (!TryParseOperation(element, operations.Count, out var operation) || !ClaimsDistinctFiles(operation, files))
            {
                return unreadable;
            }

            operations.Add(operation);
        }

        return new LibraryManifestRead(
            LibraryManifestReadStatus.Read, new LibraryManifest(id, generation, baseGeneration, prepared, operations));
    }

    // Rule R5: one operation per target, so no done-condition depends on another operation's.
    private static bool ClaimsDistinctFiles(LibraryManifestOperation operation, HashSet<string> files) =>
        files.Add(operation.Target) && (operation.From is null || files.Add(operation.From)) &&
        (operation.To is null || files.Add(operation.To));

    private static bool TryParseOperation(JsonElement element, int expectedNumber, out LibraryManifestOperation operation)
    {
        operation = null!;
        if (element.ValueKind != JsonValueKind.Object ||
            !TryGetLong(element, "n", out var number) || number != expectedNumber ||
            !TryGetString(element, "op", out var kindName) || !TryParseKind(kindName, out var kind) ||
            !TryGetString(element, "target", out var target))
        {
            return false;
        }

        string? library = null;
        if (element.TryGetProperty("library", out var libraryElement))
        {
            if (libraryElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(libraryElement.GetString()))
            {
                return false;
            }

            library = libraryElement.GetString();
        }

        if (!TryGetOptionalHash(element, "preImage", out var preImage, out var hasPreImage) ||
            !TryGetOptionalHash(element, "staged", out var staged, out var hasStaged) ||
            !TryGetOptionalHash(element, "fromHash", out var fromHash, out var hasFromHash) ||
            !TryGetOptionalString(element, "keepAs", out var keepAs) ||
            !TryGetOptionalString(element, "replaced", out var replacedName) ||
            !TryGetOptionalString(element, "setAsideStem", out var setAsideStem) ||
            !TryGetOptionalString(element, "to", out var to) ||
            !TryGetOptionalString(element, "from", out var from))
        {
            return false;
        }

        var replacedOrNull = replacedName switch
        {
            null => LibraryEditsReplacement.None,
            "previous" => LibraryEditsReplacement.Previous,
            "setAside" => LibraryEditsReplacement.SetAside,
            _ => (LibraryEditsReplacement?)null,
        };
        if (replacedOrNull is not { } replaced)
        {
            return false;
        }

        var edits = IsEditsDocumentPath(target);
        var custom = IsTopLevelCsv(target);
        var valid = kind switch
        {
            LibraryOperationKind.Write =>
                (custom || edits) && hasPreImage && preImage is not null && hasStaged && staged is not null &&
                (custom ? keepAs is not null && IsTopLevelCsv(keepAs) && setAsideStem is null && replaced == LibraryEditsReplacement.None
                        : keepAs is null && IsSetAsideStem(setAsideStem) && replaced != LibraryEditsReplacement.None) &&
                to is null && from is null && !hasFromHash,
            LibraryOperationKind.Create =>
                (custom || edits) && !hasPreImage && hasStaged && staged is not null && replaced == LibraryEditsReplacement.None &&
                (custom ? keepAs is not null && IsTopLevelCsv(keepAs) && setAsideStem is null
                        : keepAs is null && IsSetAsideStem(setAsideStem)) &&
                to is null && from is null && !hasFromHash,
            LibraryOperationKind.Remove =>
                edits && hasPreImage && preImage is not null && !hasStaged && keepAs is null &&
                IsSetAsideStem(setAsideStem) && replaced != LibraryEditsReplacement.None &&
                to is null && from is null && !hasFromHash,
            LibraryOperationKind.Delete =>
                custom && hasPreImage && preImage is not null && !hasStaged && keepAs is null && setAsideStem is null &&
                replaced == LibraryEditsReplacement.None && to is not null && IsDeletedCsv(to) && from is null && !hasFromHash,
            LibraryOperationKind.Restore =>
                custom && !hasPreImage && hasStaged && staged is not null && keepAs is not null && IsTopLevelCsv(keepAs) &&
                setAsideStem is null && replaced == LibraryEditsReplacement.None && to is null &&
                from is not null && IsDeletedCsv(from) && hasFromHash && fromHash is not null,
            LibraryOperationKind.Purge =>
                IsDeletedCsv(target) && !hasStaged && keepAs is null && setAsideStem is null &&
                replaced == LibraryEditsReplacement.None && to is null && from is null && !hasFromHash,
            _ => false,
        };
        if (!valid)
        {
            return false;
        }

        operation = new LibraryManifestOperation(
            (int)number, kind, target, library, preImage, staged, keepAs, replaced, setAsideStem, to, from, fromHash);
        return true;
    }

    /// <summary>A file directly in the libraries folder whose name ends in <c>.csv</c>.</summary>
    public static bool IsTopLevelCsv(string? path) =>
        path is not null && IsValidName(path) && path.EndsWith(CsvExtension, StringComparison.OrdinalIgnoreCase) &&
        path.Length > CsvExtension.Length;

    /// <summary>A file directly in <c>edits/</c> whose name ends in <c>.json</c>.</summary>
    public static bool IsEditsDocumentPath(string? path) =>
        path is not null && path.StartsWith(EditsPrefix, StringComparison.OrdinalIgnoreCase) &&
        IsValidName(path[EditsPrefix.Length..]) && path.EndsWith(JsonExtension, StringComparison.OrdinalIgnoreCase) &&
        path.Length > EditsPrefix.Length + JsonExtension.Length;

    /// <summary>A file directly in <c>deleted/</c> whose name ends in <c>.csv</c>.</summary>
    public static bool IsDeletedCsv(string? path) =>
        path is not null && path.StartsWith(DeletedPrefix, StringComparison.OrdinalIgnoreCase) &&
        IsValidName(path[DeletedPrefix.Length..]) && path.EndsWith(CsvExtension, StringComparison.OrdinalIgnoreCase) &&
        path.Length > DeletedPrefix.Length + CsvExtension.Length;

    /// <summary>A stem directly in <c>edits/</c>, to which R7 appends a hash infix and <c>.backup.json</c>.</summary>
    public static bool IsSetAsideStem(string? path) =>
        path is not null && path.StartsWith(EditsPrefix, StringComparison.OrdinalIgnoreCase) &&
        IsValidName(path[EditsPrefix.Length..]);

    // One path segment Windows cannot read as anything else: no separator, drive or device syntax, no "." or "..", and no
    // trailing dot or space, which Windows strips and so could make one name stand for another.
    private static bool IsValidName(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(InvalidNameChars) < 0 &&
        !name.EndsWith('.') && !name.EndsWith(' ');

    private static string KindName(LibraryOperationKind kind) => kind switch
    {
        LibraryOperationKind.Write => "write",
        LibraryOperationKind.Create => "create",
        LibraryOperationKind.Remove => "remove",
        LibraryOperationKind.Delete => "delete",
        LibraryOperationKind.Restore => "restore",
        LibraryOperationKind.Purge => "purge",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static bool TryParseKind(string name, out LibraryOperationKind kind)
    {
        (var known, kind) = name switch
        {
            "write" => (true, LibraryOperationKind.Write),
            "create" => (true, LibraryOperationKind.Create),
            "remove" => (true, LibraryOperationKind.Remove),
            "delete" => (true, LibraryOperationKind.Delete),
            "restore" => (true, LibraryOperationKind.Restore),
            "purge" => (true, LibraryOperationKind.Purge),
            _ => (false, default),
        };
        return known;
    }

    private static void WriteHash(Utf8JsonWriter writer, string name, LibraryContentHash? hash)
    {
        if (hash is { } value)
        {
            writer.WriteString(name, value.Value);
        }
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()!;
        return true;
    }

    private static bool TryGetLong(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out value);
    }

    // Absent is fine; present must be a string.
    private static bool TryGetOptionalString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }

    // Absent, null (a purge of an entry listed unreadable) or 64 lowercase hexadecimal digits.
    private static bool TryGetOptionalHash(JsonElement element, string name, out LibraryContentHash? value, out bool present)
    {
        value = null;
        present = element.TryGetProperty(name, out var property);
        if (!present || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String || !LibraryContentHashing.TryParse(property.GetString(), out var hash))
        {
            return false;
        }

        value = hash;
        return true;
    }
}

/// <summary>A Save turned into operations (contract 6.6.2), or the reason it cannot be.</summary>
/// <param name="OutsideEditIds">Libraries whose file no longer holds what the draft started from; nothing is written then.</param>
/// <param name="ReadFailure">Why a file the checks needed could not be read.</param>
/// <param name="Operations">One per target, in the order writes, creates, removes, restores, deletes, purges (rule R5).</param>
/// <param name="RedoImages">The committed bytes of every operation that has them, by operation number.</param>
/// <param name="State">The change set's local state with the content of every file the Save writes accepted (review finding A4).</param>
/// <param name="IdentitiesAfter">The libraries the commit leaves: minus deletions, plus creations and restores.</param>
/// <param name="Encoding">The local state encoded for the settings store over the libraries before and after.</param>
/// <param name="RemapsAfter">The remapped ids of hand-placed files still there after the commit, for <c>libraries.file_ids</c>.</param>
/// <param name="Counts">What the Save changes, as counts.</param>
/// <param name="Prepared">The time stamp the manifest, its set-aside stems and its Recently deleted entries carry.</param>
internal sealed record LibrarySavePlan(
    IReadOnlyList<string> OutsideEditIds,
    LibraryIoFailure ReadFailure,
    IReadOnlyList<LibraryManifestOperation> Operations,
    IReadOnlyDictionary<int, byte[]> RedoImages,
    LibraryLocalState? State,
    IReadOnlyList<LibraryIdentity> IdentitiesAfter,
    LibraryStateEncoding? Encoding,
    IReadOnlyDictionary<string, string> RemapsAfter,
    LibraryChangeCounts Counts,
    DateTimeOffset Prepared)
{
    public static LibrarySavePlan Refused(IReadOnlyList<string> outsideEditIds, LibraryIoFailure failure, DateTimeOffset prepared) =>
        new(outsideEditIds, failure, [], new Dictionary<int, byte[]>(), null, [], null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), default, prepared);
}

/// <summary>
/// How a change set becomes a manifest's operations (contract 6.6.2): every pre-image checked against the files the
/// catalog was just read from, exactly one operation per target (a restore the draft also edited is one restore of the
/// edited content), and every destination a later step may need reserved now, so completion never chooses a name.
/// </summary>
internal static class LibrarySavePlanner
{
    private sealed record Draft(
        LibraryOperationKind Kind,
        string Target,
        string LibraryId,
        LibraryContentHash? PreImage = null,
        byte[]? Bytes = null,
        string? KeepAsName = null,
        LibraryEditsReplacement Replaced = LibraryEditsReplacement.None,
        string? SetAsideStem = null,
        string? To = null,
        string? From = null,
        LibraryContentHash? FromHash = null);

    public static LibrarySavePlan Plan(
        LibraryCatalog catalog,
        LibraryFolderSnapshot folder,
        IReadOnlyDictionary<string, string> remaps,
        IReadOnlyList<LibraryIdentity> identitiesBefore,
        LibraryChangeSet changes,
        LibraryServiceParts parts,
        Func<string, LibraryFileRead> readRelative,
        DateTimeOffset now)
    {
        var outside = new List<string>();
        var failure = LibraryIoFailure.None;
        var stamp = LibraryJournalNames.FormatStamp(now);
        var drafts = new List<Draft>();
        var accepted = new Dictionary<string, LibraryContentHash?>(StringComparer.OrdinalIgnoreCase);
        var created = new List<LibraryIdentity>();
        var deletedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deletedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int createdCount = 0, updatedCount = 0, deletedCount = 0, restoredCount = 0, terms = 0;

        void Refuse(string id) => outside.Add(id);

        bool Unreadable(LibraryFileRead? read)
        {
            if (read is { Readable: false })
            {
                if (failure == LibraryIoFailure.None)
                {
                    failure = read.Failure == LibraryIoFailure.None ? LibraryIoFailure.Other : read.Failure;
                }

                return true;
            }

            return false;
        }

        var restoreIds = changes.RecentlyDeletedActions
            .Where(action => action.Kind == RecentlyDeletedActionKind.Restore && action.RestoreAsId is not null)
            .Select(action => action.RestoreAsId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var restoreEdits = changes.Writes
            .Where(write => !write.BuiltIn && restoreIds.Contains(write.LibraryId))
            .ToDictionary(write => write.LibraryId, StringComparer.OrdinalIgnoreCase);

        foreach (var write in changes.Writes)
        {
            if (write.BuiltIn)
            {
                PlanBuiltIn(write);
            }
            else if (!restoreIds.Contains(write.LibraryId))
            {
                PlanCustom(write);
            }
        }

        foreach (var deletion in changes.Deletions)
        {
            RequireName(LibraryManifest.IsTopLevelCsv(deletion.FileName));
            var current = folder.Custom.GetValueOrDefault(deletion.FileName);
            if (Unreadable(current))
            {
                continue;
            }

            if (current is null || current.Hash != deletion.ExpectedPreImage)
            {
                Refuse(deletion.LibraryId);
                continue;
            }

            var takenEntries = new HashSet<string>(folder.Deleted.Keys, StringComparer.OrdinalIgnoreCase);
            takenEntries.UnionWith(drafts.Where(draft => draft.To is not null).Select(draft => draft.To![LibraryManifest.DeletedPrefix.Length..]));
            drafts.Add(new Draft(
                LibraryOperationKind.Delete, deletion.FileName, deletion.LibraryId, deletion.ExpectedPreImage,
                To: LibraryManifest.DeletedPrefix + RecentlyDeletedStore.NextEntryName(deletion.FileName, now, takenEntries)));
            deletedIds.Add(deletion.LibraryId);
            deletedFiles.Add(deletion.FileName);
            deletedCount++;
        }

        foreach (var action in changes.RecentlyDeletedActions)
        {
            var entryPath = LibraryManifest.DeletedPrefix + action.EntryName;
            RequireName(LibraryManifest.IsDeletedCsv(entryPath));
            if (action.Kind == RecentlyDeletedActionKind.DeletePermanently)
            {
                drafts.Add(new Draft(LibraryOperationKind.Purge, entryPath, string.Empty, action.ExpectedHash));
                continue;
            }

            if (action.RestoreAsId is not { } restoreAs || action.ExpectedHash is not { } expected)
            {
                throw new ArgumentException("A restore names the id it comes back as and the hash it was read with.", nameof(changes));
            }

            var target = restoreAs + ".csv";
            RequireName(LibraryManifest.IsTopLevelCsv(target));
            var entry = folder.Deleted.GetValueOrDefault(action.EntryName);
            if (Unreadable(entry))
            {
                continue;
            }

            restoreEdits.TryGetValue(restoreAs, out var edit);
            if (entry is null || entry.Hash != expected || folder.Custom.ContainsKey(target) ||
                (edit is not null && edit.ExpectedPreImage != expected))
            {
                Refuse(restoreAs);
                continue;
            }

            var content = edit?.Content;
            var bytes = content is null ? entry.Bytes! : parts.Codec.WriteManaged(content);
            var name = content?.Name ?? parts.Codec.ReadManaged(entry.Bytes!).Name ?? restoreAs;
            terms += content?.Rows.Count ?? parts.Codec.ReadManaged(entry.Bytes!).Terms.Count;
            accepted[restoreAs] = LibraryContentHashing.Of(bytes);
            created.Add(LibraryIdentity.NewCustom(restoreAs));
            drafts.Add(new Draft(
                LibraryOperationKind.Restore, target, restoreAs, Bytes: bytes, KeepAsName: name,
                From: entryPath, FromHash: expected));
            restoredCount++;
        }

        if (outside.Count > 0 || failure != LibraryIoFailure.None)
        {
            return LibrarySavePlan.Refused(outside, failure, now);
        }

        var state = WithAccepted(changes.LocalState, accepted);
        var after = identitiesBefore.Where(identity => !deletedIds.Contains(identity.Id))
            .Concat(created)
            .DistinctBy(identity => identity.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var encoding = parts.Composer.EncodeLocalState(state, catalog.LocalState, identitiesBefore, after);

        // keepAs names avoid every id in use or listed (review finding A18's third case): the document's list keeps ids no
        // library has, and an older build would apply and send a kept version landing on one under someone else's flag.
        var taken = new HashSet<string>(CustomLibraryStore.BuiltInIds, StringComparer.OrdinalIgnoreCase);
        taken.UnionWith(catalog.Libraries.Select(library => library.Content.Id));
        taken.UnionWith(changes.Writes.Select(write => write.LibraryId));
        taken.UnionWith(restoreIds);
        taken.UnionWith(folder.TopLevelNames.Select(Path.GetFileNameWithoutExtension).OfType<string>());
        taken.UnionWith(folder.Custom.Keys.Select(CustomLibraryStore.Stem));
        taken.UnionWith(catalog.RecentlyDeleted.Select(entry => entry.OriginalId));
        taken.UnionWith(drafts.Select(draft => CustomLibraryStore.Stem(draft.Target)));
        var listed = new HashSet<string>(state.LegacyEnabledIds, StringComparer.OrdinalIgnoreCase);
        listed.UnionWith(encoding.EnabledLibraryIds);

        var operations = new List<LibraryManifestOperation>();
        var redo = new Dictionary<int, byte[]>();
        foreach (var kind in (LibraryOperationKind[])
                 [LibraryOperationKind.Write, LibraryOperationKind.Create, LibraryOperationKind.Remove,
                  LibraryOperationKind.Restore, LibraryOperationKind.Delete, LibraryOperationKind.Purge])
        {
            foreach (var draft in drafts.Where(draft => draft.Kind == kind))
            {
                var number = operations.Count;
                string? keepAs = null;
                if (draft.KeepAsName is { } keepAsName && !draft.Target.StartsWith(LibraryManifest.EditsPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var id = InterimLibraryNaming.NewCustomId(keepAsName, candidate => taken.Contains(candidate) || SeriesMeets(candidate, listed));
                    taken.Add(id);
                    keepAs = id + ".csv";
                }

                var staged = draft.Bytes is { } bytes ? LibraryContentHashing.Of(bytes) : (LibraryContentHash?)null;
                if (draft.Bytes is not null)
                {
                    redo[number] = draft.Bytes;
                }

                operations.Add(new LibraryManifestOperation(
                    number, draft.Kind, draft.Target, draft.LibraryId.Length == 0 ? null : draft.LibraryId, draft.PreImage,
                    staged, keepAs, draft.Replaced, draft.SetAsideStem, draft.To, draft.From, draft.FromHash));
            }
        }

        var remapsAfter = remaps.Where(pair => !deletedFiles.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return new LibrarySavePlan(
            [], LibraryIoFailure.None, operations, redo, state, after, encoding, remapsAfter,
            new LibraryChangeCounts(createdCount, updatedCount, deletedCount, restoredCount, terms), now);

        void PlanCustom(LibraryWrite write)
        {
            if (write.Content is not { BuiltIn: false } content ||
                !string.Equals(content.Id, write.LibraryId, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A custom library write carries that library's whole content.", nameof(changes));
            }

            // An existing library is written to its physical file (github.csv for a remapped custom-github, A16); only a
            // library not written yet takes <id>.csv.
            var existing = catalog.Find(write.LibraryId);
            var target = existing is { Content.BuiltIn: false, FileName: { } fileName } ? fileName : write.LibraryId + ".csv";
            RequireName(LibraryManifest.IsTopLevelCsv(target));
            var current = folder.Custom.GetValueOrDefault(target);
            if (Unreadable(current))
            {
                return;
            }

            LibraryOperationKind kind;
            if (write.ExpectedPreImage is { } preImage)
            {
                if (current is null || current.Hash != preImage)
                {
                    Refuse(write.LibraryId);
                    return;
                }

                kind = LibraryOperationKind.Write;
                updatedCount++;
            }
            else
            {
                if (current is not null)
                {
                    Refuse(write.LibraryId);
                    return;
                }

                kind = LibraryOperationKind.Create;
                createdCount++;
                created.Add(LibraryIdentity.NewCustom(write.LibraryId));
            }

            var bytes = parts.Codec.WriteManaged(content);
            accepted[write.LibraryId] = LibraryContentHashing.Of(bytes);
            terms += content.Rows.Count;

            // Where an outside version found at completion goes (a write), or Scribe's own content when another app takes
            // the planned name (a create): reserved now, named after the library.
            var keepAsName = kind == LibraryOperationKind.Write
                ? InterimLibraryNaming.ChangedOutsideName(existing?.Content.Name ?? content.Name)
                : content.Name;
            drafts.Add(new Draft(kind, target, write.LibraryId, write.ExpectedPreImage, bytes, keepAsName));
        }

        void PlanBuiltIn(LibraryWrite write)
        {
            var id = write.LibraryId;
            RequireName(CustomLibraryStore.ShippedIds.Contains(id));
            var target = LibraryManifest.EditsPrefix + id + ".json";
            var setAsideStem = LibraryManifest.EditsPrefix + id + "." + stamp;
            var current = folder.Edits.GetValueOrDefault(id);
            if (Unreadable(current))
            {
                return;
            }

            if (write.ExpectedPreImage is { } preImage ? current is null || current.Hash != preImage : current is not null)
            {
                Refuse(id);
                return;
            }

            var exists = write.ExpectedPreImage is not null;
            switch (write.Recovery)
            {
                case BuiltInEditsRecovery.None when write.Edits is { } edits:
                {
                    var bytes = parts.Overlay.WriteEdits(edits);
                    accepted[id] = LibraryContentHashing.Of(bytes);
                    terms += edits.Terms.Count;
                    updatedCount++;
                    drafts.Add(exists
                        ? new Draft(LibraryOperationKind.Write, target, id, write.ExpectedPreImage, bytes,
                            Replaced: LibraryEditsReplacement.Previous, SetAsideStem: setAsideStem)
                        : new Draft(LibraryOperationKind.Create, target, id, Bytes: bytes, SetAsideStem: setAsideStem));
                    break;
                }

                case BuiltInEditsRecovery.None or BuiltInEditsRecovery.BackUpAndReset when exists:
                    accepted[id] = null;
                    updatedCount++;
                    drafts.Add(new Draft(
                        LibraryOperationKind.Remove, target, id, write.ExpectedPreImage,
                        Replaced: write.Recovery == BuiltInEditsRecovery.None ? LibraryEditsReplacement.Previous : LibraryEditsReplacement.SetAside,
                        SetAsideStem: setAsideStem));
                    break;

                case BuiltInEditsRecovery.RestorePrevious:
                {
                    if (!folder.PreviousEdits.Contains(id))
                    {
                        Refuse(id);
                        return;
                    }

                    var previous = readRelative(LibraryManifest.EditsPrefix + id + ".previous.json");
                    if (Unreadable(previous) || previous.Bytes is not { } bytes)
                    {
                        return;
                    }

                    accepted[id] = LibraryContentHashing.Of(bytes);
                    updatedCount++;
                    drafts.Add(exists
                        ? new Draft(LibraryOperationKind.Write, target, id, write.ExpectedPreImage, bytes,
                            Replaced: LibraryEditsReplacement.SetAside, SetAsideStem: setAsideStem)
                        : new Draft(LibraryOperationKind.Create, target, id, Bytes: bytes, SetAsideStem: setAsideStem));
                    break;
                }

                default:
                    // No document on disk and none to write: nothing to do for this built-in.
                    break;
            }
        }
    }

    // Whether any id of the list is the candidate or a later name of its R7 series (candidate-2, candidate-3 and so on).
    private static bool SeriesMeets(string candidate, IReadOnlySet<string> listed)
    {
        foreach (var id in listed)
        {
            if (string.Equals(id, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (id.Length > candidate.Length + 1 && id.StartsWith(candidate + "-", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = id.AsSpan(candidate.Length + 1);
                if (suffix[0] != '0' && suffix.Length <= 9 &&
                    int.TryParse(suffix, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) &&
                    index >= 2)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void RequireName(bool valid)
    {
        if (!valid)
        {
            throw new ArgumentException("A library change names a file outside the libraries folder's rules.");
        }
    }

    /// <summary>The state with the hash of every file the Save writes accepted, and a removed document's entry gone.</summary>
    internal static LibraryLocalState WithAccepted(LibraryLocalState state, IReadOnlyDictionary<string, LibraryContentHash?> accepted)
    {
        var content = new Dictionary<string, LibraryContentHash>(state.AcceptedContent, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, hash) in accepted)
        {
            if (hash is { } value)
            {
                content[id] = value;
            }
            else
            {
                content.Remove(id);
            }
        }

        return LibraryLocalState.Create(
            state.EnabledIds, state.LegacyEnabledIds, state.AiPermissions, state.LegacyMarkers, state.AiUpgradeNotice,
            state.Health, content, state.AiPermissionsLost);
    }
}

/// <summary>
/// Well-formed UTF-16 (contract 2.2): every high surrogate immediately followed by a low one and every low surrogate
/// immediately preceded by a high one, so the text decodes to Unicode scalar values. A writer could store an unpaired
/// surrogate only as U+FFFD, so nothing the journal writes may hold one, and no file name that holds one becomes an id.
/// </summary>
internal static class LibraryText
{
    private const string Refusal = "A library change holds text that is not well-formed UTF-16 (an unpaired surrogate).";

    public static bool IsWellFormed(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        var text = value.AsSpan();
        var first = text.IndexOfAnyInRange('\uD800', '\uDFFF');
        if (first < 0)
        {
            return true;
        }

        text = text[first..];
        while (!text.IsEmpty)
        {
            if (System.Text.Rune.DecodeFromUtf16(text, out _, out var consumed) != OperationStatus.Done)
            {
                return false;
            }

            text = text[consumed..];
        }

        return true;
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when any id, key, value string or hash of <paramref name="changes"/> is not
    /// well-formed: a bug upstream, since the editor refuses such text and no decoder produces it (contract 3.1.5, J-24).
    /// </summary>
    public static void RequireWellFormed(LibraryChangeSet changes)
    {
        foreach (var write in changes.Writes)
        {
            Require(write.LibraryId);
            Require(write.ExpectedPreImage);
            if (write.Content is { } content)
            {
                Require(content);
            }

            if (write.Edits is { } edits)
            {
                Require(edits.LibraryId);
                foreach (var term in edits.Terms)
                {
                    Require(term);
                }
            }
        }

        foreach (var deletion in changes.Deletions)
        {
            Require(deletion.LibraryId);
            Require(deletion.FileName);
            Require(deletion.ExpectedPreImage);
        }

        foreach (var action in changes.RecentlyDeletedActions)
        {
            Require(action.EntryName);
            Require(action.RestoreAsId);
            Require(action.ExpectedHash);
        }

        var state = changes.LocalState;
        RequireAll(state.EnabledIds);
        RequireAll(state.LegacyEnabledIds);
        RequireAll(state.AiPermissions.Keys);
        RequireAll(state.AiUpgradeNotice);
        foreach (var marker in state.LegacyMarkers)
        {
            Require(marker.LibraryId);
            Require(marker.Key.Value);
        }

        foreach (var (id, hash) in state.AcceptedContent)
        {
            Require(id);
            Require(hash);
        }
    }

    private static void Require(LibraryContent content)
    {
        Require(content.Id);
        Require(content.Name);
        Require(content.Category);
        Require(content.Description);
        Require(content.BasedOn);
        foreach (var row in content.Rows)
        {
            Require(row.Key.Value);
            Require(row.Values);
            Require(row.Shipped);
            if (row.Edit is { } edit)
            {
                Require(edit);
            }

            if (row.Review is { } review)
            {
                Require(review.Yours);
                Require(review.UpdatedBuiltIn);
            }
        }
    }

    private static void Require(BuiltInTermEdit term)
    {
        Require(term.Key.Value);
        Require(term.Base);
        Require(term.Value);
        Require(term.Acknowledged);
    }

    private static void Require(TermValues? values)
    {
        if (values is not null)
        {
            Require(values.Spoken);
            Require(values.Written);
        }
    }

    private static void Require(LibraryContentHash? hash) => Require(hash?.Value);

    private static void RequireAll(IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            Require(value);
        }
    }

    private static void Require(string? value)
    {
        if (!IsWellFormed(value))
        {
            throw new ArgumentException(Refusal, "changes");
        }
    }
}
