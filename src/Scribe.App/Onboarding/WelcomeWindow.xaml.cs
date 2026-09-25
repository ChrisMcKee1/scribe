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

    /// <param name="gesture">
    /// What to say about the push-to-talk gesture, composed from the user's actual bindings
    /// (<see cref="Scribe.Core.Hotkeys.HotkeyText.Gesture"/>) rather than a hard-coded key.
    /// </param>
    /// <param name="openSettings">Invoked when the user clicks "Open settings".</param>
    public WelcomeWindow((string Title, string Body) gesture, Action openSettings)
    {
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));

        // Match the settings/history windows: follow the OS light/dark theme live.
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        InitializeComponent();

        GestureTitle.Text = gesture.Title;
        GestureHint.Text = gesture.Body;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _openSettings();
        Close();
    }

    private void GotItButton_Click(object sender, RoutedEventArgs e) => Close();
}
