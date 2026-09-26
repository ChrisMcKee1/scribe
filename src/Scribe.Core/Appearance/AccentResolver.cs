using Scribe.Core.Models;

namespace Scribe.Core.Appearance;

public enum AccentResolverKind
{
    None,
    ScribeSet,
    WindowsDerived,
}

public sealed record AccentResolution(AccentResolverKind Kind, AccentSet? ScribeAccent)
{
    public static AccentResolution None { get; } = new(AccentResolverKind.None, null);

    public static AccentResolution WindowsDerived { get; } = new(AccentResolverKind.WindowsDerived, null);

    public static AccentResolution ScribeSet(AccentSet set) => new(AccentResolverKind.ScribeSet, set);
}

public static class AccentResolver
{
    public static AccentResolution Decide(AccentSource source, AppearanceTheme theme, bool systemHighContrast)
    {
        if (theme == AppearanceTheme.HighContrast || systemHighContrast)
        {
            return AccentResolution.WindowsDerived;
        }

        return theme switch
        {
            AppearanceTheme.Unknown => AccentResolution.None,
            AppearanceTheme.Light when source == AccentSource.Scribe => AccentResolution.ScribeSet(ScribeBrand.LightAccent),
            AppearanceTheme.Dark when source == AccentSource.Scribe => AccentResolution.ScribeSet(ScribeBrand.DarkAccent),
            AppearanceTheme.Light or AppearanceTheme.Dark => AccentResolution.WindowsDerived,
            _ => AccentResolution.None,
        };
    }
}
