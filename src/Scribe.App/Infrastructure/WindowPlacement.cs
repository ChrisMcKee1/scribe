using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Scribe.Core.Settings;

namespace Scribe.App.Infrastructure;

internal static class WindowPlacement
{
    private const uint MonitorDefaultToNearest = 0x00000002;

    public static WorkArea WorkAreaFor(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var monitor = hwnd != IntPtr.Zero ? MonitorFromWindow(hwnd, MonitorDefaultToNearest) : MonitorFromPointer();
        if (monitor == IntPtr.Zero || !TryGetMonitorInfo(monitor, out var info))
        {
            var fallback = SystemParameters.WorkArea;
            return new WorkArea(fallback.Left, fallback.Top, fallback.Width, fallback.Height);
        }

        var source = hwnd == IntPtr.Zero ? null : HwndSource.FromHwnd(hwnd);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? ScaleFromVisual(window);
        var topLeft = fromDevice.Transform(new Point(info.Work.Left, info.Work.Top));
        var bottomRight = fromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom));
        return new WorkArea(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    private static Matrix ScaleFromVisual(Visual visual)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        return new Matrix(1 / dpi.DpiScaleX, 0, 0, 1 / dpi.DpiScaleY, 0, 0);
    }

    private static IntPtr MonitorFromPointer()
    {
        return GetCursorPos(out var point)
            ? MonitorFromPoint(point, MonitorDefaultToNearest)
            : IntPtr.Zero;
    }

    private static bool TryGetMonitorInfo(IntPtr monitor, out MonitorInfo info)
    {
        info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = false)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct POINT
    {
        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
