namespace Scribe.Core.Appearance;

/// <summary>
/// WCAG 2.2's contrast arithmetic, from its definitions of relative luminance and contrast ratio
/// (https://www.w3.org/TR/WCAG22/#dfn-relative-luminance), and the two minimums the accent rules are held to.
/// </summary>
public static class WcagContrast
{
    /// <summary>Success Criterion 1.4.3 Contrast (Minimum): text and images of text, at least 4.5:1.</summary>
    public const double TextMinimum = 4.5;

    /// <summary>
    /// Success Criterion 1.4.11 Non-text Contrast: the visual information that identifies a control or its state, such
    /// as the check in a check box, a switch's knob or a selection outline, at least 3:1 against the adjacent colours.
    /// </summary>
    public const double NonTextMinimum = 3.0;

    /// <summary>0 for black to 1 for white. Alpha is ignored: composite a translucent colour first.</summary>
    public static double RelativeLuminance(SrgbColor color) =>
        (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));

    /// <summary>
    /// (L1 + 0.05) / (L2 + 0.05), the lighter colour's luminance over the darker's, from 1 to 21. Alpha is ignored:
    /// composite a translucent colour with <see cref="SrgbColor.Over"/> before measuring it.
    /// </summary>
    public static double Ratio(SrgbColor first, SrgbColor second)
    {
        var a = RelativeLuminance(first);
        var b = RelativeLuminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    // The sRGB transfer function as WCAG 2.2 writes it. Its threshold was 0.03928 before May 2021; no 8-bit channel
    // value lies between the two (10/255 is below both, 11/255 above), so either gives the same answer here.
    private static double Linear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
