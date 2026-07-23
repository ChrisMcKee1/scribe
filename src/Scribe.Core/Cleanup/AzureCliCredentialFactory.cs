using Azure.Core;
using Azure.Identity;

namespace Scribe.Core.Cleanup;

internal static class AzureCliCredentialFactory
{
    internal static TokenCredential Create(string? tenantId, string? subscriptionId = null)
    {
        // DefaultAzureCredential can exclude managed identity and the other deployed credentials, but
        // Azure CLI is Scribe's explicit user contract. A concrete credential is deterministic, avoids
        // desktop IMDS probes, and supports selecting the cached CLI account by subscription.
        var options = new AzureCliCredentialOptions
        {
            ProcessTimeout = TimeSpan.FromSeconds(60),
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

        return new SerializedAzureCliCredential(new AzureCliCredential(options));
    }
}