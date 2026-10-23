using System;
using System.Windows;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    internal event EventHandler? ShowWelcomeRequested;

    private void AboutOpenDiagnostics_Click(object sender, RoutedEventArgs e) =>
        ShowPage(SettingsPage.Diagnostics);

    private void TryDictationLink_Click(object sender, RoutedEventArgs e) =>
        ShowPage(SettingsPage.TryDictation);

    private void AdvancedTypingLink_Click(object sender, RoutedEventArgs e) =>
        ShowPage(SettingsPage.Advanced, nameof(InjectionCombo));

    private void DictationSpeechModelLink_Click(object sender, RoutedEventArgs e) =>
        ShowPage(SettingsPage.Advanced, nameof(TranscriptionModelCombo));

    private void DictationMaiAuthLink_Click(object sender, RoutedEventArgs e)
    {
        if (AiProviderFoundryRadio is not null && AiProviderFoundryRadio.IsChecked != true)
        {
            AiProviderFoundryRadio.IsChecked = true;
        }

        ShowPage(SettingsPage.AiCleanup, nameof(AiProviderFoundryRadio));
    }

    private void AboutShowWelcomeButton_Click(object sender, RoutedEventArgs e) =>
        ShowWelcomeRequested?.Invoke(this, EventArgs.Empty);
}
