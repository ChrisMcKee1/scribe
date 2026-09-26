namespace Scribe.Core.Overlay;

/// <summary>
/// One of the nine places the recording pill can sit in a monitor's work area. The names and their order are those of
/// <c>Scribe.Core.Models.OverlayPosition</c> and of the overlay's <c>OverlayAnchor</c>, which converts to this by value.
/// </summary>
public enum PillAnchor
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    Center,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

/// <summary>A rectangle in physical pixels: a monitor's work area.</summary>
public readonly record struct PillRect(int X, int Y, int Width, int Height);

/// <summary>Where the pill's window goes, in physical pixels, and whether keeping it inside the work area moved it.</summary>
public readonly record struct PillPlacement(int X, int Y, int Width, int Height, bool Clamped);

/// <summary>
/// The recording pill's size and place as Windows text size scales it. The whole pill scales as one unit: no text on it
/// scales by itself, the window is the 100% window times the text scale, and the content, laid out at the 100% size, is
/// drawn scaled to fill it, so every line keeps the width it has at 100% and its text is drawn at the size Windows asks
/// for (WCAG 1.4.4). Pure arithmetic with no WinUI in it: the overlay, which has no reference to Scribe.Core, compiles
/// this file itself through a linked Compile item and calls it for every size and place.
/// </summary>
public static class PillGeometry
{
    /// <summary>The window's width in DIP at 100% text size: the 210 DIP pill and 27 DIP on each side.</summary>
    public const double LogicalWidth = 264;

    /// <summary>The window's height in DIP at 100% text size: the 56 DIP pill and 27 DIP above and below.</summary>
    public const double LogicalHeight = 110;

    /// <summary>The smallest text scale: Windows text size does not go below 100%.</summary>
    public const double MinTextScale = 1.0;

    /// <summary>The largest text scale Windows offers: 225%.</summary>
    public const double MaxTextScale = 2.25;

    /// <summary>The gap between the window and the work area's edge at an edge anchor, in DIP. It is not text-scaled.</summary>
    public const double EdgeMarginDip = 8;

    /// <summary>
    /// The scale for Windows' text size factor (<c>UISettings.TextScaleFactor</c>): clamped to 1 to 2.25, and 1 for a
    /// value that is not a finite number, so a setting that could not be read leaves the pill at its 100% size.
    /// </summary>
    public static double TextScale(double textScaleFactor) =>
        double.IsFinite(textScaleFactor) ? Math.Clamp(textScaleFactor, MinTextScale, MaxTextScale) : MinTextScale;

    /// <summary>The window's size in DIP: the 100% size times <paramref name="textScale"/>.</summary>
    public static (double Width, double Height) SizeDip(double textScale) =>
        (LogicalWidth * textScale, LogicalHeight * textScale);

    /// <summary>
    /// The window's size in physical pixels for a display scale (DPI / 96), rounded as the overlay always has (to even), so
    /// the 100% size is unchanged: 462 x 192 at 175%.
    /// </summary>
    public static (int Width, int Height) SizePixels(double textScale, double dpiScale)
    {
        var (width, height) = SizeDip(textScale);
        return ((int)Math.Round(width * dpiScale), (int)Math.Round(height * dpiScale));
    }

    /// <summary>
    /// Where the window goes for <paramref name="anchor"/> in <paramref name="workArea"/>, in physical pixels: an edge
    /// anchor keeps <see cref="EdgeMarginDip"/> times the display scale from the edge, and a centre anchor centres. A place
    /// that would put any part of the window outside the work area moves inside it, and says so
    /// (<see cref="PillPlacement.Clamped"/>); a window larger than the work area keeps its top left corner in it.
    /// </summary>
    public static PillPlacement Place(PillAnchor anchor, PillRect workArea, double textScale, double dpiScale)
    {
        var (width, height) = SizePixels(textScale, dpiScale);
        var margin = (int)Math.Round(EdgeMarginDip * dpiScale);
        var x = anchor switch
        {
            PillAnchor.TopLeft or PillAnchor.MiddleLeft or PillAnchor.BottomLeft => workArea.X + margin,
            PillAnchor.TopRight or PillAnchor.MiddleRight or PillAnchor.BottomRight => workArea.X + workArea.Width - width - margin,
            _ => workArea.X + (workArea.Width - width) / 2,
        };
        var y = anchor switch
        {
            PillAnchor.TopLeft or PillAnchor.TopCenter or PillAnchor.TopRight => workArea.Y + margin,
            PillAnchor.MiddleLeft or PillAnchor.Center or PillAnchor.MiddleRight => workArea.Y + (workArea.Height - height) / 2,
            _ => workArea.Y + workArea.Height - height - margin,
        };

        var insideX = Math.Clamp(x, workArea.X, Math.Max(workArea.X, workArea.X + workArea.Width - width));
        var insideY = Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Y + workArea.Height - height));
        return new PillPlacement(insideX, insideY, width, height, insideX != x || insideY != y);
    }
}
