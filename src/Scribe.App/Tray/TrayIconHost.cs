using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Scribe.App.Dictation;

namespace Scribe.App.Tray;

/// <summary>
/// Owns the system-tray icon and its context menu, and reflects the current
/// <see cref="DictationState"/> through the icon and tooltip. All UI mutations are marshalled
/// to the WPF dispatcher because dictation state changes arrive on background threads.
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _pauseItem;

    /// <summary>Raised when the user picks "Quit" from the tray menu.</summary>
    public event Action? QuitRequested;

    /// <summary>Raised when the user picks "Settings…" from the tray menu.</summary>
    public event Action? SettingsRequested;

    /// <summary>Raised when the user picks "History…" from the tray menu.</summary>
    public event Action? HistoryRequested;

    /// <summary>Raised when the user toggles pause; the argument is the requested paused state.</summary>
    public event Action<bool>? PauseToggled;

    public TrayIconHost()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Scribe", IsEnabled = false });
        menu.Items.Add(new Separator());

        var settings = new MenuItem { Header = "Settings…" };
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        menu.Items.Add(settings);

        var history = new MenuItem { Header = "History…" };
        history.Click += (_, _) => HistoryRequested?.Invoke();
        menu.Items.Add(history);
        menu.Items.Add(new Separator());

        // Checkable item: WPF flips IsChecked before Click fires, so it already reflects the
        // requested state by the time the handler runs. Programmatic IsChecked updates from
        // SetState do not raise Click, so there is no feedback loop.
        _pauseItem = new MenuItem { Header = "Pause dictation", IsCheckable = true };
        _pauseItem.Click += (_, _) => PauseToggled?.Invoke(_pauseItem.IsChecked);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Separator());

        var quit = new MenuItem { Header = "Quit Scribe" };
        quit.Click += (_, _) => QuitRequested?.Invoke();
        menu.Items.Add(quit);

        _icon = new TaskbarIcon
        {
            ToolTipText = "Scribe — ready",
            Icon = TrayIcons.Idle,
            ContextMenu = menu,
            MenuActivation = PopupActivationMode.RightClick,
        };
        _icon.ForceCreate(false);
    }

    /// <summary>Updates the tray icon and tooltip to match the current dictation state.</summary>
    public void SetState(DictationState state) => Dispatch(() =>
    {
        (_icon.Icon, _icon.ToolTipText) = state switch
        {
            DictationState.Recording => (TrayIcons.Recording, "Scribe — recording…"),
            DictationState.Processing => (TrayIcons.Processing, "Scribe — transcribing…"),
            DictationState.Paused => (TrayIcons.Paused, "Scribe — paused"),
            _ => (TrayIcons.Idle, "Scribe — ready"),
        };

        _pauseItem.IsChecked = state == DictationState.Paused;
    });

    /// <summary>Surfaces a transient error to the user via the tray tooltip.</summary>
    public void ShowError(string message) => Dispatch(() =>
        _icon.ToolTipText = $"Scribe — {message}");

    private static void Dispatch(Action action)
    {
        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            app.Dispatcher.Invoke(action);
        }
    }

    public void Dispose() => _icon.Dispose();
}
