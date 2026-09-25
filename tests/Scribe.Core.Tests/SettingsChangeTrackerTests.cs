using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class SettingsChangeTrackerTests
{
    [Fact]
    public void Clean_after_load_and_describe_all_saved()
    {
        var baseline = AppSettings.CreateDefault();
        var changes = SettingsChangeTracker.Compare(baseline, baseline.Clone());

        Assert.False(changes.IsDirty);
        Assert.Equal("All changes saved", SettingsChangeTracker.Describe(changes));
    }

    [Fact]
    public void Dirty_on_edit_and_clean_again_when_reverted()
    {
        var baseline = AppSettings.CreateDefault();
        var draft = baseline.Clone();
        draft.HistoryRetentionDays = 30;

        var changes = SettingsChangeTracker.Compare(baseline, draft);

        Assert.Equal([SettingsPage.History], changes.Pages);
        Assert.Equal("Unsaved changes: History", SettingsChangeTracker.Describe(changes));

        draft.HistoryRetentionDays = baseline.HistoryRetentionDays;
        Assert.False(SettingsChangeTracker.Compare(baseline, draft).IsDirty);
    }

    [Fact]
    public void Recovered_mode_is_dirty_from_the_start()
    {
        var settings = AppSettings.CreateForExistingInstall();

        var changes = SettingsChangeTracker.Compare(settings, settings.Clone(), recoveredMode: true);

        Assert.True(changes.IsDirty);
        Assert.Equal("Unsaved changes: Dictation", SettingsChangeTracker.Describe(changes));
    }

    [Fact]
    public void Compares_multiple_settings_pages_in_navigation_order()
    {
        var baseline = AppSettings.CreateDefault();
        var draft = baseline.Clone();
        draft.EnableAiCleanup = true;
        draft.DecodeThreads = 4;
        draft.StoreAudioHistory = true;

        var changes = SettingsChangeTracker.Compare(baseline, draft);

        Assert.Equal([SettingsPage.AiCleanup, SettingsPage.History, SettingsPage.Advanced], changes.Pages);
        Assert.Equal("Unsaved changes: AI cleanup, History, Advanced", SettingsChangeTracker.Describe(changes));
    }

    [Fact]
    public void Draft_rows_mark_their_own_pages_dirty()
    {
        var settings = AppSettings.CreateDefault();
        var changes = SettingsChangeTracker.Compare(
            settings,
            settings.Clone(),
            dictionaryRows: [new("d", DraftRowOrigin.Saved, Touched: true, "a", "b", "a", "b")],
            snippetRows: [new("s", DraftRowOrigin.Saved, Touched: true, "a", "b", "a", "b")],
            profileRows: [new("p", DraftRowOrigin.Saved, Touched: true, "a", "b", "a", "b")]);

        Assert.Equal([SettingsPage.Dictionary, SettingsPage.VoiceSnippets, SettingsPage.AppProfiles], changes.Pages);
    }

    [Fact]
    public void Clean_after_save_uses_the_new_baseline()
    {
        var baseline = AppSettings.CreateDefault();
        var saved = baseline.Clone();
        saved.EnableAiCleanup = true;

        Assert.False(SettingsChangeTracker.Compare(saved, saved.Clone()).IsDirty);
    }

    [Fact]
    public void Immediate_startup_change_is_not_tracked_as_dirty()
    {
        var baseline = AppSettings.CreateDefault();
        var draft = baseline.Clone();
        draft.LaunchOnLogin = !baseline.LaunchOnLogin;

        Assert.False(SettingsChangeTracker.Compare(baseline, draft).IsDirty);
    }
}
