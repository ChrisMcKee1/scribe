using System.Globalization;
using Microsoft.Data.Sqlite;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class HistoryRepositorySearchMemoryTests : IClassFixture<HistoryRepositorySearchMemoryTests.SeededHistory>
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T08:00:00Z", CultureInfo.InvariantCulture);

    private static readonly string?[] Apps =
    [
        "WINWORD", "ms-teams", "OUTLOOK.EXE", "notepad", null, "  ", "Code", "olk .exe", "wt.exe.exe", "Some App", "msedge",
    ];

    private static readonly string[] Words =
    [
        "needle", "word", "team", "budget", "100%", "ABC_123", "back\\slash", "outlook", "Été", "notes", "plan", "code",
    ];

    private readonly SeededHistory _seeded;

    public HistoryRepositorySearchMemoryTests(SeededHistory seeded) => _seeded = seeded;

    public static TheoryData<string, int> Queries => new()
    {
        { "zyxwvut", 200 }, { "needle", 200 }, { "needle", 3 }, { "Word", 200 }, { "Teams", 200 }, { "team", 5 },
        { "Outlook", 200 }, { "100%", 200 }, { "ABC_123", 200 }, { "back\\slash", 200 }, { " word ", 200 }, { "été", 200 },
        { "o", 1 }, { "e", 200 }, { "Terminal", 200 }, { "Some App", 200 }, { "Edge", 50 }, { "needle", 1_000 },
    };

    [Theory]
    [MemberData(nameof(Queries))]
    public void Search_returns_exactly_what_the_previous_loop_returned(string query, int limit)
    {
        var expected = OracleSearch(_seeded.Database, query, limit);
        var actual = _seeded.Repository.Search(query, limit);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("timestamp_utc", "'not a time'", "planted")]
    [InlineData("timestamp_utc", "'not a time'", "Notepad")]
    [InlineData("timestamp_utc", "'not a time'", "nothing matches this")]
    [InlineData("audio_ms", "9999999999", "nothing matches this")]
    [InlineData("decode_ms", "-9999999999", "planted")]
    [InlineData("ai_rating", "9999999999", "nothing matches this")]
    [InlineData("cleanup_ms", "9999999999", "nothing matches this")]
    public void A_row_that_cannot_be_read_fails_the_search_as_it_did(string column, string value, string query)
    {
        using var database = ScribeDatabase.CreateInMemory();
        var repository = Seed(database, rows: 40);

        // The newest row, so it is read first; its app keeps it among the rows SQLite returns whether or not it matches.
        using (var connection = database.Open())
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO history (timestamp_utc, text, audio_ms, decode_ms, target_app) " +
                "VALUES ('2099-01-01T00:00:00.0000000+00:00', 'planted row', 1, 1, 'notepad');";
            insert.ExecuteNonQuery();
            insert.CommandText = $"UPDATE history SET {column} = {value} WHERE text = 'planted row';";
            insert.ExecuteNonQuery();
        }

        var expected = Record.Exception(() => OracleSearch(database, query, 200));
        var actual = Record.Exception(() => repository.Search(query, 200));

        Assert.Equal(expected?.GetType(), actual?.GetType());
        if (column == "timestamp_utc")
        {
            Assert.IsType<FormatException>(actual);
        }
    }

    [Fact]
    public void A_malformed_row_after_the_last_result_is_never_read()
    {
        using var database = ScribeDatabase.CreateInMemory();
        var repository = Seed(database, rows: 40);
        using (var connection = database.Open())
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO history (timestamp_utc, text, audio_ms, decode_ms, target_app) " +
                "VALUES ('0000 oldest', 'needle oldest', 1, 1, 'notepad');";
            insert.ExecuteNonQuery();
        }

        Assert.Equal(OracleSearch(database, "needle", 2), repository.Search("needle", 2));
    }

    // In the collection that runs alone: no other test allocates on this thread while it measures.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Fact]
        public void A_search_no_longer_reads_the_transcript_of_every_row_it_passes_over()
        {
            using var database = ScribeDatabase.CreateInMemory();
            var repository = Seed(database, rows: 2_000);
            _ = repository.Search("zyxwvut", 200);
            _ = OracleSearch(database, "zyxwvut", 200);

            var oracleBefore = GC.GetAllocatedBytesForCurrentThread();
            _ = OracleSearch(database, "zyxwvut", 200);
            var oracleBytes = GC.GetAllocatedBytesForCurrentThread() - oracleBefore;

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var results = repository.Search("zyxwvut", 200);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            // A ratio, not a size: what SQLite and the reader allocate per row is theirs. Every row here still costs its
            // timestamp and app name; the transcript, the model id and the entry were the rest, and they are gone.
            Assert.Empty(results);
            Assert.True(
                allocated * 3 < oracleBytes,
                $"{allocated} bytes against the previous loop's {oracleBytes} for 2,000 rows. During it: {during}.");
        }
    }

    private static HistoryRepository Seed(ScribeDatabase database, int rows)
    {
        var repository = new HistoryRepository(database);
        var random = new Random(rows);
        for (var i = 0; i < rows; i++)
        {
            var text = string.Join(' ', Enumerable.Range(0, 6 + random.Next(30)).Select(_ => Words[random.Next(Words.Length)]));
            repository.Add(new HistoryEntry(
                0,
                Start.AddMinutes(i % 7 == 0 ? i - 1 : i),
                text,
                1_000 + random.Next(30_000),
                100 + random.Next(2_000),
                random.Next(3) == 0 ? null : random.Next(5_000),
                Apps[random.Next(Apps.Length)],
                TranscriptionModelId: random.Next(4) == 0 ? null : "parakeet-tdt-0.6b-v3-int8"));
        }

        _ = repository.GetRecent(1);
        return repository;
    }

    // HistoryRepository.Search's statement and loop as they were before the change: every row the statement returns is read
    // into an entry, and only then tested.
    private static List<HistoryEntry> OracleSearch(ScribeDatabase database, string query, int limit)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, timestamp_utc, text, audio_ms, decode_ms, cleanup_ms,
                   target_app, audio_blob_id, transcription_model_id, ai_rating, text LIKE $query ESCAPE '\' AS text_match
             FROM history
            WHERE text LIKE $query ESCAPE '\'
                OR target_app IS NOT NULL
            ORDER BY timestamp_utc DESC, id DESC;
            """;
        command.Parameters.AddWithValue("$query", $"%{EscapeLike(query.Trim())}%");

        var results = new List<HistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read() && results.Count < limit)
        {
            var entry = ReadHistoryEntry(reader);
            if (reader.GetInt64(10) != 0 || FriendlyAppMatches(entry, query))
            {
                results.Add(entry);
            }
        }

        return results;
    }

    private static HistoryEntry ReadHistoryEntry(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? AiRating.Unrated : (AiRating)reader.GetInt32(9));

    private static bool FriendlyAppMatches(HistoryEntry entry, string query) =>
        !string.IsNullOrWhiteSpace(entry.TargetApp) &&
        AppDisplayName.For(entry.TargetApp).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string EscapeLike(string query) =>
        query.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    public sealed class SeededHistory : IDisposable
    {
        public SeededHistory()
        {
            Database = ScribeDatabase.CreateInMemory();
            Repository = Seed(Database, rows: 400);
        }

        public ScribeDatabase Database { get; }

        public HistoryRepository Repository { get; }

        public void Dispose() => Database.Dispose();
    }
}
