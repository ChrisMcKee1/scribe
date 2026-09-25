using System.Text.Json.Nodes;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>
/// <see cref="AppSettings.AddSpaceAfterDictation"/> is on for every install, new or upgraded (the maintainer's call for
/// issue #78), so its default is the property initializer rather than a first-run opt-in in
/// <see cref="AppSettings.CreateDefault"/>: a document written before the key existed reads as on, a saved off stays
/// off, and an older build that drops the key on its own save hands this version a document that reads as on again.
/// </summary>
public sealed class AddSpaceAfterDictationSettingsTests
{
    [Fact]
    public void A_new_install_adds_the_space()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);

        var loaded = repo.Load();

        Assert.False(repo.LastLoadFailed);
        Assert.True(loaded.AddSpaceAfterDictation);
        Assert.True(AppSettings.CreateDefault().AddSpaceAfterDictation);
        Assert.True(new AppSettings().AddSpaceAfterDictation);
    }

    [Fact]
    public void An_upgraded_install_whose_document_has_no_such_key_adds_the_space()
    {
        // A 0.4.3 document: the keys of its day, this one not among them.
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        repo.Set(
            "app_settings",
            """{"showOverlay":false,"hasCompletedFirstRun":true,"shiftEnterLineBreaks":false,"newlineHandling":"KeepNewlines"}""");

        var loaded = repo.Load();

        Assert.False(repo.LastLoadFailed);
        Assert.False(loaded.ShowOverlay); // the stored document was read, not replaced
        Assert.False(loaded.ShiftEnterLineBreaks);
        Assert.True(loaded.AddSpaceAfterDictation);
    }

    [Fact]
    public void Turned_off_it_stays_off_across_a_restart_and_a_partial_update()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        var settings = AppSettings.CreateDefault();
        settings.AddSpaceAfterDictation = false;

        repo.Save(settings);
        repo.Update(stored => stored.HasCompletedFirstRun = true); // what the welcome records, a read-modify-write

        Assert.Contains("\"addSpaceAfterDictation\":false", repo.Get("app_settings"), StringComparison.Ordinal);
        var reloaded = new SettingsRepository(db).Load();
        Assert.False(reloaded.AddSpaceAfterDictation);
        Assert.True(reloaded.HasCompletedFirstRun);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_settings_window_s_save_stores_the_switch_as_shown(bool shown)
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        var stored = AppSettings.CreateDefault();
        stored.AddSpaceAfterDictation = !shown;
        repo.Save(stored);

        // Save writes every field onto the window's document and stores it whole; no tray intent is involved.
        var window = repo.Load();
        window.AddSpaceAfterDictation = shown;
        repo.SaveBundle(window, dictionaryEntries: null, snippets: null);

        Assert.Equal(shown, new SettingsRepository(db).Load().AddSpaceAfterDictation);
    }

    [Fact]
    public void An_older_build_that_drops_the_key_hands_back_a_document_that_reads_as_on()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        var settings = AppSettings.CreateDefault();
        settings.AddSpaceAfterDictation = false;
        settings.ShowOverlay = false;
        repo.Save(settings);

        // What an older build writes when it saves: the document it read, less every key it does not know.
        var document = JsonNode.Parse(repo.Get("app_settings")!)!.AsObject();
        Assert.True(document.Remove("addSpaceAfterDictation"));
        repo.Set("app_settings", document.ToJsonString());

        var loaded = repo.Load();

        Assert.False(repo.LastLoadFailed);
        Assert.False(loaded.ShowOverlay);
        Assert.True(loaded.AddSpaceAfterDictation);
    }

    [Fact]
    public void A_key_this_build_does_not_know_is_ignored_the_way_an_older_build_ignores_this_one()
    {
        // The same options read the document in every build, so an older build meeting addSpaceAfterDictation does this.
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        repo.Set("app_settings", """{"showOverlay":false,"someSettingFromANewerBuild":true}""");

        var loaded = repo.Load();

        Assert.False(repo.LastLoadFailed);
        Assert.False(loaded.ShowOverlay);
    }

    [Fact]
    public void A_session_whose_saved_settings_could_not_be_used_adds_the_space()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        repo.Set("app_settings", """{"addSpaceAfterDictation":false, not json""");

        var loaded = repo.Load();

        Assert.True(repo.LastLoadFailed);
        Assert.True(loaded.AddSpaceAfterDictation);
        Assert.True(AppSettings.CreateForExistingInstall().AddSpaceAfterDictation);
    }

    [Fact]
    public void Clone_copies_the_choice_without_sharing_it()
    {
        var settings = AppSettings.CreateDefault();
        settings.AddSpaceAfterDictation = false;

        var clone = settings.Clone();
        Assert.False(clone.AddSpaceAfterDictation);

        clone.AddSpaceAfterDictation = true;
        Assert.False(settings.AddSpaceAfterDictation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_hotkeys_insert_with_the_saved_choice(bool addSpace)
    {
        var settings = AppSettings.CreateDefault();
        settings.EnableAiCleanup = true;
        settings.AddSpaceAfterDictation = addSpace;

        var standard = DictationCaptureSettingsResolver.Resolve(settings, HotkeyTrigger.Standard);
        var dictationOnly = DictationCaptureSettingsResolver.Resolve(settings, HotkeyTrigger.DictationOnly);

        Assert.Equal(addSpace, standard.AddSpaceAfterDictation);
        Assert.Equal(addSpace, dictationOnly.AddSpaceAfterDictation);
        Assert.True(standard.EnableAiCleanup);
        Assert.False(dictationOnly.EnableAiCleanup);
    }
}
