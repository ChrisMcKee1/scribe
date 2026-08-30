using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Scribe.App.Dictation;
using Scribe.Core.PostProcessing;
using Wpf.Ui.Appearance;

namespace Scribe.App.Tray;

/// <summary>
/// Owns the system-tray icon and its context menu, and reflects the current
/// <see cref="DictationState"/> through the icon and tooltip. All UI mutations are marshalled
/// to the WPF dispatcher because dictation state changes arrive on background threads.
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly ContextMenu _menu;
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _pauseItem;
    private readonly MenuItem _aiItem;
    private readonly MenuItem _textActionsItem;

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

    /// <summary>
    /// Raised when the user picks "Rewrite selected text". Hidden unless the feature is switched on,
    /// so a user who has never enabled it sees no menu entry for it.
    /// </summary>
    public event Action? TextActionsRequested;

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

    public TrayIconHost()
    {
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

        _textActionsItem = new MenuItem
        {
            Header = "Rewrite selected text",
            Visibility = Visibility.Collapsed,
        };
        _textActionsItem.Click += (_, _) => TextActionsRequested?.Invoke();
        menu.Items.Add(_textActionsItem);

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
        menu.Opened += (_, _) =>
        {
            ApplyMenuTheme();
            PopulateRecentDictations(copyRecentDictation);
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
        _icon.ForceCreate(false);
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

    /// <summary>Shows or hides the "Rewrite selected text" entry as the feature is toggled.</summary>
    public void SetTextActionsVisible(bool visible) => Dispatch(() =>
        _textActionsItem.Visibility = visible ? Visibility.Visible : Visibility.Collapsed);

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

    public void Dispose()
    {
        ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
        _icon.Dispose();
        _retiredIcon?.Dispose();
        _retiredIcon = null;
        _currentIcon?.Dispose();
        _currentIcon = null;
    }
}
