using System.Text.Json;

namespace Scribe.Core.Cleanup;

/// <summary>Parses the cross-tenant subscription inventory returned by <c>az account list --all</c>.</summary>
public static class AzureCliAccountParser
{
    public static IReadOnlyList<AzureSubscription> ParseSubscriptions(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<AzureSubscription>();
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AzureSubscription>();
        }

        var subscriptions = new Dictionary<string, AzureSubscription>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in document.RootElement.EnumerateArray())
        {
            var id = GetString(account, "id");
            var tenantId = GetString(account, "tenantId");
            var state = GetString(account, "state");
            if (!Guid.TryParse(id, out var parsedId) ||
                !Guid.TryParse(tenantId, out var parsedTenantId) ||
                (!string.IsNullOrWhiteSpace(state) &&
                 !string.Equals(state, "Enabled", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var normalizedId = parsedId.ToString("D");
            var name = GetString(account, "name");
            var accountName = account.TryGetProperty("user", out var user)
                ? GetString(user, "name")
                : string.Empty;
            var isDefault =
                account.TryGetProperty("isDefault", out var defaultValue) &&
                defaultValue.ValueKind is JsonValueKind.True;
            subscriptions[normalizedId] = new AzureSubscription(
                normalizedId,
                string.IsNullOrWhiteSpace(name) ? normalizedId : name,
                parsedTenantId.ToString("D"),
                accountName,
                isDefault);
        }

        return subscriptions.Values
            .OrderBy(subscription => subscription.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(subscription => subscription.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}