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
        RevealAzureSpeechCredentials(nameof(AzureCliRadio));
    }

    private void RevealAzureSpeechCredentials(string focusName)
    {
        _showAzureSpeechCredentials = true;
        UpdateAiProviderPanels();
        ApplyAzureSettingsAccess();
        RefreshAiStatus();
        ShowPage(SettingsPage.AiCleanup);
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_closed && FindName(focusName) is FrameworkElement target && target.IsVisible)
            {
                target.BringIntoView();
                target.Focus();
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void AboutShowWelcomeButton_Click(object sender, RoutedEventArgs e) =>
        ShowWelcomeRequested?.Invoke(this, EventArgs.Empty);
}
