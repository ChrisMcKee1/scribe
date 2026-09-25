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

internal sealed class RemoteActivityFingerprint : IEquatable<RemoteActivityFingerprint>
{
    private readonly CleanupProvider _provider;
    private readonly string? _foundryModel;
    private readonly string? _azureEndpoint;
    private readonly string? _azureDeployment;
    private readonly string? _azureSubscriptionId;
    private readonly string? _azureSubscriptionTenantId;
    private readonly AzureAuthMode _azureAuthMode;
    private readonly string? _azureTenantId;
    private readonly string? _azureClientId;
    private readonly string? _azureClientSecret;
    private readonly string? _azureApiKey;
    private readonly string? _customEndpoint;
    private readonly string? _customModel;
    private readonly string? _customApiKey;
    private readonly string? _copilotModel;

    private RemoteActivityFingerprint(AppSettings settings)
    {
        _provider = settings.AiCleanupProvider;
        _foundryModel = EmptyToNull(settings.AiCleanupModel);
        _azureEndpoint = EmptyToNull(settings.AiCleanupAzureEndpoint);
        _azureDeployment = EmptyToNull(settings.AiCleanupAzureDeployment);
        _azureSubscriptionId = EmptyToNull(settings.AiCleanupAzureSubscriptionId);
        _azureSubscriptionTenantId = EmptyToNull(settings.AiCleanupAzureSubscriptionTenantId);
        _azureAuthMode = settings.AiCleanupAzureAuthMode;
        _azureTenantId = EmptyToNull(settings.AiCleanupAzureTenantId);
        _azureClientId = EmptyToNull(settings.AiCleanupAzureClientId);
        _azureClientSecret = EmptyToNull(settings.AiCleanupAzureClientSecret);
        _azureApiKey = EmptyToNull(settings.AiCleanupAzureApiKey);
        _customEndpoint = EmptyToNull(settings.AiCleanupCustomEndpoint);
        _customModel = EmptyToNull(settings.AiCleanupCustomModel);
        _customApiKey = EmptyToNull(settings.AiCleanupCustomApiKey);
        _copilotModel = EmptyToNull(settings.AiCleanupCopilotModel);
    }

    public static RemoteActivityFingerprint From(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new RemoteActivityFingerprint(settings);
    }

    public bool Equals(RemoteActivityFingerprint? other) =>
        other is not null &&
        _provider == other._provider &&
        Same(_foundryModel, other._foundryModel) &&
        Same(_azureEndpoint, other._azureEndpoint) &&
        Same(_azureDeployment, other._azureDeployment) &&
        Same(_azureSubscriptionId, other._azureSubscriptionId) &&
        Same(_azureSubscriptionTenantId, other._azureSubscriptionTenantId) &&
        _azureAuthMode == other._azureAuthMode &&
        Same(_azureTenantId, other._azureTenantId) &&
        Same(_azureClientId, other._azureClientId) &&
        Same(_azureClientSecret, other._azureClientSecret) &&
        Same(_azureApiKey, other._azureApiKey) &&
        Same(_customEndpoint, other._customEndpoint) &&
        Same(_customModel, other._customModel) &&
        Same(_customApiKey, other._customApiKey) &&
        Same(_copilotModel, other._copilotModel);

    public override bool Equals(object? obj) => Equals(obj as RemoteActivityFingerprint);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_provider);
        hash.Add(_foundryModel, StringComparer.Ordinal);
        hash.Add(_azureEndpoint, StringComparer.Ordinal);
        hash.Add(_azureDeployment, StringComparer.Ordinal);
        hash.Add(_azureSubscriptionId, StringComparer.Ordinal);
        hash.Add(_azureSubscriptionTenantId, StringComparer.Ordinal);
        hash.Add(_azureAuthMode);
        hash.Add(_azureTenantId, StringComparer.Ordinal);
        hash.Add(_azureClientId, StringComparer.Ordinal);
        hash.Add(_azureClientSecret, StringComparer.Ordinal);
        hash.Add(_azureApiKey, StringComparer.Ordinal);
        hash.Add(_customEndpoint, StringComparer.Ordinal);
        hash.Add(_customModel, StringComparer.Ordinal);
        hash.Add(_customApiKey, StringComparer.Ordinal);
        hash.Add(_copilotModel, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool Same(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);

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

        return RemoteActivityFingerprint.From(savedSettings).Equals(RemoteActivityFingerprint.From(draft));
    }
}
