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
        bool HasUsableAuthentication);

    public enum ValidationIssue
    {
        None,
        AuthenticationRequired,
        EndpointRequired,
        DeploymentRequired,
    }

    public static State Resolve(
        bool cliInstalled,
        bool signedIn,
        bool manualConfigurationRequested,
        bool hasApiKey)
    {
        var manualConfigurationAvailable = manualConfigurationRequested || hasApiKey;
        return new State(
            ShowCliSetup: !cliInstalled,
            ShowDiscovery: signedIn,
            ShowConfiguration: signedIn || manualConfigurationAvailable,
            ShowManualConfigurationAction: !signedIn && !manualConfigurationAvailable,
            CanStartSignIn: cliInstalled,
            HasUsableAuthentication: signedIn || hasApiKey);
    }

    public static ValidationIssue ValidateCleanup(
        bool enabled,
        bool usesAzureProvider,
        bool signedIn,
        string? apiKey,
        string? endpoint,
        string? deployment)
    {
        if (!enabled || !usesAzureProvider)
        {
            return ValidationIssue.None;
        }

        if (!signedIn && string.IsNullOrWhiteSpace(apiKey))
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
