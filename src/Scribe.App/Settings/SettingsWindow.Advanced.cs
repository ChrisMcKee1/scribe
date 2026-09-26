using System.Windows;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private void RestoreAdvancedDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        AdvancedDefaults.ApplyTo(_settings);
        LoadFromSettings();
        ShowInfo("Advanced defaults restored. Choose Save to keep them.");
    }
}
