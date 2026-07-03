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
    private readonly MenuItem _aiItem;

    /// <summary>Raised when the user picks "Quit" from the tray menu.</summary>
    public event Action? QuitRequested;

    /// <summary>Raised when the user picks "Settings…" from the tray menu.</summary>
    public event Action? SettingsRequested;

    /// <summary>Raised when the user picks "History…" from the tray menu.</summary>
    public event Action? HistoryRequested;

    /// <summary>Raised when the user toggles pause; the argument is the requested paused state.</summary>
    public event Action<bool>? PauseToggled;

    /// <summary>Raised when the user toggles AI cleanup; the argument is the requested enabled state.</summary>
    public event Action<bool>? AiCleanupToggled;

    public TrayIconHost()
    {
        var menu = new ContextMenu();

        // Header: the app name + version, bold and clickable (opens settings) — a live entry
        // point rather than a greyed-out label that looks like a broken button.
        var version = typeof(TrayIconHost).Assembly.GetName().Version;
        var header = new MenuItem
        {
            Header = $"Scribe {version?.ToString(3) ?? string.Empty}".TrimEnd(),
            FontWeight = FontWeights.SemiBold,
        };
        header.Click += (_, _) => SettingsRequested?.Invoke();
        menu.Items.Add(header);
        menu.Items.Add(new Separator());

        var settings = new MenuItem { Header = "Settings…" };
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        menu.Items.Add(settings);

        var history = new MenuItem { Header = "History…" };
        history.Click += (_, _) => HistoryRequested?.Invoke();
        menu.Items.Add(history);
        menu.Items.Add(new Separator());

        // Checkable items: WPF flips IsChecked before Click fires, so it already reflects the
        // requested state by the time the handler runs. Programmatic IsChecked updates
        // do not raise Click, so there is no feedback loop.
        _aiItem = new MenuItem { Header = "AI cleanup", IsCheckable = true };
        _aiItem.Click += (_, _) => AiCleanupToggled?.Invoke(_aiItem.IsChecked);
        menu.Items.Add(_aiItem);

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

    /// <summary>Reflects the persisted AI-cleanup setting in the quick-toggle check mark.</summary>
    public void SetAiCleanupChecked(bool enabled) => Dispatch(() => _aiItem.IsChecked = enabled);

    /// <summary>Surfaces a transient error to the user via the tray tooltip.</summary>
    public void ShowError(string message) => Dispatch(() =>
        _icon.ToolTipText = $"Scribe — {message}");

    /// <summary>Surfaces a transient, non-error status (e.g. an update is ready) via the tooltip.</summary>
    public void ShowInfo(string message) => Dispatch(() =>
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
