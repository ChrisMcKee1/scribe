using System.Windows;
using System.Windows.Controls;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    // How many dictations History holds, once read off the UI thread; null until then and after a failed read.
    private int? _firstRunHistoryCount;

    // The Advanced page's controls from one settings document: the saved one on load, the defaults on Restore advanced
    // defaults. Only this page's controls, so the draft the other pages hold in their controls is left alone.
    private void LoadAdvancedControls(AppSettings source)
    {
        LoadTranscriptionModelChoices(source.TranscriptionModelId);
        UpdateTranscriptionModelUi();
        ThreadsCombo.ItemsSource = ThreadChoices.Build(source.DecodeThreads);
        ThreadsCombo.SelectedValuePath = nameof(ThreadChoice.Value);
        ThreadsCombo.SelectedValue = source.DecodeThreads;
        LoadDurationChoices(IdleReleaseCombo, IdleReleaseCustomBox, DurationChoiceKind.IdleRelease, source.ReleaseModelsAfterIdleMinutes);
        VadCheck.IsChecked = source.UseVoiceActivityDetection;
        LoadDurationChoices(MaxDictationCombo, MaxDictationCustomBox, DurationChoiceKind.MaxDictation, source.MaxDictationMinutes);

        var injection = (InjectionChoice[])InjectionCombo.ItemsSource;
        InjectionCombo.SelectedItem = injection.FirstOrDefault(i => i.Method == source.InjectionMethod) ?? injection[0];
        var newline = (NewlineChoice[])NewlineCombo.ItemsSource;
        NewlineCombo.SelectedItem = newline.FirstOrDefault(i => i.Mode == source.NewlineHandling) ?? newline[0];

        ShiftEnterCheck.IsChecked = source.ShiftEnterLineBreaks;
        PostCheck.IsChecked = source.ApplyPostProcessing;
        AccentSourceCheck.IsChecked = source.AccentSource == AccentSource.Windows;
    }

    private async void RestoreAdvancedDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmRiskyAsync(
                "Restore the advanced settings to their defaults?",
                "Nothing changes until you save.",
                "Restore defaults"))
        {
            return;
        }

        // Staged like any other edit: the controls change, the saved settings do not until Save.
        var defaults = AppSettings.CreateDefault();
        LoadAdvancedControls(defaults);
        UpdateAdvancedSectionHeaders(defaults);
        ShowInfo("Advanced defaults restored. Choose Save to keep them.");
    }

    private void ShortcutModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || AutoStopCheck is null)
        {
            return;
        }

        UpdateSilenceStop();
        UpdateFirstRunHint();
    }

    // Silence stop only works for a shortcut set to Press to start and stop (CaptureTriggerBinding.StopsOnSilence), so the
    // check box is enabled only then, with the description saying which shortcut it applies to. Its value is kept while
    // it is disabled.
    private void UpdateSilenceStop()
    {
        var state = ShortcutRules.SilenceStop(
            SelectedMode,
            _pendingDictationOnlyBinding is null ? null : DictationOnlySelectedMode);
        AutoStopCheck.IsEnabled = state.IsEnabled;
        AutoStopDescription.Text = state.Description;
    }

    // The first-run hint needs to know whether History is empty. The read goes off the UI thread and is guarded: a history
    // read that fails, or waits behind a write, must never keep Settings from opening, and until it answers the hint stays
    // hidden.
    private async void RefreshFirstRunHint()
    {
        try
        {
            var count = await Task.Run(() => _history.GetRecent(1).Count);
            if (_closed)
            {
                return;
            }

            _firstRunHistoryCount = count;
        }
        catch (Exception ex)
        {
            _firstRunHistoryCount = null;
            TryLog(ex, "Could not check whether History is empty for the first-run hint.");
        }

        UpdateFirstRunHint();
    }
}