using System.Globalization;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Libraries;

/// <summary>
/// Recently deleted (contract 6.7): <c>LibrariesDir\deleted\&lt;yyyyMMdd'T'HHmmss'Z'&gt;[-&lt;n&gt;].&lt;original file name&gt;</c>,
/// the deleted file's bytes unchanged under a name that carries the UTC time of the Save that deleted it. Neither older
/// builds nor any loader look in the folder, and <c>AppPaths.TryMigrateLibraries</c> never copies it.
/// </summary>
internal static class RecentlyDeletedStore
{
    private const int StampLength = 16;
    private const string CsvSuffix = ".csv";

    /// <summary>
    /// An entry name parsed whole: the stamp, the same-second sequence (1 when there is none, else at least 2 without
    /// leading zeros) and the original file name, which ends in <c>.csv</c>.
    /// </summary>
    public static bool TryParseEntryName(string? name, out DateTimeOffset deletedUtc, out int sequence, out string originalFileName)
    {
        deletedUtc = default;
        sequence = 1;
        originalFileName = string.Empty;
        if (name is null || name.Length <= StampLength + 1 ||
            !LibraryJournalNames.TryParseStamp(name.AsSpan(0, StampLength), out deletedUtc))
        {
            return false;
        }

        var rest = name.AsSpan(StampLength);
        if (rest[0] == '-')
        {
            var digits = 1;
            while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
            {
                digits++;
            }

            if (digits == 1 || rest[1] == '0' ||
                !int.TryParse(rest[1..digits], NumberStyles.None, CultureInfo.InvariantCulture, out sequence) || sequence < 2)
            {
                return false;
            }

            rest = rest[digits..];
        }

        if (rest.Length < 2 || rest[0] != '.')
        {
            return false;
        }

        originalFileName = rest[1..].ToString();
        return LibraryManifest.IsTopLevelCsv(originalFileName);
    }

    /// <summary>
    /// The id the entry's library had, as far as its file name tells: the stem, or for a file whose stem is a built-in
    /// id the remapped id it would have been given (the recorded id went with the file's entry in the settings row).
    /// </summary>
    public static string OriginalId(string originalFileName)
    {
        var stem = CustomLibraryStore.Stem(originalFileName);
        return CustomLibraryStore.BuiltInIds.Contains(stem) ? "custom-" + stem : stem;
    }

    /// <summary>The first free entry name for a file deleted at <paramref name="stamp"/>: then <c>-2</c>, <c>-3</c> in the same second.</summary>
    public static string NextEntryName(string originalFileName, DateTimeOffset stamp, ISet<string> taken)
    {
        var prefix = LibraryJournalNames.FormatStamp(stamp);
        var candidate = prefix + "." + originalFileName;
        for (var n = 2; taken.Contains(candidate); n++)
        {
            candidate = prefix + "-" + n.ToString(CultureInfo.InvariantCulture) + "." + originalFileName;
        }

        return candidate;
    }

    /// <summary>The entries as the read path found them, newest first.</summary>
    public static IReadOnlyList<RecentlyDeletedLibrary> List(LibraryFolderSnapshot snapshot, ILibraryCsvCodec codec)
    {
        var entries = new List<(RecentlyDeletedLibrary Entry, int Sequence)>();
        foreach (var (entryName, read) in snapshot.Deleted)
        {
            var parsed = TryParseEntryName(entryName, out var deletedUtc, out var sequence, out var originalFileName);
            var originalId = snapshot.DeletedIds.TryGetValue(entryName, out var pendingId)
                ? pendingId
                : parsed ? OriginalId(originalFileName) : CustomLibraryStore.Stem(entryName);
            var fallbackName = BuiltInDictionaryLibraries.Humanize(parsed ? CustomLibraryStore.Stem(originalFileName) : CustomLibraryStore.Stem(entryName));
            if (read.Bytes is { } bytes)
            {
                var document = codec.ReadManaged(bytes);
                entries.Add((new RecentlyDeletedLibrary(
                    entryName, originalId, document.Name ?? fallbackName, document.Terms.Count,
                    parsed ? deletedUtc : DateTimeOffset.MinValue,
                    parsed ? LibraryFileState.Available : LibraryFileState.Unreadable,
                    read.Hash), sequence));
            }
            else
            {
                var state = read.Failure == LibraryIoFailure.SharingViolation && parsed
                    ? LibraryFileState.AwaitingRelease
                    : LibraryFileState.Unreadable;
                entries.Add((new RecentlyDeletedLibrary(
                    entryName, originalId, fallbackName, 0, parsed ? deletedUtc : DateTimeOffset.MinValue, state), sequence));
            }
        }

        return [.. entries
            .OrderByDescending(item => item.Entry.DeletedUtc)
            .ThenByDescending(item => item.Sequence)
            .ThenBy(item => item.Entry.EntryName, StringComparer.Ordinal)
            .Select(item => item.Entry)];
    }

    /// <summary>
    /// An entry's content for a restore (review finding A8): read from its file and checked against the entry's hash, or
    /// null when the entry is gone, cannot be read, or no longer matches.
    /// </summary>
    public static RecentlyDeletedContent? ReadContent(RecentlyDeletedLibrary entry, LibraryFileRead read, ILibraryCsvCodec codec)
    {
        if (entry.ContentHash is not { } expected || read.Bytes is not { } bytes || read.Hash != expected)
        {
            return null;
        }

        var document = codec.ReadManaged(bytes);
        var stem = TryParseEntryName(entry.EntryName, out _, out _, out var originalFileName)
            ? CustomLibraryStore.Stem(originalFileName)
            : CustomLibraryStore.Stem(entry.EntryName);
        var content = new LibraryContent(
            entry.OriginalId,
            BuiltIn: false,
            document.Name ?? BuiltInDictionaryLibraries.Humanize(stem),
            document.Category ?? "Custom",
            document.Description,
            [.. document.Terms.Select(LibraryRow.Custom)],
            document.BasedOn);
        return new RecentlyDeletedContent(
            entry, content, document.Errors.Count > 0 ? LibraryFileState.PartlyReadable : LibraryFileState.Available);
    }
}
