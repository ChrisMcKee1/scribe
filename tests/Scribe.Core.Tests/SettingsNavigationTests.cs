using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class SettingsNavigationTests
{
    [Fact]
    public void Items_follow_the_plan_order_groups_and_positions()
    {
        SettingsPage[] pages =
        [
            SettingsPage.Dictation,
            SettingsPage.TryDictation,
            SettingsPage.AiCleanup,
            SettingsPage.Dictionary,
            SettingsPage.VoiceSnippets,
            SettingsPage.AppProfiles,
            SettingsPage.History,
            SettingsPage.Usage,
            SettingsPage.Advanced,
            SettingsPage.Diagnostics,
            SettingsPage.About,
        ];
        string[] labels =
        [
            "Dictation",
            "Try dictation",
            "AI cleanup",
            "Dictionary",
            "Voice snippets",
            "App profiles",
            "History",
            "Usage",
            "Advanced",
            "Diagnostics",
            "About",
        ];
        string[] groups =
        [
            string.Empty,
            string.Empty,
            string.Empty,
            "Personalize",
            "Personalize",
            "Personalize",
            "Review",
            "Review",
            "More",
            "More",
            "More",
        ];

        Assert.Equal(pages, SettingsNavigation.Items.Select(item => item.Page).ToArray());
        Assert.Equal(labels, SettingsNavigation.Items.Select(item => item.Label).ToArray());
        Assert.Equal(groups, SettingsNavigation.Items.Select(item => item.Group).ToArray());
        Assert.Equal(Enumerable.Range(1, 11), SettingsNavigation.Items.Select(item => item.Position));
        Assert.Equal([1, 4, 7, 9], SettingsNavigation.Items.Where(item => item.IsFirstInGroup).Select(item => item.Position));
        Assert.Equal(SettingsNavigation.Items.Count, SettingsNavigation.Items.Select(item => item.Page).Distinct().Count());
    }

    [Fact]
    public void Title_returns_the_label_for_each_page()
    {
        foreach (var item in SettingsNavigation.Items)
        {
            Assert.Equal(item.Label, SettingsNavigation.Title(item.Page));
        }
    }

    [Theory]
    [InlineData("--settings=dictation", SettingsPage.Dictation)]
    [InlineData("--settings=try-dictation", SettingsPage.TryDictation)]
    [InlineData("--settings=AI cleanup", SettingsPage.AiCleanup)]
    [InlineData("--settings=voice-snippets", SettingsPage.VoiceSnippets)]
    [InlineData("--settings=appProfiles", SettingsPage.AppProfiles)]
    public void Settings_argument_accepts_page_ids_and_labels(string argument, SettingsPage expected)
    {
        Assert.True(SettingsNavigation.TryParseSettingsArgument([argument], out var page));
        Assert.Equal(expected, page);
    }

    [Fact]
    public void Bare_settings_argument_opens_the_default_page()
    {
        Assert.True(SettingsNavigation.TryParseSettingsArgument(["--settings"], out var page));
        Assert.Equal(SettingsPage.Dictation, page);
    }

    [Fact]
    public void Missing_or_unknown_settings_argument_returns_false()
    {
        Assert.False(SettingsNavigation.TryParseSettingsArgument(["--other"], out _));
        Assert.False(SettingsNavigation.TryParseSettingsArgument(["--settings=unknown"], out _));
    }
}
