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

    public static bool CanTest(CleanupOptions candidate)
    {
        var options = Canonicalize(candidate);
        return options.Provider switch
        {
            CleanupProvider.AzureFoundry => options.AzureAuthMode switch
            {
                AzureAuthMode.ServicePrincipal =>
                    !string.IsNullOrWhiteSpace(options.AzureTenantId) &&
                    !string.IsNullOrWhiteSpace(options.AzureClientId) &&
                    !string.IsNullOrEmpty(options.AzureClientSecret) &&
                    HasAzureDeployment(options),
                AzureAuthMode.AzureCli when !string.IsNullOrEmpty(options.AzureApiKey) =>
                    HasAzureDeployment(options),
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
