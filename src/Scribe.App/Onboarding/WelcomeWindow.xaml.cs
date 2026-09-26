using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Scribe.App.Infrastructure;
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
    private DoubleAnimation? _welcomeLastAnimation;
    private bool _welcomeAnimationStarted;
    private bool _welcomeAnimationFinished;

    public WelcomeWindow((string Title, string Body) gesture, Action openSettings, Action? tryItNow = null)
    {
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        _tryItNow = tryItNow ?? openSettings;

        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this, Wpf.Ui.Controls.WindowBackdropType.Mica, updateAccents: false);
        InitializeComponent();
        GestureTitle.Text = NormalizeGestureTitle(gesture.Title);
        GestureHint.Text = NormalizeGestureBody(gesture.Body);
        ApplyWindowFit();
        Loaded += WelcomeWindow_Loaded;
        StateChanged += WelcomeWindow_StateChanged;
        IsVisibleChanged += WelcomeWindow_IsVisibleChanged;
        Closed += WelcomeWindow_Closed;
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    private void WelcomeWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_welcomeAnimationStarted)
        {
            return;
        }

        _welcomeAnimationStarted = true;
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast || !IsVisible)
        {
            ShowStaticWelcomeMark();
            return;
        }

        WelcomeStaticMark.Visibility = Visibility.Collapsed;
        WelcomeAnimatedMark.Visibility = Visibility.Visible;
        var scales = WelcomeBarScales();
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (var i = 0; i < scales.Length; i++)
        {
            var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd,
            };
            if (i == scales.Length - 1)
            {
                animation.Completed += WelcomeBarsAnimation_Completed;
                _welcomeLastAnimation = animation;
            }

            scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
    }

    private void WelcomeWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            FinishWelcomeAnimation();
        }
    }

    private void WelcomeWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            FinishWelcomeAnimation();
        }
    }

    private void WelcomeWindow_Closed(object? sender, EventArgs e)
    {
        FinishWelcomeAnimation();
        Loaded -= WelcomeWindow_Loaded;
        StateChanged -= WelcomeWindow_StateChanged;
        IsVisibleChanged -= WelcomeWindow_IsVisibleChanged;
        Closed -= WelcomeWindow_Closed;
    }

    private void WelcomeBarsAnimation_Completed(object? sender, EventArgs e) => FinishWelcomeAnimation();

    private ScaleTransform[] WelcomeBarScales() =>
    [
        WelcomeBar1Scale,
        WelcomeBar2Scale,
        WelcomeBar3Scale,
        WelcomeBar4Scale,
        WelcomeBar5Scale,
    ];

    private void FinishWelcomeAnimation()
    {
        if (_welcomeAnimationFinished)
        {
            return;
        }

        _welcomeAnimationFinished = true;
        if (_welcomeLastAnimation is not null)
        {
            _welcomeLastAnimation.Completed -= WelcomeBarsAnimation_Completed;
            _welcomeLastAnimation = null;
        }

        foreach (var scale in WelcomeBarScales())
        {
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleY = 1;
        }

        ShowStaticWelcomeMark();
    }

    private void ShowStaticWelcomeMark()
    {
        WelcomeStaticMark.Visibility = Visibility.Visible;
        WelcomeAnimatedMark.Visibility = Visibility.Collapsed;
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
        var area = WindowPlacement.WorkAreaFor(this);
        var fit = WindowFit.Compute(560, 640, 440, 460, area, Left, Top);
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

