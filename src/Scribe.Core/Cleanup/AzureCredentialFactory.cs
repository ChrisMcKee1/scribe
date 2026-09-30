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
    // Microsoft warns that an app which "doesn't reuse credentials may encounter HTTP 429 throttling responses from
    // Microsoft Entra ID". Settings discovery and cleanup validation both build credentials on their own schedules, so
    // hand back the same instance while the identity is unchanged. Reusing the instance caches tokens only where the
    // credential itself does: ClientSecretCredential keeps an MSAL token cache, but AzureCliCredential has none and runs
    // `az account get-access-token` on every call, and the cleanup clients' System.ClientModel BearerTokenPolicy asks the
    // credential on every request (every retry included), so with Azure CLI sign-in each cleanup request runs az once.
    private static readonly Lock Gate = new();
    private static AzureCredentialRequest _cachedRequest;
    private static TokenCredential? _cached;

    // The access-token cache's instance for cleanup (on unless PerfFlags.CliTokenEveryRequest), kept apart so Settings
    // discovery (never cached) and cleanup never evict each other's credential.
    private static AzureCredentialRequest _cachedCachingRequest;
    private static TokenCredential? _cachedCaching;
    private static int _invalidationVersion;

    /// <summary>
    /// Bumped by every <see cref="Invalidate"/>: a <see cref="CachingCliTokenCredential"/> a serving client already holds
    /// compares it on each call, so an identity change made through Scribe reaches that client's next request.
    /// </summary>
    internal static int InvalidationVersion => Volatile.Read(ref _invalidationVersion);

    /// <summary>
    /// The credential without the access-token cache: Settings discovery, Test connection, and cleanup under
    /// PerfFlags.CliTokenEveryRequest.
    /// </summary>
    internal static TokenCredential Create(AzureCredentialRequest request) => Create(request, cacheCliTokens: false);

    /// <param name="request">The identity to present.</param>
    /// <param name="cacheCliTokens">
    /// Wrap an Azure CLI credential in the in-memory token cache: cleanup's two serving clients, unless
    /// PerfFlags.CliTokenEveryRequest. A service principal is returned as it always is (MSAL caches its tokens), whatever
    /// this says.
    /// </param>
    internal static TokenCredential Create(AzureCredentialRequest request, bool cacheCliTokens)
    {
        var normalized = Normalize(request);
        var caching = cacheCliTokens && normalized.Mode == AzureAuthMode.AzureCli;
        lock (Gate)
        {
            if (caching)
            {
                if (_cachedCaching is not null && _cachedCachingRequest == normalized)
                {
                    return _cachedCaching;
                }

                var wrapped = new CachingCliTokenCredential(Build(normalized), sharedWait: RecordSharedTokenWait);
                _cachedCachingRequest = normalized;
                _cachedCaching = wrapped;
                return wrapped;
            }

            if (_cached is not null && _cachedRequest == normalized)
            {
                return _cached;
            }

            var credential = Build(normalized);
            _cachedRequest = normalized;
            _cached = credential;
            return credential;
        }
    }

    internal static TokenCredential CreateUncached(AzureCredentialRequest request) =>
        Build(Normalize(request));

    // The cleanup log's numbers for a request that waited on another request's token acquisition: timing only, on the
    // admission of the flow that waited (the cache itself never looks at admission).
    private static void RecordSharedTokenWait(TimeSpan wait, bool received) =>
        CleanupAdmission.Current?.Timings?.AddSharedTokenWait(wait, received);

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
            _cachedCachingRequest = default;
            _cachedCaching = null;
            Interlocked.Increment(ref _invalidationVersion);
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
