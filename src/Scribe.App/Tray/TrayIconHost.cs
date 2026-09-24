using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Scribe.App.Dictation;
using Scribe.Core.Lifecycle;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Wpf.Ui.Appearance;

namespace Scribe.App.Tray;

/// <summary>
/// Owns the system-tray icon and its context menu, and reflects the current
/// <see cref="DictationState"/> through the icon and tooltip. UI mutations requested from a background
/// thread are posted to the WPF dispatcher, never invoked synchronously: dictation state, errors and
/// warnings arrive on the audio capture thread and dictation processing, and the UI thread waits for
/// both at shutdown, so a caller parked on the UI thread there would never be released.
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly ContextMenu _menu;
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _pauseItem;
    private readonly MenuItem _aiItem;
    private readonly UiThreadDispatch _ui;

    // Set on the UI thread by Dispose; posted work runs there too and drops itself once this is set.
    private bool _disposed;

    // The icon currently assigned to the tray. Held so its handle can be released once it has been
    // replaced; H.NotifyIcon owns nothing beyond the instance it is showing.
    private System.Drawing.Icon? _currentIcon;
    private System.Drawing.Icon? _retiredIcon;

    /// <summary>Raised when the user picks "Quit" from the tray menu.</summary>
    public event Action? QuitRequested;

    /// <summary>Raised when the user picks "Settings" from the tray menu.</summary>
    public event Action? SettingsRequested;

    /// <summary>Raised when the user picks "Learn from history" from the tray menu.</summary>
    public event Action? LearnFromHistoryRequested;

    /// <summary>Raised when the user picks "Add to dictionary" from the tray menu.</summary>
    public event Action? AddToDictionaryRequested;

    /// <summary>Raised when the user explicitly asks to copy the last finalized dictation.</summary>
    public event Action? CopyLastDictationRequested;

    /// <summary>
    /// Raised when the user picks a specific entry from the "Copy recent dictation" submenu.
    /// Carries the full transcript to copy, not the truncated preview shown in the menu.
    /// </summary>
    public event Action<string>? CopyRecentDictationRequested;

    /// <summary>
    /// Supplies the current recoverable transcripts, most recent first, each time the tray menu
    /// opens. Injected by the app shell so this class stays free of Core persistence wiring.
    /// </summary>
    public Func<IReadOnlyList<string>>? RecentDictationsProvider { get; set; }

    /// <summary>Raised when the user picks "Show welcome" to reopen the first-run intro.</summary>
    public event Action? WelcomeRequested;

    /// <summary>Raised when the user picks "Open in Microsoft Store".</summary>
    public event Action? OpenStoreRequested;

    /// <summary>Raised when the user picks "Share app" to copy the Store link.</summary>
    public event Action? ShareAppRequested;

    /// <summary>Raised when the user toggles pause; the argument is the requested paused state.</summary>
    public event Action<bool>? PauseToggled;

    /// <summary>Raised when the user toggles AI cleanup; the argument is the requested enabled state.</summary>
    public event Action<bool>? AiCleanupToggled;

    /// <summary>
    /// Supplies the microphone picker (the devices Windows offers now, and the current choice) each time the tray menu
    /// opens. Injected by the app shell so this class stays free of Core audio wiring.
    /// </summary>
    public Func<MicrophoneMenu>? MicrophoneMenuProvider { get; set; }

    /// <summary>Raised when the user picks an entry from the "Microphone" submenu.</summary>
    public event Action<MicrophoneSelection>? MicrophoneChosen;

    /// <summary>Raised when the user picks "Sound settings" from the "Microphone" submenu.</summary>
    public event Action? SoundSettingsRequested;

    /// <param name="onUpdateFailure">
    /// Told about a tray update that threw. Updates are best effort and many run posted, where nobody else could see the
    /// failure.
    /// </param>
    public TrayIconHost(Action<Exception>? onUpdateFailure = null)
    {
        _ui = new UiThreadDispatch(
            isOnUiThread: () => Application.Current?.Dispatcher.CheckAccess() ?? true,
            post: work => Application.Current?.Dispatcher.BeginInvoke(work),
            isClosed: () => _disposed,
            onFailure: onUpdateFailure);

        _menu = new ContextMenu();
        var menu = _menu;
        ApplyMenuTheme();
        ApplicationThemeManager.Changed += OnApplicationThemeChanged;

        // Header: the app name + version, bold and clickable (opens settings); a live entry
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

        var settings = new MenuItem { Header = "Settings" };
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        menu.Items.Add(settings);

        var addToDictionary = new MenuItem { Header = "Add to dictionary" };
        addToDictionary.Click += (_, _) => AddToDictionaryRequested?.Invoke();
        menu.Items.Add(addToDictionary);

        var learnFromHistory = new MenuItem { Header = "Learn from history" };
        learnFromHistory.Click += (_, _) => LearnFromHistoryRequested?.Invoke();
        menu.Items.Add(learnFromHistory);

        var copyLastDictation = new MenuItem { Header = "Copy last dictation" };
        copyLastDictation.Click += (_, _) => CopyLastDictationRequested?.Invoke();
        menu.Items.Add(copyLastDictation);

        // Rebuilt on every menu open (not once at startup) so the submenu always mirrors the
        // transcripts that are actually recoverable right now.
        var copyRecentDictation = new MenuItem { Header = "Copy recent dictation" };
        menu.Items.Add(copyRecentDictation);

        // Rebuilt on every open as well, so it lists the microphones Windows offers right now and names the device the
        // Windows default means at that moment.
        var microphone = new MenuItem { Header = "Microphone" };
        menu.Opened += (_, _) =>
        {
            ApplyMenuTheme();
            PopulateRecentDictations(copyRecentDictation);
            PopulateMicrophones(microphone);
        };

        // Lets a user who dismissed the first-run intro reopen it to re-learn the gesture.
        var welcome = new MenuItem { Header = "Show welcome" };
        welcome.Click += (_, _) => WelcomeRequested?.Invoke();
        menu.Items.Add(welcome);
        menu.Items.Add(new Separator());

        var openStore = new MenuItem { Header = "Open in Microsoft Store" };
        openStore.Click += (_, _) => OpenStoreRequested?.Invoke();
        menu.Items.Add(openStore);

        var shareApp = new MenuItem { Header = "Share app" };
        shareApp.Click += (_, _) => ShareAppRequested?.Invoke();
        menu.Items.Add(shareApp);
        menu.Items.Add(new Separator());

        // The quick toggles, led by the microphone submenu built above.
        menu.Items.Add(microphone);

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

        _currentIcon = TrayIcons.CreateIdle();
        _icon = new TaskbarIcon
        {
            ToolTipText = "Scribe: ready",
            Icon = _currentIcon,
            ContextMenu = menu,
            MenuActivation = PopupActivationMode.RightClick,
        };

        // H.NotifyIcon 2.4.1 opens the menu and only then activates the popup that holds it
        // (TaskbarIcon.ShowContextMenu), so the menu never had keyboard focus: the arrow keys, Enter and Escape did
        // nothing until the pointer rested on an item, whether a right-click, Shift+F10 or the menu key opened it.
        // This event is raised once the popup is active.
        _icon.TrayContextMenuOpen += (_, _) => FocusMenu();
        _icon.ForceCreate(false);
    }

    private void FocusMenu()
    {
        try
        {
            _menu.Focus();
        }
        catch
        {
            // A menu that cannot take focus still works with the pointer; keyboard access is not worth breaking
            // right-click access over.
        }
    }


    private void OnApplicationThemeChanged(Wpf.Ui.Appearance.ApplicationTheme currentApplicationTheme, Color systemAccent) =>
        Dispatch(ApplyMenuTheme);

    private void ApplyMenuTheme()
    {
        try
        {
            ApplicationThemeManager.Apply(_menu);
        }
        catch
        {
            // The tray menu is a fallback path; a theme refresh failure must not break right-click access.
        }
    }

    /// <summary>Updates the tray icon and tooltip to match the current dictation state.</summary>
    public void SetState(DictationState state) => Dispatch(() =>
    {
        var (icon, tooltip) = state switch
        {
            DictationState.Recording => (TrayIcons.CreateRecording(), "Scribe: recording…"),
            DictationState.Processing => (TrayIcons.CreateProcessing(), "Scribe: transcribing…"),
            DictationState.Paused => (TrayIcons.CreatePaused(), "Scribe: paused"),
            _ => (TrayIcons.CreateIdle(), "Scribe: ready"),
        };

        var previous = _currentIcon;
        _currentIcon = icon;
        _icon.Icon = icon;
        _icon.ToolTipText = tooltip;

        RetireIcon(previous);

        _pauseItem.IsChecked = state == DictationState.Paused;
    });

    /// <summary>Reflects the persisted AI-cleanup setting in the quick-toggle check mark.</summary>
    public void SetAiCleanupChecked(bool enabled) => Dispatch(() => _aiItem.IsChecked = enabled);

    /// <summary>Surfaces a transient error to the user via the tray tooltip.</summary>
    public void ShowError(string message) => Dispatch(() =>
        _icon.ToolTipText = $"Scribe: {message}");

    /// <summary>Surfaces a transient, non-error status (e.g. an update is ready) via the tooltip.</summary>
    public void ShowInfo(string message) => Dispatch(() =>
        _icon.ToolTipText = $"Scribe: {message}");

    /// <summary>Shows a transient Windows notification for a completed user action.</summary>
    public void ShowNotification(string message, bool isError = false) => Dispatch(() =>
        _icon.ShowNotification(
            "Scribe",
            message,
            isError ? NotificationIcon.Error : NotificationIcon.Info,
            timeout: TimeSpan.FromSeconds(6)));

    /// <summary>
    /// Fills the "Copy recent dictation" submenu from the current ring snapshot. Runs on the UI
    /// thread (the menu's Opened event), so no dispatching is needed here.
    /// </summary>
    private void PopulateRecentDictations(MenuItem parent)
    {
        parent.Items.Clear();

        IReadOnlyList<string> recent;
        try
        {
            recent = RecentDictationsProvider?.Invoke() ?? [];
        }
        catch
        {
            // The submenu is a convenience view; a provider hiccup must never break opening the
            // tray menu, so degrade to the empty placeholder instead.
            recent = [];
        }

        if (recent.Count == 0)
        {
            parent.Items.Add(new MenuItem { Header = "No recent dictations", IsEnabled = false });
            return;
        }

        foreach (var transcript in recent)
        {
            // WPF treats "_" in a header as an access-key marker, so double it to render
            // dictated underscores literally. The click carries the full transcript; the
            // header is only the truncated single-line preview.
            var item = new MenuItem
            {
                Header = LastTranscriptStore.FormatPreview(transcript).Replace("_", "__"),
            };
            var fullText = transcript;
            item.Click += (_, _) => CopyRecentDictationRequested?.Invoke(fullText);
            parent.Items.Add(item);
        }
    }

    /// <summary>
    /// Fills the "Microphone" submenu: the Windows default first, naming the device it means now, then every microphone
    /// Windows offers, a check mark on the current choice, and a way into the Windows sound settings. Runs on the UI
    /// thread (the menu's Opened event). The entries are checkable so a screen reader announces which one is chosen;
    /// choosing the one already chosen changes nothing, and the menu is rebuilt on the next open either way.
    /// </summary>
    private void PopulateMicrophones(MenuItem parent)
    {
        parent.Items.Clear();

        MicrophoneMenu? picker;
        try
        {
            picker = MicrophoneMenuProvider?.Invoke();
        }
        catch
        {
            // Listing the devices can fail while the audio service restarts; the tray menu must still open, and the
            // Windows sound settings stay one click away.
            picker = null;
        }

        if (picker is null)
        {
            parent.Items.Add(new MenuItem { Header = "Microphones unavailable", IsEnabled = false });
        }
        else
        {
            for (var i = 0; i < picker.Choices.Count; i++)
            {
                var choice = picker.Choices[i];
                var item = new MenuItem
                {
                    // WPF reads "_" in a header as an access key, so a device name keeps its underscores doubled.
                    Header = choice.Label.Replace("_", "__"),
                    IsCheckable = true,
                    IsChecked = i == picker.SelectedIndex,

                    // Listed, and checked, so the saved choice stays visible while its device is away; there is nothing
                    // to choose about it until it comes back.
                    IsEnabled = choice.Kind != MicrophoneChoiceKind.Unavailable,
                };
                var selection = choice.Selection;
                item.Click += (_, _) => MicrophoneChosen?.Invoke(selection);
                parent.Items.Add(item);

                if (choice.Kind == MicrophoneChoiceKind.WindowsDefault && picker.Choices.Count > 1)
                {
                    parent.Items.Add(new Separator());
                }
            }
        }

        parent.Items.Add(new Separator());
        var soundSettings = new MenuItem { Header = "Sound settings" };
        soundSettings.Click += (_, _) => SoundSettingsRequested?.Invoke();
        parent.Items.Add(soundSettings);
    }

    /// <summary>
    /// Releases the icon replaced one update ago, rather than the one replaced just now.
    ///
    /// Assigning <see cref="TaskbarIcon.Icon"/> ends in a Shell_NotifyIcon call that pumps
    /// messages, so a state change dispatched from a background thread can run re-entrantly while
    /// the outer assignment is still reading the icon handle it was given. Disposing inline
    /// therefore raced the tray and threw ObjectDisposedException out of the state notification.
    /// Deferring by one generation guarantees the icon being freed is no longer the one any
    /// in-flight update is reading, and still frees every handle.
    /// </summary>
    private void RetireIcon(System.Drawing.Icon? replaced)
    {
        var due = _retiredIcon;
        _retiredIcon = replaced;

        if (!ReferenceEquals(due, _currentIcon))
        {
            due?.Dispose();
        }
    }

    // Inline on the UI thread, posted from anywhere else (see UiThreadDispatch). Never Dispatcher.Invoke.
    private void Dispatch(Action action) => _ui.Run(action);

    public void Dispose()
    {
        _disposed = true;
        ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
        _icon.Dispose();
        _retiredIcon?.Dispose();
        _retiredIcon = null;
        _currentIcon?.Dispose();
        _currentIcon = null;
    }
}
