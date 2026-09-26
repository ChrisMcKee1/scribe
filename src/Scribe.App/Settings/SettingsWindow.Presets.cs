using System.Windows;
using System.Windows.Controls;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private void LoadDurationChoices(ComboBox combo, Wpf.Ui.Controls.NumberBox customBox, DurationChoiceKind kind, int value)
    {
        var set = DurationChoices.Build(kind, value);
        combo.ItemsSource = set.Choices;
        combo.SelectedItem = set.Choices.First(choice => choice.IsSelected);
        customBox.Value = value;
        UpdateDurationCustomVisibility(combo, customBox);
    }

    private void DurationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDurationCustomVisibility(HistoryRetentionCombo, HistoryRetentionCustomBox);
        UpdateDurationCustomVisibility(MaxDictationCombo, MaxDictationCustomBox);
        UpdateDurationCustomVisibility(IdleReleaseCombo, IdleReleaseCustomBox);
    }

    private static void UpdateDurationCustomVisibility(ComboBox combo, Wpf.Ui.Controls.NumberBox customBox)
    {
        customBox.Visibility = combo.SelectedItem is DurationChoice { IsCustom: true }
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static int SelectedDurationValue(ComboBox combo, Wpf.Ui.Controls.NumberBox customBox, int fallback)
    {
        if (combo.SelectedItem is not DurationChoice choice)
        {
            return fallback;
        }

        if (!choice.IsCustom)
        {
            return choice.Value ?? fallback;
        }

        return customBox.Value is double value ? (int)Math.Round(value) : fallback;
    }

    private bool ValidateDurationChoices()
    {
        return ValidateDuration(DurationChoiceKind.HistoryRetention, SettingsPage.History, HistoryRetentionCustomBox, SelectedDurationValue(HistoryRetentionCombo, HistoryRetentionCustomBox, _settings.HistoryRetentionDays)) &&
               ValidateDuration(DurationChoiceKind.MaxDictation, SettingsPage.Advanced, MaxDictationCustomBox, SelectedDurationValue(MaxDictationCombo, MaxDictationCustomBox, _settings.MaxDictationMinutes)) &&
               ValidateDuration(DurationChoiceKind.IdleRelease, SettingsPage.Advanced, IdleReleaseCustomBox, SelectedDurationValue(IdleReleaseCombo, IdleReleaseCustomBox, _settings.ReleaseModelsAfterIdleMinutes));
    }

    private bool ValidateDuration(DurationChoiceKind kind, SettingsPage page, Wpf.Ui.Controls.NumberBox control, int value)
    {
        if (DurationChoices.ValidateCustom(kind, page, control.Name, value) is not { } issue)
        {
            return true;
        }

        ShowPage(page, issue.ControlName);
        ShowThemedMessage("Check this value", issue.Message);
        return false;
    }
}
