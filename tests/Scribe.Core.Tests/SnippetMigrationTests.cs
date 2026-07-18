using Scribe.Core.Models;
using Scribe.Core.Infrastructure;
using Scribe.Core.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class SnippetMigrationTests
{
    [Fact]
    public void V4_migration_reopens_v3_purges_exact_junk_and_advances_schema()
    {
        var root = Path.Combine(Path.GetTempPath(), "scribe-v3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "scribe.db");
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE snippets (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        phrase TEXT NOT NULL,
                        template TEXT NOT NULL,
                        enabled INTEGER NOT NULL DEFAULT 1
                    );
                    CREATE UNIQUE INDEX ux_snippets_phrase ON snippets (phrase);
                    INSERT INTO snippets (phrase, template) VALUES
                      ('insert my standup', 'My standup update for today.'),
                      ('2026-06-26T15:11:17 release checklist', 'Keep this user-authored trigger.'),
                      ('2026-06-26T15:11:17.9120711Z', 'Keep a different timestamp representation.'),
                      ('2026-06-26T15:11:17.9120711+00:00', 'Yeah.');
                    PRAGMA user_version=3;
                    """;
                command.ExecuteNonQuery();
            }

            var paths = new AppPaths(root);
            using var db = new ScribeDatabase(paths, NullLogger<ScribeDatabase>.Instance);
            var remaining = new SnippetRepository(db).GetAll();

            Assert.Equal(3, remaining.Count);
            Assert.DoesNotContain(remaining, snippet => snippet.Phrase.EndsWith("+00:00"));
            using var migrated = db.Open();
            using var version = migrated.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.Equal(5L, (long)(version.ExecuteScalar() ?? 0L));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Future_schema_is_rejected_without_retry_leaks()
    {
        var root = Path.Combine(Path.GetTempPath(), "scribe-future-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "scribe.db");
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version=99;";
                command.ExecuteNonQuery();
            }

            using var db = new ScribeDatabase(new AppPaths(root), NullLogger<ScribeDatabase>.Instance);
            Assert.Throws<InvalidOperationException>(() => db.Initialize());
            Assert.Throws<InvalidOperationException>(() => db.Initialize());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
