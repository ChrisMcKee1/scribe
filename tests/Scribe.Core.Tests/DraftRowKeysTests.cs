using System.Globalization;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// LeanFooterRefresh keeps each Settings row's key instead of building it on every read. The keys must be exactly the ones
/// the rows have always made (the old expressions are the oracle here) and must follow every change of what they are made
/// from: an Id a Save assigns, a profile's loaded name and processes, its origin.
/// </summary>
public sealed class DraftRowKeysTests
{
    // The row types' RowKey expressions as 0.5.0 wrote them.
    private static string OldIdKey(long id, object row) =>
        id > 0 ? id.ToString(CultureInfo.InvariantCulture) : $"new:{row.GetHashCode()}";

    private static string OldProfileKey(DraftRowOrigin origin, string? loadedName, string? loadedProcesses, object row) =>
        origin == DraftRowOrigin.Saved ? $"saved:{loadedName}:{loadedProcesses}" : $"new:{row.GetHashCode()}";

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void The_id_key_is_the_one_rows_always_made(long id)
    {
        var row = new object();

        Assert.Equal(OldIdKey(id, row), DraftRowKeys.ForId(id, row));
    }

    [Theory]
    [InlineData(DraftRowOrigin.Saved, "Command windows", "cmd,pwsh")]
    [InlineData(DraftRowOrigin.Saved, null, null)]
    [InlineData(DraftRowOrigin.Saved, "", "")]
    [InlineData(DraftRowOrigin.New, "Mail", "outlook")]
    public void The_profile_key_is_the_one_rows_always_made(DraftRowOrigin origin, string? name, string? processes)
    {
        var row = new object();

        Assert.Equal(OldProfileKey(origin, name, processes, row), DraftRowKeys.ForProfile(origin == DraftRowOrigin.Saved, name, processes, row));
    }

    [Fact]
    public void A_kept_key_is_the_same_string_until_its_id_changes()
    {
        var row = new object();
        var cache = default(DraftRowKeyCache);

        var first = cache.ForId(0, row);
        Assert.Same(first, cache.ForId(0, row));
        Assert.Equal(OldIdKey(0, row), first);

        // A Save gives the row its Id: the key follows at once.
        var saved = cache.ForId(17, row);
        Assert.Equal("17", saved);
        Assert.Same(saved, cache.ForId(17, row));
        Assert.Equal("18", cache.ForId(18, row));
    }

    [Fact]
    public void A_kept_profile_key_follows_its_origin_name_and_processes()
    {
        var row = new object();
        var cache = default(DraftRowKeyCache);

        var fresh = cache.ForProfile(false, null, null, row);
        Assert.Equal(OldProfileKey(DraftRowOrigin.New, null, null, row), fresh);
        Assert.Same(fresh, cache.ForProfile(false, null, null, row));

        Assert.Equal("saved:Mail:outlook", cache.ForProfile(true, "Mail", "outlook", row));
        Assert.Equal("saved:Mail:outlook,thunderbird", cache.ForProfile(true, "Mail", "outlook,thunderbird", row));
        Assert.Equal("saved:Email:outlook,thunderbird", cache.ForProfile(true, "Email", "outlook,thunderbird", row));

        // An equal name in a new string instance is the same key.
        var name = new string("Email".AsSpan());
        var kept = cache.ForProfile(true, name, "outlook,thunderbird", row);
        Assert.Equal("saved:Email:outlook,thunderbird", kept);
        Assert.Same(kept, cache.ForProfile(true, "Email", "outlook,thunderbird", row));
    }

    [Fact]
    public void Seeded_edit_sequences_always_give_the_old_keys()
    {
        // 500 rows of each kind through 60 random changes each, with the old expressions recomputed at every read.
        var random = new Random(20260928);
        string?[] names = [null, string.Empty, "Mail", "Command windows", "Chat", "caf\u00e9"];
        string?[] processes = [null, string.Empty, "outlook", "cmd,pwsh,wt", "teams"];
        for (var r = 0; r < 500; r++)
        {
            var idRow = new object();
            var profileRow = new object();
            var idCache = default(DraftRowKeyCache);
            var profileCache = default(DraftRowKeyCache);
            long id = random.Next(0, 3) == 0 ? 0 : random.Next(1, 5);
            var origin = DraftRowOrigin.New;
            var name = names[random.Next(names.Length)];
            var process = processes[random.Next(processes.Length)];
            for (var step = 0; step < 60; step++)
            {
                switch (random.Next(5))
                {
                    case 0:
                        id = random.Next(-2, 6);
                        break;
                    case 1:
                        origin = origin == DraftRowOrigin.New ? DraftRowOrigin.Saved : DraftRowOrigin.New;
                        break;
                    case 2:
                        name = names[random.Next(names.Length)];
                        break;
                    case 3:
                        process = processes[random.Next(processes.Length)];
                        break;
                }

                Assert.Equal(OldIdKey(id, idRow), idCache.ForId(id, idRow));
                Assert.Equal(OldProfileKey(origin, name, process, profileRow), profileCache.ForProfile(origin == DraftRowOrigin.Saved, name, process, profileRow));
            }
        }
    }
}
