using Azure.Core;
using Azure.Identity;
using Scribe.Core.Settings;

namespace Scribe.Core.Cleanup;

/// <summary>
/// Identity Scribe should present to Microsoft Foundry, resolved from the user's settings.
/// </summary>
internal readonly record struct AzureCredentialRequest(
    AzureAuthMode Mode,
    string? TenantId,
    string? SubscriptionId,
    string? ClientId,
    string? ClientSecret)
{
    internal static AzureCredentialRequest Cli(string? tenantId, string? subscriptionId = null) =>
        new(AzureAuthMode.AzureCli, tenantId, subscriptionId, null, null);
}

/// <summary>
/// Builds the <see cref="TokenCredential"/> for the Microsoft Foundry provider.
/// </summary>
/// <remarks>
/// Deliberately returns a concrete credential rather than <see cref="DefaultAzureCredential"/> with
/// exclusions. Microsoft's own guidance is that the winning credential in a chain "can't be
/// guaranteed ahead of time", that chained environment variables "apply globally and therefore alter
/// the behavior of DefaultAzureCredential at runtime in any app running on that machine", and that
/// once several Exclude flags are set "the advantages of using DefaultAzureCredential diminish".
/// On a user's own desktop that unpredictability is exactly the bug we already shipped once, when
/// managed identity probed a nonexistent IMDS endpoint ahead of the CLI sign-in.
/// </remarks>
internal static class AzureCredentialFactory
{
    // Azure.Identity caches tokens per credential instance, and Microsoft warns that an app which
    // "doesn't reuse credentials may encounter HTTP 429 throttling responses from Microsoft Entra
    // ID". Settings discovery and cleanup validation both build credentials on their own schedules,
    // so hand back the same instance while the identity is unchanged.
    private static readonly Lock Gate = new();
    private static AzureCredentialRequest _cachedRequest;
    private static TokenCredential? _cached;

    internal static TokenCredential Create(AzureCredentialRequest request)
    {
        lock (Gate)
        {
            if (_cached is not null && _cachedRequest == Normalize(request))
            {
                return _cached;
            }

            var normalized = Normalize(request);
            var credential = Build(normalized);
            _cachedRequest = normalized;
            _cached = credential;
            return credential;
        }
    }

    /// <summary>
    /// Drops the cached credential. Called when settings change so a re-entered secret or a fresh
    /// <c>az login</c> is picked up instead of serving a credential built from the old identity.
    /// </summary>
    internal static void Invalidate()
    {
        lock (Gate)
        {
            _cachedRequest = default;
            _cached = null;
        }
    }

    private static TokenCredential Build(AzureCredentialRequest request)
    {
        if (request.Mode == AzureAuthMode.ServicePrincipal)
        {
            // Guarded rather than assumed: a partially filled form would otherwise surface as an
            // opaque Entra error on the first dictation instead of a validation message in Settings.
            if (!AzureServicePrincipalValidator.IsComplete(
                    request.TenantId, request.ClientId, request.ClientSecret))
            {
                throw new InvalidOperationException(
                    "The service principal is incomplete. Enter the tenant ID, client ID, and client secret in Settings.");
            }

            return new ClientSecretCredential(
                request.TenantId!.Trim(),
                request.ClientId!.Trim(),
                request.ClientSecret!);
        }

        var options = new AzureCliCredentialOptions
        {
            ProcessTimeout = TimeSpan.FromSeconds(60),
        };

        // A subscription selects the matching cached CLI account as well as its tenant. Supplying
        // --tenant alongside it can force Azure CLI's active account instead, which breaks caches
        // containing subscriptions from more than one signed-in account.
        if (!string.IsNullOrWhiteSpace(request.SubscriptionId))
        {
            options.Subscription = request.SubscriptionId.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(request.TenantId))
        {
            options.TenantId = request.TenantId.Trim();
        }

        return new SerializedAzureCliCredential(new AzureCliCredential(options));
    }

    // Blank and whitespace-only values are the same identity, so they must not miss the cache.
    private static AzureCredentialRequest Normalize(AzureCredentialRequest request) => new(
        request.Mode,
        Clean(request.TenantId),
        Clean(request.SubscriptionId),
        Clean(request.ClientId),
        // The secret keeps its exact value: only its presence and identity matter for cache keying,
        // and trimming a secret would silently change the credential.
        string.IsNullOrEmpty(request.ClientSecret) ? null : request.ClientSecret);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
