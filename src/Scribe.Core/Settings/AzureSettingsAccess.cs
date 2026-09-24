namespace Scribe.Core.Settings;

/// <summary>
/// Pure policy for the Microsoft Foundry settings surface. The WPF window owns presentation, while
/// this type decides which configuration paths are honest to expose for the current authentication
/// state and whether enabled Azure cleanup has enough information to work.
/// </summary>
public static class AzureSettingsAccess
{
    public readonly record struct State(
        bool ShowCliSetup,
        bool ShowDiscovery,
        bool ShowConfiguration,
        bool ShowManualConfigurationAction,
        bool CanStartSignIn,
        bool HasUsableAuthentication,
        bool ShowServicePrincipalFields,
        bool ShowCliTenant);

    public enum ValidationIssue
    {
        None,
        AuthenticationRequired,
        EndpointRequired,
        DeploymentRequired,
        ServicePrincipalIncomplete,
    }

    /// <summary>Decides which parts of the Microsoft Foundry settings show for the current sign-in state.</summary>
    /// <param name="apiKeySelected">
    /// The API key sign-in method is chosen. It is stored as <see cref="AzureAuthMode.AzureCli"/> plus
    /// a key, so <paramref name="authMode"/> alone cannot tell it apart.
    /// </param>
    public static State Resolve(
        bool cliInstalled,
        bool signedIn,
        bool manualConfigurationRequested,
        bool hasApiKey,
        AzureAuthMode authMode = AzureAuthMode.AzureCli,
        bool servicePrincipalComplete = false,
        bool apiKeySelected = false)
    {
        var manualConfigurationAvailable = manualConfigurationRequested || hasApiKey;

        if (authMode == AzureAuthMode.ServicePrincipal)
        {
            // A service principal never shells out to Azure CLI, so the install prompt is noise
            // here. The action button verifies the app registration instead of opening a browser,
            // so it stays disabled until there is something complete to verify.
            //
            // Discovery stays hidden on purpose. Enumerating subscriptions and deployments is a
            // control-plane operation that would additionally require Reader across the
            // subscription, whereas calling the model only needs the inference role on the one
            // resource. Asking a corporate admin for the smaller grant is the difference between
            // this feature being approvable and not, so this mode takes an endpoint and deployment
            // name directly.
            //
            // Configuration keys off the credential being COMPLETE rather than live-verified. A
            // service principal is the credential, so "do I have one" is answerable offline, and
            // gating on a network round trip meant a saved setup rendered as an empty unconfigured
            // panel until the user pressed Verify again on every visit. It also made first-time
            // setup circular: the endpoint boxes stayed hidden until you verified, but verifying
            // was not what supplied them.
            //
            // The app registration names its own tenant among these fields, so the Azure CLI tenant
            // field would be a second control for the same setting.
            return new State(
                ShowCliSetup: false,
                ShowDiscovery: false,
                ShowConfiguration: servicePrincipalComplete || signedIn || manualConfigurationAvailable,
                ShowManualConfigurationAction: false,
                CanStartSignIn: servicePrincipalComplete,
                HasUsableAuthentication: servicePrincipalComplete || signedIn || hasApiKey,
                ShowServicePrincipalFields: true,
                ShowCliTenant: false);
        }

        // The tenant is what an az login authenticates against: signing in passes it on whenever no
        // subscription is selected. It therefore shows whatever the sign-in state, because someone who
        // is not signed in yet, or whose saved tenant is the wrong one, has to be able to set it before
        // signing in. An API key never asks Entra for a token, so there is nothing for it to pin.
        return new State(
            ShowCliSetup: !cliInstalled,
            ShowDiscovery: signedIn,
            ShowConfiguration: signedIn || manualConfigurationAvailable,
            ShowManualConfigurationAction: !signedIn && !manualConfigurationAvailable,
            CanStartSignIn: cliInstalled,
            HasUsableAuthentication: signedIn || hasApiKey,
            ShowServicePrincipalFields: false,
            ShowCliTenant: !apiKeySelected);
    }

    public static ValidationIssue ValidateCleanup(
        bool enabled,
        bool usesAzureProvider,
        bool signedIn,
        string? apiKey,
        string? endpoint,
        string? deployment,
        AzureAuthMode authMode = AzureAuthMode.AzureCli,
        string? tenantId = null,
        string? clientId = null,
        string? clientSecret = null)
    {
        if (!enabled || !usesAzureProvider)
        {
            return ValidationIssue.None;
        }

        var hasApiKey = !string.IsNullOrWhiteSpace(apiKey);
        var servicePrincipalComplete = authMode == AzureAuthMode.ServicePrincipal
            && AzureServicePrincipalValidator.IsComplete(tenantId, clientId, clientSecret);

        // An API key bypasses Entra entirely, so half-entered app registration details are only a
        // blocker when the token path is actually the one being used.
        if (authMode == AzureAuthMode.ServicePrincipal && !hasApiKey && !servicePrincipalComplete)
        {
            return ValidationIssue.ServicePrincipalIncomplete;
        }

        // A complete service principal counts as authentication even when it has not been verified
        // in this session. Verification is a live network call, so requiring it to save would let a
        // dropped connection block edits to unrelated settings, and cleanup already falls back to
        // the raw transcript when a credential stops working.
        if (!signedIn && !hasApiKey && !servicePrincipalComplete)
        {
            return ValidationIssue.AuthenticationRequired;
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return ValidationIssue.EndpointRequired;
        }

        return string.IsNullOrWhiteSpace(deployment)
            ? ValidationIssue.DeploymentRequired
            : ValidationIssue.None;
    }
}
