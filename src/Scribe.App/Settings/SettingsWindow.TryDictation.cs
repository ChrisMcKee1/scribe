using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Scribe.App.Dictation;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.TextInjection;
using Scribe.Core.Transcription;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
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
                ShowInfo("Changes saved.");
            }
        }
        finally
        {
            _saveInProgress = false;
            UpdateTryDictationPage();
            ScheduleFooterRefresh();
        }
    }

    private void TryDictationClear_Click(object sender, RoutedEventArgs e)
    {
        PlaygroundInput.Text = string.Empty;
        ShowTryDictationEmptyResult();
    }

    private void TryDictationOpenAiCleanup_Click(object sender, RoutedEventArgs e) =>
        ShowPage(SettingsPage.AiCleanup);

    private void UpdateTryDictationPage(SettingsChangeSet? currentChanges = null)
    {
        try
        {
            UpdateTryDictationPageCore(currentChanges);
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not refresh the Try dictation page.");
        }
    }

    private void UpdateTryDictationPageCore(SettingsChangeSet? currentChanges)
    {
        if (TryDictationPrimaryInstruction is null)
        {
            return;
        }

        var primary = _committedSettings.Hotkey;
        TryDictationPrimaryInstruction.Text = TryDictationInstruction(primary, primaryShortcut: true);

        var secondary = _committedSettings.EnableAiCleanup
            ? _committedSettings.DictationOnlyHotkey
            : null;
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

        var samples = TryDictationSample.For(TryDictationSampleDictionaryEntries());
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

        var changes = currentChanges ?? ComputeCurrentChanges();
        var restartNeeded = !changes.IsDirty && TryDictationRestartNotice.Needed(
            _committedSettings.TranscriptionModelId,
            _committedSettings.DecodeThreads,
            _runningTranscription.ModelId,
            _runningTranscription.NumThreads);
        TryDictationUnsavedBar.Message = SettingsChangeTracker.TryDictationUnsavedNotice;
        TryDictationUnsavedBar.IsOpen = changes.IsDirty;
        TryDictationUnsavedHost.Visibility = changes.IsDirty ? Visibility.Visible : Visibility.Collapsed;
        TryDictationSaveNowButton.Visibility = changes.IsDirty ? Visibility.Visible : Visibility.Collapsed;
        TryDictationRestartBar.Message = SettingsChangeTracker.TryDictationRestartNotice;
        TryDictationRestartBar.IsOpen = restartNeeded;
    }

    private IReadOnlyList<DictionaryEntry> TryDictationSampleDictionaryEntries() =>
        _dictionaryLoad.IsLoaded
            ? _rows.Select(row => new DictionaryEntry(row.Id, row.Pattern, row.Replacement, row.WholeWord, row.Enabled)).ToList()
            : [];

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

    private void RenderTryDictationReport(DictationPipelineReport report)
    {
        try
        {
            RenderTryDictationReportCore(report);
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not render the Try dictation result.");
        }
    }

    private void RenderTryDictationReportCore(DictationPipelineReport report)
    {
UpdateTryDictationPage();
        TryDictationEmptyText.Visibility = Visibility.Collapsed;
        TryDictationResultPanel.Visibility = Visibility.Visible;

        var displayedText = report.FinalText ?? report.PostProcessing?.Text ?? report.CleanedText ?? report.RawText ?? string.Empty;
        var displayedResult = report.PostProcessing is { } postProcessing &&
            string.Equals(postProcessing.Text, displayedText, StringComparison.Ordinal)
                ? postProcessing
                : new TextPostProcessingResult(displayedText, []);
        var processing = TryDictationTiming.ProcessingDuration(
            report.VadEnabled ? report.VadDuration : TimeSpan.Zero,
            report.DecodeDuration,
            report.CleanupEnabled ? report.CleanupDuration : TimeSpan.Zero,
            report.PostProcessingEnabled ? report.PostProcessingDuration : TimeSpan.Zero,
            report.Injection is null ? TimeSpan.Zero : report.InjectionDuration);
        var cleanupFailed = report.Cleanup?.Outcome == CleanupOutcome.Failed;
        var cleanupNotReady = report.Cleanup?.Outcome == CleanupOutcome.Skipped && report.Cleanup.SkippedUnexpectedly;
        var cleanupReason = cleanupFailed
            ? report.Cleanup?.DisplayDetail ?? report.Cleanup?.FailureReason
            : cleanupNotReady ? report.Cleanup?.DisplayDetail : null;
        var view = TryDictationResultView.For(new TryDictationResultViewInput(
            report.FailureStage,
            report.FailureReason,
            report.RawText,
            report.Injection?.Succeeded,
            processing.TotalSeconds,
            report.CleanupEnabled,
            TryDictationCleanupPhrase.For(BuildTryDictationCleanupOptions()),
            cleanupFailed,
            cleanupNotReady,
            cleanupReason));

        TryDictationSummaryText.Text = TryDictationSummary.Describe(view.Summary);
        TryDictationSoundSettingsButton.Visibility = view.Action == TryDictationSummaryAction.OpenSoundSettings ? Visibility.Visible : Visibility.Collapsed;
        TryDictationAiCleanupButton.Visibility = view.Action == TryDictationSummaryAction.OpenAiCleanup ? Visibility.Visible : Visibility.Collapsed;
        SetTryDictationSummaryIcon(TryDictationSummary.ToneFor(view.Summary));
        AnnounceTryDictationSummary();

        TryDictationHeardSection.Visibility = view.ShowHeard ? Visibility.Visible : Visibility.Collapsed;
        TryDictationHeardText.Text = view.ShowHeard ? report.RawText ?? string.Empty : string.Empty;
        TryDictationTypedSection.Visibility = view.ShowTyped ? Visibility.Visible : Visibility.Collapsed;
        TryDictationFormattingText.Text = report.Formatting is { } formatting
            ? DictationFormattingText.Describe(formatting) +
                (report.Injection is { Succeeded: true } delivered
                    ? " " + InjectionMethodLabel.Describe(delivered.Method, report.SpaceAddedAfterText) + "." : string.Empty) +
                (formatting.Decision == DictationFormatDecision.MarkdownNewlineConflict
                    ? " " + DictationFormattingText.RuntimeNewlineConflict : string.Empty)
            : string.Empty;
        if (view.ShowTyped)
        {
            RenderTryDictationTypedText(displayedResult);
        }
        else
        {
            TryDictationTypedText.Inlines.Clear();
        }

        var cleanupChanged = report.Cleanup?.Changed == true ||
            (report.CleanedText is { } cleaned && report.RawText is { } raw && !string.Equals(cleaned, raw, StringComparison.Ordinal));
        var changes = TryDictationChangeList.Describe(report.PostProcessing?.Replacements ?? [], cleanupChanged);
        TryDictationChangesSection.Visibility = view.ShowChanges ? Visibility.Visible : Visibility.Collapsed;
        TryDictationChangesTitle.Text = changes.Count == 0 ? "Changes" : $"Changes ({changes.Count})";
        TryDictationChangesList.ItemsSource = changes.Count == 0 ? ["No changes."] : changes;
        TryDictationTimingExpander.Visibility = view.ShowTimingDetails ? Visibility.Visible : Visibility.Collapsed;

        PlaygroundCaptureDuration.Text = TryFormatDuration(report.CaptureDuration);
        PlaygroundCaptureDetail.Text = $"{report.SpeechDuration.TotalSeconds:N1} seconds of speech kept";
        PlaygroundVadDuration.Text = report.VadEnabled ? TryFormatDuration(report.VadDuration) : "Skipped";
        PlaygroundVadDetail.Text = report.VadEnabled
            ? report.VadAvailable ? "Trimmed" : TryDictationTimingDetail.SilenceTrimmingUnavailable
            : "Off in settings";
        PlaygroundDecodeDuration.Text = report.DecodeDuration > TimeSpan.Zero ? TryFormatDuration(report.DecodeDuration) : "Not run";
        var speed = TryDictationTiming.SpeedLabel(report.RealTimeFactor);
        PlaygroundDecodeDetail.Text = report.RawText is null
            ? "Not reached"
            : string.IsNullOrEmpty(speed) ? $"{report.RawText.Length:N0} characters" : $"{report.RawText.Length:N0} characters, {speed}";
        PlaygroundAiDuration.Text = report.CleanupEnabled ? TryFormatDuration(report.CleanupDuration) : "Skipped";
        PlaygroundAiDetail.Text = TryDictationTimingDetail.Cleanup(report.Cleanup, report.CleanupEnabled);
        PlaygroundPostDuration.Text = report.PostProcessingEnabled ? TryFormatDuration(report.PostProcessingDuration) : "Skipped";
        PlaygroundPostDetail.Text = report.PostProcessing is { } processed
            ? TryDictationTimingDetail.ChangeCount(processed.Replacements.Count)
            : "Not reached";
        PlaygroundInjectionDuration.Text = report.Injection is null ? "Not run" : TryFormatDuration(report.InjectionDuration);
        PlaygroundInjectionDetail.Text = report.Injection is { } injection
            ? injection.Succeeded ? InjectionMethodLabel.Describe(injection.Method, report.SpaceAddedAfterText) : "Typing failed"
            : "Not reached";
        PlaygroundTotalDuration.Text = TryFormatDuration(processing);
        PlaygroundTotalDetail.Text = string.Empty;
    }

    private CleanupOptions BuildTryDictationCleanupOptions() => new(
        _committedSettings.EnableAiCleanup,
        _committedSettings.AiCleanupProvider,
        _committedSettings.AiCleanupModel,
        _committedSettings.AiCleanupAzureEndpoint,
        _committedSettings.AiCleanupAzureDeployment,
        _committedSettings.AiCleanupAzureApiKey,
        _committedSettings.AiCleanupAzureTenantId,
        CustomEndpoint: _committedSettings.AiCleanupCustomEndpoint,
        CustomModel: _committedSettings.AiCleanupCustomModel,
        CustomApiKey: _committedSettings.AiCleanupCustomApiKey,
        AzureAuthMode: _committedSettings.AiCleanupAzureAuthMode,
        AzureClientId: _committedSettings.AiCleanupAzureClientId,
        AzureClientSecret: _committedSettings.AiCleanupAzureClientSecret,
        CopilotModel: _committedSettings.AiCleanupCopilotModel,
        PromptCaching: _committedSettings.AiCleanupPromptCaching,
        CustomApiStyle: _committedSettings.AiCleanupCustomApiStyle);

    private static string TryFormatDuration(TimeSpan elapsed) =>
        elapsed.TotalMilliseconds < 1 ? "<1 ms" : $"{elapsed.TotalMilliseconds:N0} ms";

    private void RenderTryDictationTypedText(TextPostProcessingResult result)
    {
        TryDictationTypedText.Inlines.Clear();
        var position = 0;
        foreach (var replacement in result.Replacements.OrderBy(r => r.Start))
        {
            if (replacement.Start < position || replacement.Start > result.Text.Length)
            {
                continue;
            }

            AppendTryDictationText(TryDictationTypedText, result.Text[position..replacement.Start], highlight: false);
            var length = Math.Min(replacement.Length, result.Text.Length - replacement.Start);
            AppendTryDictationText(TryDictationTypedText, result.Text.Substring(replacement.Start, length), highlight: true);
            position = replacement.Start + length;
        }

        AppendTryDictationText(TryDictationTypedText, result.Text[position..], highlight: false);
    }

    private static void AppendTryDictationText(TextBlock textBlock, string text, bool highlight)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n'))
            {
                continue;
            }

            AppendTryDictationRun(textBlock, text[start..index], highlight);
            textBlock.Inlines.Add(new LineBreak());
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            start = index + 1;
        }

        AppendTryDictationRun(textBlock, text[start..], highlight);
    }

    private static void AppendTryDictationRun(TextBlock textBlock, string text, bool highlight)
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

        textBlock.Inlines.Add(run);
    }

    private void ShowTryDictationEmptyResult()
    {
        TryDictationEmptyText.Visibility = Visibility.Visible;
        TryDictationResultPanel.Visibility = Visibility.Collapsed;
        TryDictationHeardText.Text = string.Empty;
        TryDictationTypedText.Inlines.Clear();
        TryDictationChangesList.ItemsSource = null;
        TryDictationTimingExpander.IsExpanded = false;
    }

    private void SetTryDictationSummaryIcon(TryDictationSummaryTone tone)
    {
        var (symbol, brush) = tone switch
        {
            TryDictationSummaryTone.Information => (Wpf.Ui.Controls.SymbolRegular.Info24, "TextFillColorSecondaryBrush"),
            TryDictationSummaryTone.Caution => (Wpf.Ui.Controls.SymbolRegular.Warning24, "SystemFillColorCautionBrush"),
            TryDictationSummaryTone.Critical => (Wpf.Ui.Controls.SymbolRegular.ErrorCircle24, "SystemFillColorCriticalBrush"),
            _ => (Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24, "SystemFillColorSuccessBrush"),
        };
        TryDictationSummaryIcon.Symbol = symbol;
        TryDictationSummaryIcon.SetResourceReference(ForegroundProperty, brush);
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
