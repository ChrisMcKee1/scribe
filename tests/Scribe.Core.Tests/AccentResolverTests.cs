using Scribe.Core.Appearance;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

public sealed class AccentResolverTests
{
    [Theory]
    [InlineData(AccentSource.Scribe, AppearanceTheme.Light, false, AccentResolverKind.ScribeSet)]
    [InlineData(AccentSource.Scribe, AppearanceTheme.Dark, false, AccentResolverKind.ScribeSet)]
    [InlineData(AccentSource.Windows, AppearanceTheme.Light, false, AccentResolverKind.WindowsDerived)]
    [InlineData(AccentSource.Windows, AppearanceTheme.Dark, false, AccentResolverKind.WindowsDerived)]
    [InlineData(AccentSource.Scribe, AppearanceTheme.HighContrast, false, AccentResolverKind.WindowsDerived)]
    [InlineData(AccentSource.Windows, AppearanceTheme.HighContrast, false, AccentResolverKind.WindowsDerived)]
    [InlineData(AccentSource.Scribe, AppearanceTheme.Light, true, AccentResolverKind.WindowsDerived)]
    [InlineData(AccentSource.Windows, AppearanceTheme.Dark, true, AccentResolverKind.WindowsDerived)]
    [InlineData(AccentSource.Scribe, AppearanceTheme.Unknown, false, AccentResolverKind.None)]
    [InlineData(AccentSource.Windows, AppearanceTheme.Unknown, false, AccentResolverKind.None)]
    public void Decide_covers_every_source_theme_and_contrast_combination(
        AccentSource source,
        AppearanceTheme theme,
        bool systemHighContrast,
        AccentResolverKind expected)
    {
        var result = AccentResolver.Decide(source, theme, systemHighContrast);

        Assert.Equal(expected, result.Kind);
        if (expected == AccentResolverKind.ScribeSet)
        {
            Assert.Equal(theme == AppearanceTheme.Light ? ScribeBrand.LightAccent : ScribeBrand.DarkAccent, result.ScribeAccent);
        }
        else
        {
            Assert.Null(result.ScribeAccent);
        }
    }
}
