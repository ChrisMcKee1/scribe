using Scribe.Core.Cleanup;
using Scribe.Core.Models;

namespace Scribe.Core.Settings;

public enum RemoteActivityTrigger
{
    WindowOpen,
    PageSwitch,
    ProviderChange,
    SignInMethodChange,
    FieldEdit,
    ButtonPress,
}

public sealed record RemoteActivityFingerprint(
    CleanupProvider Provider,
    string? FoundryModel,
    string? AzureEndpoint,
    string? AzureDeployment,
    AzureAuthMode AzureAuthMode,
    string? AzureTenantId,
    string? AzureClientId,
    bool HasAzureClientSecret,
    bool HasAzureApiKey,
    string? CustomEndpoint,
    string? CustomModel,
    bool HasCustomApiKey,
    string? CopilotModel)
{
    public static RemoteActivityFingerprint From(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new RemoteActivityFingerprint(
            settings.AiCleanupProvider,
            EmptyToNull(settings.AiCleanupModel),
            EmptyToNull(settings.AiCleanupAzureEndpoint),
            EmptyToNull(settings.AiCleanupAzureDeployment),
            settings.AiCleanupAzureAuthMode,
            EmptyToNull(settings.AiCleanupAzureTenantId),
            EmptyToNull(settings.AiCleanupAzureClientId),
            !string.IsNullOrWhiteSpace(settings.AiCleanupAzureClientSecret),
            !string.IsNullOrWhiteSpace(settings.AiCleanupAzureApiKey),
            EmptyToNull(settings.AiCleanupCustomEndpoint),
            EmptyToNull(settings.AiCleanupCustomModel),
            !string.IsNullOrWhiteSpace(settings.AiCleanupCustomApiKey),
            EmptyToNull(settings.AiCleanupCopilotModel));
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class RemoteActivityPolicy
{
    public static bool MayContact(AppSettings savedSettings, AppSettings draft, RemoteActivityTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(savedSettings);
        ArgumentNullException.ThrowIfNull(draft);

        if (trigger == RemoteActivityTrigger.ButtonPress)
        {
            return true;
        }

        if (trigger is RemoteActivityTrigger.ProviderChange or RemoteActivityTrigger.SignInMethodChange or RemoteActivityTrigger.FieldEdit)
        {
            return false;
        }

        if (!savedSettings.EnableAiCleanup ||
            !draft.EnableAiCleanup ||
            savedSettings.AiCleanupProvider == CleanupProvider.FoundryLocal ||
            draft.AiCleanupProvider != savedSettings.AiCleanupProvider)
        {
            return false;
        }

        return RemoteActivityFingerprint.From(savedSettings) == RemoteActivityFingerprint.From(draft);
    }
}
