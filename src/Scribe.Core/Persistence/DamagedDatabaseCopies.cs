using System.Globalization;
using System.Text.Json;

namespace Scribe.Core.Persistence;

/// <summary>
/// Retention for the copies a startup repair leaves beside the database: <c>scribe.db.corrupt-</c>
/// plus a <c>yyyyMMdd-HHmmss</c> UTC stamp, with any <c>-wal</c>, <c>-shm</c> or <c>-journal</c>
/// companion. A copy can be someone's only recovery point (one repair bug moved a healthy database
/// aside), so the newest copy is kept indefinitely, whatever its age. Older copies are deleted only
/// once this build has known about them for <see cref="StorageRetentionPolicy.DamagedCopyRetentionDays"/>
/// days, counted from when a pass first saw each one (recorded in <see cref="DamagedCopyLedger"/>),
/// never from a file time or the stamp, so an upgrade never deletes a copy the moment it appears.
/// </summary>
/// <remarks>
/// Which copy is newest comes from the stamp in its name, which is when the repair happened; the
/// file times belong to the damaged original, because a rename preserves them. Earlier builds
/// formatted that stamp with the user's calendar, so a Thai, Persian or Hijri calendar produced a year
/// such as 2569 or 1447. A stamp that cannot be a real repair time falls back to the newest file time
/// in the copy. Anything that does not match the name shape is left alone.
/// </remarks>
internal static class DamagedDatabaseCopies
{
    internal const string StampFormat = "yyyyMMdd-HHmmss";

    // Earliest year a real repair stamp can carry; anything before it came from another calendar.
    private const int EarliestPlausibleYear = 2020;

    private static readonly string[] CompanionSuffixes = ["", "-wal", "-shm", "-journal"];

    /// <summary>
    /// Deletes every copy but the newest that <paramref name="firstSeen"/> says has been known for
    /// at least <paramref name="grace"/>. Updates <paramref name="firstSeen"/> in place: copies seen
    /// for the first time are added at <paramref name="nowUtc"/>, and entries for copies that no
    /// longer exist are dropped. Never throws for a single file: a copy that cannot be deleted is
    /// counted and retried on the next pass.
    /// </summary>
    internal static DamagedCopyPruneResult Prune(
        string databasePath,
        DateTimeOffset nowUtc,
        TimeSpan grace,
        IDictionary<string, DateTimeOffset> firstSeen)
    {
        var copies = FindCopies(databasePath);
        var ledgerChanged = false;
        foreach (var token in firstSeen.Keys.Where(token => !copies.ContainsKey(token)).ToList())
        {
            firstSeen.Remove(token);
            ledgerChanged = true;
        }

        if (copies.Count == 0)
        {
            return new DamagedCopyPruneResult(0, 0, 0, 0, ledgerChanged);
        }

        foreach (var token in copies.Keys)
        {
            if (!firstSeen.ContainsKey(token))
            {
                firstSeen[token] = nowUtc;
                ledgerChanged = true;
            }
        }

        var newest = copies.MaxBy(copy => RepairTime(copy.Key, copy.Value, nowUtc)).Key;
        var result = new DamagedCopyPruneResult(copies.Count, 0, 0, 0, ledgerChanged);
        foreach (var (token, files) in copies)
        {
            // A first-seen time in the future (a clock set back) reads as brand new: kept.
            if (token == newest || nowUtc - firstSeen[token] < grace)
            {
                continue;
            }

            var failed = 0;
            foreach (var path in files)
            {
                try
                {
                    var bytes = new FileInfo(path).Length;
                    File.Delete(path);
                    result = result with
                    {
                        FilesDeleted = result.FilesDeleted + 1,
                        BytesDeleted = result.BytesDeleted + bytes,
                    };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                }
            }

            result = result with { FilesFailed = result.FilesFailed + failed };
            if (failed == 0)
            {
                firstSeen.Remove(token);
                result = result with { LedgerChanged = true };
            }
        }

        return result;
    }

    /// <summary>Formats the stamp a repair puts in the copy's name, always in the Gregorian calendar.</summary>
    internal static string FormatStamp(DateTime utcNow) =>
        utcNow.ToString(StampFormat, CultureInfo.InvariantCulture);

    // Recognizes "<prefix>dddddddd-dddddd[suffix]" and returns the 15-character stamp.
    internal static bool TryGetStampToken(string fileName, string prefix, out string token)
    {
        token = string.Empty;
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            fileName.Length < prefix.Length + StampFormat.Length)
        {
            return false;
        }

        var candidate = fileName.Substring(prefix.Length, StampFormat.Length);
        for (var i = 0; i < candidate.Length; i++)
        {
            var valid = i == 8 ? candidate[i] == '-' : char.IsAsciiDigit(candidate[i]);
            if (!valid)
            {
                return false;
            }
        }

        var suffix = fileName[(prefix.Length + StampFormat.Length)..];
        if (!CompanionSuffixes.Contains(suffix, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        token = candidate;
        return true;
    }

    private static Dictionary<string, List<string>> FindCopies(string databasePath)
    {
        var copies = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return copies;
        }

        var prefix = Path.GetFileName(databasePath) + ".corrupt-";
        foreach (var path in Directory.EnumerateFiles(directory, prefix + "*"))
        {
            if (!TryGetStampToken(Path.GetFileName(path), prefix, out var token))
            {
                continue;
            }

            if (!copies.TryGetValue(token, out var files))
            {
                copies[token] = files = [];
            }

            files.Add(path);
        }

        return copies;
    }

    private static DateTimeOffset RepairTime(string token, List<string> files, DateTimeOffset nowUtc)
    {
        if (DateTime.TryParseExact(
                token,
                StampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed) &&
            parsed.Year >= EarliestPlausibleYear &&
            parsed <= nowUtc.UtcDateTime.AddDays(1))
        {
            return new DateTimeOffset(parsed, TimeSpan.Zero);
        }

        var newestWrite = DateTime.MinValue;
        foreach (var path in files)
        {
            try
            {
                var written = File.GetLastWriteTimeUtc(path);
                if (written > newestWrite)
                {
                    newestWrite = written;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable time is skipped; the copy's other files still date it.
            }
        }

        // No usable time at all: treat the copy as brand new, so the worst case is keeping an older
        // copy as well, never deleting the one a repair just made.
        return newestWrite == DateTime.MinValue
            ? nowUtc
            : new DateTimeOffset(DateTime.SpecifyKind(newestWrite, DateTimeKind.Utc), TimeSpan.Zero);
    }
}

/// <summary>
/// When each damaged-database copy was first seen, kept as one row of the <c>settings</c> key/value
/// table: it needs no schema change, older builds ignore unknown keys, and a repair's salvage carries
/// it. Losing it (a repair that could not save settings) only restarts every grace period, which
/// keeps copies longer, never shorter.
/// </summary>
internal static class DamagedCopyLedger
{
    internal const string SettingsKey = "storage.damaged_copies_first_seen";

    internal static Dictionary<string, DateTimeOffset> Load(ScribeDatabase database)
    {
        var ledger = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", SettingsKey);
        if (command.ExecuteScalar() is not string json)
        {
            return ledger;
        }

        try
        {
            foreach (var (token, seen) in JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(json) ?? [])
            {
                ledger[token] = seen;
            }
        }
        catch (JsonException)
        {
            // Unreadable: every copy starts a fresh grace period, the safe direction.
        }

        return ledger;
    }

    internal static void Save(ScribeDatabase database, IReadOnlyDictionary<string, DateTimeOffset> ledger)
    {
        using var writeScope = database.EnterWriteScope();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        if (ledger.Count == 0)
        {
            command.CommandText = "DELETE FROM settings WHERE key = $key;";
        }
        else
        {
            command.CommandText =
                """
                INSERT INTO settings (key, value) VALUES ($key, $value)
                ON CONFLICT (key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(ledger));
        }

        command.Parameters.AddWithValue("$key", SettingsKey);
        command.ExecuteNonQuery();
    }
}

/// <summary>What one damaged-copy retention pass did. A copy is a stamp plus its companions.</summary>
internal readonly record struct DamagedCopyPruneResult(
    int CopiesFound,
    int FilesDeleted,
    long BytesDeleted,
    int FilesFailed,
    bool LedgerChanged);
