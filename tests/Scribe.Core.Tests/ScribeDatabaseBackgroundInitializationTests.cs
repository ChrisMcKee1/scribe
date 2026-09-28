using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>
/// DATA-O-05b (<see cref="PerfFlags.OverlappedIntegrityCheck"/>): the database's first-use initialization, integrity check
/// and repair included, can start on a worker while startup builds the host. The guarantee these hold it to: the whole
/// check and any repair run exactly as in line (twin damaged files decide the same), no use of the database begins before
/// they have finished, a failure reaches the first caller as it would have and the next call tries again, exit waits for
/// the check, and the verdict stays bound to the file it read.
/// </summary>
public sealed class ScribeDatabaseBackgroundInitializationTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("scribe-bginit-");

    public void Dispose()
    {
        foreach (var root in _folder.GetDirectories())
        {
            DatabasePools.Release(new AppPaths(root.FullName));
        }

        try { _folder.Delete(recursive: true); } catch { /* best effort */ }
    }

    public static TheoryData<string> Damage() => ["none", "garbage", "history page", "audio_blobs page", "freelist trunk"];

    [Theory]
    [MemberData(nameof(Damage))]
    public async Task A_file_checked_on_a_worker_is_decided_exactly_as_one_checked_in_line(string damage)
    {
        var source = Root("source");
        BuildDatabase(source);
        Damage(source, damage);

        var inLine = Copy(source, "in-line");
        var onWorker = Copy(source, "on-worker");

        var expected = await InitializeAsync(inLine, background: false);
        var actual = await InitializeAsync(onWorker, background: true);

        Assert.Equal(expected, actual);
        Assert.Equal(damage != "none", actual.Repaired);
    }

    [Fact]
    public async Task A_locked_healthy_file_is_never_rebuilt_and_the_first_caller_gets_the_failure_the_next_one_retries()
    {
        var root = Root("locked");
        BuildDatabase(root);
        var path = Path.Combine(root, AppPaths.DatabaseFileName);

        string inLineFailure;
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using (var inLine = Open(root))
            {
                inLineFailure = Assert.ThrowsAny<Exception>(inLine.Initialize).GetType().FullName!;
            }

            using var onWorker = Open(root);
            await onWorker.InitializeInBackground().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(inLineFailure, Assert.ThrowsAny<Exception>(onWorker.Initialize).GetType().FullName);
        }

        Assert.Empty(Directory.GetFiles(root, "scribe.db.corrupt-*"));
        using var recovered = Open(root);
        await recovered.InitializeInBackground().WaitAsync(TimeSpan.FromSeconds(60));
        recovered.Initialize();
        Assert.False(recovered.RepairedAtStartup);
        Assert.Equal(40L, DatabaseProbe.QueryInt64(recovered, "SELECT count(*) FROM history"));
    }

    [Fact]
    public async Task A_file_from_a_newer_build_is_refused_to_the_first_caller_and_again_to_the_next_and_left_alone()
    {
        var root = Root("newer");
        var path = Path.Combine(root, AppPaths.DatabaseFileName);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE future_table (id INTEGER PRIMARY KEY); PRAGMA user_version=8;";
            command.ExecuteNonQuery();
        }

        using (var database = Open(root))
        {
            await database.InitializeInBackground().WaitAsync(TimeSpan.FromSeconds(60));
            var first = Assert.Throws<NewerDatabaseSchemaException>(database.Initialize);
            var second = Assert.Throws<NewerDatabaseSchemaException>(database.Initialize);
            Assert.Equal(8, first.DatabaseVersion);
            Assert.Equal(8, second.DatabaseVersion);
            Assert.NotSame(first, second);
            Assert.False(database.RepairedAtStartup);
        }

        DatabasePools.Release(new AppPaths(root));
        Assert.Empty(Directory.GetFiles(root, "scribe.db.corrupt-*"));
        using var reopened = new SqliteConnection($"Data Source={path};Pooling=False");
        reopened.Open();
        using var check = reopened.CreateCommand();
        check.CommandText = "PRAGMA user_version;";
        Assert.Equal(8L, (long)check.ExecuteScalar()!);
    }

    [Fact]
    public async Task No_use_of_the_database_begins_before_the_check_on_the_worker_has_finished()
    {
        var root = Root("wait");
        BuildDatabase(root);
        using var database = Open(root);
        using var inCheck = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        database.InitializationStarting = () =>
        {
            inCheck.Set();
            release.Wait(TimeSpan.FromSeconds(60));
        };

        var background = database.InitializeInBackground();
        Assert.True(inCheck.Wait(TimeSpan.FromSeconds(60)));
        var open = Task.Run(() =>
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM history;";
            return (long)command.ExecuteScalar()!;
        });
        var load = Task.Run(() => new SettingsRepository(database).Load());

        Assert.NotSame(open, await Task.WhenAny(open, Task.Delay(300)));
        Assert.False(load.IsCompleted);

        database.InitializationStarting = null;
        release.Set();
        await background.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(40L, await open.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.NotNull(await load.WaitAsync(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task Exit_while_the_worker_checks_waits_for_the_check_and_leaves_the_file_healthy()
    {
        var root = Root("exit");
        BuildDatabase(root);
        var database = Open(root);
        using var inCheck = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        database.InitializationStarting = () =>
        {
            inCheck.Set();
            release.Wait(TimeSpan.FromSeconds(60));
        };

        var background = database.InitializeInBackground();
        Assert.True(inCheck.Wait(TimeSpan.FromSeconds(60)));
        var dispose = Task.Run(database.Dispose);
        Assert.NotSame(dispose, await Task.WhenAny(dispose, Task.Delay(300)));

        release.Set();
        await dispose.WaitAsync(TimeSpan.FromSeconds(60));
        await background.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(TaskStatus.RanToCompletion, background.Status);
        Assert.Throws<ObjectDisposedException>(database.Initialize);

        using var reopened = Open(root);
        reopened.Initialize();
        Assert.False(reopened.RepairedAtStartup);
        Assert.Equal(40L, DatabaseProbe.QueryInt64(reopened, "SELECT count(*) FROM history"));
    }

    [Fact]
    public async Task The_verdict_stays_bound_to_the_file_it_read_until_the_database_is_closed()
    {
        var root = Root("bound");
        BuildDatabase(root);
        var path = Path.Combine(root, AppPaths.DatabaseFileName);
        using (var database = Open(root))
        {
            await database.InitializeInBackground().WaitAsync(TimeSpan.FromSeconds(60));

            // Checked and still held open: nothing can move another file into its place before the first use.
            Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".moved"));
            database.Initialize();
            Assert.Equal(40L, DatabaseProbe.QueryInt64(database, "SELECT count(*) FROM history"));
        }

        DatabasePools.Release(new AppPaths(root));
        File.Move(path, path + ".moved");
        File.WriteAllText(path, "a different file, not a database at all, padding padding padding");

        // A new database over the replaced file checks it afresh.
        using var replaced = Open(root);
        await replaced.InitializeInBackground().WaitAsync(TimeSpan.FromSeconds(60));
        replaced.Initialize();
        Assert.True(replaced.RepairedAtStartup);
    }

    [Fact]
    public async Task A_worker_start_after_the_first_use_does_nothing_and_a_disposed_database_starts_nothing()
    {
        var root = Root("late");
        BuildDatabase(root);
        using (var database = Open(root))
        {
            var runs = 0;
            database.InitializationStarting = () => runs++;
            database.Initialize();
            await database.InitializeInBackground().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(1, runs);
        }

        var disposed = Open(root);
        disposed.Dispose();
        await disposed.InitializeInBackground().WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void The_app_checks_early_only_with_the_flag_and_the_container_owns_that_one_database()
    {
        var app = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "App.xaml.cs")).ReplaceLineEndings("\n");
        string[] order =
        [
            "paths = AppPaths.CreateForStartup();",
            "var logSink = new FileLoggerProvider(",
            "if (perfFlags.IsOn(PerfFlags.OverlappedIntegrityCheck))",
            "var earlyDatabase = new ScribeDatabase(paths, earlyDatabaseLog);",
            "builder.Services.Replace(ServiceDescriptor.Singleton(_ => earlyDatabase));",
            "_ = earlyDatabase.InitializeInBackground();",
            "_host = builder.Build();",
            "earlyDatabaseLog?.Attach(_host.Services.GetRequiredService<ILoggerFactory>());",
            "_host.Start();",
            "_diagnostics.WriteBanner(log);",
        ];
        var positions = order.Select(text => app.IndexOf(text, StringComparison.Ordinal)).ToArray();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);
        Assert.Single(Regex.Matches(app, Regex.Escape("InitializeInBackground()")));
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private sealed record Outcome(
        string? Failure, bool Repaired, bool SettingsLost, bool DictionaryLost, int DamagedCopies, string Tables);

    private string Root(string name) => Directory.CreateDirectory(Path.Combine(_folder.FullName, name)).FullName;

    private static ScribeDatabase Open(string root) => new(new AppPaths(root), NullLogger<ScribeDatabase>.Instance);

    private static async Task<Outcome> InitializeAsync(string root, bool background)
    {
        using var database = Open(root);
        string? failure = null;
        try
        {
            if (background)
            {
                await database.InitializeInBackground().WaitAsync(TimeSpan.FromSeconds(60));
            }

            database.Initialize();
        }
        catch (Exception ex)
        {
            failure = ex.GetType().FullName;
        }

        var tables = failure is null
            ? string.Join(
                ", ",
                new[] { "settings", "dictionary", "history", "audio_blobs", "snippets" }.Select(
                    table => $"{table}={DatabaseProbe.QueryInt64(database, $"SELECT count(*) FROM {table}")}"))
            : string.Empty;
        return new Outcome(
            failure,
            database.RepairedAtStartup,
            database.SettingsLostInRepair,
            database.DictionaryLostInRepair,
            Directory.GetFiles(root, "scribe.db.corrupt-*").Length,
            tables);
    }

    // Settings, forty dictations with every fourth one's recording, and ten of them deleted, so history and audio_blobs
    // span several pages and the freelist has a trunk.
    private static void BuildDatabase(string root)
    {
        using (var database = Open(root))
        {
            database.Initialize();
            new SettingsRepository(database).Save(AppSettings.CreateDefault());
            var history = new HistoryRepository(database);
            var ids = new List<long>();
            for (var i = 0; i < 50; i++)
            {
                var entry = new HistoryEntry(
                    0, new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero).AddMinutes(i), new string('w', 200) + i, 1000 + i, 20);
                var audio = i % 4 == 0 ? new CapturedAudio(Enumerable.Repeat(0.1f, 16_000).ToArray(), 16_000) : null;
                ids.Add(history.Add(entry, audio).Id);
            }

            foreach (var id in ids.Where((_, index) => index % 5 == 1))
            {
                history.Delete(id);
            }
        }

        DatabasePools.Release(new AppPaths(root));
    }

    private static void Damage(string root, string damage)
    {
        var path = Path.Combine(root, AppPaths.DatabaseFileName);
        if (damage == "none")
        {
            return;
        }

        if (damage == "garbage")
        {
            File.WriteAllText(path, "this is not a sqlite database, not even close, padding padding");
            return;
        }

        long pageSize;
        long page;
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA page_size;";
            pageSize = (long)command.ExecuteScalar()!;
            command.CommandText = damage switch
            {
                "history page" => "SELECT rootpage FROM sqlite_master WHERE name = 'history';",
                "audio_blobs page" => "SELECT rootpage FROM sqlite_master WHERE name = 'audio_blobs';",
                _ => "PRAGMA freelist_count;",
            };
            page = (long)command.ExecuteScalar()!;
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (damage == "freelist trunk")
        {
            Assert.True(page > 0, "the database must have a freelist");
            var header = new byte[100];
            file.ReadExactly(header);
            page = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(32, 4));

            // The trunk's next-trunk pointer, far past the end of the file.
            file.Position = (page - 1) * pageSize;
            file.Write([0x7F, 0xFF, 0xFF, 0xF0]);
            return;
        }

        // The b-tree page's type byte and header, overwritten.
        file.Position = (page - 1) * pageSize;
        file.Write(Enumerable.Repeat((byte)0xA5, 16).ToArray());
    }

    private string Copy(string source, string name)
    {
        var target = Root(name);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        return target;
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
