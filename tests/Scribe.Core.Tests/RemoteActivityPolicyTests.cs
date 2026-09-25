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
            var draft = saved.Clone();
            SetSecret(draft, field, changed);

            Assert.False(RemoteActivityPolicy.MayContact(saved, draft, trigger));

            SetSecret(draft, field, CurrentSecret(saved, field));
            Assert.True(RemoteActivityPolicy.MayContact(saved, draft, trigger));
        }
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
