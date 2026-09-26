using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scribe.Core.Settings;
using Wpf.Ui.Controls;

namespace Scribe.App.Settings;

public partial class SettingsStatusRow : UserControl
{
    private AiCleanupAction? _primary;
    private AiCleanupAction? _secondary;

    public SettingsStatusRow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutMode();
    }

    public event EventHandler<AiCleanupActionId>? PrimaryActionInvoked;

    public event EventHandler<AiCleanupActionId>? SecondaryActionInvoked;

    public void Show(AiCleanupStatusRow? row)
    {
        if (row is null)
        {
            Visibility = Visibility.Collapsed;
            _primary = null;
            _secondary = null;
            return;
        }

        Visibility = Visibility.Visible;
        StatusTextBlock.Text = row.Text;
        _primary = row.Primary;
        _secondary = row.Secondary;
        var busy = row.Kind == AiCleanupStatusKind.Busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        if (!busy)
        {
            ApplyIcon(row.Kind);
        }

        ConfigureButton(PrimaryButton, row.Primary, busy);
        ConfigureButton(SecondaryButton, row.Secondary, busy);
        UpdateLayoutMode();
    }

    private void ApplyIcon(AiCleanupStatusKind kind)
    {
        var (symbol, brushKey) = kind switch
        {
            AiCleanupStatusKind.Success => (SymbolRegular.CheckmarkCircle24, "SystemFillColorSuccessBrush"),
            AiCleanupStatusKind.Warning => (SymbolRegular.Warning24, "SystemFillColorCautionBrush"),
            AiCleanupStatusKind.Error => (SymbolRegular.ErrorCircle24, "SystemFillColorCriticalBrush"),
            _ => (SymbolRegular.Info24, "TextFillColorSecondaryBrush"),
        };
        StatusIcon.Symbol = symbol;
        StatusIcon.SetResourceReference(ForegroundProperty, brushKey);
    }

    private static void ConfigureButton(System.Windows.Controls.Button button, AiCleanupAction? action, bool busy)
    {
        button.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        if (action is null)
        {
            return;
        }

        button.Content = action.Text;
        button.IsEnabled = action.IsEnabled && !busy;
    }

    private void UpdateLayoutMode()
    {
        var narrow = ActualWidth > 0 && ActualWidth < 480;
        Grid.SetColumn(ActionPanel, narrow ? 1 : 2);
        Grid.SetRow(ActionPanel, narrow ? 1 : 0);
        ActionPanel.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(12, 0, 0, 0);
        ActionPanel.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_primary is not null)
        {
            PrimaryActionInvoked?.Invoke(this, _primary.Id);
        }
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_secondary is not null)
        {
            SecondaryActionInvoked?.Invoke(this, _secondary.Id);
        }
    }
}
