using System.Globalization;

namespace Scribe.Core.Appearance;

/// <summary>
/// An 8-bit sRGB colour with straight (not premultiplied) alpha: the four bytes a WPF <c>Color</c> carries, so the
/// shell can hand its theme colours to the contrast rules in this folder without Scribe.Core referencing WPF.
/// </summary>
public readonly record struct SrgbColor(byte A, byte R, byte G, byte B)
{
    public static SrgbColor Black => new(255, 0, 0, 0);

    public static SrgbColor White => new(255, 255, 255, 255);

    public static SrgbColor Transparent => new(0, 0, 0, 0);

    public static SrgbColor FromRgb(byte r, byte g, byte b) => new(255, r, g, b);

    public bool IsOpaque => A == 255;

    /// <summary>This colour at another alpha, the way a theme's secondary text tone is its primary one, fainter.</summary>
    public SrgbColor WithAlpha(byte alpha) => this with { A = alpha };

    /// <summary>
    /// This colour with its alpha scaled, as a WPF brush's <c>Opacity</c> scales the alpha of the colour it paints.
    /// </summary>
    public SrgbColor WithOpacity(double opacity)
    {
        var clamped = double.IsNaN(opacity) ? 1 : Math.Clamp(opacity, 0, 1);
        return this with { A = (byte)Math.Round(A * clamped, MidpointRounding.AwayFromZero) };
    }

    /// <summary>
    /// The opaque colour this one shows as when drawn over <paramref name="background"/>: straight-alpha "over", per
    /// 8-bit sRGB channel, which is how WPF blends a translucent brush. A hover fill at 90% is measured as it is drawn.
    /// </summary>
    public SrgbColor Over(SrgbColor background)
    {
        if (!background.IsOpaque)
        {
            throw new ArgumentException("A colour is composited over an opaque background.", nameof(background));
        }

        if (IsOpaque)
        {
            return this;
        }

        var alpha = A / 255.0;
        return FromRgb(Blend(R, background.R, alpha), Blend(G, background.G, alpha), Blend(B, background.B, alpha));

        static byte Blend(byte top, byte bottom, double alpha) =>
            (byte)Math.Round((top * alpha) + (bottom * (1 - alpha)), MidpointRounding.AwayFromZero);
    }

    /// <summary>Reads <c>#RRGGBB</c> or <c>#AARRGGBB</c>, the forms XAML and this type's own text use.</summary>
    public static bool TryParse(string? text, out SrgbColor color)
    {
        color = default;
        if (text is null || text.Length is not (7 or 9) || text[0] != '#' ||
            !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        var alpha = text.Length == 9 ? (byte)(value >> 24) : (byte)255;
        color = new SrgbColor(alpha, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public static SrgbColor Parse(string text) =>
        TryParse(text, out var color) ? color : throw new FormatException("Expected #RRGGBB or #AARRGGBB.");

    public override string ToString() =>
        IsOpaque
            ? string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}")
            : string.Create(CultureInfo.InvariantCulture, $"#{A:X2}{R:X2}{G:X2}{B:X2}");
}
