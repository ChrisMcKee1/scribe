using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class AppProfileTests
{
    private static AppProfile Profile(string name, params string[] processes) =>
        new() { Name = name, ProcessNames = [.. processes] };

    [Fact]
    public void Match_is_case_insensitive_and_exe_insensitive()
    {
        var profiles = new[] { Profile("Email", "OUTLOOK") };

        Assert.Equal("Email", AppProfileMatcher.Match(profiles, "outlook")?.Name);
        Assert.Equal("Email", AppProfileMatcher.Match(profiles, "Outlook.exe")?.Name);
        Assert.Null(AppProfileMatcher.Match(profiles, "chrome"));
    }

    [Fact]
    public void Profile_entries_with_exe_suffix_still_match()
    {
        var profiles = new[] { Profile("Chat", "slack.exe", " teams ") };

        Assert.Equal("Chat", AppProfileMatcher.Match(profiles, "slack")?.Name);
        Assert.Equal("Chat", AppProfileMatcher.Match(profiles, "Teams")?.Name);
    }

    [Fact]
    public void First_matching_profile_wins()
    {
        var profiles = new[]
        {
            Profile("Specific", "Code"),
            Profile("Catch-all dev", "Code", "WindowsTerminal"),
        };

        Assert.Equal("Specific", AppProfileMatcher.Match(profiles, "Code")?.Name);
        Assert.Equal("Catch-all dev", AppProfileMatcher.Match(profiles, "WindowsTerminal")?.Name);
    }

    [Fact]
    public void Null_or_empty_inputs_match_nothing()
    {
        Assert.Null(AppProfileMatcher.Match(null, "chrome"));
        Assert.Null(AppProfileMatcher.Match([], "chrome"));
        Assert.Null(AppProfileMatcher.Match([Profile("X", "chrome")], null));
        Assert.Null(AppProfileMatcher.Match([Profile("X", "chrome")], "  "));
    }

    [Fact]
    public void Profiles_round_trip_through_settings_persistence()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);

        var settings = AppSettings.CreateDefault();
        settings.Profiles.Add(new AppProfile
        {
            Name = "Terminal",
            ProcessNames = ["WindowsTerminal", "pwsh"],
            WritingStyle = "Terse. One sentence.",
            NewlineHandling = NewlineInjectionMode.AlwaysFlatten,
        });
        settings.Profiles.Add(new AppProfile { Name = "Email", ProcessNames = ["OUTLOOK"] });

        repo.Save(settings);
        var loaded = repo.Load();

        Assert.Equal(2, loaded.Profiles.Count);
        var terminal = loaded.Profiles[0];
        Assert.Equal("Terminal", terminal.Name);
        Assert.Equal(["WindowsTerminal", "pwsh"], terminal.ProcessNames);
        Assert.Equal("Terse. One sentence.", terminal.WritingStyle);
        Assert.Equal(NewlineInjectionMode.AlwaysFlatten, terminal.NewlineHandling);
        Assert.Null(loaded.Profiles[1].WritingStyle);   // blank override stays "use global"
        Assert.Null(loaded.Profiles[1].NewlineHandling);
    }

    [Fact]
    public void Clone_deep_copies_profiles()
    {
        var settings = AppSettings.CreateDefault();
        settings.Profiles.Add(Profile("A", "one"));

        var clone = settings.Clone();
        clone.Profiles[0].Name = "changed";
        clone.Profiles[0].ProcessNames.Add("two");

        Assert.Equal("A", settings.Profiles[0].Name);
        Assert.Single(settings.Profiles[0].ProcessNames);
    }
}
