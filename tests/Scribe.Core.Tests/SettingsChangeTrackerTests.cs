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
    public void Try_dictation_unsaved_notice_uses_the_plan_copy()
    {
        Assert.Equal(
            "You have unsaved changes. Try dictation uses the settings Scribe is running with.",
            SettingsChangeTracker.TryDictationUnsavedNotice);
        Assert.Equal("Save now", SettingsChangeTracker.TryDictationSaveNow);
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
    public void Draft_rows_compare_values_not_touched_state()
    {
        var settings = AppSettings.CreateDefault();
        var clean = SettingsChangeTracker.Compare(
            settings,
            settings.Clone(),
            dictionaryRows: [new("d", DraftRowOrigin.Saved, Touched: true, "a", "b", "a", "b")],
            snippetRows: [new("s", DraftRowOrigin.Saved, Touched: true, "a", "b", "a", "b")],
            profileRows: [new("p", DraftRowOrigin.Saved, Touched: true, "a", "b", "a", "b")],
            loadedDictionaryRows: [new("d", "a", "b", WholeWord: true, Enabled: true)],
            loadedSnippetRows: [new("s", "a", "b", Enabled: true)],
            loadedProfileRows: [new("p", "a", "b")]);
        Assert.False(clean.IsDirty);

        var changes = SettingsChangeTracker.Compare(
            settings,
            settings.Clone(),
            dictionaryRows: [new("d", DraftRowOrigin.Saved, Touched: true, "a", "changed", "a", "b")],
            snippetRows: [new("s", DraftRowOrigin.Saved, Touched: true, "a", "changed", "a", "b")],
            profileRows: [new("p", DraftRowOrigin.Saved, Touched: true, "changed", "b", "a", "b")],
            loadedDictionaryRows: [new("d", "a", "b", WholeWord: true, Enabled: true)],
            loadedSnippetRows: [new("s", "a", "b", Enabled: true)],
            loadedProfileRows: [new("p", "a", "b")]);
        Assert.Equal([SettingsPage.Dictionary, SettingsPage.VoiceSnippets, SettingsPage.AppProfiles], changes.Pages);
    }

    [Fact]
    public void Deleted_saved_rows_are_dirty()
    {
        var settings = AppSettings.CreateDefault();

        Assert.Equal(
            [SettingsPage.Dictionary],
            SettingsChangeTracker.Compare(
                settings,
                settings.Clone(),
                dictionaryRows: [],
                loadedDictionaryRows: [new("last", "a", "b", WholeWord: true, Enabled: true)]).Pages);

        Assert.Equal(
            [SettingsPage.VoiceSnippets],
            SettingsChangeTracker.Compare(
                settings,
                settings.Clone(),
                snippetRows: [new("kept", DraftRowOrigin.Saved, Touched: false, "c", "d", "c", "d")],
                loadedSnippetRows: [new("last", "a", "b", Enabled: true), new("kept", "c", "d", Enabled: true)]).Pages);
    }

    [Fact]
    public void Flag_only_row_changes_are_dirty()
    {
        var settings = AppSettings.CreateDefault();

        var changes = SettingsChangeTracker.Compare(
            settings,
            settings.Clone(),
            dictionaryRows: [new("d", DraftRowOrigin.Saved, Touched: false, "a", "b", "a", "b", WholeWord: false, Enabled: true)],
            snippetRows: [new("s", DraftRowOrigin.Saved, Touched: false, "a", "b", "a", "b", Enabled: false)],
            loadedDictionaryRows: [new("d", "a", "b", WholeWord: true, Enabled: true)],
            loadedSnippetRows: [new("s", "a", "b", Enabled: true)]);

        Assert.Equal([SettingsPage.Dictionary, SettingsPage.VoiceSnippets], changes.Pages);
    }

    [Fact]
    public void Word_pack_id_changes_are_dictionary_changes_and_revert_cleanly()
    {
        var baseline = AppSettings.CreateDefault();
        var draft = baseline.Clone();
        draft.EnabledDictionaryLibraryIds.Add("github");

        Assert.Equal([SettingsPage.Dictionary], SettingsChangeTracker.Compare(baseline, draft).Pages);

        draft.EnabledDictionaryLibraryIds.Remove("github");
        Assert.False(SettingsChangeTracker.Compare(baseline, draft).IsDirty);

        draft.EnabledDictionaryLibraryIds.Reverse();
        Assert.False(SettingsChangeTracker.Compare(baseline, draft).IsDirty);

        draft.EnabledDictionaryLibraryIds.RemoveAt(0);
        Assert.Equal([SettingsPage.Dictionary], SettingsChangeTracker.Compare(baseline, draft).Pages);
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
