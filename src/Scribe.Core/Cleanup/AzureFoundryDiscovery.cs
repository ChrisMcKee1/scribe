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
/// Result of a cheap "is the user already signed in?" probe. <see cref="IsSignedIn"/> is true when
/// <see cref="DefaultAzureCredential"/> can mint an ARM token without an interactive prompt (i.e. a
/// valid <c>az login</c> / VS / environment / managed-identity session exists). <see cref="Account"/>
/// is the signed-in identity (UPN/email or app id) when it can be read from the token, else null.
/// </summary>
public sealed record AzureSignInStatus(bool IsSignedIn, string? Account);

/// <summary>
/// Discovers chat-capable model deployments across the Azure subscriptions the signed-in user can
/// see. Authentication uses <see cref="DefaultAzureCredential"/>, so an existing <c>az login</c>
/// (or Visual Studio / VS Code / environment / managed-identity) session is reused with no key.
/// </summary>
public interface IAzureFoundryDiscovery
{
    /// <summary>
    /// Enumerates deployments. Throws on auth failure (e.g. not signed in) so the UI can prompt the
    /// user to run <c>az login</c>; per-account errors are swallowed so one bad resource never hides
    /// the rest. <paramref name="tenantId"/> optionally pins discovery to a specific Entra tenant
    /// instead of the Azure CLI's active tenant (useful when the user juggles corp and demo tenants).
    /// </summary>
    Task<IReadOnlyList<AzureFoundryDeployment>> DiscoverAsync(
        string? tenantId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cheaply checks whether the user is already signed in to Azure (an existing <c>az login</c> or
    /// other <see cref="DefaultAzureCredential"/> source) by requesting an ARM token without any
    /// interactive prompt. Lets the UI show "already signed in" and list deployments automatically
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
        string? tenantId = null, CancellationToken cancellationToken = default)
    {
        var credential = CreateCredential(tenantId);

        var arm = new ArmClient(credential);

        try
        {
            return await DiscoverViaResourceGraphAsync(arm, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !IsAuthFailure(ex))
        {
            // Resource Graph is the fast way to locate accounts, but can be unavailable (e.g. the
            // provider isn't registered, or a sovereign-cloud quirk). Fall back to the slower
            // per-subscription crawl rather than failing outright. Auth failures still bubble up so
            // the UI can prompt for az login.
            _log.LogWarning(ex, "Resource Graph account discovery failed; falling back to per-subscription enumeration.");
            return await DiscoverViaEnumerationAsync(arm, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<AzureSignInStatus> GetSignInStatusAsync(
        string? tenantId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var credential = CreateCredential(tenantId);
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

    // One credential configuration shared by the listing call and the sign-in probe, so "are we
    // signed in?" is answered with the exact same credential chain that discovery will use.
    private static DefaultAzureCredential CreateCredential(string? tenantId)
    {
        // Exclude the interactive browser flow: this is a headless background call and must fail
        // fast to a clear "run az login" message rather than popping a browser. An explicit tenant
        // overrides the Azure CLI's active tenant so the right subscriptions are enumerated.
        var credentialOptions = new DefaultAzureCredentialOptions
        {
            ExcludeInteractiveBrowserCredential = true,
        };

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            credentialOptions.TenantId = tenantId.Trim();
        }

        return new DefaultAzureCredential(credentialOptions);
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
        ArmClient arm, CancellationToken cancellationToken)
    {
        TenantResource? tenant = null;
        await foreach (var t in arm.GetTenants().GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            tenant = t;
            break;
        }

        if (tenant is null)
        {
            return Array.Empty<AzureFoundryDeployment>();
        }

        var subscriptionNames = await QuerySubscriptionNamesAsync(tenant, cancellationToken).ConfigureAwait(false);

        var accounts = new List<AzureAccount>();
        await foreach (var row in QueryRowsAsync(tenant, AccountsQuery, cancellationToken).ConfigureAwait(false))
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
        await foreach (var row in QueryRowsAsync(tenant, SubscriptionsQuery, cancellationToken).ConfigureAwait(false))
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
        ArmClient arm, CancellationToken cancellationToken)
    {
        var results = new List<AzureFoundryDeployment>();

        await foreach (var subscription in arm.GetSubscriptions().GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            if (TryGetCapabilityFlag(capabilities, "responses", out var supportsResponses) && supportsResponses)
            {
                return true;
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

    private static bool IsChatModelByName(string modelName, string deploymentName)
    {
        var haystack = $"{modelName} {deploymentName}".ToLowerInvariant();
        foreach (var marker in NonChatMarkers)
        {
            if (haystack.Contains(marker, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
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
