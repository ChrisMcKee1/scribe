using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

public sealed class SavedSettingsNoticeTests
{
    [Fact]
    public void Each_place_says_the_same_thing_in_its_own_frame()
    {
        // The tray shows every message after "Scribe: "; the Settings window shows a whole sentence.
        Assert.Equal(
            "couldn't use your saved settings. Open Settings, review them and save before changing AI cleanup.",
            SavedSettingsNotice.FromTray("AI cleanup"));
        Assert.Equal(
            "Scribe couldn't use your saved settings. Review them and save before changing Start with Windows.",
            SavedSettingsNotice.InSettings("Start with Windows"));
        Assert.Equal("couldn't use your saved settings. Open Settings, review them and save.", SavedSettingsNotice.AtStartup);
    }

    [Fact]
    public void No_notice_claims_the_settings_were_recovered_or_has_a_dash()
    {
        // True for an unreadable document and for one a repair lost, which is why none says "recovered".
        foreach (var text in new[]
                 {
                     SavedSettingsNotice.AtStartup,
                     SavedSettingsNotice.FromTray("AI cleanup"),
                     SavedSettingsNotice.InSettings("Start with Windows"),
                 })
        {
            Assert.DoesNotContain("recover", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('\u2013', text);
            Assert.DoesNotContain('\u2014', text);
        }
    }
}
