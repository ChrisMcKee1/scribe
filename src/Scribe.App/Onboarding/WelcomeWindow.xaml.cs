using System.Windows;
using System.Windows.Interop;
using Scribe.Core.Settings;

namespace Scribe.App.Onboarding;

/// <summary>
/// One-time first-run welcome. Scribe is tray-only with no main window, so a brand-new user needs the gesture and the
/// tray entry points before the window gets out of the way.
/// </summary>
public partial class WelcomeWindow : Wpf.Ui.Controls.FluentWindow
{
    private const int WmDpiChanged = 0x02E0;

    private readonly Action _openSettings;
    private readonly Action _tryItNow;

    public WelcomeWindow((string Title, string Body) gesture, Action openSettings, Action? tryItNow = null)
    {
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        _tryItNow = tryItNow ?? openSettings;

        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        InitializeComponent();
        GestureTitle.Text = NormalizeGestureTitle(gesture.Title);
        GestureHint.Text = NormalizeGestureBody(gesture.Body);
        ApplyWindowFit();
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDpiChanged)
        {
            Dispatcher.BeginInvoke(ApplyWindowFit);
        }

        return IntPtr.Zero;
    }

    private void ApplyWindowFit()
    {
        var area = SystemParameters.WorkArea;
        var fit = WindowFit.Compute(560, 640, 440, 460, new WorkArea(area.Left, area.Top, area.Width, area.Height), Left, Top);
        MinWidth = fit.MinWidth;
        MinHeight = fit.MinHeight;
        Width = fit.Width;
        Height = fit.Height;
        Left = fit.Left;
        Top = fit.Top;
    }

    private static string NormalizeGestureTitle(string title)
    {
        if (title.Contains("press", StringComparison.OrdinalIgnoreCase) && title.Contains("again", StringComparison.OrdinalIgnoreCase))
        {
            return "Press, speak, press again";
        }

        return "Hold, speak, let go";
    }

    private static string NormalizeGestureBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "Hold your shortcut and start talking. Let go when you're done, and your words appear wherever your cursor is.";
        }

        return body.Replace("release", "let go", StringComparison.OrdinalIgnoreCase)
            .Replace("your words land", "your words appear", StringComparison.OrdinalIgnoreCase);
    }

    private void TryItButton_Click(object sender, RoutedEventArgs e)
    {
        _tryItNow();
        Close();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _openSettings();
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
