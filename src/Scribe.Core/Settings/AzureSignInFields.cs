using Scribe.Core.Models;

namespace Scribe.Core.Settings;

/// <summary>
/// The Microsoft Foundry sign-in fields as a Save stores them for the sign-in method shown. An API key stores its key and
/// no tenant or app registration; Azure CLI stores its optional tenant and no app registration; a service principal stores
/// its own tenant, client id and secret. The window keeps a hidden mode's fields (so switching back restores them), and
/// both the Save and the page's saved-and-active comparison read the fields only through this, so a field a mode hides
/// never decides what that mode stores or whether it reads as saved.
/// </summary>
public static class AzureSignInFields
{
    public readonly record struct Stored(string? TenantId, string? ClientId, string? ClientSecret, string? ApiKey);

    public static Stored For(
        AzureAuthMode mode,
        bool apiKeySelected,
        string? cliTenant,
        string? servicePrincipalTenant,
        string? clientId,
        string? clientSecret,
        string? apiKey)
    {
        if (apiKeySelected)
        {
            return new Stored(TenantId: null, ClientId: null, ClientSecret: null, ApiKey: NullIfBlank(apiKey));
        }

        return mode == AzureAuthMode.ServicePrincipal
            ? new Stored(NullIfBlank(servicePrincipalTenant), NullIfBlank(clientId), NullIfBlank(clientSecret), ApiKey: null)
            : new Stored(NullIfBlank(cliTenant), ClientId: null, ClientSecret: null, ApiKey: null);
    }

    // As the Settings window has always stored these fields.
    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
