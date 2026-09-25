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
    /// The opaque colour this one shows as when drawn over <paramref name="background"/>, as WPF blends it: the colour is
    /// premultiplied by its alpha and rounded to 8 bits first, then the background is added under it, so a hover fill at
    /// alpha 229 is measured as drawn. Blending exactly instead is off by one level where the premultiplied value rounds
    /// up: the pressed accent fill of #0E0E70 in the dark theme is #4D4D82 in WPF's render, not #4E4E82.
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

        var alpha = A;
        return FromRgb(Blend(R, background.R), Blend(G, background.G), Blend(B, background.B));

        byte Blend(byte top, byte bottom)
        {
            var premultiplied = Math.Round(top * alpha / 255.0);
            return (byte)Math.Round(premultiplied + (bottom * (255 - alpha) / 255.0), MidpointRounding.AwayFromZero);
        }
    }
    /// <summary>
    /// Hue in degrees from 0 to 360, saturation and lightness from 0 to 1, as HSL writes them. Alpha is not part of it.
    /// </summary>
    public (double Hue, double Saturation, double Lightness) ToHsl()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2;
        if (max == min)
        {
            return (0, 0, lightness);
        }

        var delta = max - min;
        var saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        double hue;
        if (max == r)
        {
            hue = ((g - b) / delta) + (g < b ? 6 : 0);
        }
        else if (max == g)
        {
            hue = ((b - r) / delta) + 2;
        }
        else
        {
            hue = ((r - g) / delta) + 4;
        }

        return (hue * 60, saturation, lightness);
    }

    /// <summary>The colour with the given HSL values, rounded to 8-bit channels.</summary>
    public static SrgbColor FromHsl(double hue, double saturation, double lightness, byte alpha = 255)
    {
        saturation = Math.Clamp(saturation, 0, 1);
        lightness = Math.Clamp(lightness, 0, 1);
        if (saturation == 0)
        {
            var grey = ToByte(lightness);
            return new SrgbColor(alpha, grey, grey, grey);
        }

        var q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - (lightness * saturation);
        var p = (2 * lightness) - q;
        var h = (((hue % 360) + 360) % 360) / 360;
        return new SrgbColor(alpha, ToByte(Channel(h + (1.0 / 3))), ToByte(Channel(h)), ToByte(Channel(h - (1.0 / 3))));

        double Channel(double t)
        {
            if (t < 0)
            {
                t += 1;
            }

            if (t > 1)
            {
                t -= 1;
            }

            return t switch
            {
                < 1.0 / 6 => p + ((q - p) * 6 * t),
                < 0.5 => q,
                < 2.0 / 3 => p + ((q - p) * ((2.0 / 3) - t) * 6),
                _ => p,
            };
        }

        static byte ToByte(double value) => (byte)Math.Round(value * 255, MidpointRounding.AwayFromZero);
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
