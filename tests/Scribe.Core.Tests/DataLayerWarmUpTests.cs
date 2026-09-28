using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>
/// DATA-O-01 (<see cref="PerfFlags.DataLayerWarmUp"/>): the data layer's first use is compiled on a worker against a
/// throwaway in-memory database, touching no file, never faulting, and changing nothing the real database decides. The
/// provider's one-time initialization runs once and holds a second caller until it has returned.
/// </summary>
public sealed class DataLayerWarmUpTests
{
    [Fact]
    public async Task The_warm_up_ends_without_a_fault()
    {
        var warmUp = DataLayerWarmUp.Start();

        await warmUp.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(TaskStatus.RanToCompletion, warmUp.Status);
    }

    [Fact]
    public void The_warm_up_uses_an_in_memory_database_touches_no_path_and_starts_nothing_that_outlives_it()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.Core", "Persistence", "DataLayerWarmUp.cs"));
        var code = string.Join('\n', source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.Contains("using var database = ScribeDatabase.CreateInMemory();", code, StringComparison.Ordinal);
        Assert.Contains("catch (Exception)", code, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 {
                     "new ScribeDatabase(", "AppPaths", "File.", "Directory.", "Path.", "HistoryRepository(", "SnippetRepository(",
                     "HistoryDeletionNotifier", "Log(", "LogInformation", "LogWarning", "Environment.",
                 })
        {
            Assert.DoesNotContain(forbidden, code.Replace("<see cref=", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_warm_up_is_started_only_by_its_flag_first_and_never_awaited()
    {
        var app = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "App.xaml.cs")).ReplaceLineEndings("\n");

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(app, "DataLayerWarmUp\\.Start\\(\\)"));
        Assert.Contains(
            "if (perfFlags.IsOn(PerfFlags.DataLayerWarmUp))\n        {\n            _ = DataLayerWarmUp.Start();\n        }",
            app,
            StringComparison.Ordinal);
        var start = app.IndexOf("_ = DataLayerWarmUp.Start();", StringComparison.Ordinal);
        Assert.True(start < app.IndexOf("paths = AppPaths.CreateForStartup();", StringComparison.Ordinal));
        Assert.True(start < app.IndexOf("var builder = Host.CreateApplicationBuilder();", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_one_run_happens_and_every_other_caller_waits_until_it_has_returned()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var once = new RunOnce();
            var runs = 0;
            var finished = 0;
            var sawUnfinished = 0;
            using var barrier = new Barrier(2);

            void Caller()
            {
                barrier.SignalAndWait();
                once.Run(() =>
                {
                    Interlocked.Increment(ref runs);
                    Thread.SpinWait(20_000);
                    Volatile.Write(ref finished, 1);
                });

                if (Volatile.Read(ref finished) == 0)
                {
                    Interlocked.Increment(ref sawUnfinished);
                }
            }

            var first = new Thread(Caller);
            var second = new Thread(Caller);
            first.Start();
            second.Start();
            Assert.True(first.Join(TimeSpan.FromSeconds(30)));
            Assert.True(second.Join(TimeSpan.FromSeconds(30)));

            Assert.Equal(1, runs);
            Assert.Equal(0, sawUnfinished);
            Assert.True(once.Started);
        }
    }

    [Fact]
    public void A_run_that_throws_is_not_run_again_and_the_callers_after_it_go_on_without_it()
    {
        var once = new RunOnce();
        var later = 0;

        Assert.Throws<InvalidOperationException>(() => once.Run(() => throw new InvalidOperationException("no native library")));
        once.Run(() => later++);
        once.Run(() => later++);

        Assert.True(once.Started);
        Assert.Equal(0, later);
    }

    [Fact]
    public void After_a_warm_up_a_real_database_still_decides_its_own_state()
    {
        DataLayerWarmUp.Run();

        // A first run: no document, nothing lost, the defaults.
        using (var folder = new TempDatabaseFolder())
        {
            using var database = folder.Open();
            database.Initialize();
            var settings = new SettingsRepository(database);
            var loaded = settings.Load();

            Assert.False(settings.LastLoadFailed);
            Assert.False(database.RepairedAtStartup);
            Assert.Equal(Scribe.Core.Models.AppSettings.CreateDefault().Hotkey.VirtualKey, loaded.Hotkey.VirtualKey);
            folder.ReleasePooledConnections();
        }

        // A file a newer build wrote is still refused as it was, and left alone.
        using (var folder = new TempDatabaseFolder())
        {
            using (var connection = new SqliteConnection($"Data Source={folder.DatabasePath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE future_table (id INTEGER PRIMARY KEY); PRAGMA user_version=8;";
                command.ExecuteNonQuery();
            }

            using (var database = new ScribeDatabase(new AppPaths(folder.Root), NullLogger<ScribeDatabase>.Instance))
            {
                var refused = Assert.Throws<NewerDatabaseSchemaException>(() => database.Initialize());
                Assert.Equal(8, refused.DatabaseVersion);
            }

            folder.ReleasePooledConnections();
        }
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
