using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public class SessionBannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "scribe-banner-test-" + Guid.NewGuid().ToString("N"));

    private readonly SessionIdentity _session = new("abc123", 4242, new DateTimeOffset(2026, 8, 20, 9, 30, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AppPaths Paths() => new(_root);

    private string Compose(AppSettings? settings, string? packageFamily = null) =>
        string.Join(Environment.NewLine, SessionBanner.Compose(
            _session, "0.3.11", InstallChannel.Packaged, Paths(), settings, packageFamily));

    [Fact]
    public void Banner_opens_with_a_marker_and_the_session_identity()
    {
        var text = Compose(AppSettings.CreateDefault());

        Assert.Contains(SessionBanner.StartMarker, text);
        Assert.Contains("session=abc123", text);
        Assert.Contains("pid=4242", text);
        Assert.Contains("version=0.3.11", text);
        Assert.Contains("channel=Packaged", text);
    }

    [Fact]
    public void Banner_records_the_settings_a_dictation_bug_depends_on()
    {
        var settings = AppSettings.CreateDefault();

        var text = Compose(settings);

        // The exact set that decides how a recording starts and ends. A report of "it stopped after
        // ten seconds" is unanswerable without all four.
        Assert.Contains("mode=Hold", text);
        Assert.Contains("autoStopOnSilence=False", text);
        Assert.Contains("vad=True", text);
        Assert.Contains("cleanup: off", text);
    }

    [Fact]
    public void Banner_never_contains_a_secret()
    {
        var settings = AppSettings.CreateDefault();
        settings.EnableAiCleanup = true;
        settings.AiCleanupProvider = CleanupProvider.AzureFoundry;
        settings.AiCleanupAzureEndpoint = "https://contoso-secret-resource.openai.azure.com/";
        settings.AiCleanupAzureApiKey = "sk-do-not-log-me";
        settings.AiCleanupAzureClientSecret = "client-secret-value";
        settings.AiCleanupAzureAuthMode = AzureAuthMode.ServicePrincipal;
        settings.AiCleanupWritingStyle = "Write like a pirate about project Nimbus";
        settings.AiCleanupLocalPrompt = "internal prompt text";

        var text = Compose(settings);

        // The banner is the easiest place in the codebase to leak something a user would not expect
        // to hand out with a log file, so the contract is asserted rather than assumed.
        Assert.DoesNotContain("sk-do-not-log-me", text);
        Assert.DoesNotContain("client-secret-value", text);
        Assert.DoesNotContain("contoso-secret-resource", text);
        Assert.DoesNotContain("pirate", text);
        Assert.DoesNotContain("internal prompt text", text);

        // What it says instead: enough to tell a configured endpoint from a missing one.
        Assert.Contains("endpoint=configured", text);
        Assert.Contains("writingStyle=configured", text);
        Assert.Contains("auth=ServicePrincipal", text);
    }

    [Fact]
    public void Unset_optional_configuration_reads_as_unset_rather_than_configured()
    {
        var settings = AppSettings.CreateDefault();
        settings.EnableAiCleanup = true;
        settings.AiCleanupProvider = CleanupProvider.AzureFoundry;

        var text = Compose(settings);

        Assert.Contains("endpoint=unset", text);
        Assert.Contains("writingStyle=unset", text);
    }

    [Fact]
    public void A_settings_store_that_failed_to_load_is_reported_as_such()
    {
        // Reporting substituted defaults as if they were the user's settings sends support chasing
        // a configuration that never existed.
        var text = Compose(settings: null);

        Assert.Contains("settings: unavailable", text);
        Assert.DoesNotContain("hotkeys:", text);
    }

    [Fact]
    public void Package_family_is_reported_when_present_and_none_when_not()
    {
        Assert.Contains("package=53984VeteranApps.ScribeAI_e3jkm6dfkwwbm",
            Compose(AppSettings.CreateDefault(), "53984VeteranApps.ScribeAI_e3jkm6dfkwwbm"));
        Assert.Contains("package=none", Compose(AppSettings.CreateDefault()));
    }

    [Fact]
    public void Banner_states_the_retention_the_user_can_rely_on()
    {
        // Support tells users "send me the log for the day it happened". The banner has to agree
        // with how long that day actually survives.
        Assert.Contains($"logs: {LogRetentionPolicy.DefaultRetentionDays} day retention",
            Compose(AppSettings.CreateDefault()));
    }
}
