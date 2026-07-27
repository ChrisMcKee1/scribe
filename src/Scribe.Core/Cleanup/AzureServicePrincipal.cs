using Scribe.Core.Settings;

namespace Scribe.Core.Cleanup;

/// <summary>
/// Entra ID app registration credentials for the Microsoft Foundry provider. Lets a user who belongs
/// to several tenants pin one identity rather than depending on whichever account Azure CLI happens
/// to have active.
/// </summary>
/// <remarks>
/// The secret lives in memory here and is encrypted at rest with Windows DPAPI (current user) by
/// <see cref="Models.AppSettings.AiCleanupAzureClientSecret"/>. It is deliberately never written to
/// an environment variable or a script on disk: Microsoft documents environment variables as plain
/// unencrypted text, and persistent <c>AZURE_CLIENT_*</c> variables would additionally change how
/// every other Azure application on the machine resolves its credentials.
/// </remarks>
public sealed record AzureServicePrincipal(string TenantId, string ClientId, string ClientSecret)
{
    /// <summary>
    /// Builds a service principal from settings values, or null when the details are incomplete so
    /// callers fall back to Azure CLI rather than failing with an opaque Entra error.
    /// </summary>
    public static AzureServicePrincipal? TryCreate(
        AzureAuthMode mode, string? tenantId, string? clientId, string? clientSecret) =>
        mode == AzureAuthMode.ServicePrincipal
        && AzureServicePrincipalValidator.IsComplete(tenantId, clientId, clientSecret)
            ? new AzureServicePrincipal(tenantId!.Trim(), clientId!.Trim(), clientSecret!)
            : null;

    internal AzureCredentialRequest ToRequest(string? subscriptionId) => new(
        AzureAuthMode.ServicePrincipal, TenantId, subscriptionId, ClientId, ClientSecret);
}
