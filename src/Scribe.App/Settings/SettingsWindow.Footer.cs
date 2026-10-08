using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private readonly DispatcherTimer _footerRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(1) };
    private string _footerStatus = SettingsChangeTracker.AllChangesSaved;
    private bool _imeComposing;
    private SettingsChangeSet _currentChanges = new(new HashSet<SettingsPage>());

    // LeanFooterRefresh: the controls whose typing and selection change nothing Save stores (the search boxes, Try
    // dictation's box, the navigation rail, the History list, the Dictionary page's tabs and the usage period), so their
    // events no longer run the whole dirty check; and the row subscriptions made without LINQ iterators or a delegate per
    // row. Null with the flag off, when every event schedules the check as before.
    private HashSet<DependencyObject>? _footerIgnoredSources;
    private PropertyChangedEventHandler? _rowChangedForFooter;

    private void InitializeFooterAndClose()
    {
        _footerRefreshTimer.Tick += (_, _) =>
        {
            _footerRefreshTimer.Stop();
            RefreshFooterNow();
        };

        if (_perfFlags.IsOn(PerfFlags.LeanFooterRefresh))
        {
            _footerIgnoredSources =
            [
                SettingsSearchBox, DictionarySearchBox, LibrarySearchBox, HistorySearchBox, PlaygroundInput,
                NavList, HistoryGrid, DictionaryTabs, UsagePeriodBox, OllamaDownloadModelBox,
            ];
            AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, e) => ScheduleFooterRefreshFor(e)));
            AddHandler(PasswordBox.PasswordChangedEvent, new RoutedEventHandler((_, _) => ScheduleFooterRefresh()));
            AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, e) => ScheduleFooterRefreshFor(e)));
            AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => ScheduleFooterRefresh()));
            AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => ScheduleFooterRefresh()));
        }
        else
        {
            AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ScheduleFooterRefresh()));
            AddHandler(PasswordBox.PasswordChangedEvent, new RoutedEventHandler((_, _) => ScheduleFooterRefresh()));
            AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => ScheduleFooterRefresh()));
            AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => ScheduleFooterRefresh()));
            AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => ScheduleFooterRefresh()));
        }

        _rows.CollectionChanged += RowsChangedForFooter;
        _snippetRows.CollectionChanged += RowsChangedForFooter;
        _profileRows.CollectionChanged += RowsChangedForFooter;
        _libraryRows.CollectionChanged += RowsChangedForFooter;
        _libraryTermRows.CollectionChanged += RowsChangedForFooter;
        TextCompositionManager.AddPreviewTextInputStartHandler(this, (_, _) => _imeComposing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(this, (_, _) => _imeComposing = true);
        TextCompositionManager.AddPreviewTextInputHandler(this, (_, _) => _imeComposing = false);
        RecoveredSettingsNotice.IsOpen = _settingsRecovered;
        RefreshFooterNow();
    }

    private void RowsChangedForFooter(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_footerIgnoredSources is not null)
        {
            // The same subscriptions, in the same order, with one cached handler (a delegate equal to the method group,
            // so a removal matches exactly as before).
            var handler = _rowChangedForFooter ??= RowChangedForFooter;
            if (e.OldItems is { } oldItems)
            {
                for (var i = 0; i < oldItems.Count; i++)
                {
                    if (oldItems[i] is INotifyPropertyChanged item)
                    {
                        item.PropertyChanged -= handler;
                    }
                }
            }

            if (e.NewItems is { } newItems)
            {
                for (var i = 0; i < newItems.Count; i++)
                {
                    if (newItems[i] is INotifyPropertyChanged item)
                    {
                        item.PropertyChanged += handler;
                    }
                }
            }

            ScheduleFooterRefresh();
            return;
        }

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

    // IncrementalWordPackRows: rows put in by one Reset (ReplaceableObservableCollection.ReplaceAll), which names no new
    // items, watched as an Add per row would have had RowsChangedForFooter watch them (the Reset itself schedules the check).
    private void WatchRowsForFooter<T>(IReadOnlyList<T> rows)
        where T : INotifyPropertyChanged
    {
        var handler = _rowChangedForFooter ??= RowChangedForFooter;
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].PropertyChanged += handler;
        }
    }

    // LeanFooterRefresh: an event from a control whose value Save never stores schedules nothing. The source as the window
    // sees it, the element that raised it, and that element's templated parent (the text box inside Find a setting's
    // suggest box) are each checked against the ignored controls.
    private void ScheduleFooterRefreshFor(RoutedEventArgs e)
    {
        var ignored = _footerIgnoredSources!;
        if ((e.Source is DependencyObject source && ignored.Contains(source)) ||
            (e.OriginalSource is DependencyObject original && ignored.Contains(original)) ||
            ((e.OriginalSource as FrameworkElement)?.TemplatedParent is { } templated && ignored.Contains(templated)))
        {
            return;
        }

        ScheduleFooterRefresh();
    }

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
        if (SectionTryDictation.Visibility == Visibility.Visible)
        {
            UpdateTryDictationPage(_currentChanges);
        }

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
            _dictionaryLoad.IsLoaded ? _loadedDictionaryRows : null,
            _snippetLoad.IsLoaded ? _loadedSnippetRows : null,
            _loadedProfileRows);
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
        draft.AppAwareFormattingEnabled = AppAwareFormattingCheck.IsChecked == true;
        draft.DefaultTextFormat = SelectedDefaultTextFormat;
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
        var customService = ShownCustomService;
        draft.AiCleanupCustomEndpoint = customService.Endpoint;
        draft.AiCleanupCustomModel = customService.Model;
        draft.AiCleanupCustomApiStyle = customService.ApiStyle;
        var rememberedService = ShownRememberedService;
        draft.AiCleanupOtherServiceEndpoint = rememberedService.Endpoint;
        draft.AiCleanupOtherServiceModel = rememberedService.Model;
        draft.AiCleanupOtherServiceApiKey = rememberedService.ApiKey;
        draft.AiCleanupOtherServiceApiStyle = rememberedService.ApiStyle;
        draft.AiCleanupCopilotModel = NullIfBlank(CopilotModelCombo.Text);
        draft.AiCleanupPromptCaching = AiPromptCachingCheck.IsChecked != false;
        draft.AiCleanupOllamaContextTokens = SelectedOllamaContextTokens;
        draft.AiCleanupLmStudioContextTokens = SelectedLmStudioContextTokens;
        draft.AiCleanupOllamaSendWholeVocabulary = OllamaWholeVocabularyCheck.IsChecked == true;
        draft.AiCleanupLmStudioSendWholeVocabulary = LmStudioWholeVocabularyCheck.IsChecked == true;
        draft.AiCleanupFoundryLocalSendWholeVocabulary = FoundryWholeVocabularyCheck.IsChecked == true;
        draft.AiCleanupCustomApiKey = customService.ApiKey;
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

    private IReadOnlyList<LoadedDictionaryDraftRow> LoadedDictionaryDraftRowsFromRows() =>
        [.. _rows.Where(row => row.Origin == DraftRowOrigin.Saved).Select(row => new LoadedDictionaryDraftRow(
            row.RowKey,
            row.LoadedPattern,
            row.LoadedReplacement,
            row.LoadedWholeWord,
            row.LoadedEnabled))];

    private IReadOnlyList<LoadedSnippetDraftRow> LoadedSnippetDraftRowsFromRows() =>
        [.. _snippetRows.Where(row => row.Origin == DraftRowOrigin.Saved).Select(row => new LoadedSnippetDraftRow(
            row.RowKey,
            row.LoadedPhrase,
            row.LoadedTemplate,
            row.LoadedEnabled))];

    private IReadOnlyList<LoadedProfileDraftRow> LoadedProfileDraftRowsFromRows() =>
        [.. _profileRows.Where(row => row.Origin == DraftRowOrigin.Saved).Select(row => new LoadedProfileDraftRow(
            row.RowKey,
            row.LoadedName,
            row.LoadedProcesses,
            row.LoadedWritingStyle,
            row.LoadedNewlineHandling,
            row.LoadedTextFormat,
            row.LoadedInjectionMethod,
            row.LoadedShiftEnterLineBreaks))];
}
