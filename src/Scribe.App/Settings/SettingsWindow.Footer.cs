using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private readonly DispatcherTimer _footerRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(1) };
    private string _footerStatus = SettingsChangeTracker.AllChangesSaved;
    private SettingsChangeSet _currentChanges = new(new HashSet<SettingsPage>());

    private void InitializeFooterAndClose()
    {
        _footerRefreshTimer.Tick += (_, _) =>
        {
            _footerRefreshTimer.Stop();
            RefreshFooterNow();
        };

        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ScheduleFooterRefresh()));
        AddHandler(PasswordBox.PasswordChangedEvent, new RoutedEventHandler((_, _) => ScheduleFooterRefresh()));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => ScheduleFooterRefresh()));
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => ScheduleFooterRefresh()));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => ScheduleFooterRefresh()));
        _rows.CollectionChanged += RowsChangedForFooter;
        _snippetRows.CollectionChanged += RowsChangedForFooter;
        _profileRows.CollectionChanged += RowsChangedForFooter;
        _libraryRows.CollectionChanged += RowsChangedForFooter;
        _libraryTermRows.CollectionChanged += RowsChangedForFooter;
        RecoveredSettingsNotice.IsOpen = _settingsRecovered;
        RefreshFooterNow();
    }

    private void RowsChangedForFooter(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var item in e.OldItems?.OfType<INotifyPropertyChanged>() ?? [])
        {
            item.PropertyChanged -= RowChangedForFooter;
        }

        foreach (var item in e.NewItems?.OfType<INotifyPropertyChanged>() ?? [])
        {
            item.PropertyChanged += RowChangedForFooter;
        }

        ScheduleFooterRefresh();
    }

    private void RowChangedForFooter(object? sender, PropertyChangedEventArgs e) => ScheduleFooterRefresh();

    private void ScheduleFooterRefresh()
    {
        if (_closed)
        {
            return;
        }

        _footerRefreshTimer.Stop();
        _footerRefreshTimer.Start();
    }

    private void RefreshFooterNow()
    {
        _currentChanges = ComputeCurrentChanges();
        var text = SettingsChangeTracker.Describe(_currentChanges);
        var dirty = _currentChanges.IsDirty;
        FooterDirtyDot.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        FooterStatusText.Text = text;
        SaveButton.IsEnabled = dirty || _settingsRecovered;
        SaveCloseButton.IsEnabled = !_saveInProgress;
        RecoveredSettingsNotice.IsOpen = _settingsRecovered;
        if (!string.Equals(_footerStatus, text, StringComparison.Ordinal))
        {
            _footerStatus = text;
            AnnounceFrom(FooterStatusText, text);
        }
    }

    private SettingsChangeSet ComputeCurrentChanges()
    {
        var draft = CaptureDraftSettings();
        var changes = SettingsChangeTracker.Compare(
            _committedSettings,
            draft,
            _dictionaryLoad.IsLoaded ? DictionaryDraftRows() : null,
            _snippetLoad.IsLoaded ? SnippetDraftRows() : null,
            ProfileDraftRows(),
            _settingsRecovered,
            _dictionaryLoad.IsLoaded ? LoadedDictionaryDraftRows() : null,
            _snippetLoad.IsLoaded ? LoadedSnippetDraftRows() : null,
            LoadedProfileDraftRows());
        if (_wordPackWorkspace?.HasUnsavedChanges == true)
        {
            var pages = new HashSet<SettingsPage>(changes.Pages) { SettingsPage.Dictionary };
            return new SettingsChangeSet(pages);
        }

        return changes;
    }

    private AppSettings CaptureDraftSettings()
    {
        var draft = _committedSettings.Clone();
        _externalMicrophone.ForSave(ShownMicrophone).ApplyTo(draft);
        draft.Hotkey = _pendingBinding with { Mode = SelectedMode };
        draft.DictationOnlyHotkey = _pendingDictationOnlyBinding is null ? null : _pendingDictationOnlyBinding with { Mode = DictationOnlySelectedMode };
        draft.ShowOverlay = OverlayCheck.IsChecked == true;
        draft.OverlayPosition = SelectedOverlayPosition;
        draft.UseVoiceActivityDetection = VadCheck.IsChecked == true;
        draft.AutoStopOnSilence = AutoStopCheck.IsChecked == true;
        draft.ApplyPostProcessing = PostCheck.IsChecked == true;
        draft.StoreAudioHistory = StoreAudioCheck.IsChecked == true;
        draft.ShiftEnterLineBreaks = ShiftEnterCheck.IsChecked == true;
        draft.AccentSource = AccentSourceCheck.IsChecked == true ? AccentSource.Windows : AccentSource.Scribe;
        draft.AddSpaceAfterDictation = SpaceAfterDictationCheck.IsChecked == true;
        draft.MaxDictationMinutes = SelectedDurationValue(MaxDictationCombo, MaxDictationCustomBox, draft.MaxDictationMinutes);
        draft.ReleaseModelsAfterIdleMinutes = SelectedDurationValue(IdleReleaseCombo, IdleReleaseCustomBox, draft.ReleaseModelsAfterIdleMinutes);
        draft.HistoryRetentionDays = SelectedDurationValue(HistoryRetentionCombo, HistoryRetentionCustomBox, draft.HistoryRetentionDays);
        draft.InjectionMethod = ((InjectionChoice?)InjectionCombo.SelectedItem)?.Method ?? InjectionMethod.UnicodeType;
        draft.NewlineHandling = ((NewlineChoice?)NewlineCombo.SelectedItem)?.Mode ?? NewlineInjectionMode.SmartFlatten;
        draft.Profiles = BuildProfiles();
        draft.DecodeThreads = ((ThreadChoice?)ThreadsCombo.SelectedItem)?.Value ?? draft.DecodeThreads;
        draft.TranscriptionModelId = ((TranscriptionModelChoice?)TranscriptionModelCombo.SelectedItem)?.Id ?? Scribe.Core.Transcription.TranscriptionModelCatalog.DefaultId;
        draft.EnableAiCleanup = _externalAiCleanup.ForSave(AiCleanupCheck.IsChecked == true);
        draft.AiCleanupProvider = SelectedProvider;
        draft.AiCleanupModel = NullIfBlank(SelectedFoundryModelAlias) ?? CleanupModelCatalog.DefaultAlias;
        draft.AiCleanupAzureEndpoint = NullIfBlank(AzureEndpointBox.Text);
        draft.AiCleanupAzureDeployment = NullIfBlank(AzureDeploymentBox.Text);
        draft.AiCleanupAzureAuthMode = SelectedAzureAuthMode;
        var signIn = ShownAzureSignInFields;
        draft.AiCleanupAzureApiKey = signIn.ApiKey;
        draft.AiCleanupAzureTenantId = signIn.TenantId;
        draft.AiCleanupAzureClientId = signIn.ClientId;
        draft.AiCleanupAzureClientSecret = signIn.ClientSecret;
        var subscription = AzureSubscriptionSelection.ResolveAuthenticationSubscription(_selectedAzureDeployment, SelectedAzureSubscription, AzureEndpointBox.Text, AzureDeploymentBox.Text);
        draft.AiCleanupAzureSubscriptionId = subscription?.Id;
        draft.AiCleanupAzureSubscriptionName = subscription?.Name;
        draft.AiCleanupAzureSubscriptionTenantId = subscription?.TenantId;
        draft.AiCleanupCustomEndpoint = NullIfBlank(CustomEndpointBox.Text);
        draft.AiCleanupCustomModel = NullIfBlank(CustomModelBox.Text);
        draft.AiCleanupCopilotModel = NullIfBlank(CopilotModelCombo.Text);
        draft.AiCleanupCustomApiKey = NullIfBlank(CustomApiKeyBox.Password);
        var writingStyle = AiWritingStyleBox.Text?.Trim() ?? string.Empty;
        draft.AiCleanupWritingStyle = writingStyle.Length == 0 || writingStyle == CleanupPrompt.DefaultWritingStyle ? string.Empty : writingStyle;
        draft.AiCleanupPromptStyle = SelectedPromptStyle;
        var frontierPrompt = NormalizePrompt(AiFrontierPromptBox.Text);
        draft.AiCleanupFrontierPrompt = frontierPrompt.Length == 0 || frontierPrompt == CleanupPrompt.DefaultFrontierPrompt ? string.Empty : frontierPrompt;
        var localPrompt = NormalizePrompt(AiLocalPromptBox.Text);
        draft.AiCleanupLocalPrompt = localPrompt.Length == 0 || localPrompt == CleanupPrompt.DefaultLocalPrompt ? string.Empty : localPrompt;
        return draft;
    }

    private IReadOnlyList<DictionaryDraftRow> DictionaryDraftRows() =>
        [.. _rows.Select(row => new DictionaryDraftRow(
            row.RowKey,
            row.Origin,
            row.Touched,
            row.Pattern,
            row.Replacement,
            row.LoadedPattern,
            row.LoadedReplacement,
            row.WholeWord,
            row.Enabled,
            row.LoadedWholeWord,
            row.LoadedEnabled))];

    private IReadOnlyList<LoadedDictionaryDraftRow> LoadedDictionaryDraftRows() =>
        [.. _rows.Where(row => row.Origin == DraftRowOrigin.Saved).Select(row => new LoadedDictionaryDraftRow(
            row.RowKey,
            row.LoadedPattern,
            row.LoadedReplacement,
            row.LoadedWholeWord,
            row.LoadedEnabled))];

    private IReadOnlyList<LoadedSnippetDraftRow> LoadedSnippetDraftRows() =>
        [.. _snippetRows.Where(row => row.Origin == DraftRowOrigin.Saved).Select(row => new LoadedSnippetDraftRow(
            row.RowKey,
            row.LoadedPhrase,
            row.LoadedTemplate,
            row.LoadedEnabled))];

    private IReadOnlyList<LoadedProfileDraftRow> LoadedProfileDraftRows() =>
        [.. _profileRows.Where(row => row.Origin == DraftRowOrigin.Saved).Select(row => new LoadedProfileDraftRow(
            row.RowKey,
            row.LoadedName,
            row.LoadedProcesses,
            row.LoadedWritingStyle,
            row.LoadedNewlineHandling))];
}

