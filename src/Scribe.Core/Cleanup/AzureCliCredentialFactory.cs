using Azure.Identity;

namespace Scribe.Core.Cleanup;

internal static class AzureCliCredentialFactory
{
    internal static AzureCliCredential Create(string? tenantId, string? subscriptionId = null)
    {
        var options = new AzureCliCredentialOptions
        {
            ProcessTimeout = TimeSpan.FromSeconds(30),
        };

        // A subscription selects the matching cached CLI account as well as its tenant. Supplying
        // --tenant alongside it can force Azure CLI's active account instead, which breaks caches
        // containing subscriptions from more than one signed-in account.
        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            options.Subscription = subscriptionId.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(tenantId))
        {
            options.TenantId = tenantId.Trim();
        }

        return new AzureCliCredential(options);
    }
}