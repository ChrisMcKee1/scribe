using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;

namespace Scribe.StyleEval.Runner;

/// <summary>
/// Enumerates the Azure deployments the current sign-in can actually reach, through the same
/// <see cref="AzureFoundryDiscovery"/> the app uses to populate its settings dropdown.
/// </summary>
/// <remarks>
/// Discovery rather than a hardcoded list, because a hardcoded endpoint is how an eval run silently
/// measures the wrong resource. <c>--list-deployments</c> prints what is really there so the run
/// command can name it explicitly.
/// </remarks>
internal static class DeploymentDiscovery
{
    public static async Task<int> ListAsync(StyleEvalOptions options, CancellationToken ct)
    {
        var discovery = new AzureFoundryDiscovery(NullLogger<AzureFoundryDiscovery>.Instance);

        Console.WriteLine("Signed-in identity:");
        try
        {
            var status = await discovery
                .GetSignInStatusAsync(options.TenantId, options.Subscription, ct)
                .ConfigureAwait(false);
            Console.WriteLine(status.IsSignedIn
                ? $"  {status.Account} in tenant {status.TenantId}"
                : $"  not signed in. {status.FailureReason ?? "Run 'az login'."}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  could not be read: {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("Text-capable deployments visible to this sign-in:");

        IReadOnlyList<AzureFoundryDeployment> deployments;
        try
        {
            deployments = await discovery.DiscoverAsync(options.TenantId, options.Subscription, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  discovery failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        if (deployments.Count == 0)
        {
            Console.WriteLine("  none. Run 'az login' for the tenant that owns the deployment.");
            return 1;
        }

        foreach (var group in deployments.GroupBy(d => d.Endpoint, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine($"  {group.Key}");
            var first = group.First();
            Console.WriteLine($"    account {first.AccountName} ({first.Kind}) in {first.SubscriptionName} / {first.Location}");
            Console.WriteLine($"    subscription {first.SubscriptionId}   tenant {first.TenantId}");
            foreach (var deployment in group.OrderBy(d => d.DeploymentName, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"    - {deployment.DeploymentName}  (model {deployment.ModelName} {deployment.ModelVersion})");
            }
        }

        return 0;
    }
}
