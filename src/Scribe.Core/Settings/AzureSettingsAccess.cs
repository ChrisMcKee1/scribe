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
        bool ShowServicePrincipalFields);

    public enum ValidationIssue
    {
        None,
        AuthenticationRequired,
        EndpointRequired,
        DeploymentRequired,
        ServicePrincipalIncomplete,
    }

    public static State Resolve(
        bool cliInstalled,
        bool signedIn,
        bool manualConfigurationRequested,
        bool hasApiKey,
        AzureAuthMode authMode = AzureAuthMode.AzureCli,
        bool servicePrincipalComplete = false)
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
            return new State(
                ShowCliSetup: false,
                ShowDiscovery: false,
                ShowConfiguration: signedIn || manualConfigurationAvailable,
                ShowManualConfigurationAction: false,
                CanStartSignIn: servicePrincipalComplete,
                HasUsableAuthentication: signedIn || hasApiKey,
                ShowServicePrincipalFields: true);
        }

        return new State(
            ShowCliSetup: !cliInstalled,
            ShowDiscovery: signedIn,
            ShowConfiguration: signedIn || manualConfigurationAvailable,
            ShowManualConfigurationAction: !signedIn && !manualConfigurationAvailable,
            CanStartSignIn: cliInstalled,
            HasUsableAuthentication: signedIn || hasApiKey,
            ShowServicePrincipalFields: false);
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

        // An API key bypasses Entra entirely, so half-entered app registration details are only a
        // blocker when the token path is actually the one being used.
        if (authMode == AzureAuthMode.ServicePrincipal && !hasApiKey
            && !AzureServicePrincipalValidator.IsComplete(tenantId, clientId, clientSecret))
        {
            return ValidationIssue.ServicePrincipalIncomplete;
        }

        if (!signedIn && !hasApiKey)
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
