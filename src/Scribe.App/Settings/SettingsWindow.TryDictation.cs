using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using Scribe.App.Dictation;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.Transcription;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private static readonly TextPostProcessingResult EmptyTryDictationResult = new(string.Empty, []);
    private string? _tryDictationRunningModelId;
    private int? _tryDictationRunningDecodeThreads;

    private void TryDictationSection_Loaded(object sender, RoutedEventArgs e) =>
        UpdateTryDictationPage();

    private void TryDictationSection_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (SectionTryDictation.Visibility == Visibility.Visible)
        {
            UpdateTryDictationPage();
        }
    }

    private async void TryDictationSaveNow_Click(object sender, RoutedEventArgs e)
    {
        if (_saveInProgress)
        {
            return;
        }

        _saveInProgress = true;
        try
        {
            if (await ConfirmDictionaryOverlapAsync() && await TrySaveAsync())
            {
                ShowInfo("Settings saved.");
            }
        }
        finally
        {
            _saveInProgress = false;
            UpdateTryDictationPage();
        }
    }

    private void TryDictationClear_Click(object sender, RoutedEventArgs e)
    {
        PlaygroundInput.Text = string.Empty;
        ShowTryDictationEmptyResult();
    }

    private void UpdateTryDictationPage()
    {
        if (TryDictationPrimaryInstruction is null)
        {
            return;
        }

        var primary = _pendingBinding with { Mode = SelectedMode };
        TryDictationPrimaryInstruction.Text = TryDictationInstruction(primary, primaryShortcut: true);

        var secondary = _pendingDictationOnlyBinding is null
            ? null
            : _pendingDictationOnlyBinding with { Mode = DictationOnlySelectedMode };
        if (secondary is null)
        {
            TryDictationSecondaryInstruction.Visibility = Visibility.Collapsed;
            TryDictationSecondaryInstruction.Text = string.Empty;
        }
        else
        {
            TryDictationSecondaryInstruction.Text = TryDictationInstruction(secondary, primaryShortcut: false);
            TryDictationSecondaryInstruction.Visibility = Visibility.Visible;
        }

        var samples = TryDictationSample.For(CurrentDictionaryEntries());
        TryDictationSampleText.Text = samples[0];
        if (samples.Count > 1)
        {
            TryDictationSecondSampleText.Text = samples[1];
            TryDictationSecondSampleText.Visibility = Visibility.Visible;
        }
        else
        {
            TryDictationSecondSampleText.Text = string.Empty;
            TryDictationSecondSampleText.Visibility = Visibility.Collapsed;
        }

        RememberTryDictationRunningSpeechSettings();
        var changes = CurrentTryDictationChanges();
        var restartNeeded = !changes.IsDirty && TryDictationNeedsRestart();
        TryDictationUnsavedBar.Message = SettingsChangeTracker.TryDictationUnsavedNotice;
        TryDictationUnsavedBar.IsOpen = changes.IsDirty;
        TryDictationUnsavedBar.Visibility = changes.IsDirty ? Visibility.Visible : Visibility.Collapsed;
        TryDictationRestartBar.Message = SettingsChangeTracker.TryDictationRestartNotice;
        TryDictationRestartBar.IsOpen = restartNeeded;
        TryDictationRestartBar.Visibility = restartNeeded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RememberTryDictationRunningSpeechSettings()
    {
        _tryDictationRunningModelId ??= _settings.TranscriptionModelId;
        _tryDictationRunningDecodeThreads ??= _settings.DecodeThreads;
    }

    private bool TryDictationNeedsRestart() =>
        !string.Equals(_tryDictationRunningModelId, _settings.TranscriptionModelId, StringComparison.Ordinal) ||
        _tryDictationRunningDecodeThreads != _settings.DecodeThreads;

    private SettingsChangeSet CurrentTryDictationChanges()
    {
        var pages = new SortedSet<SettingsPage>(SettingsChangeTracker.Compare(_settings, TryDictationDraft(), recoveredMode: _settingsRecovered).Pages);
        if (_dictionaryLoad.HasChanges(DictionarySignature()) || _libraryLoad.HasChanges(LibrarySignature()))
        {
            pages.Add(SettingsPage.Dictionary);
        }

        if (_snippetLoad.HasChanges(SnippetSignature()))
        {
            pages.Add(SettingsPage.VoiceSnippets);
        }

        return new SettingsChangeSet(pages);
    }

    private AppSettings TryDictationDraft()
    {
        var draft = _settings.Clone();
        draft.Hotkey = _pendingBinding with { Mode = SelectedMode };
        draft.DictationOnlyHotkey = _pendingDictationOnlyBinding is null
            ? null
            : _pendingDictationOnlyBinding with { Mode = DictationOnlySelectedMode };
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
        draft.DecodeThreads = ((ThreadChoice?)ThreadsCombo.SelectedItem)?.Value ?? draft.DecodeThreads;
        draft.TranscriptionModelId = ((TranscriptionModelChoice?)TranscriptionModelCombo.SelectedItem)?.Id ?? TranscriptionModelCatalog.DefaultId;
        draft.EnableAiCleanup = _externalAiCleanup.ForSave(AiCleanupCheck.IsChecked == true);
        draft.AiCleanupProvider = SelectedProvider;
        draft.AiCleanupModel = NullIfBlank(AiModelBox.Text) ?? CleanupModelCatalog.DefaultAlias;
        draft.AiCleanupAzureEndpoint = NullIfBlank(AzureEndpointBox.Text);
        draft.AiCleanupAzureDeployment = NullIfBlank(AzureDeploymentBox.Text);
        draft.AiCleanupAzureApiKey = NullIfBlank(SelectedAzureApiKey);
        draft.AiCleanupAzureAuthMode = SelectedAzureAuthMode;
        draft.AiCleanupAzureTenantId = SelectedAzureAuthMode == AzureAuthMode.ServicePrincipal
            ? NullIfBlank(SpTenantBox.Text)
            : NullIfBlank(AzureTenantBox.Text);
        draft.AiCleanupAzureClientId = NullIfBlank(SpClientIdBox.Text);
        draft.AiCleanupAzureClientSecret = NullIfBlank(SpClientSecretBox.Password);
        draft.AiCleanupCustomEndpoint = NullIfBlank(CustomEndpointBox.Text);
        draft.AiCleanupCustomModel = NullIfBlank(CustomModelBox.Text);
        draft.AiCleanupCopilotModel = NullIfBlank(CopilotModelCombo.Text);
        draft.AiCleanupCustomApiKey = NullIfBlank(CustomApiKeyBox.Password);
        var writingStyle = NormalizePrompt(AiWritingStyleBox.Text);
        draft.AiCleanupWritingStyle = writingStyle.Length == 0 || writingStyle == CleanupPrompt.DefaultWritingStyle
            ? string.Empty
            : writingStyle;
        draft.AiCleanupPromptStyle = SelectedPromptStyle;
        var frontierPrompt = NormalizePrompt(AiFrontierPromptBox.Text);
        draft.AiCleanupFrontierPrompt = frontierPrompt.Length == 0 || frontierPrompt == CleanupPrompt.DefaultFrontierPrompt
            ? string.Empty
            : frontierPrompt;
        var localPrompt = NormalizePrompt(AiLocalPromptBox.Text);
        draft.AiCleanupLocalPrompt = localPrompt.Length == 0 || localPrompt == CleanupPrompt.DefaultLocalPrompt
            ? string.Empty
            : localPrompt;
        draft.Profiles = BuildProfiles();
        if (_libraryLoad.IsLoaded)
        {
            draft.EnabledDictionaryLibraryIds = CollectEnabledLibraryIds();
        }

        _externalMicrophone.ForSave(ShownMicrophone).ApplyTo(draft);
        return draft;
    }

    private static string TryDictationInstruction(HotkeyBinding binding, bool primaryShortcut)
    {
        var shortcut = HotkeyText.SentenceName(binding);
        if (primaryShortcut)
        {
            return binding.Mode == HotkeyMode.Toggle
                ? $"Click in the box, press {shortcut} and speak. Press it again to finish."
                : $"Click in the box, hold {shortcut} and speak. Let go to finish.";
        }

        return binding.Mode == HotkeyMode.Toggle
            ? $"Press {shortcut} to try it without AI cleanup."
            : $"Hold {shortcut} to try it without AI cleanup.";
    }

    private void ShowTryDictationPipeline(DictationPipelineReport report)
    {
        var targetHandle = new WindowInteropHelper(this).Handle;
        var hwndFreeRender = targetHandle == 0 && report.TargetWindow == 0;
        if ((!hwndFreeRender && !IsVisible) ||
            SectionTryDictation.Visibility != Visibility.Visible ||
            (!hwndFreeRender && targetHandle != report.TargetWindow))
        {
            return;
        }

        UpdateTryDictationPage();
        TryDictationEmptyText.Visibility = Visibility.Collapsed;
        TryDictationResultPanel.Visibility = Visibility.Visible;

        TryDictationHeardText.Text = report.RawText ?? string.Empty;
        var displayedText = report.FinalText ?? report.PostProcessing?.Text ?? report.CleanedText ?? report.RawText ?? string.Empty;
        var displayedResult = report.PostProcessing is { } postProcessing &&
            string.Equals(postProcessing.Text, displayedText, StringComparison.Ordinal)
                ? postProcessing
                : new TextPostProcessingResult(displayedText, []);
        RenderTryDictationTypedText(displayedResult);

        var processing = TryDictationTiming.ProcessingDuration(
            report.VadEnabled ? report.VadDuration : TimeSpan.Zero,
            report.DecodeDuration,
            report.CleanupEnabled ? report.CleanupDuration : TimeSpan.Zero,
            report.PostProcessingEnabled ? report.PostProcessingDuration : TimeSpan.Zero,
            report.Injection is null ? TimeSpan.Zero : report.InjectionDuration);
        var summaryInput = BuildTryDictationSummaryInput(report, processing);
        TryDictationSummaryText.Text = TryDictationSummary.Describe(summaryInput);
        TryDictationSoundSettingsButton.Visibility = summaryInput.MicrophoneProblem ? Visibility.Visible : Visibility.Collapsed;
        SetTryDictationSummaryIcon(summaryInput);
        AnnounceTryDictationSummary();

        var cleanupChanged = report.Cleanup?.Changed == true ||
            (report.CleanedText is { } cleaned && report.RawText is { } raw && !string.Equals(cleaned, raw, StringComparison.Ordinal));
        var changes = TryDictationChangeList.Describe(displayedResult.Replacements, cleanupChanged);
        TryDictationChangesTitle.Text = $"Changes ({changes.Count})";
        TryDictationChangesList.ItemsSource = changes.Count == 0 ? ["No changes."] : changes;

        PlaygroundCaptureDuration.Text = TryFormatDuration(report.CaptureDuration);
        PlaygroundCaptureDetail.Text = $"{report.SpeechDuration.TotalSeconds:N1} seconds of speech kept";
        PlaygroundVadDuration.Text = report.VadEnabled ? TryFormatDuration(report.VadDuration) : "Skipped";
        PlaygroundVadDetail.Text = report.VadEnabled
            ? report.VadAvailable ? "Trimmed" : "Off, VAD model unavailable"
            : "Off in settings";
        PlaygroundDecodeDuration.Text = report.DecodeDuration > TimeSpan.Zero ? TryFormatDuration(report.DecodeDuration) : "Not run";
        var speed = TryDictationTiming.SpeedLabel(report.RealTimeFactor);
        PlaygroundDecodeDetail.Text = report.RawText is null
            ? "Not reached"
            : string.IsNullOrEmpty(speed) ? $"{report.RawText.Length:N0} characters" : $"{report.RawText.Length:N0} characters, {speed}";
        PlaygroundAiDuration.Text = report.CleanupEnabled ? TryFormatDuration(report.CleanupDuration) : "Skipped";
        PlaygroundAiDetail.Text = report.Cleanup is { } cleanup ? DescribeTryDictationCleanup(cleanup, report.CleanupEnabled) : "Not reached";
        PlaygroundPostDuration.Text = report.PostProcessingEnabled ? TryFormatDuration(report.PostProcessingDuration) : "Skipped";
        PlaygroundPostDetail.Text = report.PostProcessing is { } processed
            ? $"{processed.Replacements.Count:N0} changes"
            : "Not reached";
        PlaygroundInjectionDuration.Text = report.Injection is null ? "Not run" : TryFormatDuration(report.InjectionDuration);
        PlaygroundInjectionDetail.Text = report.Injection is { } injection
            ? injection.Succeeded ? InjectionMethodLabel.Describe(injection.Method, report.SpaceAddedAfterText) : "Typing failed"
            : "Not reached";
        PlaygroundTotalDuration.Text = TryFormatDuration(processing);
        PlaygroundTotalDetail.Text = string.Empty;
    }

    private TryDictationSummaryInput BuildTryDictationSummaryInput(DictationPipelineReport report, TimeSpan processing)
    {
        if (IsNoSpeech(report))
        {
            return new TryDictationSummaryInput(false, NoSpeech: true);
        }

        if (IsMicrophoneProblem(report))
        {
            return new TryDictationSummaryInput(false, MicrophoneProblem: true);
        }

        if (report.Cleanup?.Outcome == CleanupOutcome.Failed)
        {
            return new TryDictationSummaryInput(
                false,
                CleanupFailed: true,
                Reason: report.Cleanup.DisplayDetail ?? report.Cleanup.FailureReason ?? "Try again, or turn AI cleanup off.");
        }

        if (StageFrom(report.FailureStage) is { } stopped)
        {
            return new TryDictationSummaryInput(false, StoppedAt: stopped);
        }

        return new TryDictationSummaryInput(
            Success: report.Injection?.Succeeded != false,
            ProcessingSeconds: processing.TotalSeconds,
            AiCleanupEnabled: report.CleanupEnabled,
            Model: TryDictationModelName(report.CleanupEnabled),
            Where: TryDictationModelLocation());
    }

    private static bool IsNoSpeech(DictationPipelineReport report) =>
        report.FailureStage is "Voice activity detection" or "Speech recognition" &&
        (report.FailureReason?.Contains("speech", StringComparison.OrdinalIgnoreCase) == true ||
         report.FailureReason?.Contains("silence", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsMicrophoneProblem(DictationPipelineReport report) =>
        string.Equals(report.FailureStage, "Audio capture", StringComparison.Ordinal);

    private static FailureStage? StageFrom(string? stage) => stage switch
    {
        "Audio capture" => FailureStage.AudioCapture,
        "Voice activity detection" => FailureStage.VoiceActivityDetection,
        "Speech recognition" => FailureStage.SpeechRecognition,
        "Dictionary and snippets" => FailureStage.DictionaryAndSnippets,
        "Text insertion" => FailureStage.TextInsertion,
        _ => null,
    };

    private static string DescribeTryDictationCleanup(CleanupResult cleanup, bool enabled)
    {
        if (!enabled)
        {
            return "Skipped";
        }

        return cleanup.Outcome switch
        {
            CleanupOutcome.Cleaned => "Cleaned",
            CleanupOutcome.Unchanged => "Ran, no changes needed",
            CleanupOutcome.Failed => "Failed, raw text kept",
            _ => "Skipped",
        };
    }

    private static string TryFormatDuration(TimeSpan elapsed) =>
        elapsed.TotalMilliseconds < 1 ? "<1 ms" : $"{elapsed.TotalMilliseconds:N0} ms";

    private string TryDictationModelName(bool cleanupEnabled)
    {
        if (!cleanupEnabled)
        {
            return string.Empty;
        }

        return _settings.AiCleanupProvider switch
        {
            CleanupProvider.FoundryLocal => StripRecommendation(CleanupModelCatalog.Resolve(_settings.AiCleanupModel).DisplayName),
            CleanupProvider.AzureFoundry => NullIfBlank(_settings.AiCleanupAzureDeployment) ?? "configured model",
            CleanupProvider.OpenAiCompatible => NullIfBlank(_settings.AiCleanupCustomModel) ?? "configured model",
            CleanupProvider.GitHubCopilot => NullIfBlank(_settings.AiCleanupCopilotModel) ?? "GitHub Copilot",
            _ => "configured model",
        };
    }

    private string TryDictationModelLocation() =>
        _settings.AiCleanupProvider == CleanupProvider.FoundryLocal ? "on this PC" : "online";

    private static string StripRecommendation(string displayName)
    {
        const string suffix = " (recommended)";
        return displayName.EndsWith(suffix, StringComparison.Ordinal)
            ? displayName[..^suffix.Length]
            : displayName;
    }

    private void RenderTryDictationTypedText(TextPostProcessingResult result)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var position = 0;
        foreach (var replacement in result.Replacements.OrderBy(r => r.Start))
        {
            if (replacement.Start < position || replacement.Start > result.Text.Length)
            {
                continue;
            }

            AppendTryDictationText(paragraph, result.Text[position..replacement.Start], highlight: false);
            var length = Math.Min(replacement.Length, result.Text.Length - replacement.Start);
            AppendTryDictationText(paragraph, result.Text.Substring(replacement.Start, length), highlight: true);
            position = replacement.Start + length;
        }

        AppendTryDictationText(paragraph, result.Text[position..], highlight: false);
        PlaygroundOutput.Document = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            FontFamily = PlaygroundOutput.FontFamily,
            FontSize = PlaygroundOutput.FontSize,
            Foreground = PlaygroundOutput.Foreground,
        };
    }

    private static void AppendTryDictationText(Paragraph paragraph, string text, bool highlight)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n'))
            {
                continue;
            }

            AppendTryDictationRun(paragraph, text[start..index], highlight);
            paragraph.Inlines.Add(new LineBreak());
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            start = index + 1;
        }

        AppendTryDictationRun(paragraph, text[start..], highlight);
    }

    private static void AppendTryDictationRun(Paragraph paragraph, string text, bool highlight)
    {
        if (text.Length == 0)
        {
            return;
        }

        var run = new Run(text);
        if (highlight)
        {
            run.FontWeight = FontWeights.SemiBold;
            run.TextDecorations = TextDecorations.Underline;
        }

        paragraph.Inlines.Add(run);
    }

    private void ShowTryDictationEmptyResult()
    {
        TryDictationEmptyText.Visibility = Visibility.Visible;
        TryDictationResultPanel.Visibility = Visibility.Collapsed;
        TryDictationHeardText.Text = string.Empty;
        RenderTryDictationTypedText(EmptyTryDictationResult);
        TryDictationChangesList.ItemsSource = null;
        TryDictationTimingExpander.IsExpanded = false;
    }

    private void SetTryDictationSummaryIcon(TryDictationSummaryInput input)
    {
        if (input.Success || input.CleanupFailed)
        {
            TryDictationSummaryIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24;
            TryDictationSummaryIcon.Foreground = (Brush)FindResource("SystemFillColorSuccessBrush");
            return;
        }

        if (input.NoSpeech)
        {
            TryDictationSummaryIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Info24;
            TryDictationSummaryIcon.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
            return;
        }

        TryDictationSummaryIcon.Symbol = input.MicrophoneProblem
            ? Wpf.Ui.Controls.SymbolRegular.ErrorCircle24
            : Wpf.Ui.Controls.SymbolRegular.Warning24;
        TryDictationSummaryIcon.Foreground = (Brush)FindResource(input.MicrophoneProblem
            ? "SystemFillColorCriticalBrush"
            : "SystemFillColorCautionBrush");
    }

    private void AnnounceTryDictationSummary()
    {
        try
        {
            var peer = UIElementAutomationPeer.FromElement(TryDictationSummaryText)
                ?? UIElementAutomationPeer.CreatePeerForElement(TryDictationSummaryText);
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.ImportantMostRecent,
                TryDictationSummaryText.Text,
                "Scribe.TryDictationResult");
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not announce the Try dictation result to assistive technology.");
        }
    }
}
