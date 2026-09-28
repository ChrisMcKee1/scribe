using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>
/// DATA-A-03 (<see cref="PerfFlags.ReuseSaveCommands"/>) and DATA-O-07 (<see cref="PerfFlags.DictionaryDiffSave"/>): a
/// dictionary or snippet save rebinding one prepared command per statement, and a dictionary save leaving out an UPDATE
/// that would write what the row already holds, end with exactly the table today's command per row leaves, and fail where
/// it fails with the same SQLite codes. Today's path is the oracle: twin databases take the same saves each way, over the
/// two counterexamples that broke the first diff, a third, and a seeded random corpus, and every column is compared as
/// SQLite stores it (bytes, storage class and value).
/// </summary>
public sealed class SaveCommandsEquivalenceTests
{
    private static readonly DictionaryRepository.SaveCommands[] Modes =
    [
        DictionaryRepository.SaveCommands.PerRow,
        DictionaryRepository.SaveCommands.Reused,
        DictionaryRepository.SaveCommands.ReusedSkippingUnchanged,
    ];

    [Fact]
    public void An_id_listed_twice_ends_with_its_last_entry_every_way()
    {
        // Astra's first counterexample: a diff against the rows as first read kept the first change.
        var outcomes = EachWay(
            seed: [(5, "alpha", "Y", 1, 1)],
            [new(5, "alpha", "X", true, true), new(5, "alpha", "Y", true, true)]);

        AssertAllSame(outcomes);
        Assert.Contains("5|alpha|text|Y|text|1|integer|1|integer", outcomes[0].Rows, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flag_stored_as_two_is_rewritten_as_one_every_way()
    {
        // Astra's second counterexample: read as a boolean, a stored 2 looked unchanged, stayed 2, and GetEnabled
        // (enabled = 1) dropped the row.
        var outcomes = EachWay(
            seed: [(7, "beta", "B", 1, 2), (8, "gamma", "G", 7, 1)],
            [new(7, "beta", "B", true, true), new(8, "gamma", "G", true, true)]);

        AssertAllSame(outcomes);
        Assert.Contains("7|beta|text|B|text|1|integer|1|integer", outcomes[0].Rows, StringComparison.Ordinal);
        Assert.Contains("8|gamma|text|G|text|1|integer|1|integer", outcomes[0].Rows, StringComparison.Ordinal);
    }

    [Fact]
    public void An_id_the_save_itself_inserts_is_updated_by_a_later_entry_every_way()
    {
        // The id an earlier entry of the same save inserts, named by a later entry: its UPDATE finds that new row.
        var outcomes = EachWay(
            seed: [(3, "delta", "D", 1, 1)],
            [new(3, "delta", "D", true, true), new(0, "epsilon", "E", true, true), new(4, "zeta", "Z", false, false)]);

        AssertAllSame(outcomes);
        Assert.Contains("4|zeta|text|Z|text|0|integer|0|integer", outcomes[0].Rows, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_stored_as_invalid_utf8_or_a_blob_or_a_flag_stored_as_text_is_rewritten_every_way()
    {
        var outcomes = EachWay(
            seed: [],
            [new(1, "\uFFFD(", "R", true, true), new(2, "blob", "R", true, true), new(3, "texty", "R", true, true)],
            rawSeed:
            [
                "INSERT INTO dictionary (id, pattern, replacement, whole_word, enabled) VALUES (1, CAST(X'C328' AS TEXT), 'R', 1, 1);",
                "INSERT INTO dictionary (id, pattern, replacement, whole_word, enabled) VALUES (2, X'626C6F62', 'R', 1, 1);",
                "INSERT INTO dictionary (id, pattern, replacement, whole_word, enabled) VALUES (3, 'texty', 'R', '1', 1);",
            ]);

        AssertAllSame(outcomes);
        Assert.DoesNotContain("blob|blob", outcomes[0].Rows, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rename_into_a_deleted_row_s_pattern_and_a_collision_fail_or_succeed_the_same_way()
    {
        var rename = EachWay(
            seed: [(1, "one", "1", 1, 1), (2, "two", "2", 1, 1)],
            [new(1, "two", "1", true, true)]);
        AssertAllSame(rename);
        Assert.Null(rename[0].Failure);

        var collision = EachWay(
            seed: [(1, "one", "1", 1, 1), (2, "two", "2", 1, 1)],
            [new(1, "two", "1", true, true), new(2, "two", "2", true, true)]);
        AssertAllSame(collision);
        Assert.Equal("19/2067", collision[0].Failure); // SQLITE_CONSTRAINT_UNIQUE; the save rolled back
    }

    [Fact]
    public void A_seeded_random_corpus_of_saves_ends_the_same_every_way()
    {
        var random = new Random(20260921);
        var failures = 0;
        for (var iteration = 0; iteration < 400; iteration++)
        {
            var (seed, entries) = RandomSave(random);
            var outcomes = EachWay(seed, entries);
            AssertAllSame(outcomes, $"iteration {iteration}");
            failures += outcomes[0].Failure is null ? 0 : 1;
        }

        // The corpus reaches both outcomes, so the comparison covers failures as well as successes.
        Assert.InRange(failures, 20, 380);
    }

    [Fact]
    public void Snippet_saves_end_the_same_either_way_over_a_seeded_random_corpus()
    {
        var random = new Random(4242);
        for (var iteration = 0; iteration < 300; iteration++)
        {
            var seedCount = random.Next(0, 8);
            var seed = new List<(long Id, string Phrase, string Template, long Enabled)>();
            for (var i = 0; i < seedCount; i++)
            {
                seed.Add((i + 1, "phrase" + i, "template " + random.Next(3), random.Next(4) == 0 ? 2 : random.Next(2)));
            }

            var snippets = new List<Snippet>();
            foreach (var row in seed)
            {
                switch (random.Next(5))
                {
                    case 0: break; // deleted
                    case 1: snippets.Add(new Snippet(row.Id, row.Phrase, row.Template, row.Enabled != 0)); break;
                    case 2: snippets.Add(new Snippet(row.Id, "phrase" + random.Next(8), row.Template, true)); break;
                    case 3: snippets.Add(new Snippet(row.Id, row.Phrase, "changed", false)); snippets.Add(new Snippet(row.Id, row.Phrase, "again", true)); break;
                    default: snippets.Add(new Snippet(row.Id, row.Phrase, row.Template + "!", row.Enabled == 0)); break;
                }
            }

            for (var i = random.Next(0, 3); i > 0; i--)
            {
                snippets.Add(new Snippet(0, "phrase" + random.Next(10), "new", true));
            }

            if (random.Next(4) == 0)
            {
                snippets.Add(new Snippet(seedCount + 1, "late", "late", true));
            }

            Shuffle(random, snippets);
            var perRow = ApplySnippets(seed, snippets, reuse: false);
            var reused = ApplySnippets(seed, snippets, reuse: true);
            Assert.True(perRow == reused, $"iteration {iteration}: {perRow} against {reused}");
        }
    }

    [Fact]
    public void Adding_seeding_and_disabling_through_the_repository_end_the_same_either_way()
    {
        var added = Twin((repository, _) =>
        {
            var persisted = repository.AddRange([new(0, "a", "A", true, true), new(0, "b", "B", false, true)]);
            return string.Join(",", persisted.Select(entry => entry.Id));
        });
        Assert.Equal(added.PerRow, added.Reused);

        var duplicate = Twin((repository, _) => repository.AddRange([new(0, "a", "A", true, true), new(0, "a", "B", true, true)]).Count.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(duplicate.PerRow, duplicate.Reused);
        Assert.Contains("19/2067", duplicate.PerRow, StringComparison.Ordinal);

        var seeded = Twin((repository, _) =>
            repository.SeedIfEmpty([new(0, "a", "A", true, true), new(0, "a", "B", true, true), new(0, "c", "C", false, false)])
                .ToString(CultureInfo.InvariantCulture));
        Assert.Equal(seeded.PerRow, seeded.Reused);

        var disabled = Twin((repository, database) =>
        {
            repository.AddRange([new(0, "a", "A", true, true), new(0, "b", "B", true, true), new(0, "c", "C", true, false)]);
            Execute(database, "UPDATE dictionary SET replacement = 'edited' WHERE pattern = 'b';");
            return repository.DisableUnmodifiedEntries([new(0, "a", "A", true, true), new(0, "b", "B", true, true), new(0, "c", "C", true, true)])
                .ToString(CultureInfo.InvariantCulture);
        });
        Assert.Equal(disabled.PerRow, disabled.Reused);
    }

    [Fact]
    public void A_settings_save_carries_the_flags_to_its_dictionary_and_snippet_rows()
    {
        string Save(PerfFlags flags)
        {
            using var database = ScribeDatabase.CreateInMemory();
            database.Initialize();
            SeedDictionary(database, [(1, "one", "1", 1, 2), (2, "two", "2", 1, 1)], []);
            Execute(database, "INSERT INTO snippets (id, phrase, template, enabled) VALUES (1, 'sig', 'Regards', 2);");
            var settings = new SettingsRepository(database, flags);
            settings.SaveBundle(
                AppSettings.CreateDefault(),
                [new(1, "one", "1", true, true), new(0, "three", "3", true, true)],
                [new Snippet(1, "sig", "Regards", true), new Snippet(0, "addr", "Street", false)]);
            return Dump(database) + "\n" + DumpSnippets(database);
        }

        var today = Save(PerfFlags.None);
        Assert.Equal(today, Save(PerfFlags.Parse(PerfFlags.ReuseSaveCommands)));
        Assert.Equal(today, Save(PerfFlags.Parse(PerfFlags.DictionaryDiffSave)));
        Assert.Equal(today, Save(PerfFlags.Parse($"{PerfFlags.ReuseSaveCommands},{PerfFlags.DictionaryDiffSave}")));
    }

    [Fact]
    public void The_flags_pick_the_way_a_save_writes()
    {
        Assert.Equal(DictionaryRepository.SaveCommands.PerRow, DictionaryRepository.SaveCommandsFor(null));
        Assert.Equal(DictionaryRepository.SaveCommands.PerRow, DictionaryRepository.SaveCommandsFor(PerfFlags.None));
        Assert.Equal(
            DictionaryRepository.SaveCommands.Reused,
            DictionaryRepository.SaveCommandsFor(PerfFlags.Parse(PerfFlags.ReuseSaveCommands)));
        Assert.Equal(
            DictionaryRepository.SaveCommands.ReusedSkippingUnchanged,
            DictionaryRepository.SaveCommandsFor(PerfFlags.Parse(PerfFlags.DictionaryDiffSave)));
        Assert.Equal(
            DictionaryRepository.SaveCommands.ReusedSkippingUnchanged,
            DictionaryRepository.SaveCommandsFor(PerfFlags.Parse($"{PerfFlags.DictionaryDiffSave};{PerfFlags.ReuseSaveCommands}")));
    }

    // ---- Harness ----------------------------------------------------------------------------------------------------

    private sealed record Outcome(string? Failure, string Rows);

    private static Outcome[] EachWay(
        IEnumerable<(long Id, string Pattern, string Replacement, object WholeWord, object Enabled)> seed,
        IReadOnlyList<DictionaryEntry> entries,
        string[]? rawSeed = null) =>
        [.. Modes.Select(mode =>
        {
            using var database = ScribeDatabase.CreateInMemory();
            database.Initialize();
            SeedDictionary(database, seed, rawSeed ?? []);
            string? failure = null;
            using (var connection = database.Open())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    DictionaryRepository.SaveAll(connection, transaction, entries, mode);
                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    failure = Describe(ex);
                }
            }

            return new Outcome(failure, Dump(database));
        })];

    private static void AssertAllSame(Outcome[] outcomes, string? context = null)
    {
        for (var i = 1; i < outcomes.Length; i++)
        {
            Assert.True(
                outcomes[0] == outcomes[i],
                $"{context} {Modes[0]}: {outcomes[0]}\n{Modes[i]}: {outcomes[i]}");
        }
    }

    private static (List<(long, string, string, object, object)> Seed, List<DictionaryEntry> Entries) RandomSave(Random random)
    {
        var seed = new List<(long, string, string, object, object)>();
        var count = random.Next(0, 12);
        for (var i = 0; i < count; i++)
        {
            object wholeWord = random.Next(6) switch { 0 => 0L, 1 => 7L, _ => 1L };
            object enabled = random.Next(7) switch { 0 => 0L, 1 => 2L, 2 => "1", _ => 1L };
            seed.Add((i + 1, "p" + i, "r" + random.Next(3), wholeWord, enabled));
        }

        var entries = new List<DictionaryEntry>();
        foreach (var (id, pattern, replacement, wholeWord, enabled) in seed)
        {
            var asRead = new DictionaryEntry(id, pattern, replacement, !Equals(wholeWord, 0L), !Equals(enabled, 0L));
            switch (random.Next(8))
            {
                case 0: break; // deleted
                case 1: entries.Add(asRead with { Replacement = "changed" + random.Next(2) }); break;
                case 2: entries.Add(asRead with { Pattern = "p" + random.Next(count + 3) }); break;
                case 3: entries.Add(asRead with { Enabled = !asRead.Enabled }); break;
                case 4: entries.Add(asRead with { Replacement = "first" }); entries.Add(asRead); break;
                default: entries.Add(asRead); break;
            }
        }

        for (var i = random.Next(0, 4); i > 0; i--)
        {
            entries.Add(new DictionaryEntry(0, "p" + random.Next(count + 4), "new", random.Next(2) == 0, true));
        }

        if (random.Next(3) == 0)
        {
            entries.Add(new DictionaryEntry(count + 1 + random.Next(2), "late" + random.Next(2), "late", true, true)); // an id this save may insert
        }

        if (random.Next(5) == 0)
        {
            entries.Add(new DictionaryEntry(1000 + random.Next(5), "missing", "missing", true, true));
        }

        Shuffle(random, entries);
        return (seed, entries);
    }

    private static void SeedDictionary(
        ScribeDatabase database, IEnumerable<(long Id, string Pattern, string Replacement, object WholeWord, object Enabled)> rows, string[] raw)
    {
        using var connection = database.Open();
        foreach (var row in rows)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO dictionary (id, pattern, replacement, whole_word, enabled) VALUES ($id, $p, $r, $w, $e);";
            command.Parameters.AddWithValue("$id", row.Id);
            command.Parameters.AddWithValue("$p", row.Pattern);
            command.Parameters.AddWithValue("$r", row.Replacement);
            command.Parameters.AddWithValue("$w", row.WholeWord);
            command.Parameters.AddWithValue("$e", row.Enabled);
            command.ExecuteNonQuery();
        }

        foreach (var sql in raw)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    private static string ApplySnippets(
        List<(long Id, string Phrase, string Template, long Enabled)> seed, IReadOnlyList<Snippet> snippets, bool reuse)
    {
        using var database = ScribeDatabase.CreateInMemory();
        database.Initialize();
        foreach (var row in seed)
        {
            Execute(database, string.Create(
                CultureInfo.InvariantCulture,
                $"INSERT INTO snippets (id, phrase, template, enabled) VALUES ({row.Id}, '{row.Phrase}', '{row.Template}', {row.Enabled});"));
        }

        string? failure = null;
        using (var connection = database.Open())
        using (var transaction = connection.BeginTransaction())
        {
            try
            {
                SnippetRepository.SaveAll(connection, transaction, snippets, reuse);
                transaction.Commit();
            }
            catch (Exception ex)
            {
                failure = Describe(ex);
            }
        }

        return (failure ?? "committed") + "\n" + DumpSnippets(database);
    }

    private static (string PerRow, string Reused) Twin(Func<DictionaryRepository, ScribeDatabase, string> act)
    {
        string Run(PerfFlags flags)
        {
            using var database = ScribeDatabase.CreateInMemory();
            database.Initialize();
            string result;
            try
            {
                result = act(new DictionaryRepository(database, flags), database);
            }
            catch (Exception ex)
            {
                result = Describe(ex);
            }

            return result + "\n" + Dump(database);
        }

        return (Run(PerfFlags.None), Run(PerfFlags.Parse(PerfFlags.ReuseSaveCommands)));
    }

    private static string Describe(Exception ex) => ex is SqliteException sqlite
        ? string.Create(CultureInfo.InvariantCulture, $"{sqlite.SqliteErrorCode}/{sqlite.SqliteExtendedErrorCode}")
        : ex.GetType().FullName!;

    private static string Dump(ScribeDatabase database)
    {
        var builder = new StringBuilder();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, CAST(pattern AS BLOB), typeof(pattern), CAST(replacement AS BLOB), typeof(replacement),
                   quote(whole_word), typeof(whole_word), quote(enabled), typeof(enabled)
            FROM dictionary ORDER BY id;
            """;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                builder.Append(reader.GetInt64(0)).Append('|')
                    .Append(Text(reader, 1)).Append('|').Append(reader.GetString(2)).Append('|')
                    .Append(Text(reader, 3)).Append('|').Append(reader.GetString(4)).Append('|')
                    .Append(reader.GetString(5)).Append('|').Append(reader.GetString(6)).Append('|')
                    .Append(reader.GetString(7)).Append('|').Append(reader.GetString(8)).Append('\n');
            }
        }

        command.CommandText = "SELECT group_concat(name || '=' || seq, ',') FROM sqlite_sequence;";
        builder.Append("seq ").Append(command.ExecuteScalar());
        return builder.ToString();
    }

    private static string DumpSnippets(ScribeDatabase database)
    {
        var builder = new StringBuilder();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, hex(phrase), typeof(phrase), hex(template), quote(enabled), typeof(enabled) FROM snippets ORDER BY id;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                builder.Append(reader.GetValue(i)).Append('|');
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    // Stored bytes, shown as text where they are valid UTF-8 and as hex otherwise, so a failure reads plainly.
    private static string Text(SqliteDataReader reader, int ordinal)
    {
        var bytes = (byte[])reader.GetValue(ordinal);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return "0x" + Convert.ToHexString(bytes);
        }
    }

    private static void Execute(ScribeDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Shuffle<T>(Random random, List<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
