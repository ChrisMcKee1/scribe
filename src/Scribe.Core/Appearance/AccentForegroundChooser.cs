namespace Scribe.Core.Appearance;

/// <summary>
/// Chooses the foreground for text and glyphs drawn on a coloured fill (an accent button, a checked box, a selected
/// item, a badge) from the colour the fill actually has: black or white, whichever keeps it legible.
/// </summary>
/// <remarks>
/// <para>
/// WPF-UI 4.3.0 resolves each of its text-on-accent brushes once per theme (black in the dark theme, white in the
/// light one) while it derives the fills from whatever accent the user picked, so a dark accent in the dark theme got
/// black text on a dark fill: 2.48:1 on #42429B, the fill WPF-UI makes of #0E0E70. Microsoft asks that text and
/// backgrounds using the accent keep sufficient contrast whatever the accent
/// (https://learn.microsoft.com/windows/apps/develop/ui/theming#choosing-an-accent-color).
/// </para>
/// <para>
/// The theme's own foreground stays whenever it is legible, because it is the pairing Windows builds its accent
/// palette around; the other one is used only where a fill needs it. One of black and white always reaches 4.58:1 on
/// an opaque fill (they tie where the fill's relative luminance is about 0.179), so a single fill is never left
/// unreadable. A foreground that serves several fills (a button's rest, hover and pressed tones) is measured against
/// the lowest of them.
/// </para>
/// </remarks>
public static class AccentForegroundChooser
{
    /// <param name="restFills">The fills the foreground sits on at rest, composited opaque. At least one.</param>
    /// <param name="interactionFills">The fills it sits on while hovered or pressed, composited opaque; may be empty.</param>
    /// <param name="themeForeground">The theme's own foreground for these fills, black or white.</param>
    /// <param name="required">The contrast to reach, 4.5:1 for text by default.</param>
    public static AccentForegroundChoice Choose(
        IReadOnlyList<SrgbColor> restFills,
        IReadOnlyList<SrgbColor> interactionFills,
        SrgbColor themeForeground,
        double required = WcagContrast.TextMinimum)
    {
        ArgumentNullException.ThrowIfNull(restFills);
        ArgumentNullException.ThrowIfNull(interactionFills);
        if (restFills.Count == 0)
        {
            throw new ArgumentException("A foreground is chosen against at least one fill.", nameof(restFills));
        }

        if (restFills.Any(fill => !fill.IsOpaque) || interactionFills.Any(fill => !fill.IsOpaque))
        {
            throw new ArgumentException("Fills are measured as they are drawn: composite translucent ones first.");
        }

        if (themeForeground != SrgbColor.Black && themeForeground != SrgbColor.White)
        {
            throw new ArgumentException("The theme's foreground on a fill is black or white.", nameof(themeForeground));
        }

        if (!(required > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(required), "A contrast requirement is above 1:1.");
        }

        var other = themeForeground == SrgbColor.Black ? SrgbColor.White : SrgbColor.Black;
        var preferred = Measure(themeForeground, isThemeForeground: true);
        var alternative = Measure(other, isThemeForeground: false);

        // Legible in every state beats legible at rest, and at each step the theme's own foreground wins a tie.
        if (preferred.MeetsInEveryState)
        {
            return preferred;
        }

        if (alternative.MeetsInEveryState)
        {
            return alternative;
        }

        if (preferred.MeetsAtRest)
        {
            return preferred;
        }

        if (alternative.MeetsAtRest)
        {
            return alternative;
        }

        // Only reachable with rest fills of very different lightness; the better of the two at rest is still better.
        return alternative.RestRatio > preferred.RestRatio ? alternative : preferred;

        AccentForegroundChoice Measure(SrgbColor foreground, bool isThemeForeground)
        {
            var rest = restFills.Min(fill => WcagContrast.Ratio(foreground, fill));
            var all = interactionFills.Count == 0
                ? rest
                : Math.Min(rest, interactionFills.Min(fill => WcagContrast.Ratio(foreground, fill)));
            return new AccentForegroundChoice(foreground, isThemeForeground, rest, all, required);
        }
    }
}

/// <summary>What <see cref="AccentForegroundChooser"/> chose, and how it measures against the fills it serves.</summary>
/// <param name="Foreground">Black or white.</param>
/// <param name="IsThemeForeground">True when this is the theme's own foreground, false when the fills needed the other.</param>
/// <param name="RestRatio">The lowest contrast against the fills at rest.</param>
/// <param name="AllStatesRatio">The lowest contrast against every fill, hovered and pressed included.</param>
/// <param name="Required">The contrast it was chosen to reach.</param>
public sealed record AccentForegroundChoice(
    SrgbColor Foreground,
    bool IsThemeForeground,
    double RestRatio,
    double AllStatesRatio,
    double Required)
{
    public bool MeetsAtRest => RestRatio >= Required;

    public bool MeetsInEveryState => AllStatesRatio >= Required;
}
