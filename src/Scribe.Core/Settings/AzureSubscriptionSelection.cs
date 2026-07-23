using Scribe.Core.Cleanup;

namespace Scribe.Core.Settings;

/// <summary>Chooses the truthful initial subscription for a verified Azure CLI identity.</summary>
public static class AzureSubscriptionSelection
{
    public static string? ChooseInitialSubscriptionId(
        IReadOnlyList<AzureSubscription> subscriptions,
        string? currentSubscriptionId,
        string? verifiedTenantId)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);

        var tenantSubscriptions = string.IsNullOrWhiteSpace(verifiedTenantId)
            ? subscriptions
            : subscriptions
                .Where(subscription => string.Equals(
                    subscription.TenantId,
                    verifiedTenantId,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (!string.IsNullOrWhiteSpace(currentSubscriptionId))
        {
            var current = tenantSubscriptions.FirstOrDefault(subscription => string.Equals(
                subscription.Id,
                currentSubscriptionId,
                StringComparison.OrdinalIgnoreCase));
            if (current is not null)
            {
                return current.Id;
            }
        }

        return tenantSubscriptions.FirstOrDefault(subscription => subscription.IsDefault)?.Id;
    }

    public static AzureSubscription? ResolveAuthenticationSubscription(
        AzureFoundryDeployment? selectedDeployment,
        AzureSubscription? selectedFilter,
        string? endpoint,
        string? deploymentName)
    {
        if (selectedDeployment is not null &&
            !string.IsNullOrWhiteSpace(selectedDeployment.SubscriptionId) &&
            string.Equals(selectedDeployment.Endpoint, endpoint?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(selectedDeployment.DeploymentName, deploymentName?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new AzureSubscription(
                selectedDeployment.SubscriptionId,
                selectedDeployment.SubscriptionName,
                selectedDeployment.TenantId);
        }

        return selectedFilter;
    }

    public static string? ResolveTenantId(
        string? subscriptionId,
        string? subscriptionTenantId,
        string? manualTenantId) =>
        !string.IsNullOrWhiteSpace(subscriptionId)
            ? NullIfBlank(subscriptionTenantId)
            : NullIfBlank(manualTenantId);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
