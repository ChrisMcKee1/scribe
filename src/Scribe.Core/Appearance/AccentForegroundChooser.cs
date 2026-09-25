namespace Scribe.Core.Appearance;

/// <summary>
/// Chooses the foreground for text and glyphs drawn on a coloured fill (an accent button, a checked box, a selected
/// item, a badge) from the colour the fill actually has, keeping the theme's own foreground whenever it is legible.
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
/// The candidates are tried in order: the colours preferred for the role, the first being the theme's own as the theme
/// draws it (a translucent pressed tone over its fill), then the first one's hue at full opacity, then the opposite. The
/// first legible in every state it serves wins, else the first legible at rest, else the one that does best at rest.
/// One of black and white always reaches 4.58:1 on an opaque fill (they tie where the fill's relative luminance is about
/// 0.179), so a single fill is never left unreadable. A foreground that serves several fills (a button at rest and
/// hovered) is measured against the lowest of them.
/// </para>
/// </remarks>
public static class AccentForegroundChooser
{
    /// <param name="restFills">The fills the foreground sits on at rest, composited opaque. At least one.</param>
    /// <param name="interactionFills">The fills it sits on while hovered or pressed, composited opaque; may be empty.</param>
    /// <param name="themeForeground">The theme's own foreground for these fills, translucent or not.</param>
    /// <param name="required">The contrast to reach, 4.5:1 for text by default.</param>
    public static AccentForegroundChoice Choose(
        IReadOnlyList<SrgbColor> restFills,
        IReadOnlyList<SrgbColor> interactionFills,
        SrgbColor themeForeground,
        double required = WcagContrast.TextMinimum) =>
        Choose(restFills, interactionFills, [themeForeground], required);

    /// <param name="restFills">The fills the foreground sits on at rest, composited opaque. At least one.</param>
    /// <param name="interactionFills">The fills it sits on while hovered or pressed, composited opaque; may be empty.</param>
    /// <param name="preferred">
    /// The foregrounds to keep if they read, most preferred first; the first is the theme's own, the one drawn today.
    /// </param>
    /// <param name="required">The contrast to reach, 4.5:1 for text by default.</param>
    public static AccentForegroundChoice Choose(
        IReadOnlyList<SrgbColor> restFills,
        IReadOnlyList<SrgbColor> interactionFills,
        IReadOnlyList<SrgbColor> preferred,
        double required = WcagContrast.TextMinimum)
    {
        ArgumentNullException.ThrowIfNull(restFills);
        ArgumentNullException.ThrowIfNull(interactionFills);
        ArgumentNullException.ThrowIfNull(preferred);
        if (restFills.Count == 0)
        {
            throw new ArgumentException("A foreground is chosen against at least one fill.", nameof(restFills));
        }

        if (preferred.Count == 0)
        {
            throw new ArgumentException("A foreground is chosen with the theme's own first.", nameof(preferred));
        }

        if (restFills.Any(fill => !fill.IsOpaque) || interactionFills.Any(fill => !fill.IsOpaque))
        {
            throw new ArgumentException("Fills are measured as they are drawn: composite translucent ones first.");
        }

        if (!(required > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(required), "A contrast requirement is above 1:1.");
        }

        var themeForeground = preferred[0];
        var sameHue = WcagContrast.RelativeLuminance(themeForeground) < 0.5 ? SrgbColor.Black : SrgbColor.White;
        var opposite = sameHue == SrgbColor.Black ? SrgbColor.White : SrgbColor.Black;
        var candidates = new List<AccentForegroundChoice>(preferred.Count + 2);
        foreach (var foreground in preferred.Append(sameHue).Append(opposite))
        {
            if (candidates.All(c => c.Foreground != foreground))
            {
                candidates.Add(Measure(foreground, foreground == themeForeground));
            }
        }

        return candidates.FirstOrDefault(c => c.MeetsInEveryState)
            ?? candidates.FirstOrDefault(c => c.MeetsAtRest)
            ?? candidates.OrderByDescending(c => c.RestRatio).First();

        AccentForegroundChoice Measure(SrgbColor foreground, bool isThemeForeground)
        {
            var rest = restFills.Min(fill => WcagContrast.Ratio(foreground.Over(fill), fill));
            var all = interactionFills.Count == 0
                ? rest
                : Math.Min(rest, interactionFills.Min(fill => WcagContrast.Ratio(foreground.Over(fill), fill)));
            return new AccentForegroundChoice(foreground, isThemeForeground, rest, all, required);
        }
    }
}

/// <summary>What <see cref="AccentForegroundChooser"/> chose, and how it measures against the fills it serves.</summary>
/// <param name="Foreground">One of the preferred foregrounds, or black or white; translucent if a preferred one was.</param>
/// <param name="IsThemeForeground">True when this is the theme's own foreground, false when the fills needed another.</param>
/// <param name="RestRatio">The lowest contrast against the fills at rest, the foreground drawn over each.</param>
/// <param name="AllStatesRatio">The lowest contrast against every fill it serves, hovered included.</param>
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
