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
        Assert.Equal("Restart Scribe before Try dictation uses these changes.", SettingsChangeTracker.TryDictationRestartNotice);
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
        draft.AccentSource = AccentSource.Windows;

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
    public void Draft_rows_require_explicit_loaded_snapshots()
    {
        var settings = AppSettings.CreateDefault();

        Assert.Throws<ArgumentException>(() => SettingsChangeTracker.Compare(
            settings,
            settings.Clone(),
            dictionaryRows: []));
        Assert.Throws<ArgumentException>(() => SettingsChangeTracker.Compare(
            settings,
            settings.Clone(),
            snippetRows: []));
        Assert.Throws<ArgumentException>(() => SettingsChangeTracker.Compare(
            settings,
            settings.Clone(),
            profileRows: []));
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
    public void Flag_changes_revert_cleanly_for_each_row_type()
    {
        var settings = AppSettings.CreateDefault();

        Assert.False(SettingsChangeTracker.Compare(
            settings,
            settings.Clone(),
            dictionaryRows: [new("d", DraftRowOrigin.Saved, Touched: true, "a", "b", "a", "b", WholeWord: true, Enabled: true)],
            loadedDictionaryRows: [new("d", "a", "b", WholeWord: true, Enabled: true)]).IsDirty);
        Assert.False(SettingsChangeTracker.Compare(
            settings,
            settings.Clone(),
            snippetRows: [new("s", DraftRowOrigin.Saved, Touched: true, "a", "b", "a", "b", Enabled: true)],
            loadedSnippetRows: [new("s", "a", "b", Enabled: true)]).IsDirty);
        Assert.False(SettingsChangeTracker.Compare(
            settings,
            settings.Clone(),
            profileRows: [new("p", DraftRowOrigin.Saved, Touched: true, "a", "b", "a", "b", WritingStyle: "style", LoadedWritingStyle: "style", NewlineHandling: NewlineInjectionMode.KeepNewlines, LoadedNewlineHandling: NewlineInjectionMode.KeepNewlines)],
            loadedProfileRows: [new("p", "a", "b", "style", NewlineInjectionMode.KeepNewlines)]).IsDirty);
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
    public void Accent_source_counts_as_an_advanced_change()
    {
        var baseline = AppSettings.CreateDefault();
        var draft = baseline.Clone();
        draft.AccentSource = AccentSource.Windows;

        var changes = SettingsChangeTracker.Compare(baseline, draft);

        Assert.Equal([SettingsPage.Advanced], changes.Pages);
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
    public void Save_finishing_after_newer_edits_does_not_mark_the_newer_edits_saved()
    {
        var saved = AppSettings.CreateDefault();
        saved.EnableAiCleanup = true;
        var newerDraft = saved.Clone();
        newerDraft.HistoryRetentionDays = 30;

        var changes = SettingsChangeTracker.Compare(saved, newerDraft);

        Assert.Equal([SettingsPage.History], changes.Pages);
    }

    [Fact]
    public void Immediate_startup_change_is_not_tracked_as_dirty()
    {
        var baseline = AppSettings.CreateDefault();
        var draft = baseline.Clone();
        draft.LaunchOnLogin = !baseline.LaunchOnLogin;

        Assert.False(SettingsChangeTracker.Compare(baseline, draft).IsDirty);
    }

    [Fact]
    public void A_missing_snapshot_throws_even_when_another_change_already_marks_the_page()
    {
        var baseline = AppSettings.CreateDefault();
        var draft = baseline.Clone();
        draft.EnabledDictionaryLibraryIds.Add("github");

        Assert.Throws<ArgumentException>(() => SettingsChangeTracker.Compare(baseline, draft, dictionaryRows: []));
    }

    [Fact]
    public void Deleting_the_only_row_is_dirty_for_every_row_type()
    {
        var settings = AppSettings.CreateDefault();

        Assert.Equal(
            [SettingsPage.Dictionary],
            SettingsChangeTracker.Compare(
                settings, settings.Clone(),
                dictionaryRows: [],
                loadedDictionaryRows: [new("only", "a", "b", WholeWord: true, Enabled: true)]).Pages);
        Assert.Equal(
            [SettingsPage.VoiceSnippets],
            SettingsChangeTracker.Compare(
                settings, settings.Clone(),
                snippetRows: [],
                loadedSnippetRows: [new("only", "a", "b", Enabled: true)]).Pages);
        Assert.Equal(
            [SettingsPage.AppProfiles],
            SettingsChangeTracker.Compare(
                settings, settings.Clone(),
                profileRows: [],
                loadedProfileRows: [new("only", "Email", "OUTLOOK")]).Pages);
    }

    [Fact]
    public void Each_stored_row_field_is_a_change_on_its_own_and_reverts_cleanly()
    {
        var settings = AppSettings.CreateDefault();
        LoadedDictionaryDraftRow[] loadedWord = [new("d", "a", "b", WholeWord: true, Enabled: true)];
        LoadedSnippetDraftRow[] loadedSnippet = [new("s", "a", "b", Enabled: true)];
        LoadedProfileDraftRow[] loadedProfile = [new("p", "Email", "OUTLOOK", "Formal.", NewlineInjectionMode.KeepNewlines)];
        var word = new DictionaryDraftRow("d", DraftRowOrigin.Saved, Touched: true, "a", "b", "a", "b");
        var snippet = new SnippetDraftRow("s", DraftRowOrigin.Saved, Touched: true, "a", "b", "a", "b");
        var profile = new ProfileDraftRow(
            "p", DraftRowOrigin.Saved, Touched: true, "Email", "OUTLOOK", "Email", "OUTLOOK",
            WritingStyle: "Formal.", LoadedWritingStyle: "Formal.",
            NewlineHandling: NewlineInjectionMode.KeepNewlines, LoadedNewlineHandling: NewlineInjectionMode.KeepNewlines);

        bool WordDirty(DictionaryDraftRow row) =>
            SettingsChangeTracker.Compare(settings, settings.Clone(), dictionaryRows: [row], loadedDictionaryRows: loadedWord).IsDirty;
        bool SnippetDirty(SnippetDraftRow row) =>
            SettingsChangeTracker.Compare(settings, settings.Clone(), snippetRows: [row], loadedSnippetRows: loadedSnippet).IsDirty;
        bool ProfileDirty(ProfileDraftRow row) =>
            SettingsChangeTracker.Compare(settings, settings.Clone(), profileRows: [row], loadedProfileRows: loadedProfile).IsDirty;

        Assert.False(WordDirty(word));
        Assert.True(WordDirty(word with { WholeWord = false }));
        Assert.True(WordDirty(word with { Enabled = false }));
        Assert.False(WordDirty(word with { WholeWord = false } with { WholeWord = true }));

        Assert.False(SnippetDirty(snippet));
        Assert.True(SnippetDirty(snippet with { Enabled = false }));
        Assert.False(SnippetDirty(snippet with { Enabled = false } with { Enabled = true }));

        Assert.False(ProfileDirty(profile));
        Assert.True(ProfileDirty(profile with { WritingStyle = "Casual." }));
        Assert.True(ProfileDirty(profile with { NewlineHandling = NewlineInjectionMode.AlwaysFlatten }));
        Assert.False(ProfileDirty(profile with { WritingStyle = "Casual." } with { WritingStyle = "Formal." }));
    }

    [Fact]
    public void A_blank_new_row_is_not_a_change_even_when_touched()
    {
        var settings = AppSettings.CreateDefault();

        Assert.False(SettingsChangeTracker.Compare(
            settings, settings.Clone(),
            dictionaryRows: [new("new", DraftRowOrigin.New, Touched: true, " ", "")],
            loadedDictionaryRows: []).IsDirty);
    }

    [Fact]
    public void A_save_adopts_only_the_snapshot_it_submitted()
    {
        var loaded = AppSettings.CreateDefault();
        var baseline = new SettingsSaveBaseline<AppSettings>(loaded.Clone());
        var draft = loaded.Clone();
        draft.EnableAiCleanup = true;

        var submission = baseline.Submit(draft.Clone());
        draft.HistoryRetentionDays = 30;
        Assert.True(baseline.Complete(submission));

        Assert.True(baseline.Current.EnableAiCleanup);
        Assert.Equal([SettingsPage.History], SettingsChangeTracker.Compare(baseline.Current, draft).Pages);
    }

    [Fact]
    public void A_failed_save_keeps_the_baseline()
    {
        var loaded = AppSettings.CreateDefault();
        var baseline = new SettingsSaveBaseline<AppSettings>(loaded);
        var draft = loaded.Clone();
        draft.EnableAiCleanup = true;

        var submission = baseline.Submit(draft.Clone());
        baseline.Fail(submission);

        Assert.False(baseline.Complete(submission));
        Assert.Same(loaded, baseline.Current);
        Assert.Equal([SettingsPage.AiCleanup], SettingsChangeTracker.Compare(baseline.Current, draft).Pages);
    }

    [Fact]
    public void An_older_save_completing_late_never_replaces_a_newer_baseline()
    {
        var baseline = new SettingsSaveBaseline<AppSettings>(AppSettings.CreateDefault());
        var first = AppSettings.CreateDefault();
        first.HistoryRetentionDays = 30;
        var second = AppSettings.CreateDefault();
        second.HistoryRetentionDays = 7;

        var older = baseline.Submit(first);
        var newer = baseline.Submit(second);
        Assert.True(baseline.Complete(newer));
        Assert.False(baseline.Complete(older));

        Assert.Same(second, baseline.Current);
    }
}
