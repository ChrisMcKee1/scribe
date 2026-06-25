using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Scribe.App.Tray;

/// <summary>
/// Builds the tray icons as GDI+-drawn microphone glyphs converted to native
/// <see cref="Icon"/>s. H.NotifyIcon's <c>IconSource</c> path only accepts URI-backed images,
/// so the app sets the native <c>Icon</c> property instead and ships no <c>.ico</c> assets.
/// One colour per dictation state: neutral idle, red recording, amber busy.
/// </summary>
internal static class TrayIcons
{
    // Drawn in a 32-unit coordinate space, rendered onto a 64px bitmap (2x) for high-DPI crispness.
    private const int LogicalSize = 32;
    private const int PixelSize = 64;

    /// <summary>Neutral idle icon (ready to dictate).</summary>
    public static Icon Idle { get; } = Build(Color.FromArgb(0x9A, 0xA0, 0xA6));

    /// <summary>Recording icon (capture in progress).</summary>
    public static Icon Recording { get; } = Build(Color.FromArgb(0xE5, 0x48, 0x4D));

    /// <summary>Processing icon (transcribing / injecting).</summary>
    public static Icon Processing { get; } = Build(Color.FromArgb(0xF2, 0xA6, 0x0C));

    private static Icon Build(Color color)
    {
        using var bitmap = new Bitmap(PixelSize, PixelSize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.ScaleTransform((float)PixelSize / LogicalSize, (float)PixelSize / LogicalSize);

            using var brush = new SolidBrush(color);
            using var pen = new Pen(color, 2.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };

            using var body = RoundedRect(new RectangleF(12, 4, 8, 14), 4);
            g.FillPath(brush, body);            // microphone capsule
            g.DrawArc(pen, 9, 9, 14, 14, 20, 140); // cradle under the capsule
            g.DrawLine(pen, 16, 23, 16, 27);    // stem
            g.DrawLine(pen, 11, 27, 21, 27);    // base
        }

        var handle = bitmap.GetHicon();
        // Wrap the HICON in a long-lived Icon kept alive for the process lifetime. We must not
        // dispose it or DestroyIcon the handle: H.NotifyIcon reads Icon.Handle every time the
        // tray icon changes, and a disposed Icon / destroyed handle throws ObjectDisposedException.
        return Icon.FromHandle(handle);
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
