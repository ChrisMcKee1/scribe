using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Tray;

namespace Scribe.Core.Tests;

public sealed class TrayAiCleanupTests
{
    [Fact]
    public void Not_setup_opens_settings_without_toggle()
    {
        var settings = AppSettings.CreateDefault();
        settings.EnableAiCleanup = false;

        var item = TrayAiCleanup.Describe(settings, setupComplete: false, CleanupStatus.Disabled, settingsRecovered: false);

        Assert.Equal(TrayAiCleanupKind.SetUp, item.Kind);
        Assert.Equal("Set up AI cleanup...", item.Label);
        Assert.False(item.IsCheckItem);
        Assert.False(item.Checked);
    }

    [Fact]
    public void On_without_setup_is_checked_toggle_so_turning_off_never_downloads()
    {
        var settings = AppSettings.CreateDefault();
        settings.EnableAiCleanup = true;

        var item = TrayAiCleanup.Describe(settings, setupComplete: false, CleanupStatus.Initializing, settingsRecovered: false);

        Assert.Equal(TrayAiCleanupKind.Toggle, item.Kind);
        Assert.True(item.Checked);
    }

    [Fact]
    public void Off_with_downloaded_model_is_toggle()
    {
        var settings = AppSettings.CreateDefault();
        settings.EnableAiCleanup = false;

        var item = TrayAiCleanup.Describe(settings, setupComplete: true, CleanupStatus.Disabled, settingsRecovered: false);

        Assert.Equal(TrayAiCleanupKind.Toggle, item.Kind);
        Assert.False(item.Checked);
    }

    [Fact]
    public void Unavailable_is_checked_not_ready()
    {
        var settings = AppSettings.CreateDefault();
        settings.EnableAiCleanup = true;

        var item = TrayAiCleanup.Describe(settings, setupComplete: true, CleanupStatus.Unavailable, settingsRecovered: false);

        Assert.Equal(TrayAiCleanupKind.Toggle, item.Kind);
        Assert.Equal("AI cleanup (not ready)", item.Label);
        Assert.True(item.Checked);
    }

    [Fact]
    public void Providers_have_setup_requirements()
    {
        var foundry = AppSettings.CreateDefault().withSettings(s => { s.AiCleanupProvider = CleanupProvider.FoundryLocal; s.EnableAiCleanup = false; });
        var azure = AppSettings.CreateDefault().withSettings(s => { s.AiCleanupProvider = CleanupProvider.AzureFoundry; s.AiCleanupAzureEndpoint = "https://example.openai.azure.com"; s.AiCleanupAzureDeployment = null; });
        var custom = AppSettings.CreateDefault().withSettings(s => { s.AiCleanupProvider = CleanupProvider.OpenAiCompatible; s.AiCleanupCustomEndpoint = "http://localhost:11434/v1"; s.AiCleanupCustomModel = null; });
        var copilot = AppSettings.CreateDefault().withSettings(s => s.AiCleanupProvider = CleanupProvider.GitHubCopilot);

        Assert.Equal(TrayAiCleanupKind.Toggle, TrayAiCleanup.Describe(foundry, true, CleanupStatus.Ready, false).Kind);
        Assert.Equal(TrayAiCleanupKind.SetUp, TrayAiCleanup.Describe(azure, true, CleanupStatus.Ready, false).Kind);
        Assert.Equal(TrayAiCleanupKind.SetUp, TrayAiCleanup.Describe(custom, true, CleanupStatus.Ready, false).Kind);
        Assert.Equal(TrayAiCleanupKind.Toggle, TrayAiCleanup.Describe(copilot, true, CleanupStatus.Ready, false).Kind);
    }

    [Fact]
    public void Recovered_settings_refusal_stays_enabled_without_setup_variant()
    {
        var settings = AppSettings.CreateDefault();
        settings.EnableAiCleanup = true;

        var item = TrayAiCleanup.Describe(settings, setupComplete: false, CleanupStatus.Unavailable, settingsRecovered: true);

        Assert.Equal(TrayAiCleanupKind.Refused, item.Kind);
        Assert.Equal("AI cleanup", item.Label);
        Assert.True(item.Checked);
        Assert.True(item.Enabled);
    }
}

file static class SettingsMutator
{
    public static AppSettings withSettings(this AppSettings settings, Action<AppSettings> mutate)
    {
        mutate(settings);
        return settings;
    }
}
