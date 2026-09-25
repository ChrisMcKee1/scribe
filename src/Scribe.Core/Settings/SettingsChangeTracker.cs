using Scribe.Core.Models;

namespace Scribe.Core.Settings;

public sealed record SettingsChangeSet(IReadOnlySet<SettingsPage> Pages)
{
    public bool IsDirty => Pages.Count > 0;
}

public sealed record LoadedDictionaryDraftRow(
    string RowKey,
    string? Pattern,
    string? Replacement,
    bool WholeWord,
    bool Enabled);

public sealed record LoadedSnippetDraftRow(
    string RowKey,
    string? Phrase,
    string? Template,
    bool Enabled);

public sealed record LoadedProfileDraftRow(
    string RowKey,
    string? Name,
    string? Apps,
    string? WritingStyle = null,
    NewlineInjectionMode? NewlineHandling = null);

/// <summary>
/// The saved baseline across a Save that can finish after the user has made further edits (plan 6.3): a Save submits an
/// immutable snapshot of what it writes, and only that snapshot becomes the baseline when the Save completes, so edits
/// made while it ran stay unsaved. A failed Save leaves the baseline as it was. Not thread-safe: the window calls it on
/// its dispatcher.
/// </summary>
/// <typeparam name="TSnapshot">An immutable snapshot of the saved document and its loaded rows, built by the caller.</typeparam>
public sealed class SettingsSaveBaseline<TSnapshot>
    where TSnapshot : class
{
    private readonly Dictionary<long, TSnapshot> _submitted = [];
    private long _lastSubmission;
    private long _lastCompleted;

    public SettingsSaveBaseline(TSnapshot loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        Current = loaded;
    }

    /// <summary>What is stored now, as far as this window knows.</summary>
    public TSnapshot Current { get; private set; }

    /// <summary>Records the snapshot a Save is about to write and returns its submission number.</summary>
    public long Submit(TSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _submitted[++_lastSubmission] = snapshot;
        return _lastSubmission;
    }

    /// <summary>
    /// Makes the snapshot of <paramref name="submission"/> the baseline, unless a later submission has already completed.
    /// Returns whether the baseline moved.
    /// </summary>
    public bool Complete(long submission)
    {
        if (!_submitted.Remove(submission, out var snapshot) || submission <= _lastCompleted)
        {
            return false;
        }

        _lastCompleted = submission;
        Current = snapshot;
        foreach (var older in _submitted.Keys.Where(key => key < submission).ToList())
        {
            _submitted.Remove(older);
        }

        return true;
    }

    /// <summary>Forgets a Save that failed; the baseline stays what it was.</summary>
    public void Fail(long submission) => _submitted.Remove(submission);
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
        bool recoveredMode = false,
        IReadOnlyList<LoadedDictionaryDraftRow>? loadedDictionaryRows = null,
        IReadOnlyList<LoadedSnippetDraftRow>? loadedSnippetRows = null,
        IReadOnlyList<LoadedProfileDraftRow>? loadedProfileRows = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(draft);

        // Checked before any comparison, so an unrelated change that short-circuits a page's test can never let a
        // caller skip a snapshot it owes.
        RequireLoadedSnapshot(dictionaryRows, loadedDictionaryRows, nameof(loadedDictionaryRows));
        RequireLoadedSnapshot(snippetRows, loadedSnippetRows, nameof(loadedSnippetRows));
        RequireLoadedSnapshot(profileRows, loadedProfileRows, nameof(loadedProfileRows));

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

        AddIf(SettingsPage.Dictionary,
            !SameLibraryIds(baseline.EnabledDictionaryLibraryIds, draft.EnabledDictionaryLibraryIds) ||
            DictionaryRowsChanged(dictionaryRows, loadedDictionaryRows));
        AddIf(SettingsPage.VoiceSnippets, SnippetRowsChanged(snippetRows, loadedSnippetRows));
        AddIf(SettingsPage.AppProfiles,
            ProfileRowsChanged(profileRows, loadedProfileRows) ||
            ProfilesChanged(baseline.Profiles, draft.Profiles));

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

    private static bool DictionaryRowsChanged(
        IReadOnlyList<DictionaryDraftRow>? rows,
        IReadOnlyList<LoadedDictionaryDraftRow>? loadedRows)
    {
        var loaded = (loadedRows ?? [])
            .ToDictionary(row => row.RowKey, StringComparer.Ordinal);
        var draft = (rows ?? [])
            .Where(row => !SettingsDraftValidator.IsPlaceholder(row))
            .ToDictionary(row => row.RowKey, StringComparer.Ordinal);
        if (!SameKeys(loaded.Keys, draft.Keys))
        {
            return true;
        }

        foreach (var (key, before) in loaded)
        {
            var after = draft[key];
            if (!Same(before.Pattern, after.Pattern) ||
                !Same(before.Replacement, after.Replacement) ||
                before.WholeWord != after.WholeWord ||
                before.Enabled != after.Enabled)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SnippetRowsChanged(
        IReadOnlyList<SnippetDraftRow>? rows,
        IReadOnlyList<LoadedSnippetDraftRow>? loadedRows)
    {
        var loaded = (loadedRows ?? [])
            .ToDictionary(row => row.RowKey, StringComparer.Ordinal);
        var draft = (rows ?? [])
            .Where(row => !SettingsDraftValidator.IsPlaceholder(row))
            .ToDictionary(row => row.RowKey, StringComparer.Ordinal);
        if (!SameKeys(loaded.Keys, draft.Keys))
        {
            return true;
        }

        foreach (var (key, before) in loaded)
        {
            var after = draft[key];
            if (!Same(before.Phrase, after.Phrase) ||
                !Same(before.Template, after.Template) ||
                before.Enabled != after.Enabled)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ProfileRowsChanged(
        IReadOnlyList<ProfileDraftRow>? rows,
        IReadOnlyList<LoadedProfileDraftRow>? loadedRows)
    {
        var loaded = (loadedRows ?? [])
            .ToDictionary(row => row.RowKey, StringComparer.Ordinal);
        var draft = (rows ?? [])
            .Where(row => !SettingsDraftValidator.IsPlaceholder(row))
            .ToDictionary(row => row.RowKey, StringComparer.Ordinal);
        if (!SameKeys(loaded.Keys, draft.Keys))
        {
            return true;
        }

        foreach (var (key, before) in loaded)
        {
            var after = draft[key];
            if (!Same(before.Name, after.Name) ||
                !Same(before.Apps, after.Apps) ||
                !Same(before.WritingStyle, after.WritingStyle) ||
                before.NewlineHandling != after.NewlineHandling)
            {
                return true;
            }
        }

        return false;
    }

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

    private static bool Same(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

    private static bool SameLibraryIds(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        new HashSet<string>(left, StringComparer.OrdinalIgnoreCase).SetEquals(right);

    private static bool SameKeys(IEnumerable<string> left, IEnumerable<string> right) =>
        new HashSet<string>(left, StringComparer.Ordinal).SetEquals(right);

    private static void RequireLoadedSnapshot<TRow, TLoaded>(
        IReadOnlyList<TRow>? rows,
        IReadOnlyList<TLoaded>? loadedRows,
        string parameterName)
    {
        if (rows is not null && loadedRows is null)
        {
            throw new ArgumentException("A loaded row snapshot is required when draft rows are supplied.", parameterName);
        }
    }
}
