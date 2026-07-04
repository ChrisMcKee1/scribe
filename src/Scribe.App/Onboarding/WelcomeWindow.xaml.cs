using System.Windows;

namespace Scribe.App.Onboarding;

/// <summary>
/// One-time first-run welcome. Scribe is tray-only with no main window, so a brand-new user has
/// nothing on screen to teach them the push-to-talk gesture; this fills that gap. It teaches the
/// core hold-speak-release flow, the privacy stance, and where the app lives, then gets out of the
/// way. Shown non-modally so the tray and dictation loop stay live behind it.
/// </summary>
public partial class WelcomeWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly Action _openSettings;

    /// <param name="hotkeyDisplayName">
    /// The user's configured push-to-talk key (e.g. "Right Ctrl"), shown so the gesture text
    /// matches their actual binding rather than a hard-coded default.
    /// </param>
    /// <param name="openSettings">Invoked when the user clicks "Open settings".</param>
    public WelcomeWindow(string hotkeyDisplayName, Action openSettings)
    {
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));

        // Match the settings/history windows: follow the OS light/dark theme live.
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        InitializeComponent();

        var key = string.IsNullOrWhiteSpace(hotkeyDisplayName) ? "Right Ctrl" : hotkeyDisplayName;
        GestureHint.Text =
            $"Hold {key} and start talking. Release when you are done, and the text appears wherever your cursor is.";
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _openSettings();
        Close();
    }

    private void GotItButton_Click(object sender, RoutedEventArgs e) => Close();
}
