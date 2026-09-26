using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private DispatcherTimer? _windowFitTimer;
    private IntPtr _lastFitMonitor;

    private void InitializeWindowFit()
    {
        SourceInitialized += (_, _) =>
        {
            ApplyWindowFit(GetCursorMonitor(), center: true);
            _lastFitMonitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, MonitorDefaultToNearest);
        };
        DpiChanged += (_, _) => ApplyWindowFit(GetWindowMonitor(), center: false);
        if (_textScale is not null)
        {
            _textScale.Changed += TextScale_Changed;
        }

        LocationChanged += (_, _) => ScheduleMonitorFit();
        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
    }

    private void TextScale_Changed(object? sender, EventArgs e)
    {
        ApplyWindowFit(GetWindowMonitor(), center: false);
        UpdateProfileLayout();
        UpdateUsageMetricLayout();
        ApplyWordPackLayout();
    }

    private void ScheduleMonitorFit()
    {
        if (_closed)
        {
            return;
        }

        _windowFitTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _windowFitTimer.Tick -= WindowFitTimer_Tick;
        _windowFitTimer.Tick += WindowFitTimer_Tick;
        _windowFitTimer.Stop();
        _windowFitTimer.Start();
    }

    private void WindowFitTimer_Tick(object? sender, EventArgs e)
    {
        _windowFitTimer?.Stop();
        var monitor = GetWindowMonitor();
        if (monitor != _lastFitMonitor)
        {
            ApplyWindowFit(monitor, center: false);
        }
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closed)
            {
                ApplyWindowFit(GetWindowMonitor(), center: false);
            }
        }));

    private void SystemParameters_StaticPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SystemParameters.WorkArea), StringComparison.Ordinal))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_closed)
                {
                    ApplyWindowFit(GetWindowMonitor(), center: false);
                }
            }));
        }
    }

    private void CleanupWindowFit()
    {
        _windowFitTimer?.Stop();
        if (_textScale is not null)
        {
            _textScale.Changed -= TextScale_Changed;
        }

        SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
    }

    private void ApplyWindowFit(IntPtr monitor, bool center)
    {
        if (_closed)
        {
            return;
        }

        if (monitor == IntPtr.Zero || !TryGetMonitorWorkArea(monitor, out var workArea))
        {
            var fallback = SystemParameters.WorkArea;
            workArea = new WorkArea(fallback.Left, fallback.Top, fallback.Width, fallback.Height);
        }

        var result = WindowFit.Compute(
            WindowFit.DesiredWidth,
            WindowFit.DesiredHeight,
            WindowFit.MinimumWidth,
            WindowFit.MinimumHeight,
            workArea,
            center ? null : Left,
            center ? null : Top,
            _textScale?.Factor ?? 1);
        MinWidth = result.MinWidth;
        MinHeight = result.MinHeight;
        Width = result.Width;
        Height = result.Height;
        Left = result.Left;
        Top = result.Top;
        _lastFitMonitor = monitor;
    }

    private static bool TryGetMonitorWorkArea(IntPtr monitor, out WorkArea workArea)
    {
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            workArea = default;
            return false;
        }

        var scale = GetDpiScaleForMonitor(monitor);
        workArea = new WorkArea(
            info.rcWork.Left / scale,
            info.rcWork.Top / scale,
            (info.rcWork.Right - info.rcWork.Left) / scale,
            (info.rcWork.Bottom - info.rcWork.Top) / scale);
        return true;
    }

    private static double GetDpiScaleForMonitor(IntPtr monitor)
    {
        if (GetDpiForMonitor(monitor, 0, out var x, out _) == 0 && x > 0)
        {
            return x / 96.0;
        }

        return 1.0;
    }

    private static IntPtr GetCursorMonitor()
    {
        GetCursorPos(out var point);
        return MonitorFromPoint(point, MonitorDefaultToNearest);
    }

    private IntPtr GetWindowMonitor() =>
        MonitorFromWindow(new WindowInteropHelper(this).Handle, MonitorDefaultToNearest);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public int dwFlags;
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
