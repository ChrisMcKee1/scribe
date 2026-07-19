using Scribe.Core.Cleanup;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Covers the subscription filter that scopes Azure model discovery. Discovery historically merged
/// deployments from every subscription the sign-in could see, which surfaced models from shared or
/// foreign projects; the filter narrows both the Resource Graph scope and the enumeration fallback
/// to one subscription id.
/// </summary>
public sealed class AzureSubscriptionTests
{
    [Theory]
    [InlineData("d3adbeef-0000-4000-8000-000000000001", "d3adbeef-0000-4000-8000-000000000001")]
    [InlineData("  D3ADBEEF-0000-4000-8000-000000000001  ", "d3adbeef-0000-4000-8000-000000000001")]
    public void Valid_subscription_ids_are_normalized(string input, string expected)
    {
        Assert.Equal(expected, AzureFoundryDiscovery.NormalizeSubscriptionId(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("prod' | where 1==1")]
    public void Invalid_subscription_ids_degrade_to_no_filter(string? input)
    {
        // A corrupt or hand-edited saved id must fall back to "all subscriptions" rather than
        // producing a malformed ARM scope (or worse, being spliced into a query).
        Assert.Null(AzureFoundryDiscovery.NormalizeSubscriptionId(input));
    }

    [Fact]
    public void Display_name_prefers_the_subscription_name()
    {
        var subscription = new AzureSubscription("d3adbeef-0000-4000-8000-000000000001", "Contoso Prod");

        Assert.Equal("Contoso Prod", subscription.DisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Display_name_falls_back_to_the_id_when_the_name_is_blank(string name)
    {
        var subscription = new AzureSubscription("d3adbeef-0000-4000-8000-000000000001", name);

        Assert.Equal("d3adbeef-0000-4000-8000-000000000001", subscription.DisplayName);
    }

    [Fact]
    public void Cli_inventory_includes_enabled_subscriptions_across_tenants()
    {
        const string json = """
            [
              { "id": "d3adbeef-0000-4000-8000-000000000001", "name": "Zulu", "tenantId": "aaaaaaaa-0000-4000-8000-000000000001", "state": "Enabled", "user": { "name": "first@example.test" } },
              { "id": "d3adbeef-0000-4000-8000-000000000002", "name": "Alpha", "tenantId": "bbbbbbbb-0000-4000-8000-000000000002", "state": "Enabled", "user": { "name": "second@example.test" } },
              { "id": "d3adbeef-0000-4000-8000-000000000003", "name": "Disabled", "tenantId": "bbbbbbbb-0000-4000-8000-000000000002", "state": "Disabled" }
            ]
            """;

        var subscriptions = AzureCliAccountParser.ParseSubscriptions(json);

        Assert.Collection(
            subscriptions,
            subscription =>
            {
                Assert.Equal("Alpha", subscription.Name);
                Assert.Equal("bbbbbbbb-0000-4000-8000-000000000002", subscription.TenantId);
                Assert.Equal("second@example.test", subscription.AccountName);
            },
            subscription =>
            {
                Assert.Equal("Zulu", subscription.Name);
                Assert.Equal("aaaaaaaa-0000-4000-8000-000000000001", subscription.TenantId);
            });
    }

    [Fact]
    public void Cli_inventory_deduplicates_ids_and_ignores_malformed_rows()
    {
        const string json = """
            [
              { "id": "d3adbeef-0000-4000-8000-000000000001", "name": "Old", "tenantId": "aaaaaaaa-0000-4000-8000-000000000001", "state": "Enabled" },
              { "id": "D3ADBEEF-0000-4000-8000-000000000001", "name": "Current", "tenantId": "aaaaaaaa-0000-4000-8000-000000000001", "state": "Enabled" },
              { "id": "not-a-guid", "name": "Broken", "tenantId": "aaaaaaaa-0000-4000-8000-000000000001", "state": "Enabled" },
              { "id": "d3adbeef-0000-4000-8000-000000000002", "name": "No tenant", "state": "Enabled" }
            ]
            """;

        var subscription = Assert.Single(AzureCliAccountParser.ParseSubscriptions(json));

        Assert.Equal("Current", subscription.Name);
    }
}
