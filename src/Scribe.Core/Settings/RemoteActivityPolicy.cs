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
    private readonly CustomApiStyle _customApiStyle;
    private readonly string? _copilotModel;

    private RemoteActivityFingerprint(AppSettings settings)
    {
        _provider = settings.AiCleanupProvider;
        _foundryModel = EmptyToNull(settings.AiCleanupModel);
        _azureEndpoint = EmptyToNull(settings.AiCleanupAzureEndpoint);
        _azureDeployment = EmptyToNull(settings.AiCleanupAzureDeployment);
        _azureApiKey = RawEmptyToNull(settings.AiCleanupAzureApiKey);
        if (_azureApiKey is not null)
        {
            _azureSubscriptionId = null;
            _azureSubscriptionTenantId = null;
            _azureAuthMode = AzureAuthMode.AzureCli;
            _azureTenantId = null;
            _azureClientId = null;
            _azureClientSecret = null;
        }
        else
        {
            _azureSubscriptionId = EmptyToNull(settings.AiCleanupAzureSubscriptionId);
            _azureSubscriptionTenantId = EmptyToNull(settings.AiCleanupAzureSubscriptionTenantId);
            _azureAuthMode = settings.AiCleanupAzureAuthMode;
            _azureTenantId = settings.AiCleanupAzureAuthMode == AzureAuthMode.ServicePrincipal ? EmptyToNull(settings.AiCleanupAzureTenantId) : EmptyToNull(settings.AiCleanupAzureTenantId);
            _azureClientId = settings.AiCleanupAzureAuthMode == AzureAuthMode.ServicePrincipal ? EmptyToNull(settings.AiCleanupAzureClientId) : null;
            _azureClientSecret = settings.AiCleanupAzureAuthMode == AzureAuthMode.ServicePrincipal ? RawEmptyToNull(settings.AiCleanupAzureClientSecret) : null;
        }

        _customEndpoint = EmptyToNull(settings.AiCleanupCustomEndpoint);
        _customModel = EmptyToNull(settings.AiCleanupCustomModel);
        _customApiKey = RawEmptyToNull(settings.AiCleanupCustomApiKey);
        _customApiStyle = CustomServiceAddress.Effective(settings.AiCleanupProvider, _customEndpoint, settings.AiCleanupCustomApiStyle);
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
        _customApiStyle == other._customApiStyle &&
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
        hash.Add(_customApiStyle);
        hash.Add(_copilotModel, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? RawEmptyToNull(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static bool Same(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);
}

public sealed class RemoteActivityAuthorization
{
    internal RemoteActivityAuthorization(CleanupProvider provider, RemoteActivityFingerprint fingerprint, string? cliPath, string? model)
    {
        Provider = provider;
        Fingerprint = fingerprint;
        CliPath = EmptyToNull(cliPath);
        Model = EmptyToNull(model);
    }

    public CleanupProvider Provider { get; }

    internal RemoteActivityFingerprint Fingerprint { get; }

    public string? CliPath { get; }

    public string? Model { get; }

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

        return RemoteActivityFingerprint.From(savedSettings).Equals(RemoteActivityFingerprint.From(draft));
    }

    public static RemoteActivityAuthorization? CaptureAutomaticContact(
        AppSettings savedSettings,
        AppSettings draft,
        RemoteActivityTrigger trigger,
        string? cliPath = null)
    {
        if (!MayContact(savedSettings, draft, trigger))
        {
            return null;
        }

        return new RemoteActivityAuthorization(
            savedSettings.AiCleanupProvider,
            RemoteActivityFingerprint.From(savedSettings),
            cliPath,
            savedSettings.AiCleanupProvider == CleanupProvider.GitHubCopilot ? EmptyToNull(savedSettings.AiCleanupCopilotModel) : null);
    }

    /// <summary>
    /// Whether an automatic contact captured earlier may still run: the saved settings still have that provider on with the
    /// configuration it was captured for, and the page still shows exactly those saved settings. A draft edited meanwhile
    /// (cleanup unticked, another model, another provider) withdraws it, even though nothing was saved.
    /// </summary>
    public static bool IsStillAuthorized(
        RemoteActivityAuthorization? authorization,
        AppSettings committedSettings,
        AppSettings currentDraft,
        string? cliPath = null)
    {
        ArgumentNullException.ThrowIfNull(committedSettings);
        ArgumentNullException.ThrowIfNull(currentDraft);
        if (authorization is null || !committedSettings.EnableAiCleanup || committedSettings.AiCleanupProvider != authorization.Provider ||
            !IsSavedAndActive(committedSettings, currentDraft))
        {
            return false;
        }

        if (!authorization.Fingerprint.Equals(RemoteActivityFingerprint.From(committedSettings)))
        {
            return false;
        }

        if (authorization.Provider == CleanupProvider.GitHubCopilot)
        {
            return Same(EmptyToNull(cliPath), authorization.CliPath) &&
                Same(EmptyToNull(committedSettings.AiCleanupCopilotModel), authorization.Model);
        }

        return true;
    }

    public static bool IsSavedAndActive(AppSettings savedSettings, AppSettings draft)
    {
        ArgumentNullException.ThrowIfNull(savedSettings);
        ArgumentNullException.ThrowIfNull(draft);

        return savedSettings.EnableAiCleanup &&
            draft.EnableAiCleanup &&
            savedSettings.AiCleanupProvider == draft.AiCleanupProvider &&
            SameProviderConfiguration(savedSettings, draft, savedSettings.AiCleanupProvider);
    }

    private static bool SameProviderConfiguration(AppSettings saved, AppSettings draft, CleanupProvider provider) => provider switch
    {
        CleanupProvider.FoundryLocal =>
            Same(EmptyToNull(saved.AiCleanupModel), EmptyToNull(draft.AiCleanupModel)),
        CleanupProvider.AzureFoundry => SameAzureConfiguration(saved, draft),
        CleanupProvider.OpenAiCompatible =>
            Same(EmptyToNull(saved.AiCleanupCustomEndpoint), EmptyToNull(draft.AiCleanupCustomEndpoint)) &&
            Same(EmptyToNull(saved.AiCleanupCustomModel), EmptyToNull(draft.AiCleanupCustomModel)) &&
            Same(RawEmptyToNull(saved.AiCleanupCustomApiKey), RawEmptyToNull(draft.AiCleanupCustomApiKey)) &&
            CustomServiceAddress.Effective(provider, EmptyToNull(saved.AiCleanupCustomEndpoint), saved.AiCleanupCustomApiStyle) ==
                CustomServiceAddress.Effective(provider, EmptyToNull(draft.AiCleanupCustomEndpoint), draft.AiCleanupCustomApiStyle),
        CleanupProvider.GitHubCopilot =>
            Same(EmptyToNull(saved.AiCleanupCopilotModel), EmptyToNull(draft.AiCleanupCopilotModel)),
        _ => false,
    };

    private static bool SameAzureConfiguration(AppSettings saved, AppSettings draft)
    {
        var savedApiKey = RawEmptyToNull(saved.AiCleanupAzureApiKey);
        var draftApiKey = RawEmptyToNull(draft.AiCleanupAzureApiKey);
        if (savedApiKey is not null || draftApiKey is not null)
        {
            return Same(EmptyToNull(saved.AiCleanupAzureEndpoint), EmptyToNull(draft.AiCleanupAzureEndpoint)) &&
                Same(EmptyToNull(saved.AiCleanupAzureDeployment), EmptyToNull(draft.AiCleanupAzureDeployment)) &&
                Same(savedApiKey, draftApiKey);
        }

        return Same(EmptyToNull(saved.AiCleanupAzureEndpoint), EmptyToNull(draft.AiCleanupAzureEndpoint)) &&
            Same(EmptyToNull(saved.AiCleanupAzureDeployment), EmptyToNull(draft.AiCleanupAzureDeployment)) &&
            Same(EmptyToNull(saved.AiCleanupAzureSubscriptionId), EmptyToNull(draft.AiCleanupAzureSubscriptionId)) &&
            Same(EmptyToNull(saved.AiCleanupAzureSubscriptionTenantId), EmptyToNull(draft.AiCleanupAzureSubscriptionTenantId)) &&
            saved.AiCleanupAzureAuthMode == draft.AiCleanupAzureAuthMode &&
            Same(EmptyToNull(saved.AiCleanupAzureTenantId), EmptyToNull(draft.AiCleanupAzureTenantId)) &&

            // The app registration counts only for a service principal: Azure CLI stores none (AzureSignInFields), so a
            // field its mode hides can't make saved settings read as unsaved.
            (saved.AiCleanupAzureAuthMode != AzureAuthMode.ServicePrincipal ||
                (Same(EmptyToNull(saved.AiCleanupAzureClientId), EmptyToNull(draft.AiCleanupAzureClientId)) &&
                 Same(RawEmptyToNull(saved.AiCleanupAzureClientSecret), RawEmptyToNull(draft.AiCleanupAzureClientSecret))));
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? RawEmptyToNull(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static bool Same(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);
}
