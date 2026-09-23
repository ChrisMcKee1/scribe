using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>
/// Damaged-database copies used to be kept forever, each as large as the database it replaced. A
/// copy can be someone's only recovery point, so the newest is kept indefinitely; older ones go only
/// after this build has known about them for two weeks, counted from first sight, never from a file
/// time or the stamp in the name.
/// </summary>
public sealed class DamagedDatabaseCopiesTests : IDisposable
{
    private static readonly TimeSpan Grace = TimeSpan.FromDays(StorageRetentionPolicy.DamagedCopyRetentionDays);

    private readonly TempDatabaseFolder _folder = new();
    private readonly DateTimeOffset _now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    private readonly Dictionary<string, DateTimeOffset> _ledger = new(StringComparer.Ordinal);

    public void Dispose() => _folder.Dispose();

    private string Copy(DateTimeOffset repairedUtc, string suffix = "", int bytes = 100)
    {
        var path = _folder.DatabasePath + ".corrupt-" + DamagedDatabaseCopies.FormatStamp(repairedUtc.UtcDateTime) + suffix;
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private DamagedCopyPruneResult Prune(DateTimeOffset at) =>
        DamagedDatabaseCopies.Prune(_folder.DatabasePath, at, Grace, _ledger);

    [Fact]
    public void The_newest_copy_is_kept_indefinitely_whatever_its_age()
    {
        var ancient = Copy(_now.AddDays(-400));
        var ancientWal = Copy(_now.AddDays(-400), "-wal");

        Assert.Equal(0, Prune(_now).FilesDeleted);
        Assert.Equal(0, Prune(_now.AddDays(365)).FilesDeleted);

        Assert.True(File.Exists(ancient));
        Assert.True(File.Exists(ancientWal));
    }

    [Fact]
    public void Older_copies_go_only_after_this_build_has_known_them_for_the_grace_period()
    {
        var oldest = Copy(_now.AddDays(-40));
        var oldestWal = Copy(_now.AddDays(-40), "-wal");
        var older = Copy(_now.AddDays(-3));
        var olderShm = Copy(_now.AddDays(-3), "-shm");
        var newest = Copy(_now.AddDays(-1));
        var newestWal = Copy(_now.AddDays(-1), "-wal");

        // Stamped 40 days ago and written 100 days ago, yet first seen now: nothing goes yet.
        File.SetLastWriteTimeUtc(oldest, _now.AddDays(-100).UtcDateTime);
        var first = Prune(_now);
        Assert.Equal(new DamagedCopyPruneResult(3, 0, 0, 0, LedgerChanged: true), first);
        Assert.Equal(3, _ledger.Count);
        Assert.All(_ledger.Values, seen => Assert.Equal(_now, seen));

        Assert.Equal(0, Prune(_now + Grace - TimeSpan.FromMinutes(1)).FilesDeleted);

        var later = Prune(_now + Grace);

        Assert.Equal(new DamagedCopyPruneResult(3, 4, 400, 0, LedgerChanged: true), later);
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(oldestWal));
        Assert.False(File.Exists(older));
        Assert.False(File.Exists(olderShm));
        Assert.True(File.Exists(newest));
        Assert.True(File.Exists(newestWal));
        Assert.Equal(new[] { DamagedDatabaseCopies.FormatStamp(_now.AddDays(-1).UtcDateTime) }, _ledger.Keys);
    }

    [Fact]
    public void A_copy_that_appears_later_starts_its_own_grace_period()
    {
        var firstRepair = Copy(_now.AddDays(-1));
        Prune(_now);

        // A new repair ten days on: the first copy becomes the older one, known for ten days only.
        Copy(_now.AddDays(10));
        Assert.Equal(0, Prune(_now.AddDays(10)).FilesDeleted);
        Assert.True(File.Exists(firstRepair));

        Assert.Equal(1, Prune(_now + Grace).FilesDeleted);
        Assert.False(File.Exists(firstRepair));
    }

    [Fact]
    public void Entries_for_copies_that_are_gone_are_dropped_and_a_future_first_seen_keeps_a_copy()
    {
        _ledger["20200101-000000"] = _now.AddDays(-30);
        var older = Copy(_now.AddDays(-5));
        Copy(_now.AddDays(-1));

        // First seen "in the future": the clock was set back since. It reads as new, so it stays.
        _ledger[DamagedDatabaseCopies.FormatStamp(_now.AddDays(-5).UtcDateTime)] = _now.AddDays(3);

        var result = Prune(_now.AddDays(1));

        Assert.True(result.LedgerChanged);
        Assert.False(_ledger.ContainsKey("20200101-000000"));
        Assert.Equal(0, result.FilesDeleted);
        Assert.True(File.Exists(older));
    }

    [Fact]
    public void Files_that_are_not_repair_copies_are_never_touched()
    {
        var bystanders = new[]
        {
            _folder.DatabasePath,
            _folder.DatabasePath + "-wal",
            _folder.DatabasePath + ".corrupt-notastamp",
            _folder.DatabasePath + ".corrupt-20260101-000000.bak",
            Path.Combine(_folder.Root, "other.db.corrupt-20200101-000000"),
            Path.Combine(_folder.Root, "notes.txt"),
        };
        foreach (var path in bystanders)
        {
            File.WriteAllBytes(path, [1, 2, 3]);
        }

        var result = Prune(_now.AddYears(1));

        Assert.Equal(default, result);
        Assert.Empty(_ledger);
        Assert.All(bystanders, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void A_stamp_written_in_another_calendar_is_dated_by_the_file_when_picking_the_newest()
    {
        // An earlier build formatted the stamp with the user's calendar: Thai Buddhist year 2569
        // cannot be a real repair time, so the file time dates it, and it is the newest here.
        var thaiStamped = _folder.DatabasePath + ".corrupt-25690920-101010";
        File.WriteAllBytes(thaiStamped, [0]);
        File.SetLastWriteTimeUtc(thaiStamped, _now.AddDays(-2).UtcDateTime);
        var gregorian = Copy(_now.AddDays(-5));

        Prune(_now);
        var result = Prune(_now + Grace);

        Assert.Equal(1, result.FilesDeleted);
        Assert.True(File.Exists(thaiStamped));
        Assert.False(File.Exists(gregorian));
    }

    [Fact]
    public void A_copy_that_cannot_be_deleted_is_counted_and_retried_on_the_next_pass()
    {
        var locked = Copy(_now.AddDays(-30));
        Copy(_now.AddDays(-1));
        Prune(_now);

        DamagedCopyPruneResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = Prune(_now + Grace);
        }

        Assert.Equal(1, result.FilesFailed);
        Assert.True(File.Exists(locked));
        Assert.Equal(2, _ledger.Count);

        Assert.Equal(1, Prune(_now + Grace).FilesDeleted);
        Assert.False(File.Exists(locked));
    }

    [Fact]
    public void A_missing_folder_is_not_an_error()
    {
        var result = DamagedDatabaseCopies.Prune(Path.Combine(_folder.Root, "absent", "scribe.db"), _now, Grace, _ledger);

        Assert.Equal(default, result);
    }
}
