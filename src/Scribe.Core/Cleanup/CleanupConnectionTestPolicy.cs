using Scribe.Core.Settings;

namespace Scribe.Core.Cleanup;

public static class CleanupConnectionTestPolicy
{
    public static CleanupOptions Canonicalize(CleanupOptions options)
    {
        var alias = string.IsNullOrWhiteSpace(options.FoundryModelAlias)
            ? CleanupModelCatalog.DefaultAlias
            : options.FoundryModelAlias.Trim();

        return options with
        {
            Enabled = true,
            FoundryModelAlias = alias,
            AzureEndpoint = Clean(options.AzureEndpoint),
            AzureDeployment = Clean(options.AzureDeployment),
            AzureTenantId = Clean(options.AzureTenantId),
            AzureSubscriptionId = Clean(options.AzureSubscriptionId),
            AzureClientId = Clean(options.AzureClientId),
            AzureClientSecret = string.IsNullOrEmpty(options.AzureClientSecret) ? null : options.AzureClientSecret,
            AzureApiKey = string.IsNullOrEmpty(options.AzureApiKey) ? null : options.AzureApiKey,
            CustomEndpoint = Clean(options.CustomEndpoint),
            CustomModel = Clean(options.CustomModel),
            CustomApiKey = string.IsNullOrEmpty(options.CustomApiKey) ? null : options.CustomApiKey,
            CopilotModel = Clean(options.CopilotModel),
        };
    }

    /// <summary>Whether Test connection can run for <paramref name="candidate"/>; the button and the click both ask this.</summary>
    /// <param name="candidate">The page's current draft.</param>
    /// <param name="apiKeySelected">
    /// The page's sign-in method is "An API key". An API key is stored as Azure CLI mode with a key, not as a mode of its
    /// own, so without this an empty key would silently test the Azure CLI sign-in instead of the key the user chose.
    /// </param>
    public static bool CanTest(CleanupOptions candidate, bool apiKeySelected = false)
    {
        var options = Canonicalize(candidate);
        return options.Provider switch
        {
            CleanupProvider.AzureFoundry => options.AzureAuthMode switch
            {
                // The same validation the credential applies, so an enabled button never meets a rejected identity.
                AzureAuthMode.ServicePrincipal =>
                    AzureServicePrincipalValidator.IsComplete(options.AzureTenantId, options.AzureClientId, options.AzureClientSecret) &&
                    HasAzureDeployment(options),
                _ when apiKeySelected =>
                    !string.IsNullOrEmpty(options.AzureApiKey) && HasAzureDeployment(options),
                _ => HasAzureDeployment(options),
            },
            CleanupProvider.OpenAiCompatible =>
                !string.IsNullOrWhiteSpace(options.CustomEndpoint) &&
                !string.IsNullOrWhiteSpace(options.CustomModel),
            _ => false,
        };
    }

    private static bool HasAzureDeployment(CleanupOptions options) =>
        !string.IsNullOrWhiteSpace(options.AzureEndpoint) &&
        !string.IsNullOrWhiteSpace(options.AzureDeployment);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
