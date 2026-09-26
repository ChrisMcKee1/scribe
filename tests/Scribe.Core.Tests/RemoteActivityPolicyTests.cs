using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class RemoteActivityPolicyTests
{
    [Theory]
    [InlineData(RemoteActivityTrigger.WindowOpen, true)]
    [InlineData(RemoteActivityTrigger.PageSwitch, true)]
    [InlineData(RemoteActivityTrigger.ProviderChange, false)]
    [InlineData(RemoteActivityTrigger.SignInMethodChange, false)]
    [InlineData(RemoteActivityTrigger.FieldEdit, false)]
    [InlineData(RemoteActivityTrigger.ButtonPress, true)]
    public void Saved_active_provider_may_contact_only_for_allowed_triggers(RemoteActivityTrigger trigger, bool expected)
    {
        var saved = Complete(CleanupProvider.AzureFoundry);
        var draft = saved.Clone();

        Assert.Equal(expected, RemoteActivityPolicy.MayContact(saved, draft, trigger));
    }

    [Theory]
    [InlineData(CleanupProvider.AzureFoundry)]
    [InlineData(CleanupProvider.OpenAiCompatible)]
    [InlineData(CleanupProvider.GitHubCopilot)]
    public void Every_remote_provider_allows_automatic_contact_only_when_saved_active_and_unchanged(CleanupProvider provider)
    {
        var saved = Complete(provider);
        var draft = saved.Clone();

        Assert.True(RemoteActivityPolicy.MayContact(saved, draft, RemoteActivityTrigger.WindowOpen));

        draft.EnableAiCleanup = false;
        Assert.False(RemoteActivityPolicy.MayContact(saved, draft, RemoteActivityTrigger.WindowOpen));
    }

    [Fact]
    public void Foundry_local_never_counts_as_remote_browse_contact()
    {
        var saved = Complete(CleanupProvider.FoundryLocal);
        Assert.False(RemoteActivityPolicy.MayContact(saved, saved.Clone(), RemoteActivityTrigger.WindowOpen));
        Assert.True(RemoteActivityPolicy.MayContact(saved, saved.Clone(), RemoteActivityTrigger.ButtonPress));
    }

    [Theory]
    [InlineData(nameof(AppSettings.AiCleanupAzureEndpoint))]
    [InlineData(nameof(AppSettings.AiCleanupAzureDeployment))]
    [InlineData(nameof(AppSettings.AiCleanupAzureSubscriptionId))]
    [InlineData(nameof(AppSettings.AiCleanupAzureSubscriptionTenantId))]
    [InlineData(nameof(AppSettings.AiCleanupAzureAuthMode))]
    [InlineData(nameof(AppSettings.AiCleanupAzureTenantId))]
    [InlineData(nameof(AppSettings.AiCleanupAzureClientId))]
    [InlineData(nameof(AppSettings.AiCleanupAzureClientSecret))]
    [InlineData(nameof(AppSettings.AiCleanupAzureApiKey))]
    public void Azure_fingerprint_fields_must_match(string field)
    {
        var saved = Complete(CleanupProvider.AzureFoundry);
        if (field != nameof(AppSettings.AiCleanupAzureApiKey))
        {
            saved.AiCleanupAzureApiKey = null;
        }

        if (field is nameof(AppSettings.AiCleanupAzureClientId) or nameof(AppSettings.AiCleanupAzureClientSecret))
        {
            saved.AiCleanupAzureAuthMode = AzureAuthMode.ServicePrincipal;
        }

        var draft = saved.Clone();
        Mutate(draft, field);

        Assert.False(RemoteActivityPolicy.MayContact(saved, draft, RemoteActivityTrigger.WindowOpen));
    }

    [Theory]
    [InlineData(nameof(AppSettings.AiCleanupCustomEndpoint))]
    [InlineData(nameof(AppSettings.AiCleanupCustomModel))]
    [InlineData(nameof(AppSettings.AiCleanupCustomApiKey))]
    public void Custom_provider_fingerprint_fields_must_match(string field)
    {
        var saved = Complete(CleanupProvider.OpenAiCompatible);
        var draft = saved.Clone();
        Mutate(draft, field);

        Assert.False(RemoteActivityPolicy.MayContact(saved, draft, RemoteActivityTrigger.WindowOpen));
    }

    [Fact]
    public void Copilot_model_is_part_of_the_fingerprint()
    {
        var saved = Complete(CleanupProvider.GitHubCopilot);
        var draft = saved.Clone();
        draft.AiCleanupCopilotModel = "other";

        Assert.False(RemoteActivityPolicy.MayContact(saved, draft, RemoteActivityTrigger.WindowOpen));
    }

    [Theory]
    [InlineData(nameof(AppSettings.AiCleanupAzureClientSecret), " secret")]
    [InlineData(nameof(AppSettings.AiCleanupAzureClientSecret), "secret ")]
    [InlineData(nameof(AppSettings.AiCleanupAzureApiKey), " azure-key")]
    [InlineData(nameof(AppSettings.AiCleanupAzureApiKey), "azure-key ")]
    [InlineData(nameof(AppSettings.AiCleanupCustomApiKey), " key")]
    [InlineData(nameof(AppSettings.AiCleanupCustomApiKey), "key ")]
    public void Secret_whitespace_changes_block_automatic_contact_until_reverted(string field, string changed)
    {
        foreach (var trigger in new[] { RemoteActivityTrigger.WindowOpen, RemoteActivityTrigger.PageSwitch })
        {
            var saved = Complete(field == nameof(AppSettings.AiCleanupCustomApiKey)
                ? CleanupProvider.OpenAiCompatible
                : CleanupProvider.AzureFoundry);
            if (field == nameof(AppSettings.AiCleanupAzureClientSecret))
            {
                saved.AiCleanupAzureApiKey = null;
                saved.AiCleanupAzureAuthMode = AzureAuthMode.ServicePrincipal;
            }

            var draft = saved.Clone();
            SetSecret(draft, field, changed);

            Assert.False(RemoteActivityPolicy.MayContact(saved, draft, trigger));

            SetSecret(draft, field, CurrentSecret(saved, field));
            Assert.True(RemoteActivityPolicy.MayContact(saved, draft, trigger));
        }
    }

    [Fact]
    public void Captured_copilot_auto_list_is_rejected_after_committed_provider_changes()
    {
        var saved = Complete(CleanupProvider.GitHubCopilot);
        var draft = saved.Clone();
        var authorization = RemoteActivityPolicy.CaptureAutomaticContact(saved, draft, RemoteActivityTrigger.WindowOpen, @"C:\Tools\copilot.cmd");
        var committed = Complete(CleanupProvider.OpenAiCompatible);

        Assert.NotNull(authorization);
        Assert.False(RemoteActivityPolicy.IsStillAuthorized(authorization, committed, draft, @"C:\Tools\copilot.cmd"));
    }

    [Fact]
    public void Captured_copilot_auto_list_requires_same_cli_path_and_model()
    {
        var saved = Complete(CleanupProvider.GitHubCopilot);
        var draft = saved.Clone();
        var authorization = RemoteActivityPolicy.CaptureAutomaticContact(saved, draft, RemoteActivityTrigger.WindowOpen, @"C:\Tools\copilot.cmd");

        Assert.True(RemoteActivityPolicy.IsStillAuthorized(authorization, saved, draft, @"C:\Tools\copilot.cmd"));
        Assert.False(RemoteActivityPolicy.IsStillAuthorized(authorization, saved, draft, @"C:\Other\copilot.cmd"));

        var otherModel = saved.Clone();
        otherModel.AiCleanupCopilotModel = "other";
        Assert.False(RemoteActivityPolicy.IsStillAuthorized(authorization, otherModel, otherModel.Clone(), @"C:\Tools\copilot.cmd"));
    }

    [Fact]
    public void A_captured_copilot_auto_list_is_withdrawn_by_a_draft_edited_meanwhile()
    {
        var saved = Complete(CleanupProvider.GitHubCopilot);
        var authorization = RemoteActivityPolicy.CaptureAutomaticContact(saved, saved.Clone(), RemoteActivityTrigger.WindowOpen, @"C:\Tools\copilot.cmd");
        Assert.NotNull(authorization);

        // The reviewer's cases: cleanup unticked, or another model typed, while the detection was held. Nothing was saved.
        var unticked = saved.Clone();
        unticked.EnableAiCleanup = false;
        Assert.False(RemoteActivityPolicy.IsStillAuthorized(authorization, saved, unticked, @"C:\Tools\copilot.cmd"));

        var otherModel = saved.Clone();
        otherModel.AiCleanupCopilotModel = "another-model";
        Assert.False(RemoteActivityPolicy.IsStillAuthorized(authorization, saved, otherModel, @"C:\Tools\copilot.cmd"));

        var otherProvider = saved.Clone();
        otherProvider.AiCleanupProvider = CleanupProvider.OpenAiCompatible;
        Assert.False(RemoteActivityPolicy.IsStillAuthorized(authorization, saved, otherProvider, @"C:\Tools\copilot.cmd"));

        Assert.True(RemoteActivityPolicy.IsStillAuthorized(authorization, saved, saved.Clone(), @"C:\Tools\copilot.cmd"));
    }

    [Fact]
    public void Azure_cli_saved_settings_ignore_a_hidden_app_registration_in_the_draft()
    {
        var saved = Complete(CleanupProvider.AzureFoundry);
        saved.AiCleanupAzureApiKey = null;
        saved.AiCleanupAzureAuthMode = AzureAuthMode.AzureCli;
        saved.AiCleanupAzureClientId = null;
        saved.AiCleanupAzureClientSecret = null;
        var draft = saved.Clone();
        draft.AiCleanupAzureClientId = "stale-client";
        draft.AiCleanupAzureClientSecret = "stale-secret";

        Assert.True(RemoteActivityPolicy.IsSavedAndActive(saved, draft));

        saved.AiCleanupAzureAuthMode = AzureAuthMode.ServicePrincipal;
        draft.AiCleanupAzureAuthMode = AzureAuthMode.ServicePrincipal;
        Assert.False(RemoteActivityPolicy.IsSavedAndActive(saved, draft));
    }

    [Fact]
    public void Api_key_saved_active_ignores_hidden_cli_tenant()
    {
        var saved = Complete(CleanupProvider.AzureFoundry);
        saved.AiCleanupAzureAuthMode = AzureAuthMode.AzureCli;
        saved.AiCleanupAzureTenantId = "stale-hidden-tenant";
        saved.AiCleanupAzureClientId = "stale-client";
        saved.AiCleanupAzureClientSecret = "stale-secret";
        var draft = saved.Clone();
        draft.AiCleanupAzureTenantId = null;
        draft.AiCleanupAzureClientId = null;
        draft.AiCleanupAzureClientSecret = null;

        Assert.True(RemoteActivityPolicy.IsSavedAndActive(saved, draft));
        Assert.True(RemoteActivityPolicy.MayContact(saved, draft, RemoteActivityTrigger.WindowOpen));
    }
    private static AppSettings Complete(CleanupProvider provider)
    {
        var settings = AppSettings.CreateDefault();
        settings.EnableAiCleanup = true;
        settings.AiCleanupProvider = provider;
        settings.AiCleanupAzureEndpoint = "https://example.test";
        settings.AiCleanupAzureDeployment = "cleanup";
        settings.AiCleanupAzureSubscriptionId = "sub";
        settings.AiCleanupAzureSubscriptionTenantId = "subscription-tenant";
        settings.AiCleanupAzureTenantId = "tenant";
        settings.AiCleanupAzureClientId = "client";
        settings.AiCleanupAzureClientSecret = "secret";
        settings.AiCleanupAzureApiKey = "azure-key";
        settings.AiCleanupCustomEndpoint = "http://localhost:11434/v1";
        settings.AiCleanupCustomModel = "qwen";
        settings.AiCleanupCustomApiKey = "key";
        settings.AiCleanupCopilotModel = "gpt";
        return settings;
    }

    private static void Mutate(AppSettings settings, string field)
    {
        switch (field)
        {
            case nameof(AppSettings.AiCleanupAzureEndpoint):
                settings.AiCleanupAzureEndpoint = "https://other.test";
                break;
            case nameof(AppSettings.AiCleanupAzureDeployment):
                settings.AiCleanupAzureDeployment = "other";
                break;
            case nameof(AppSettings.AiCleanupAzureSubscriptionId):
                settings.AiCleanupAzureSubscriptionId = "sub-other";
                break;
            case nameof(AppSettings.AiCleanupAzureSubscriptionTenantId):
                settings.AiCleanupAzureSubscriptionTenantId = "tenant-other";
                break;
            case nameof(AppSettings.AiCleanupAzureAuthMode):
                settings.AiCleanupAzureAuthMode = AzureAuthMode.ServicePrincipal;
                break;
            case nameof(AppSettings.AiCleanupAzureTenantId):
                settings.AiCleanupAzureTenantId = "other";
                break;
            case nameof(AppSettings.AiCleanupAzureClientId):
                settings.AiCleanupAzureClientId = "other";
                break;
            case nameof(AppSettings.AiCleanupAzureClientSecret):
                settings.AiCleanupAzureClientSecret = "different-secret";
                break;
            case nameof(AppSettings.AiCleanupAzureApiKey):
                settings.AiCleanupAzureApiKey = "different-key";
                break;
            case nameof(AppSettings.AiCleanupCustomEndpoint):
                settings.AiCleanupCustomEndpoint = "http://localhost:1234/v1";
                break;
            case nameof(AppSettings.AiCleanupCustomModel):
                settings.AiCleanupCustomModel = "other";
                break;
            case nameof(AppSettings.AiCleanupCustomApiKey):
                settings.AiCleanupCustomApiKey = "different-key";
                break;
        }
    }

    private static void SetSecret(AppSettings settings, string field, string? value)
    {
        switch (field)
        {
            case nameof(AppSettings.AiCleanupAzureClientSecret):
                settings.AiCleanupAzureClientSecret = value;
                break;
            case nameof(AppSettings.AiCleanupAzureApiKey):
                settings.AiCleanupAzureApiKey = value;
                break;
            case nameof(AppSettings.AiCleanupCustomApiKey):
                settings.AiCleanupCustomApiKey = value;
                break;
        }
    }

    private static string? CurrentSecret(AppSettings settings, string field) => field switch
    {
        nameof(AppSettings.AiCleanupAzureClientSecret) => settings.AiCleanupAzureClientSecret,
        nameof(AppSettings.AiCleanupAzureApiKey) => settings.AiCleanupAzureApiKey,
        nameof(AppSettings.AiCleanupCustomApiKey) => settings.AiCleanupCustomApiKey,
        _ => null,
    };
}
