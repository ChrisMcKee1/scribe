using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Scribe.App.Dictation;
using Scribe.Core.Lifecycle;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.Tray;
using Wpf.Ui.Appearance;

namespace Scribe.App.Tray;

/// <summary>
/// Owns the system-tray icon and its context menu, and reflects the current <see cref="DictationState"/> through the
/// icon and tooltip. UI mutations requested from a background thread are posted to the WPF dispatcher, never invoked
/// synchronously: dictation state, errors and warnings arrive on the audio capture thread and dictation processing,
/// and the UI thread waits for both at shutdown, so a caller parked on the UI thread there would never be released.
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly ContextMenu _menu;
    private readonly TaskbarIcon _icon;
    private readonly UiThreadDispatch _ui;
    private readonly ICommand _settingsCommand;

    private bool _disposed;
    private System.Drawing.Icon? _currentIcon;
    private System.Drawing.Icon? _retiredIcon;
    private DictationState _state = DictationState.Idle;
    private bool _aiCleanupEnabled;
    private bool _updateReady;
    private string? _updateVersion;
    private string? _shortcutSentence;
    private HotkeyMode _hotkeyMode = HotkeyMode.Hold;
    private TrayCondition _condition;

    public event Action? QuitRequested;
    public event Action? SettingsRequested;
#pragma warning disable CS0067
    public event Action? LearnFromHistoryRequested;
    public event Action? AddToDictionaryRequested;
    public event Action? CopyLastDictationRequested;
    public event Action<string>? CopyRecentDictationRequested;
    public Func<IReadOnlyList<string>>? RecentDictationsProvider { get; set; }
    public event Action? WelcomeRequested;
    public event Action? OpenStoreRequested;
    public event Action? ShareAppRequested;
#pragma warning restore CS0067
    public event Action<bool>? PauseToggled;
    public event Action<bool>? AiCleanupToggled;
    public Func<MicrophoneMenu>? MicrophoneMenuProvider { get; set; }
    public event Action<MicrophoneSelection>? MicrophoneChosen;
    public event Action? SoundSettingsRequested;

    public event Action? RestartToUpdateRequested;
    public event Action? OpenHistoryRequested;
    public event Action? SetUpAiCleanupRequested;

    public Func<TrayAiCleanupItem>? AiCleanupItemProvider { get; set; }
    public Func<bool>? UpdateReadyProvider { get; set; }
    public Func<string?>? UpdateVersionProvider { get; set; }
    public Func<TrayCondition>? ConditionProvider { get; set; }

    public TrayIconHost(Action<Exception>? onUpdateFailure = null)
    {
        _ui = new UiThreadDispatch(
            isOnUiThread: () => Application.Current?.Dispatcher.CheckAccess() ?? true,
            post: work => Application.Current?.Dispatcher.BeginInvoke(work),
            isClosed: () => _disposed,
            onFailure: onUpdateFailure);

        _settingsCommand = new RelayCommand(() => SettingsRequested?.Invoke());
        _menu = new ContextMenu();
        ApplyMenuTheme();
        ApplicationThemeManager.Changed += OnApplicationThemeChanged;
        _menu.Opened += (_, _) => RebuildMenu();

        _currentIcon = TrayIcons.CreateIdle();
        _icon = new TaskbarIcon
        {
            ToolTipText = ComposeToolTip(),
            Icon = _currentIcon,
            ContextMenu = _menu,
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
            LeftClickCommand = _settingsCommand,
            DoubleClickCommand = _settingsCommand,
        };

        _icon.TrayContextMenuOpen += (_, _) => FocusMenu();
        _icon.ForceCreate(false);
        RebuildMenu();
    }

    private void FocusMenu()
    {
        if (_disposed || !_menu.IsOpen)
        {
            return;
        }

        try
        {
            _menu.Focus();
        }
        catch
        {
            // A menu that cannot take focus still works with the pointer; keyboard access must not break right-click.
        }
    }

    private void OnApplicationThemeChanged(Wpf.Ui.Appearance.ApplicationTheme currentApplicationTheme, Color systemAccent) =>
        Dispatch(() =>
        {
            ApplyMenuTheme();
            RebuildMenu();
        });

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

    private void RebuildMenu()
    {
        if (_disposed)
        {
            return;
        }

        ApplyMenuTheme();
        _menu.Items.Clear();
        var recent = ReadRecentDictations();
        _updateReady = UpdateReadyProvider?.Invoke() ?? _updateReady;
        _updateVersion = UpdateVersionProvider?.Invoke() ?? _updateVersion;
        _condition = ConditionProvider?.Invoke() ?? _condition;
        var state = new TrayMenuState(
            _updateReady,
            recent.Count > 0,
            CurrentAiCleanupItem(),
            _state == DictationState.Paused,
            recent.Take(5).Select(text => LastTranscriptStore.FormatPreview(text, maxLength: 42)).ToArray());

        foreach (var item in TrayMenu.Build(state).Items)
        {
            AddMenuItem(_menu.Items, item, recent);
        }
    }

    private IReadOnlyList<string> ReadRecentDictations()
    {
        try
        {
            return RecentDictationsProvider?.Invoke() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private TrayAiCleanupItem CurrentAiCleanupItem()
    {
        if (AiCleanupItemProvider is { } provider)
        {
            try
            {
                return provider();
            }
            catch
            {
                return new TrayAiCleanupItem(TrayAiCleanupKind.Toggle, "AI cleanup", _aiCleanupEnabled, true);
            }
        }

        return new TrayAiCleanupItem(TrayAiCleanupKind.Toggle, "AI cleanup", _aiCleanupEnabled, true);
    }

    private void AddMenuItem(ItemCollection target, TrayMenuItem item, IReadOnlyList<string> recent)
    {
        if (item.Kind == TrayItemKind.Separator)
        {
            target.Add(new Separator());
            return;
        }

        if (item.Command == TrayCommand.Microphone)
        {
            target.Add(BuildMicrophoneMenu(item));
            return;
        }

        var menuItem = new MenuItem
        {
            Header = HeaderWithAccessKey(item.Label, item.AccessKey),
            IsEnabled = item.Enabled,
            FontWeight = item.IsDefault ? FontWeights.SemiBold : FontWeights.Normal,
            IsCheckable = item.Kind == TrayItemKind.Check,
            IsChecked = item.Kind == TrayItemKind.Check && item.IsChecked,
        };
        ApplyCheckIcon(menuItem, item.Kind == TrayItemKind.Check, item.IsChecked);

        if (item.Kind == TrayItemKind.Submenu && item.Children is { Count: > 0 })
        {
            for (var i = 0; i < item.Children.Count; i++)
            {
                AddRecentMenuChild(menuItem.Items, item.Children[i], i, recent);
            }
        }
        else if (item.Command is { } command)
        {
            menuItem.Click += (_, _) => RunCommand(command, menuItem, null);
        }

        target.Add(menuItem);
    }

    private void AddRecentMenuChild(ItemCollection target, TrayMenuItem item, int index, IReadOnlyList<string> recent)
    {
        if (item.Kind == TrayItemKind.Separator)
        {
            target.Add(new Separator());
            return;
        }

        var menuItem = new MenuItem
        {
            Header = EscapeHeader(item.Label),
            IsEnabled = item.Enabled,
        };
        if (item.Command == TrayCommand.CopyRecentDictation && index < recent.Count)
        {
            var text = recent[index];
            menuItem.Click += (_, _) => RunCommand(TrayCommand.CopyRecentDictation, menuItem, text);
        }
        else if (item.Command is { } command)
        {
            menuItem.Click += (_, _) => RunCommand(command, menuItem, null);
        }

        target.Add(menuItem);
    }

    private MenuItem BuildMicrophoneMenu(TrayMenuItem item)
    {
        var parent = new MenuItem { Header = HeaderWithAccessKey(item.Label, item.AccessKey), IsEnabled = item.Enabled };
        MicrophoneMenu? picker;
        try
        {
            picker = MicrophoneMenuProvider?.Invoke();
        }
        catch
        {
            picker = null;
        }

        if (picker is null)
        {
            parent.Items.Add(new MenuItem { Header = "Couldn't list microphones", IsEnabled = false });
        }
        else
        {
            for (var i = 0; i < picker.Choices.Count; i++)
            {
                var choice = picker.Choices[i];
                var child = new MenuItem
                {
                    Header = EscapeHeader(TrayMicrophoneLabel(choice.Label)),
                    IsCheckable = true,
                    IsChecked = i == picker.SelectedIndex,
                    IsEnabled = choice.Kind != MicrophoneChoiceKind.Unavailable,
                };
                ApplyCheckIcon(child, isCheckable: true, child.IsChecked);
                var selection = choice.Selection;
                child.Click += (_, _) => MicrophoneChosen?.Invoke(selection);
                parent.Items.Add(child);
                if (choice.Kind == MicrophoneChoiceKind.WindowsDefault && picker.Choices.Count > 1)
                {
                    parent.Items.Add(new Separator());
                }
            }
        }

        parent.Items.Add(new Separator());
        var soundSettings = new MenuItem { Header = "Windows _sound settings" };
        soundSettings.Click += (_, _) => SoundSettingsRequested?.Invoke();
        parent.Items.Add(soundSettings);
        return parent;
    }

    private static string TrayMicrophoneLabel(string label) => label.Replace(" (default)", string.Empty, StringComparison.Ordinal);

    private void RunCommand(TrayCommand command, MenuItem item, string? payload)
    {
        switch (command)
        {
            case TrayCommand.RestartToUpdate:
                RestartToUpdateRequested?.Invoke();
                break;
            case TrayCommand.Settings:
                SettingsRequested?.Invoke();
                break;
            case TrayCommand.AddToDictionary:
                AddToDictionaryRequested?.Invoke();
                break;
            case TrayCommand.CopyLastDictation:
                CopyLastDictationRequested?.Invoke();
                break;
            case TrayCommand.CopyRecentDictation:
                if (payload is not null) CopyRecentDictationRequested?.Invoke(payload);
                break;
            case TrayCommand.OpenHistory:
                OpenHistoryRequested?.Invoke();
                break;
            case TrayCommand.AiCleanup:
                _aiCleanupEnabled = item.IsChecked;
                AiCleanupToggled?.Invoke(item.IsChecked);
                break;
            case TrayCommand.SetUpAiCleanup:
                SetUpAiCleanupRequested?.Invoke();
                break;
            case TrayCommand.PauseDictation:
                PauseToggled?.Invoke(item.IsChecked);
                break;
            case TrayCommand.Quit:
                QuitRequested?.Invoke();
                break;
        }
    }

    private static string HeaderWithAccessKey(string label, char? accessKey)
    {
        var escaped = EscapeHeader(label);
        if (accessKey is null)
        {
            return escaped;
        }

        var target = char.ToUpperInvariant(accessKey.Value);
        for (var i = 0; i < escaped.Length; i++)
        {
            if (char.ToUpperInvariant(escaped[i]) == target)
            {
                return escaped[..i] + "_" + escaped[i..];
            }
        }

        return escaped;
    }

    private static string EscapeHeader(string label) => label.Replace("_", "__", StringComparison.Ordinal);

    private static void ApplyCheckIcon(MenuItem item, bool isCheckable, bool isChecked)
    {
        if (!isCheckable)
        {
            item.Icon = new TextBlock { Width = 16 };
            return;
        }

        item.Icon = isChecked
            ? new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Checkmark24, FontSize = 16 }
            : new TextBlock { Width = 16 };
    }

    public void SetState(DictationState state) => Dispatch(() =>
    {
        _state = state;
        var icon = state switch
        {
            DictationState.Recording => TrayIcons.CreateRecording(),
            DictationState.Processing => TrayIcons.CreateProcessing(),
            DictationState.Paused => TrayIcons.CreatePaused(),
            _ => TrayIcons.CreateIdle(),
        };

        var previous = _currentIcon;
        _currentIcon = icon;
        _icon.Icon = icon;
        _icon.ToolTipText = ComposeToolTip();
        RetireIcon(previous);
        if (_menu.IsOpen)
        {
            RebuildMenu();
        }
    });

    public void SetShortcutSentence(string? shortcutSentence, HotkeyMode mode) => Dispatch(() =>
    {
        _shortcutSentence = shortcutSentence;
        _hotkeyMode = mode;
        _icon.ToolTipText = ComposeToolTip();
    });

    public void SetCondition(TrayCondition condition, string? version = null) => Dispatch(() =>
    {
        _condition = condition;
        if (!string.IsNullOrWhiteSpace(version))
        {
            _updateVersion = version;
        }

        _icon.ToolTipText = ComposeToolTip();
    });

    public void SetUpdateReady(bool ready, string? version = null) => Dispatch(() =>
    {
        _updateReady = ready;
        _updateVersion = version ?? _updateVersion;
        _condition = ready ? TrayCondition.UpdateReady : _condition == TrayCondition.UpdateReady ? TrayCondition.None : _condition;
        _icon.ToolTipText = ComposeToolTip();
        if (_menu.IsOpen) RebuildMenu();
    });

    private string ComposeToolTip() => TrayToolTip.Compose(ToTrayState(_state), _shortcutSentence, _hotkeyMode, _condition, _updateVersion);

    private static TrayState ToTrayState(DictationState state) => state switch
    {
        DictationState.Recording => TrayState.Recording,
        DictationState.Processing => TrayState.Processing,
        DictationState.Paused => TrayState.Paused,
        _ => TrayState.Ready,
    };

    public void SetAiCleanupChecked(bool enabled) => Dispatch(() =>
    {
        _aiCleanupEnabled = enabled;
        if (_menu.IsOpen) RebuildMenu();
    });

    public void ShowError(string message) => Dispatch(() => _icon.ShowNotification("Scribe", message, NotificationIcon.Error, timeout: TimeSpan.FromSeconds(6)));

    public void ShowInfo(string message) => Dispatch(() => _icon.ShowNotification("Scribe", message, NotificationIcon.Info, timeout: TimeSpan.FromSeconds(6)));

    public void ShowNotification(string message, bool isError = false) => Dispatch(() =>
        _icon.ShowNotification("Scribe", message, isError ? NotificationIcon.Error : NotificationIcon.Info, timeout: TimeSpan.FromSeconds(6)));

    private void RetireIcon(System.Drawing.Icon? replaced)
    {
        var due = _retiredIcon;
        _retiredIcon = replaced;
        if (!ReferenceEquals(due, _currentIcon))
        {
            due?.Dispose();
        }
    }

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

    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}


