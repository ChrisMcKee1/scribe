using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
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
    private readonly ControlTemplate _menuItemTemplate;
    private readonly ControlTemplate _submenuHeaderTemplate;

    private bool _disposed;
    private System.Drawing.Icon? _currentIcon;
    private System.Drawing.Icon? _retiredIcon;
    private int _currentIconSize;
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
    public Func<bool>? CopyLastAvailableProvider { get; set; }

    public TrayIconHost(Action<Exception>? onUpdateFailure = null)
    {
        _ui = new UiThreadDispatch(
            isOnUiThread: () => Application.Current?.Dispatcher.CheckAccess() ?? true,
            post: work => Application.Current?.Dispatcher.BeginInvoke(work),
            isClosed: () => _disposed,
            onFailure: onUpdateFailure);

        _settingsCommand = new RelayCommand(() => SettingsRequested?.Invoke());
        _menuItemTemplate = CreateMenuItemTemplate(hasSubmenu: false);
        _submenuHeaderTemplate = CreateMenuItemTemplate(hasSubmenu: true);
        _menu = new ContextMenu();
        ApplyMenuTheme();
        ApplicationThemeManager.Changed += OnApplicationThemeChanged;
        _menu.Opened += (_, _) => RebuildMenu();
        SystemEvents.DisplaySettingsChanged += OnTrayIconSizeMayHaveChanged;
        SystemEvents.UserPreferenceChanged += OnTrayIconSizeMayHaveChanged;

        _currentIconSize = TrayIcons.GetPreferredSize();
        _currentIcon = TrayIcons.CreateIdle(_currentIconSize);
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

    private void OnTrayIconSizeMayHaveChanged(object? sender, EventArgs e) => Dispatch(ReloadIconIfSizeChanged);

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
            CopyLastAvailableProvider?.Invoke() ?? true,
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
        ApplyTrayTemplate(menuItem, item.Kind == TrayItemKind.Submenu);

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
        ApplyTrayTemplate(menuItem, item.Kind == TrayItemKind.Submenu);
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
        ApplyTrayTemplate(parent, hasSubmenu: true);
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
                ApplyTrayTemplate(child, hasSubmenu: false);
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
        ApplyTrayTemplate(soundSettings, hasSubmenu: false);
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

    private void ApplyTrayTemplate(MenuItem item, bool hasSubmenu)
    {
        item.Template = hasSubmenu ? _submenuHeaderTemplate : _menuItemTemplate;
    }

    // Adapted from WPF-UI 4.3.0's SubmenuItemTemplateKey and SubmenuHeaderTemplateKey (Controls/Menu/MenuItem.xaml): the
    // same margins, highlight, pressed and disabled resources, flyout and passive scroll viewer, with ONE change: WPF-UI
    // gives a checkable item a check box in its own leading column and every other item no column at all, so labels
    // started at two different x positions. Here every item and header reserves one leading column, which shows a
    // check mark only while the item is checked. IsCheckable stays on the item, so UI Automation still reports it.
    private static ControlTemplate CreateMenuItemTemplate(bool hasSubmenu)
    {
        var chevronColumn = hasSubmenu ? "<ColumnDefinition Width=\"Auto\"/>" : string.Empty;
        var chevron = hasSubmenu
            ? """
                            <ui:SymbolIcon x:Name="Chevron" Grid.Column="2" Margin="0,3,0,0" VerticalAlignment="Center"
                                           FontSize="{TemplateBinding FontSize}" Symbol="ChevronRight20"/>
"""
            : string.Empty;
        var popup = hasSubmenu
            ? """
                <Popup x:Name="Popup" Grid.Row="1" AllowsTransparency="True" Focusable="False"
                       IsOpen="{TemplateBinding IsSubmenuOpen}" Placement="Right"
                       PlacementTarget="{Binding ElementName=MenuItemContent}" PopupAnimation="None" VerticalOffset="-20">
                    <Grid>
                        <Border x:Name="SubmenuBorder" Margin="12,10,12,30" Padding="0,3,0,3"
                                Background="{DynamicResource FlyoutBackground}" BorderBrush="{DynamicResource FlyoutBorderBrush}"
                                BorderThickness="1" CornerRadius="8" SnapsToDevicePixels="True">
                            <ui:PassiveScrollViewer CanContentScroll="True" Style="{DynamicResource UiMenuItemScrollViewer}">
                                <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle"/>
                            </ui:PassiveScrollViewer>
                            <Border.Effect>
                                <DropShadowEffect BlurRadius="20" Direction="270" Opacity="0.135" ShadowDepth="10" Color="#202020"/>
                            </Border.Effect>
                        </Border>
                    </Grid>
                </Popup>
"""
            : string.Empty;
        var disabledChevron = hasSubmenu
            ? """
                        <Setter TargetName="Chevron" Property="Foreground">
                            <Setter.Value>
                                <SolidColorBrush Color="{DynamicResource TextFillColorDisabled}"/>
                            </Setter.Value>
                        </Setter>
"""
            : string.Empty;
        var xaml = $$"""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
                             TargetType="{x:Type MenuItem}">
                <Grid>
                    <Grid.RowDefinitions>
                        <RowDefinition Height="*"/>
                        <RowDefinition Height="Auto"/>
                    </Grid.RowDefinitions>
                    <Border x:Name="Border" Grid.Row="1" Margin="4,1,4,1" Background="Transparent" CornerRadius="4">
                        <Grid x:Name="MenuItemContent" Margin="8,6,8,6">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="26"/>
                                <ColumnDefinition Width="*"/>{{chevronColumn}}
                            </Grid.ColumnDefinitions>
                            <ui:SymbolIcon x:Name="CheckGlyph" Grid.Column="0" Symbol="Checkmark20" FontSize="16"
                                           HorizontalAlignment="Left" VerticalAlignment="Center" Visibility="Collapsed"/>
                            <ContentPresenter x:Name="Header" Grid.Column="1" ContentSource="Header" RecognizesAccessKey="True"
                                              Margin="0,0,16,0" VerticalAlignment="Center"
                                              TextElement.Foreground="{TemplateBinding Foreground}"/>{{chevron}}
                        </Grid>
                    </Border>{{popup}}
                </Grid>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsHighlighted" Value="True">
                        <Setter TargetName="Border" Property="Background" Value="{DynamicResource MenuBarItemBackgroundSelected}"/>
                    </Trigger>
                    <MultiTrigger>
                        <MultiTrigger.Conditions>
                            <Condition Property="IsMouseOver" Value="True"/>
                            <Condition Property="IsPressed" Value="False"/>
                        </MultiTrigger.Conditions>
                        <Setter TargetName="Border" Property="Background" Value="{DynamicResource MenuBarItemBackgroundSelected}"/>
                    </MultiTrigger>
                    <MultiTrigger>
                        <MultiTrigger.Conditions>
                            <Condition Property="IsMouseOver" Value="True"/>
                            <Condition Property="IsPressed" Value="True"/>
                        </MultiTrigger.Conditions>
                        <Setter TargetName="Border" Property="Background" Value="{DynamicResource MenuBarItemBackgroundPressed}"/>
                        <Setter TargetName="Header" Property="TextElement.Foreground" Value="{DynamicResource MenuBarItemTextForegroundPressed}"/>
                    </MultiTrigger>
                    <Trigger Property="IsChecked" Value="True">
                        <Setter TargetName="CheckGlyph" Property="Visibility" Value="Visible"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                        <Setter Property="Foreground">
                            <Setter.Value>
                                <SolidColorBrush Color="{DynamicResource TextFillColorDisabled}"/>
                            </Setter.Value>
                        </Setter>{{disabledChevron}}
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
""";
        return (ControlTemplate)XamlReader.Parse(xaml);
    }

    public void SetState(DictationState state) => Dispatch(() =>
    {
        _state = state;
        _currentIconSize = TrayIcons.GetPreferredSize();
        var icon = CreateIconForState(state, _currentIconSize);

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

    private void ReloadIconIfSizeChanged()
    {
        if (_disposed)
        {
            return;
        }

        var size = TrayIcons.GetPreferredSize();
        if (size == _currentIconSize)
        {
            return;
        }

        var previousSize = _currentIconSize;
        _currentIconSize = size;
        var icon = CreateIconForState(_state, size);
        var previous = _currentIcon;
        _currentIcon = icon;
        _icon.Icon = icon;
        RetireIcon(previous);
        TryLogIconSizeChange(previousSize, size);
    }

    private static System.Drawing.Icon CreateIconForState(DictationState state, int size) => state switch
    {
        DictationState.Recording => TrayIcons.CreateRecording(size),
        DictationState.Processing => TrayIcons.CreateProcessing(size),
        DictationState.Paused => TrayIcons.CreatePaused(size),
        _ => TrayIcons.CreateIdle(size),
    };

    private static void TryLogIconSizeChange(int previousSize, int size)
    {
        try
        {
            App.LogSink?.CreateLogger(nameof(TrayIconHost))
                .LogInformation("Tray icon size changed from {PreviousSize} px to {Size} px.", previousSize, size);
        }
        catch
        {
            // A diagnostics failure must never break the tray icon.
        }
    }

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

    public void ShowError(string message) => Dispatch(() => _icon.ToolTipText = $"Scribe: {message}");

    public void ShowInfo(string message) => Dispatch(() => _icon.ToolTipText = $"Scribe: {message}");

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
        SystemEvents.DisplaySettingsChanged -= OnTrayIconSizeMayHaveChanged;
        SystemEvents.UserPreferenceChanged -= OnTrayIconSizeMayHaveChanged;
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
