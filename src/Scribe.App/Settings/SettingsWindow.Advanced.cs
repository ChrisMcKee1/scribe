using System.Windows;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private async void RestoreAdvancedDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmRiskyAsync(
                "Restore the advanced settings to their defaults?",
                "Nothing changes until you save.",
                "Restore defaults"))
        {
            return;
        }

        AdvancedDefaults.ApplyTo(_settings);
        LoadFromSettings();
        ShowInfo("Advanced defaults restored. Choose Save to keep them.");
    }
}
