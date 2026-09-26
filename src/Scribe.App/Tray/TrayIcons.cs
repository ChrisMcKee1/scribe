using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace Scribe.App.Tray;

/// <summary>
/// Supplies the Scribe brand mark and its state variants as native <see cref="Icon"/>s for
/// H.NotifyIcon.
/// </summary>
/// <remarks>
/// Every call hands back a <b>fresh</b> icon, and the caller owns it. H.NotifyIcon disposes the
/// icon it replaces, so a single long-lived instance per state is destroyed the first time that
/// state is replaced; assigning it again reads a zeroed handle and throws
/// <see cref="ObjectDisposedException"/> from inside the tray update. That exception escapes the
/// dictation state notification and stops every later subscriber, which is what left the recording
/// pill frozen on its last state while dictation itself kept working.
///
/// The icon is loaded at the notification area's current small-icon size, rather than handing the
/// shell a 64 px frame to scale down. That lets the hand-tuned 16, 20, 24 and 32 px frames draw when
/// the taskbar asks for them.
/// </remarks>
internal static class TrayIcons
{
    private static readonly byte[]? IdleData = ReadResource("scribe.ico");
    private static readonly byte[]? RecordingData = ReadResource("scribe-recording.ico");
    private static readonly byte[]? ProcessingData = ReadResource("scribe-processing.ico");
    private static readonly byte[]? PausedData = ReadResource("scribe-paused.ico");

    /// <summary>Neutral idle icon (ready to dictate).</summary>
    public static Icon CreateIdle() => CreateIdle(GetPreferredSize());

    /// <inheritdoc cref="CreateIdle()"/>
    public static Icon CreateIdle(int size) => Create(IdleData, size);

    /// <summary>Recording icon (capture in progress).</summary>
    public static Icon CreateRecording() => CreateRecording(GetPreferredSize());

    /// <inheritdoc cref="CreateRecording()"/>
    public static Icon CreateRecording(int size) => Create(RecordingData, size);

    /// <summary>Processing icon (transcribing / injecting).</summary>
    public static Icon CreateProcessing() => CreateProcessing(GetPreferredSize());

    /// <inheritdoc cref="CreateProcessing()"/>
    public static Icon CreateProcessing(int size) => Create(ProcessingData, size);

    /// <summary>Paused icon with the waveform muted to slate.</summary>
    public static Icon CreatePaused() => CreatePaused(GetPreferredSize());

    /// <inheritdoc cref="CreatePaused()"/>
    public static Icon CreatePaused(int size) => Create(PausedData, size);

    public static int GetPreferredSize() => TrayIconSize.Resolve();

    // Read once into memory so a state change never touches the assembly manifest again.
    private static byte[]? ReadResource(string fileName)
    {
        try
        {
            var resourceName = $"Scribe.App.Assets.{fileName}";
            using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch
        {
            // A missing or unreadable resource degrades to the framework icon rather than
            // preventing the tray from being created at all.
            return null;
        }
    }

    private static Icon Create(byte[]? data, int size)
    {
        if (data is not null)
        {
            try
            {
                using var stream = new MemoryStream(data);
                return new Icon(stream, size, size);
            }
            catch
            {
                // Fall through to the framework icon.
            }
        }

        // A damaged resource must not prevent the tray app from starting. This icon is always
        // available from the framework and keeps the application controllable so it can quit.
        return (Icon)SystemIcons.Application.Clone();
    }

    private static class TrayIconSize
    {
        private const int SmCxSmallIcon = 49;
        private const int MonitorDefaultToPrimary = 1;
        private const int MdtEffectiveDpi = 0;
        private const int DefaultDpi = 96;
        private const int DefaultSmallIcon = 16;

        public static int Resolve()
        {
            var dpi = GetPrimaryMonitorDpi();
            try
            {
                var size = NativeMethods.GetSystemMetricsForDpi(SmCxSmallIcon, (uint)dpi);
                if (size > 0)
                {
                    return size;
                }
            }
            catch
            {
                // Fall back below. Older shells should still get a correctly scaled frame.
            }

            return Math.Max(DefaultSmallIcon, (int)Math.Round(DefaultSmallIcon * dpi / (double)DefaultDpi));
        }

        private static int GetPrimaryMonitorDpi()
        {
            try
            {
                var monitor = NativeMethods.MonitorFromPoint(new Point(0, 0), MonitorDefaultToPrimary);
                if (monitor != IntPtr.Zero &&
                    NativeMethods.GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 &&
                    dpiX > 0)
                {
                    return (int)dpiX;
                }
            }
            catch
            {
                // Try the process-wide system DPI next.
            }

            try
            {
                var dpi = NativeMethods.GetDpiForSystem();
                if (dpi > 0)
                {
                    return (int)dpi;
                }
            }
            catch
            {
                // Use the baseline DPI below.
            }

            return DefaultDpi;
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr MonitorFromPoint(Point pt, int flags);

        [DllImport("shcore.dll", SetLastError = true)]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetDpiForSystem();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Point(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }
}
