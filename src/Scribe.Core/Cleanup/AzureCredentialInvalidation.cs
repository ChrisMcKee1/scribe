namespace Scribe.Core.Cleanup;

/// <summary>
/// Drops the cached Microsoft Foundry credential. Scribe keeps one instance per identity because Microsoft warns that
/// not reusing credentials invites HTTP 429 throttling from Entra ID; a service principal's instance also holds MSAL's
/// token cache, and under PerfFlags.CliAccessTokenCache an Azure CLI sign-in's cleanup instance holds its access token
/// (an Azure CLI credential caches nothing by itself). So it has to be dropped whenever the identity changes, or a
/// corrected secret or a fresh <c>az login</c> would keep serving the old credential; a cleanup client that already holds
/// the Azure CLI cache acquires again on its next request.
/// </summary>
public static class AzureCredentialInvalidation
{
    public static void Invalidate() => AzureCredentialFactory.Invalidate();
}
