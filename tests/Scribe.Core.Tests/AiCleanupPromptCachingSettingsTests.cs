using System.Text.Json.Nodes;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// <see cref="AppSettings.AiCleanupPromptCaching"/> ("Let Microsoft Foundry cache what Scribe sends") is on for every
/// install, new or upgraded, so today's request stays the default: its default is the property initializer, as for
/// <see cref="AppSettings.AddSpaceAfterDictation"/>. Turned off it stays off, the window's Save stores it as shown, the
/// page counts it as a change, Find a setting finds it, and the configuration cleanup runs on carries it.
/// </summary>
public sealed class AiCleanupPromptCachingSettingsTests
{
    [Fact]
    public void A_new_install_keeps_the_service_s_caching()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);

        Assert.True(repo.Load().AiCleanupPromptCaching);
        Assert.False(repo.LastLoadFailed);
        Assert.True(AppSettings.CreateDefault().AiCleanupPromptCaching);
        Assert.True(new AppSettings().AiCleanupPromptCaching);
        Assert.True(AppSettings.CreateForExistingInstall().AiCleanupPromptCaching);
    }

    [Fact]
    public void An_upgraded_install_whose_document_has_no_such_key_keeps_the_service_s_caching()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        repo.Set("app_settings", """{"enableAiCleanup":true,"aiCleanupProvider":"AzureFoundry","showOverlay":false}""");

        var loaded = repo.Load();

        Assert.False(repo.LastLoadFailed);
        Assert.False(loaded.ShowOverlay); // the stored document was read, not replaced
        Assert.True(loaded.AiCleanupPromptCaching);
    }

    [Fact]
    public void Turned_off_it_stays_off_across_a_restart_and_a_partial_update()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        var settings = AppSettings.CreateDefault();
        settings.AiCleanupPromptCaching = false;

        repo.Save(settings);
        repo.Update(stored => stored.HasCompletedFirstRun = true);

        Assert.Contains("\"aiCleanupPromptCaching\":false", repo.Get("app_settings"), StringComparison.Ordinal);
        Assert.False(new SettingsRepository(db).Load().AiCleanupPromptCaching);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_settings_window_s_save_stores_the_switch_as_shown(bool shown)
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        var stored = AppSettings.CreateDefault();
        stored.AiCleanupPromptCaching = !shown;
        repo.Save(stored);

        var window = repo.Load();
        window.AiCleanupPromptCaching = shown;
        repo.SaveBundle(window, dictionaryEntries: null, snippets: null);

        Assert.Equal(shown, new SettingsRepository(db).Load().AiCleanupPromptCaching);
    }

    [Fact]
    public void An_older_build_that_drops_the_key_hands_back_a_document_that_reads_as_on()
    {
        // Stated in the setting's remarks and in the results for review: caching turned off, then settings saved in a build
        // that predates the key, reads as on again.
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        var settings = AppSettings.CreateDefault();
        settings.AiCleanupPromptCaching = false;
        repo.Save(settings);

        var document = JsonNode.Parse(repo.Get("app_settings")!)!.AsObject();
        Assert.True(document.Remove("aiCleanupPromptCaching"));
        repo.Set("app_settings", document.ToJsonString());

        Assert.True(repo.Load().AiCleanupPromptCaching);
    }

    [Fact]
    public void Clone_copies_the_choice_without_sharing_it()
    {
        var settings = AppSettings.CreateDefault();
        settings.AiCleanupPromptCaching = false;

        var clone = settings.Clone();
        Assert.False(clone.AiCleanupPromptCaching);

        clone.AiCleanupPromptCaching = true;
        Assert.False(settings.AiCleanupPromptCaching);
    }

    [Fact]
    public void The_page_counts_a_change_to_it_as_an_unsaved_ai_cleanup_change()
    {
        var saved = AppSettings.CreateDefault();
        var draft = saved.Clone();
        draft.AiCleanupPromptCaching = false;

        var unchanged = SettingsChangeTracker.Compare(saved, saved.Clone());
        var changed = SettingsChangeTracker.Compare(saved, draft);

        Assert.DoesNotContain(SettingsPage.AiCleanup, unchanged.Pages);
        Assert.Contains(SettingsPage.AiCleanup, changed.Pages);
    }

    [Fact]
    public void Find_a_setting_finds_it_by_cache_on_the_ai_cleanup_page()
    {
        var result = Assert.Single(SettingsSearchIndex.Search("prompt cache"), result => result.ControlName == "AiPromptCachingCheck");

        Assert.Equal(CleanupDisclosure.PromptCachingTitle, result.Label);
        Assert.Equal(SettingsPage.AiCleanup, result.Page);
        Assert.Contains(SettingsSearchIndex.Search("caching"), found => found.ControlName == "AiPromptCachingCheck");
    }


    [Fact]
    public void The_controller_hands_cleanup_the_saved_choice()
    {
        var controller = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Dictation", "DictationController.cs"));
        var start = controller.IndexOf("private CleanupOptions BuildCleanupOptions(AppSettings settings)", StringComparison.Ordinal);
        var end = controller.IndexOf(");", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "BuildCleanupOptions was not found.");

        // The prompt cache choice, then how long Ollama or LM Studio keeps the model: Scribe's own idle setting, handed over
        // only for those two apps (LocalAiServer.KeepAliveMinutes), so an idle-time change never restarts another provider's
        // setup. Then the API another AI service is reached through, and the tuning of the app on this PC that runs the
        // model, read from the same saved settings.
        var arguments = System.Text.RegularExpressions.Regex.Replace(controller[start..end], @"\s+", " ").TrimEnd();
        Assert.Contains(
            "=> LocalModelTuning.Apply(new( settings.EnableAiCleanup,", arguments, StringComparison.Ordinal);
        Assert.Contains(
            "LocalModelKeepAliveMinutes: LocalAiServer.KeepAliveMinutes( settings.AiCleanupProvider, " +
            "settings.AiCleanupCustomEndpoint, settings.ReleaseModelsAfterIdleMinutes), " +
            "CustomApiStyle: settings.AiCleanupCustomApiStyle),",
            arguments,
            StringComparison.Ordinal);
        Assert.EndsWith(" settings", arguments, StringComparison.Ordinal);
        Assert.Contains("settings.AiCleanupPromptCaching,", arguments, StringComparison.Ordinal);
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
