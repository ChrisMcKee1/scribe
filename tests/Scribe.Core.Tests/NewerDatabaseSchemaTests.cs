using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>
/// A database a newer Scribe wrote must stop this build cleanly: the app catches the typed exception,
/// tells the user to update and exits, instead of hanging invisibly with the single-instance mutex held.
/// </summary>
public sealed class NewerDatabaseSchemaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scribe-newer-schema-" + Guid.NewGuid().ToString("N"));

    public NewerDatabaseSchemaTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void A_database_from_a_newer_build_is_refused_with_its_versions_and_left_as_it_was()
    {
        var path = Path.Combine(_root, AppPaths.DatabaseFileName);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE future_table (id INTEGER PRIMARY KEY); INSERT INTO future_table DEFAULT VALUES; PRAGMA user_version=8;";
            command.ExecuteNonQuery();
        }

        using (var db = new ScribeDatabase(new AppPaths(_root), NullLogger<ScribeDatabase>.Instance))
        {
            var refused = Assert.Throws<NewerDatabaseSchemaException>(() => db.Initialize());
            Assert.Equal(8, refused.DatabaseVersion);
            Assert.Equal(7, refused.SupportedVersion);
            Assert.False(db.RepairedAtStartup);
        }

        DatabasePools.Release(new AppPaths(_root));

        // Not moved aside as damaged, and not migrated: the newer build finds its data as it left it.
        Assert.Empty(Directory.GetFiles(_root, AppPaths.DatabaseFileName + ".corrupt-*"));
        using var reopened = new SqliteConnection($"Data Source={path};Pooling=False");
        reopened.Open();
        using var check = reopened.CreateCommand();
        check.CommandText = "PRAGMA user_version;";
        Assert.Equal(8L, (long)check.ExecuteScalar()!);
        check.CommandText = "SELECT count(*) FROM future_table;";
        Assert.Equal(1L, (long)check.ExecuteScalar()!);
        check.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'history';";
        Assert.Equal(0L, (long)check.ExecuteScalar()!);
    }

    [Fact]
    public void The_notice_tells_the_user_what_to_do()
    {
        Assert.Equal(
            "This data was created by a newer version of Scribe. Please install the latest version.",
            NewerDatabaseSchemaException.UserMessage);
    }

    public void Dispose()
    {
        DatabasePools.Release(new AppPaths(_root));
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // A leftover temp directory must never fail a test run.
        }
    }
}
