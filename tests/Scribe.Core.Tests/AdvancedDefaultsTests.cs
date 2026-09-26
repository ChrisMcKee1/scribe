using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class AdvancedDefaultsTests
{
    [Fact]
    public void Appearance_counts_only_the_accent_source()
    {
        var settings = AppSettings.CreateDefault();
        Assert.Equal(0, AdvancedDefaults.ChangedCount(AdvancedSection.Appearance, settings));

        settings.AccentSource = AccentSource.Windows;
        Assert.Equal(1, AdvancedDefaults.ChangedCount(AdvancedSection.Appearance, settings));
        Assert.Equal("1 changed", AdvancedDefaults.SectionHeader(AdvancedSection.Appearance, settings));
    }

    [Fact]
    public void ApplyTo_restores_appearance_without_touching_non_advanced_settings()
    {
        var settings = AppSettings.CreateDefault();
        settings.AccentSource = AccentSource.Windows;
        settings.EnableAiCleanup = true;
        settings.HistoryRetentionDays = 7;

        AdvancedDefaults.ApplyTo(settings);

        Assert.Equal(AccentSource.Scribe, settings.AccentSource);
        Assert.True(settings.EnableAiCleanup);
        Assert.Equal(7, settings.HistoryRetentionDays);
    }
}
