namespace Scribe.Core.Appearance;

/// <summary>
/// Keeps a colour that already reads against every surface it is drawn on, and otherwise changes only its lightness,
/// keeping its hue and saturation, by the smallest step that makes it read: lighter in a dark theme, darker in a light
/// one. Used where the colour itself is what the user sees (accent-coloured text, links, a switch's track), so that an
/// accent too dark for the dark theme, or too light for the light one, stays recognisably the user's accent.
/// </summary>
public static class ContrastShade
{
    // Fine enough that the first passing step is within a thousandth of the lightness range of the least change, and the
    // result is rounded to 8-bit channels and measured as rounded.
    private const int Steps = 1000;

    /// <param name="color">
    /// The colour as the theme has it. A translucent one is measured as it shows on each surface and stays translucent
    /// when it reads on all of them; a corrected one is opaque, so it shows the same on every surface.
    /// </param>
    /// <param name="surfaces">Every opaque surface the colour is drawn on, the page first.</param>
    /// <param name="required">The contrast to reach: 4.5:1 for text, 3:1 for a component or state.</param>
    /// <param name="lighter">True to lighten (a dark theme), false to darken (a light one).</param>
    public static ShadeResult Ensure(SrgbColor color, IReadOnlyList<SrgbColor> surfaces, double required, bool lighter)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        if (surfaces.Count == 0 || surfaces.Any(surface => !surface.IsOpaque))
        {
            throw new ArgumentException("A colour is measured against at least one opaque surface.", nameof(surfaces));
        }

        if (!(required > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(required), "A contrast requirement is above 1:1.");
        }

        var original = Lowest(color, surfaces);
        if (original >= required)
        {
            return new ShadeResult(color, color, original, original);
        }

        // From the colour as it shows on the page, the surface it is drawn on most.
        var (hue, saturation, lightness) = color.Over(surfaces[0]).ToHsl();
        for (var step = 1; step <= Steps; step++)
        {
            var target = lighter
                ? lightness + ((1 - lightness) * step / Steps)
                : lightness - (lightness * step / Steps);
            var candidate = SrgbColor.FromHsl(hue, saturation, target);
            var ratio = Lowest(candidate, surfaces);
            if (ratio >= required)
            {
                return new ShadeResult(color, candidate, original, ratio);
            }
        }

        // Only reachable if even white or black cannot read on these surfaces; the extreme is still the best there is.
        var extreme = lighter ? SrgbColor.White : SrgbColor.Black;
        return new ShadeResult(color, extreme, original, Lowest(extreme, surfaces));
    }

    /// <summary>
    /// The lowest contrast of a colour against any of the surfaces, each time as it shows on that surface: a translucent
    /// colour is composited over the surface it is measured against.
    /// </summary>
    public static double Lowest(SrgbColor color, IReadOnlyList<SrgbColor> surfaces)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        return surfaces.Min(surface => WcagContrast.Ratio(color.Over(surface), surface));
    }
}

/// <summary>What <see cref="ContrastShade.Ensure"/> kept or changed.</summary>
/// <param name="Original">The colour as it was, translucent or not.</param>
/// <param name="Color">The colour to draw: the original, or the same hue and saturation at another lightness, opaque.</param>
/// <param name="OriginalRatio">The original's lowest contrast against the surfaces, as it shows on each.</param>
/// <param name="Ratio">The drawn colour's lowest contrast against the surfaces.</param>
public sealed record ShadeResult(SrgbColor Original, SrgbColor Color, double OriginalRatio, double Ratio)
{
    public bool Changed => Color != Original;
}
