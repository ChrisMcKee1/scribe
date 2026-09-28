using System.Globalization;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>
/// DATA-A-10, which ships without a flag: every open's configuration batch is now built once per process instead of once
/// per open. The text is the proof: both batches equal the interpolation each open used to make (the whole input domain,
/// both automatic-checkpoint states), and the settings read back as they always did, through pool reuse and the switch
/// between automatic checkpoints on and off.
/// </summary>
public sealed class ScribeDatabaseConfigureTextTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Precomputed_batches_equal_todays_interpolation(bool autoCheckpoint)
    {
        // Today's expression, verbatim, with WalAutoCheckpointPages (private) written as its value.
        var today = string.Create(
            CultureInfo.InvariantCulture,
            $"PRAGMA busy_timeout={ScribeDatabase.BusyTimeoutMs}; PRAGMA synchronous=FULL; PRAGMA secure_delete=ON; PRAGMA wal_autocheckpoint={(autoCheckpoint ? 1000 : 0)};");

        Assert.Equal(today, ScribeDatabase.ConfiguredBatch(autoCheckpoint));
        Assert.Same(ScribeDatabase.ConfiguredBatch(autoCheckpoint), ScribeDatabase.ConfiguredBatch(autoCheckpoint));
    }

    [Fact]
    public void Every_open_reads_back_the_same_settings_through_pool_reuse_and_both_checkpoint_states()
    {
        using var folder = new TempDatabaseFolder();
        using var database = folder.Open();
        database.Initialize();

        foreach (var (defer, pages) in new[] { (false, 1000L), (true, 0L), (false, 1000L), (false, 1000L), (true, 0L) })
        {
            database.DeferAutoCheckpoint = defer;
            using var connection = database.Open();
            Assert.Equal(10_000L, Pragma(connection, "busy_timeout"));
            Assert.Equal(2L, Pragma(connection, "synchronous"));
            Assert.Equal(1L, Pragma(connection, "secure_delete"));
            Assert.Equal(pages, Pragma(connection, "wal_autocheckpoint"));
        }

        folder.ReleasePooledConnections();
    }

    private static long Pragma(Microsoft.Data.Sqlite.SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
