using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.CognitiveServices;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;
using Azure.ResourceManager.Resources;
using Microsoft.Extensions.Logging;

namespace Scribe.Core.Cleanup;

/// <summary>
/// A chat model the user has deployed in Azure (Azure OpenAI or Azure AI Services / Foundry),
/// discovered through Azure Resource Manager. <see cref="Endpoint"/> + <see cref="DeploymentName"/>
/// are what the cleanup service needs to call it; the rest is for display.
/// </summary>
public sealed record AzureFoundryDeployment(
    string SubscriptionName,
    string ResourceGroup,
    string AccountName,
    string Kind,
    string Endpoint,
    string DeploymentName,
    string ModelName,
    string? ModelVersion,
    string Location)
{
    /// <summary>Primary label for a settings dropdown, e.g. "gpt-4o-mini · gpt-4o-mini".</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(ModelName) || string.Equals(ModelName, DeploymentName, StringComparison.OrdinalIgnoreCase)
            ? DeploymentName
            : $"{DeploymentName}  ·  {ModelName}";

    /// <summary>Secondary label: which account / subscription this deployment lives in.</summary>
    public string Detail =>
        string.IsNullOrWhiteSpace(AccountName) ? Endpoint : $"{AccountName} ({Kind}) — {SubscriptionName}";
}

/// <summary>
/// An Azure subscription the signed-in user can see, for the Settings subscription filter.
/// <see cref="Id"/> is the subscription GUID; <see cref="Name"/> is the display name. Tenant and
/// account identify the matching cached Azure CLI sign-in for multi-account discovery.
/// </summary>
public sealed record AzureSubscription(string Id, string Name, string TenantId = "", string AccountName = "")
{
    /// <summary>Label for the settings dropdown; never blank.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

/// <summary>
/// Result of a cheap "is the user already signed in?" probe. <see cref="IsSignedIn"/> is true when
/// <see cref="AzureCliCredential"/> can mint an ARM token from the user's existing <c>az login</c>.
/// <see cref="Account"/>
/// is the signed-in identity (UPN/email or app id) when it can be read from the token, else null.
/// </summary>
public sealed record AzureSignInStatus(bool IsSignedIn, string? Account);

/// <summary>
/// Discovers chat-capable model deployments across the Azure subscriptions the signed-in user can
/// see. Authentication uses <see cref="AzureCliCredential"/>, so only the user's existing
/// <c>az login</c> session is reused with no key.
/// </summary>
public interface IAzureFoundryDiscovery
{
    /// <summary>
    /// Enumerates deployments. Throws on auth failure (e.g. not signed in) so the UI can prompt the
    /// user to run <c>az login</c>; per-account errors are swallowed so one bad resource never hides
    /// the rest. <paramref name="tenantId"/> optionally pins discovery to a specific Entra tenant
    /// instead of the Azure CLI's active tenant (useful when the user juggles corp and demo tenants).
    /// <paramref name="subscriptionId"/> optionally restricts discovery to a single subscription;
    /// null spans every subscription the sign-in can see.
    /// </summary>
    Task<IReadOnlyList<AzureFoundryDeployment>> DiscoverAsync(
        string? tenantId = null, string? subscriptionId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates deployments from an explicit set of subscriptions belonging to one cached Azure
    /// CLI account and tenant. <paramref name="credentialSubscriptionId"/> selects that CLI account
    /// without changing the user's global default subscription.
    /// </summary>
    Task<IReadOnlyList<AzureFoundryDeployment>> DiscoverAcrossSubscriptionsAsync(
        string tenantId,
        IReadOnlyCollection<string> subscriptionIds,
        string credentialSubscriptionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cheaply checks whether the user is already signed in through Azure CLI by requesting an ARM
    /// token from the existing <c>az login</c> session. Lets the UI show "already signed in" and
    /// list deployments automatically
    /// instead of forcing a sign-in click. Never throws — a failed/absent credential returns
    /// <see cref="AzureSignInStatus.IsSignedIn"/> = false.
    /// </summary>
    Task<AzureSignInStatus> GetSignInStatusAsync(
        string? tenantId = null, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AzureFoundryDiscovery : IAzureFoundryDiscovery
{
    // Cognitive Services kinds that expose an OpenAI-compatible chat-completions surface.
    private static readonly HashSet<string> ChatKinds =
        new(StringComparer.OrdinalIgnoreCase) { "OpenAI", "AIServices" };

    // Model-name fragments that are not chat models; cleanup needs a conversational model.
    private static readonly string[] NonChatMarkers =
    {
        "embedding", "whisper", "tts", "dall-e", "dalle", "sora", "image", "moderation", "transcribe",
        "realtime", "audio",
    };

    // Max accounts whose deployments we fetch concurrently. Deployments aren't indexed by Resource
    // Graph, so each account needs its own management call; cap the fan-out for large tenants.
    private const int MaxAccountConcurrency = 8;

    // Resource Graph reliably indexes the *accounts* (across every subscription in one query), but
    // NOT their `.../deployments` child resources — so we find accounts here and list deployments
    // per-account via the management API. This single query replaces a slow per-subscription crawl.
    private const string AccountsQuery = """
        resources
        | where type =~ 'microsoft.cognitiveservices/accounts'
        | where kind in~ ('OpenAI', 'AIServices')
        | extend endpoint = tostring(properties.endpoint)
        | where isnotempty(endpoint)
        | project id, accountName = name, kind, endpoint, location, resourceGroup, subscriptionId
        """;

    private const string SubscriptionsQuery = """
        resourcecontainers
        | where type =~ 'microsoft.resources/subscriptions'
        | project subscriptionId, subscriptionName = name
        """;

    private readonly ILogger<AzureFoundryDiscovery> _log;

    public AzureFoundryDiscovery(ILogger<AzureFoundryDiscovery> log) => _log = log;

    public async Task<IReadOnlyList<AzureFoundryDeployment>> DiscoverAsync(
        string? tenantId = null, string? subscriptionId = null, CancellationToken cancellationToken = default)
    {
        var subscriptionFilter = NormalizeSubscriptionId(subscriptionId);
        var subscriptionFilters = subscriptionFilter is null ? null : new[] { subscriptionFilter };
        return await DiscoverInternalAsync(
            tenantId,
            subscriptionFilters,
            subscriptionFilter,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AzureFoundryDeployment>> DiscoverAcrossSubscriptionsAsync(
        string tenantId,
        IReadOnlyCollection<string> subscriptionIds,
        string credentialSubscriptionId,
        CancellationToken cancellationToken = default)
    {
        var normalizedSubscriptions = subscriptionIds
            .Select(NormalizeSubscriptionId)
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedSubscriptions.Count == 0)
        {
            return Array.Empty<AzureFoundryDeployment>();
        }

        var normalizedCredentialSubscription = NormalizeSubscriptionId(credentialSubscriptionId)
            ?? normalizedSubscriptions[0];
        return await DiscoverInternalAsync(
            tenantId,
            normalizedSubscriptions,
            normalizedCredentialSubscription,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<AzureFoundryDeployment>> DiscoverInternalAsync(
        string? tenantId,
        IReadOnlyCollection<string>? subscriptionFilters,
        string? credentialSubscriptionId,
        CancellationToken cancellationToken)
    {
        var credential = AzureCliCredentialFactory.Create(tenantId, credentialSubscriptionId);

        var arm = new ArmClient(credential);

        try
        {
            return await DiscoverViaResourceGraphAsync(arm, tenantId, subscriptionFilters, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !IsAuthFailure(ex))
        {
            // Resource Graph is the fast way to locate accounts, but can be unavailable (e.g. the
            // provider isn't registered, or a sovereign-cloud quirk). Fall back to the slower
            // per-subscription crawl rather than failing outright. Auth failures still bubble up so
            // the UI can prompt for az login.
            _log.LogWarning(ex, "Resource Graph account discovery failed; falling back to per-subscription enumeration.");
            return await DiscoverViaEnumerationAsync(arm, subscriptionFilters, cancellationToken).ConfigureAwait(false);
        }
    }

    // Subscription ids are GUIDs; anything else (stale settings, hand-edited json) is ignored so a
    // bad filter degrades to "all subscriptions" instead of an empty or malformed ARG scope.
    internal static string? NormalizeSubscriptionId(string? subscriptionId) =>
        Guid.TryParse(subscriptionId?.Trim(), out var parsed) ? parsed.ToString("D") : null;

    public async Task<AzureSignInStatus> GetSignInStatusAsync(
        string? tenantId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var credential = AzureCliCredentialFactory.Create(tenantId);
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(ArmScopes), cancellationToken).ConfigureAwait(false);
            return new AzureSignInStatus(true, ReadAccountFromToken(token.Token));
        }
        catch (Exception ex) when (ex is AuthenticationFailedException or CredentialUnavailableException)
        {
            // Expected when the user simply isn't signed in — not an error worth surfacing as a stack.
            _log.LogDebug(ex, "Azure sign-in probe: no non-interactive credential available.");
            return new AzureSignInStatus(false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Azure sign-in probe failed.");
            return new AzureSignInStatus(false, null);
        }
    }

    // ARM scope used for both discovery and the sign-in probe.
    private static readonly string[] ArmScopes = { "https://management.azure.com/.default" };

    // Best-effort identity for the "signed in as …" hint: pull a human-readable claim from the ARM
    // access token (a JWT). Falls back to null for non-user identities (managed identity / SP) or any
    // decode failure — the caller still shows a generic "signed in" state.
    private static string? ReadAccountFromToken(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = (payload.Length % 4) switch
            {
                2 => payload + "==",
                3 => payload + "=",
                _ => payload,
            };

            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = doc.RootElement;
            foreach (var claim in new[] { "upn", "preferred_username", "unique_name", "email", "appid" })
            {
                if (root.TryGetProperty(claim, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString();
                }
            }
        }
        catch
        {
            // Token shape varies; the account label is cosmetic, so never fail the probe over it.
        }

        return null;
    }

    private static bool IsAuthFailure(Exception ex) =>
        ex is AuthenticationFailedException or CredentialUnavailableException;

    // ---- Fast path: ARG finds accounts; management API lists their deployments -----------------

    private async Task<IReadOnlyList<AzureFoundryDeployment>> DiscoverViaResourceGraphAsync(
        ArmClient arm,
        string? tenantId,
        IReadOnlyCollection<string>? subscriptionFilters,
        CancellationToken cancellationToken)
    {
        var tenant = await FindTenantAsync(arm, tenantId, cancellationToken).ConfigureAwait(false);

        if (tenant is null)
        {
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                throw new InvalidOperationException($"Azure tenant '{tenantId}' was not returned by ARM.");
            }

            return Array.Empty<AzureFoundryDeployment>();
        }

        var subscriptionNames = await QuerySubscriptionNamesAsync(tenant, cancellationToken).ConfigureAwait(false);

        var accounts = new List<AzureAccount>();
        await foreach (var row in QueryRowsAsync(tenant, AccountsQuery, subscriptionFilters, cancellationToken)
            .ConfigureAwait(false))
        {
            var id = GetString(row, "id");
            var endpoint = GetString(row, "endpoint");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(endpoint))
            {
                continue;
            }

            var subscriptionId = GetString(row, "subscriptionId");
            var subscriptionName = subscriptionNames.TryGetValue(subscriptionId, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : subscriptionId;

            accounts.Add(new AzureAccount(
                id,
                GetString(row, "accountName"),
                GetString(row, "kind"),
                endpoint,
                GetString(row, "resourceGroup"),
                subscriptionName,
                GetString(row, "location")));
        }

        // Deployments aren't in Resource Graph, so list them per-account in parallel (bounded).
        using var gate = new SemaphoreSlim(MaxAccountConcurrency);
        var tasks = accounts.Select(async account =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ListAccountDeploymentsAsync(arm, account, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        var perAccount = await Task.WhenAll(tasks).ConfigureAwait(false);
        return Sort(perAccount.SelectMany(list => list).ToList());
    }

    private static async Task<TenantResource?> FindTenantAsync(
        ArmClient arm, string? tenantId, CancellationToken cancellationToken)
    {
        await foreach (var tenant in arm.GetTenants().GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(tenantId) ||
                string.Equals(
                    tenant.Data?.TenantId?.ToString("D"),
                    tenantId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return tenant;
            }
        }

        return null;
    }

    private async Task<List<AzureFoundryDeployment>> ListAccountDeploymentsAsync(
        ArmClient arm, AzureAccount account, CancellationToken cancellationToken)
    {
        var results = new List<AzureFoundryDeployment>();
        try
        {
            var accountResource = arm.GetCognitiveServicesAccountResource(new ResourceIdentifier(account.Id));
            await foreach (var deployment in accountResource.GetCognitiveServicesAccountDeployments()
                .GetAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var model = deployment.Data?.Properties?.Model;
                var modelName = model?.Name ?? string.Empty;
                var deploymentName = deployment.Data?.Name ?? deployment.Id.Name;

                if (!SupportsTextCleanup(deployment.Data?.Properties?.Capabilities, modelName, deploymentName))
                {
                    _log.LogDebug(
                        "Skipping non-text deployment {Deployment} ({Model}); capabilities: {Capabilities}.",
                        deploymentName,
                        modelName,
                        FormatCapabilities(deployment.Data?.Properties?.Capabilities));
                    continue;
                }

                results.Add(new AzureFoundryDeployment(
                    account.SubscriptionName,
                    account.ResourceGroup,
                    account.AccountName,
                    account.Kind,
                    account.Endpoint,
                    deploymentName,
                    modelName,
                    model?.Version,
                    account.Location));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Could not list deployments for account {Account}.", account.AccountName);
        }

        return results;
    }

    private async Task<Dictionary<string, string>> QuerySubscriptionNamesAsync(
        TenantResource tenant, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var row in QueryRowsAsync(tenant, SubscriptionsQuery, subscriptionFilters: null, cancellationToken)
            .ConfigureAwait(false))
        {
            var id = GetString(row, "subscriptionId");
            if (!string.IsNullOrWhiteSpace(id))
            {
                map[id] = GetString(row, "subscriptionName");
            }
        }

        return map;
    }

    private static async IAsyncEnumerable<JsonElement> QueryRowsAsync(
        TenantResource tenant,
        string query,
        IReadOnlyCollection<string>? subscriptionFilters,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? skipToken = null;
        do
        {
            var content = new ResourceQueryContent(query)
            {
                Options = new ResourceQueryRequestOptions
                {
                    ResultFormat = ResultFormat.ObjectArray,
                    SkipToken = skipToken,
                },
            };

            // Scoping via the request (not string-built KQL) keeps the query injection-proof and
            // lets Resource Graph prune the search server-side.
            if (subscriptionFilters is not null)
            {
                foreach (var subscriptionId in subscriptionFilters)
                {
                    content.Subscriptions.Add(subscriptionId);
                }
            }

            var response = await tenant.GetResourcesAsync(content, cancellationToken).ConfigureAwait(false);
            var result = response.Value;

            using var doc = JsonDocument.Parse(result.Data.ToMemory());
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    // Clone so the value outlives the JsonDocument we dispose each page.
                    yield return element.Clone();
                }
            }

            skipToken = result.SkipToken;
        }
        while (!string.IsNullOrEmpty(skipToken));
    }

    private static string GetString(JsonElement row, string property) =>
        row.ValueKind == JsonValueKind.Object
        && row.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    // ---- Fallback: per-subscription ARM enumeration -------------------------------------------

    private async Task<IReadOnlyList<AzureFoundryDeployment>> DiscoverViaEnumerationAsync(
        ArmClient arm, IReadOnlyCollection<string>? subscriptionFilters, CancellationToken cancellationToken)
    {
        var results = new List<AzureFoundryDeployment>();
        var subscriptionSet = subscriptionFilters is null
            ? null
            : new HashSet<string>(subscriptionFilters, StringComparer.OrdinalIgnoreCase);

        await foreach (var subscription in arm.GetSubscriptions().GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (subscriptionSet is not null &&
                !subscriptionSet.Contains(subscription.Data?.SubscriptionId ?? string.Empty))
            {
                continue;
            }

            var subName = subscription.Data?.DisplayName ?? subscription.Data?.SubscriptionId ?? "subscription";

            try
            {
                await foreach (var account in subscription.GetCognitiveServicesAccountsAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var accountData = account.Data;
                    var kind = accountData?.Kind;
                    if (string.IsNullOrWhiteSpace(kind) || !ChatKinds.Contains(kind))
                    {
                        continue;
                    }

                    var endpoint = accountData?.Properties?.Endpoint;
                    if (string.IsNullOrWhiteSpace(endpoint))
                    {
                        continue;
                    }

                    var accountName = accountData?.Name ?? account.Id.Name;
                    var resourceGroup = account.Id.ResourceGroupName ?? string.Empty;
                    var location = accountData?.Location.ToString() ?? string.Empty;

                    try
                    {
                        await foreach (var deployment in account.GetCognitiveServicesAccountDeployments()
                            .GetAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var model = deployment.Data?.Properties?.Model;
                            var modelName = model?.Name ?? string.Empty;
                            var deploymentName = deployment.Data?.Name ?? deployment.Id.Name;

                            if (!SupportsTextCleanup(deployment.Data?.Properties?.Capabilities, modelName, deploymentName))
                            {
                                _log.LogDebug(
                                    "Skipping non-text deployment {Deployment} ({Model}); capabilities: {Capabilities}.",
                                    deploymentName,
                                    modelName,
                                    FormatCapabilities(deployment.Data?.Properties?.Capabilities));
                                continue;
                            }

                            results.Add(new AzureFoundryDeployment(
                                subName,
                                resourceGroup,
                                accountName,
                                kind!,
                                endpoint!,
                                deploymentName,
                                modelName,
                                model?.Version,
                                location));
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogDebug(ex, "Could not list deployments for account {Account}.", accountName);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "Could not list Cognitive Services accounts in subscription {Subscription}.", subName);
            }
        }

        return Sort(results);
    }

    private static IReadOnlyList<AzureFoundryDeployment> Sort(List<AzureFoundryDeployment> results) =>
        results
            .OrderBy(d => d.AccountName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.DeploymentName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // True when a deployment can serve the Responses-API text-cleanup path. Azure reports a per-
    // deployment capability map we treat as authoritative when present:
    //   * realtime==true  -> EXCLUDE. Realtime/audio streaming models (gpt-realtime, gpt-realtime-1.5)
    //     reject the Responses text call with HTTP 400 even though they may advertise other flags.
    //   * responses==true -> INCLUDE. The decisive flag for our path. Newer reasoning/agent models
    //     (gpt-5.4-pro, gpt-5.3-codex) report chatCompletion==false but responses==true and DO work.
    //   * chatCompletion  -> use its value. Older chat models (gpt-4o/4.1, gpt-5.x, model-router)
    //     report chatCompletion==true without a responses key; embedding/image/document models report
    //     false. ("chatCompletion" alone is NOT sufficient — it misses the responses-only models above,
    //     which is why we check "responses" first.)
    // Only when the map surfaces NONE of these decisive keys do we fall back to the model-name
    // heuristic (older API versions / some non-OpenAI AIServices deployments).
    internal static bool SupportsTextCleanup(
        IReadOnlyDictionary<string, string>? capabilities, string modelName, string deploymentName)
    {
        if (capabilities is { Count: > 0 })
        {
            if (TryGetCapabilityFlag(capabilities, "realtime", out var realtime) && realtime)
            {
                return false;
            }

            if (IsKnownNonTextModel(modelName))
            {
                return false;
            }

            if (TryGetCapabilityFlag(capabilities, "responses", out var supportsResponses))
            {
                return supportsResponses;
            }

            if (TryGetCapabilityFlag(capabilities, "chatCompletion", out var supportsChat))
            {
                return supportsChat;
            }
        }

        return IsChatModelByName(modelName, deploymentName);
    }

    private static bool TryGetCapabilityFlag(
        IReadOnlyDictionary<string, string> capabilities, string key, out bool value)
    {
        foreach (var pair in capabilities)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = string.Equals(pair.Value, "true", StringComparison.OrdinalIgnoreCase);
                return true;
            }
        }

        value = false;
        return false;
    }

    private static string FormatCapabilities(IReadOnlyDictionary<string, string>? capabilities) =>
        capabilities is { Count: > 0 }
            ? string.Join(", ", capabilities.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"))
            : "(none)";

    private static bool IsChatModelByName(string modelName, string deploymentName)
    {
        var modelIdentifier = string.IsNullOrWhiteSpace(modelName) ? deploymentName : modelName;
        return !IsKnownNonTextModel(modelIdentifier);
    }

    private static bool IsKnownNonTextModel(string modelName)
    {
        var haystack = modelName.ToLowerInvariant();
        foreach (var marker in NonChatMarkers)
        {
            if (haystack.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record AzureAccount(
        string Id,
        string AccountName,
        string Kind,
        string Endpoint,
        string ResourceGroup,
        string SubscriptionName,
        string Location);
}
