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

        // Proves the pinned SQLitePCLRaw.bundle_e_sqlite3 3.0.3 native (3.50.3) is the one in use,
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

        repo.Save(settings);
        var loaded = repo.Load();

        Assert.Equal("dev-42", loaded.InputDeviceId);
        Assert.Equal("Studio Mic", loaded.InputDeviceName);
        Assert.False(loaded.ShowOverlay);
        Assert.True(loaded.LaunchOnLogin);
        Assert.Equal(6, loaded.DecodeThreads);
        Assert.Equal(InjectionMethod.UnicodeType, loaded.InjectionMethod);
        Assert.Equal(settings.Hotkey, loaded.Hotkey);
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
}
