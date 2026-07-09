using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Xunit;

namespace Scribe.Core.Tests;

public class PersistenceTests
{
    [Fact]
    public void Database_loads_the_CVE_patched_native_sqlite()
    {
        using var db = ScribeDatabase.CreateInMemory();
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";

        var version = (string?)command.ExecuteScalar();

        // Proves the pinned SQLitePCLRaw.bundle_e_sqlite3 3.0.3 native (3.50.4) is the one in use,
        // not an older transitive bundle flagged by CVE-2025-6965.
        Assert.Equal(ScribeDatabase.ExpectedSqliteVersion, version);
    }

    [Fact]
    public void Settings_round_trip_preserves_values_and_hotkey()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);

        var settings = AppSettings.CreateDefault();
        settings.InputDeviceId = "dev-42";
        settings.InputDeviceName = "Studio Mic";
        settings.ShowOverlay = false;
        settings.LaunchOnLogin = true;
        settings.DecodeThreads = 6;
        settings.InjectionMethod = InjectionMethod.UnicodeType;
        settings.Hotkey = new HotkeyBinding(0x20, KeyModifiers.Control | KeyModifiers.Shift, HotkeyMode.Toggle, Suppress: false, "Ctrl+Shift+Space");
        settings.AiCleanupPromptStyle = Scribe.Core.Cleanup.CleanupPromptStyle.Local;
        settings.AiCleanupFrontierPrompt = "my custom frontier prompt";
        settings.AiCleanupLocalPrompt = "my custom local prompt";

        repo.Save(settings);
        var loaded = repo.Load();

        Assert.Equal("dev-42", loaded.InputDeviceId);
        Assert.Equal("Studio Mic", loaded.InputDeviceName);
        Assert.False(loaded.ShowOverlay);
        Assert.True(loaded.LaunchOnLogin);
        Assert.Equal(6, loaded.DecodeThreads);
        Assert.Equal(InjectionMethod.UnicodeType, loaded.InjectionMethod);
        Assert.Equal(settings.Hotkey, loaded.Hotkey);
        Assert.Equal(Scribe.Core.Cleanup.CleanupPromptStyle.Local, loaded.AiCleanupPromptStyle);
        Assert.Equal("my custom frontier prompt", loaded.AiCleanupFrontierPrompt);
        Assert.Equal("my custom local prompt", loaded.AiCleanupLocalPrompt);
    }

    [Fact]
    public void Settings_round_trip_preserves_enabled_dictionary_libraries()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);

        var settings = AppSettings.CreateDefault();
        settings.EnabledDictionaryLibraryIds.Add("microsoft-azure");
        settings.EnabledDictionaryLibraryIds.Add("software-development");
        repo.Save(settings);

        var loaded = repo.Load();

        Assert.Equal(new[] { "microsoft-azure", "software-development" }, loaded.EnabledDictionaryLibraryIds);
    }

    [Fact]
    public void Clone_deep_copies_enabled_dictionary_libraries()
    {
        var settings = AppSettings.CreateDefault();
        settings.EnabledDictionaryLibraryIds.Add("microsoft-azure");

        var clone = settings.Clone();
        clone.EnabledDictionaryLibraryIds.Add("data-and-ai");

        Assert.Single(settings.EnabledDictionaryLibraryIds); // original list is not shared
        Assert.Equal(2, clone.EnabledDictionaryLibraryIds.Count);
    }

    [Fact]
    public void Settings_load_returns_defaults_when_empty()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);

        var loaded = repo.Load();

        Assert.Equal(HotkeyBinding.Default, loaded.Hotkey);
        Assert.True(loaded.ShowOverlay);
    }

    [Fact]
    public void Dictionary_add_update_delete_round_trips()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new DictionaryRepository(db);

        var added = repo.Add(DictionaryEntry.New("re back", "ReBAC"));
        Assert.True(added.Id > 0);

        repo.Update(added with { Replacement = "ReBAC", Enabled = false });
        var all = repo.GetAll();
        Assert.Single(all);
        Assert.False(all[0].Enabled);
        Assert.Empty(repo.GetEnabled());

        repo.Delete(added.Id);
        Assert.Empty(repo.GetAll());
    }

    [Fact]
    public void Dictionary_save_all_inserts_updates_and_deletes_in_one_pass()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new DictionaryRepository(db);

        var kept = repo.Add(DictionaryEntry.New("azure", "Azure"));
        var removed = repo.Add(DictionaryEntry.New("foundry", "Foundry"));

        repo.SaveAll(
        [
            kept with { Replacement = "AZURE" },          // update
            DictionaryEntry.New("rebac", "ReBAC"),        // insert
            // 'removed' omitted -> delete
        ]);

        var all = repo.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Equal("AZURE", all.Single(e => e.Id == kept.Id).Replacement);
        Assert.DoesNotContain(all, e => e.Id == removed.Id);
        Assert.Contains(all, e => e.Pattern == "rebac" && e.Id > 0);
    }

    [Fact]
    public void Dictionary_save_all_rolls_back_completely_on_a_duplicate_pattern()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new DictionaryRepository(db);

        var existing = repo.Add(DictionaryEntry.New("azure", "Azure"));

        // The second new row violates ux_dictionary_pattern; nothing from the batch may stick.
        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => repo.SaveAll(
        [
            existing,
            DictionaryEntry.New("rebac", "ReBAC"),
            DictionaryEntry.New("rebac", "REBAC"),
        ]));

        var all = repo.GetAll();
        Assert.Single(all);
        Assert.Equal(existing.Id, all[0].Id);
    }

    [Fact]
    public void Dictionary_save_all_supports_swapping_a_deleted_rows_pattern_onto_a_new_row()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new DictionaryRepository(db);

        var old = repo.Add(DictionaryEntry.New("kubernetes", "K8s"));

        // Delete the old row and reuse its pattern on a fresh one in the same save.
        repo.SaveAll([DictionaryEntry.New("kubernetes", "Kubernetes")]);

        var all = repo.GetAll();
        Assert.Single(all);
        Assert.NotEqual(old.Id, all[0].Id);
        Assert.Equal("Kubernetes", all[0].Replacement);
    }

    [Fact]
    public void Dictionary_seed_only_runs_once()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new DictionaryRepository(db);

        var seed = new[]
        {
            DictionaryEntry.New("azure", "Azure"),
            DictionaryEntry.New("foundry", "Foundry"),
        };

        Assert.Equal(2, repo.SeedIfEmpty(seed));
        Assert.Equal(0, repo.SeedIfEmpty(seed)); // table no longer empty
        Assert.Equal(2, repo.GetAll().Count);
    }

    [Fact]
    public void History_stores_entries_newest_first_with_audio_blob()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new HistoryRepository(db);

        var samples = new[] { -1f, -0.5f, 0f, 0.5f, 1f };
        var blobId = repo.AddAudioBlob(new CapturedAudio(samples, 16000));

        var older = repo.Add(new HistoryEntry(0, DateTimeOffset.UtcNow.AddMinutes(-5), "first", 1000, 80, "Code.exe"));
        var newer = repo.Add(new HistoryEntry(0, DateTimeOffset.UtcNow, "second", 2000, 120, "chrome.exe", blobId));

        Assert.True(older.Id > 0 && newer.Id > older.Id);

        var recent = repo.GetRecent(10);
        Assert.Equal(2, recent.Count);
        Assert.Equal("second", recent[0].Text);
        Assert.Equal("first", recent[1].Text);
        Assert.Equal(blobId, recent[0].AudioBlobId);

        var audio = repo.GetAudio(blobId);
        Assert.NotNull(audio);
        Assert.Equal(16000, audio!.SampleRate);
        Assert.Equal(samples, audio.Samples);
    }

    [Fact]
    public void History_clear_removes_rows_and_blobs()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new HistoryRepository(db);

        var blobId = repo.AddAudioBlob(new CapturedAudio(new[] { 0.1f, 0.2f }, 16000));
        repo.Add(new HistoryEntry(0, DateTimeOffset.UtcNow, "x", 500, 40, AudioBlobId: blobId));

        repo.Clear();

        Assert.Empty(repo.GetRecent());
        Assert.Null(repo.GetAudio(blobId));
    }

    // --- Cleanup failure log (Feature A) -------------------------------------------------

    [Fact]
    public void Failure_log_add_assigns_id_and_truncates_a_long_sample()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var log = new CleanupFailureLog(db);

        var longSample = new string('a', 500);
        var saved = log.Add(CleanupFailure.New("AI cleanup timed out.", "FoundryLocal", "qwen2.5-1.5b", longSample));

        Assert.True(saved.Id > 0);
        Assert.Equal(1, log.Count());

        var recent = log.GetRecent();
        Assert.Single(recent);
        Assert.Equal("AI cleanup timed out.", recent[0].Reason);
        Assert.Equal("qwen2.5-1.5b", recent[0].Model);
        Assert.Equal(200, recent[0].Sample!.Length); // capped at SampleMaxChars
    }

    [Fact]
    public void Failure_log_returns_newest_first()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var log = new CleanupFailureLog(db);

        log.Add(CleanupFailure.New("first") with { TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-5) });
        log.Add(CleanupFailure.New("second") with { TimestampUtc = DateTimeOffset.UtcNow });

        var recent = log.GetRecent();
        Assert.Equal(2, recent.Count);
        Assert.Equal("second", recent[0].Reason);
        Assert.Equal("first", recent[1].Reason);
    }

    [Fact]
    public void Failure_log_clear_empties_the_table()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var log = new CleanupFailureLog(db);

        log.Add(CleanupFailure.New("boom"));
        log.Add(CleanupFailure.New("bang"));
        Assert.Equal(2, log.Count());

        log.Clear();

        Assert.Equal(0, log.Count());
        Assert.Empty(log.GetRecent());
    }

    [Fact]
    public void Failure_log_prune_removes_only_entries_older_than_the_cutoff()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var log = new CleanupFailureLog(db);

        log.Add(CleanupFailure.New("stale") with { TimestampUtc = DateTimeOffset.UtcNow.AddDays(-8) });
        log.Add(CleanupFailure.New("fresh") with { TimestampUtc = DateTimeOffset.UtcNow.AddHours(-1) });

        var removed = log.PruneOlderThan(DateTimeOffset.UtcNow.AddDays(-7));

        Assert.Equal(1, removed);
        var recent = log.GetRecent();
        Assert.Single(recent);
        Assert.Equal("fresh", recent[0].Reason);
    }
}

