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
        RefreshHistorySettingsSummary();
    }

    private static void UpdateDurationCustomVisibility(ComboBox combo, Wpf.Ui.Controls.NumberBox customBox)
    {
        customBox.Visibility = combo.SelectedItem is DurationChoice { IsCustom: true }
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // Only called after ValidateDurationChoices passed, so a custom selection holds a whole number in range here.
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

    private bool ValidateDurationChoices() =>
        ValidateDuration(DurationChoiceKind.HistoryRetention, SettingsPage.History, HistoryRetentionCombo, HistoryRetentionCustomBox) &&
        ValidateDuration(DurationChoiceKind.MaxDictation, SettingsPage.Advanced, MaxDictationCombo, MaxDictationCustomBox) &&
        ValidateDuration(DurationChoiceKind.IdleRelease, SettingsPage.Advanced, IdleReleaseCombo, IdleReleaseCustomBox);

    // A preset needs no check: several of them are 0 ("Until I delete them", "No limit", "Never"), which the custom range
    // rightly refuses. Only a custom entry is checked, as typed: the custom boxes have no Minimum or Maximum, because WPF-UI's
    // NumberBox clamps to those when it loses focus, which would turn a typed 0 into 1 day of history without a word. An
    // empty or fractional entry is refused like an out-of-range one.
    private bool ValidateDuration(DurationChoiceKind kind, SettingsPage page, ComboBox combo, Wpf.Ui.Controls.NumberBox customBox)
    {
        if (combo.SelectedItem is not DurationChoice { IsCustom: true })
        {
            return true;
        }

        var typed = customBox.Value;
        var value = typed is double number && !double.IsNaN(number) && Math.Abs(number - Math.Round(number)) < 1e-9
            ? (int)Math.Clamp(Math.Round(number), int.MinValue, int.MaxValue)
            : int.MinValue;
        if (DurationChoices.ValidateCustom(kind, page, customBox.Name, value) is not { } issue)
        {
            return true;
        }

        ShowPage(page, issue.ControlName);
        ShowThemedMessage("Check this value", issue.Message);
        return false;
    }
}