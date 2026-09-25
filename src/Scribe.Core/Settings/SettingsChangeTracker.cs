using Scribe.Core.Models;

namespace Scribe.Core.Settings;

public sealed record SettingsChangeSet(IReadOnlySet<SettingsPage> Pages)
{
    public bool IsDirty => Pages.Count > 0;
}

public static class SettingsChangeTracker
{
    public const string AllChangesSaved = "All changes saved";
    public const string TryDictationUnsavedNotice =
        "You have unsaved changes. Try dictation uses the settings Scribe is running with.";
    public const string TryDictationSaveNow = "Save now";

    public static SettingsChangeSet Compare(
        AppSettings baseline,
        AppSettings draft,
        IReadOnlyList<DictionaryDraftRow>? dictionaryRows = null,
        IReadOnlyList<SnippetDraftRow>? snippetRows = null,
        IReadOnlyList<ProfileDraftRow>? profileRows = null,
        bool recoveredMode = false)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(draft);

        var pages = new SortedSet<SettingsPage>();
        if (recoveredMode)
        {
            pages.Add(SettingsPage.Dictation);
        }

        AddIf(SettingsPage.Dictation,
            !Same(baseline.InputDeviceId, draft.InputDeviceId) ||
            !Same(baseline.InputDeviceName, draft.InputDeviceName) ||
            !baseline.Hotkey.SameKeysAndBehavior(draft.Hotkey) ||
            !SameHotkey(baseline.DictationOnlyHotkey, draft.DictationOnlyHotkey) ||
            baseline.AutoStopOnSilence != draft.AutoStopOnSilence ||
            baseline.AddSpaceAfterDictation != draft.AddSpaceAfterDictation ||
            baseline.ShowOverlay != draft.ShowOverlay ||
            baseline.OverlayPosition != draft.OverlayPosition);

        AddIf(SettingsPage.History,
            baseline.HistoryRetentionDays != draft.HistoryRetentionDays ||
            baseline.StoreAudioHistory != draft.StoreAudioHistory);

        AddIf(SettingsPage.AiCleanup,
            baseline.EnableAiCleanup != draft.EnableAiCleanup ||
            baseline.AiCleanupProvider != draft.AiCleanupProvider ||
            !Same(baseline.AiCleanupModel, draft.AiCleanupModel) ||
            !Same(baseline.AiCleanupAzureEndpoint, draft.AiCleanupAzureEndpoint) ||
            !Same(baseline.AiCleanupAzureDeployment, draft.AiCleanupAzureDeployment) ||
            !Same(baseline.AiCleanupAzureSubscriptionId, draft.AiCleanupAzureSubscriptionId) ||
            !Same(baseline.AiCleanupAzureTenantId, draft.AiCleanupAzureTenantId) ||
            baseline.AiCleanupAzureAuthMode != draft.AiCleanupAzureAuthMode ||
            !Same(baseline.AiCleanupAzureClientId, draft.AiCleanupAzureClientId) ||
            !Same(baseline.AiCleanupAzureClientSecret, draft.AiCleanupAzureClientSecret) ||
            !Same(baseline.AiCleanupAzureApiKey, draft.AiCleanupAzureApiKey) ||
            !Same(baseline.AiCleanupCustomEndpoint, draft.AiCleanupCustomEndpoint) ||
            !Same(baseline.AiCleanupCustomModel, draft.AiCleanupCustomModel) ||
            !Same(baseline.AiCleanupCustomApiKey, draft.AiCleanupCustomApiKey) ||
            !Same(baseline.AiCleanupCopilotModel, draft.AiCleanupCopilotModel) ||
            !Same(baseline.AiCleanupWritingStyle, draft.AiCleanupWritingStyle) ||
            baseline.AiCleanupPromptStyle != draft.AiCleanupPromptStyle ||
            !Same(baseline.AiCleanupFrontierPrompt, draft.AiCleanupFrontierPrompt) ||
            !Same(baseline.AiCleanupLocalPrompt, draft.AiCleanupLocalPrompt));

        AddIf(SettingsPage.Advanced,
            baseline.DecodeThreads != draft.DecodeThreads ||
            !Same(baseline.TranscriptionModelId, draft.TranscriptionModelId) ||
            baseline.ReleaseModelsAfterIdleMinutes != draft.ReleaseModelsAfterIdleMinutes ||
            baseline.UseVoiceActivityDetection != draft.UseVoiceActivityDetection ||
            baseline.MaxDictationMinutes != draft.MaxDictationMinutes ||
            baseline.InjectionMethod != draft.InjectionMethod ||
            baseline.NewlineHandling != draft.NewlineHandling ||
            baseline.ShiftEnterLineBreaks != draft.ShiftEnterLineBreaks ||
            baseline.ApplyPostProcessing != draft.ApplyPostProcessing);

        AddIf(SettingsPage.Dictionary, RowsChanged(dictionaryRows));
        AddIf(SettingsPage.VoiceSnippets, RowsChanged(snippetRows));
        AddIf(SettingsPage.AppProfiles, RowsChanged(profileRows) || ProfilesChanged(baseline.Profiles, draft.Profiles));

        return new SettingsChangeSet(pages);

        void AddIf(SettingsPage page, bool changed)
        {
            if (changed)
            {
                pages.Add(page);
            }
        }
    }

    public static string Describe(SettingsChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return changes.IsDirty
            ? $"Unsaved changes: {string.Join(", ", changes.Pages.Select(SettingsNavigation.Title))}"
            : AllChangesSaved;
    }

    private static bool RowsChanged<T>(IReadOnlyList<T>? rows)
        where T : notnull =>
        rows?.Count > 0 && rows.Any(row => row switch
        {
            DictionaryDraftRow dictionary => dictionary.Touched || Changed(dictionary.Pattern, dictionary.LoadedPattern) || Changed(dictionary.Replacement, dictionary.LoadedReplacement),
            SnippetDraftRow snippet => snippet.Touched || Changed(snippet.Phrase, snippet.LoadedPhrase) || Changed(snippet.Template, snippet.LoadedTemplate),
            ProfileDraftRow profile => profile.Touched || Changed(profile.Name, profile.LoadedName) || Changed(profile.Apps, profile.LoadedApps),
            _ => false,
        });

    private static bool ProfilesChanged(IReadOnlyList<AppProfile> baseline, IReadOnlyList<AppProfile> draft)
    {
        if (baseline.Count != draft.Count)
        {
            return true;
        }

        for (var i = 0; i < baseline.Count; i++)
        {
            var left = baseline[i];
            var right = draft[i];
            if (!Same(left.Name, right.Name) ||
                !Same(left.WritingStyle, right.WritingStyle) ||
                left.NewlineHandling != right.NewlineHandling ||
                !left.ProcessNames.SequenceEqual(right.ProcessNames, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameHotkey(HotkeyBinding? left, HotkeyBinding? right) =>
        left is null ? right is null : left.SameKeysAndBehavior(right);

    private static bool Changed(string? current, string? loaded) => !Same(current, loaded);

    private static bool Same(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
}
