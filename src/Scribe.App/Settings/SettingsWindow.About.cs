using System;
using System.Windows;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    internal event EventHandler? ShowWelcomeRequested;

    private void AboutOpenDiagnostics_Click(object sender, RoutedEventArgs e) =>
        ShowPage(SettingsPage.Diagnostics);

    private void AboutShowWelcomeButton_Click(object sender, RoutedEventArgs e) =>
        ShowWelcomeRequested?.Invoke(this, EventArgs.Empty);
}
