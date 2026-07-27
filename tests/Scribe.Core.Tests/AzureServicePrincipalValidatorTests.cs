using Scribe.Core.Settings;
using Xunit;

namespace Scribe.Core.Tests;

public class AzureServicePrincipalValidatorTests
{
    private const string Tenant = "6e898202-3a97-48e6-9eb2-71fd5fe7de39";
    private const string Client = "11111111-2222-3333-4444-555555555555";
    private const string Secret = "a-client-secret-value";

    [Fact]
    public void A_complete_service_principal_is_valid()
    {
        Assert.Equal(
            AzureServicePrincipalValidator.Issue.None,
            AzureServicePrincipalValidator.Validate(Tenant, Client, Secret));
        Assert.True(AzureServicePrincipalValidator.IsComplete(Tenant, Client, Secret));
    }

    [Theory]
    [InlineData("contoso.onmicrosoft.com")]
    [InlineData("contoso.com")]
    [InlineData("  contoso.onmicrosoft.com  ")]
    public void A_tenant_may_be_a_verified_domain_rather_than_a_guid(string tenant) =>
        Assert.Equal(
            AzureServicePrincipalValidator.Issue.None,
            AzureServicePrincipalValidator.Validate(tenant, Client, Secret));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_tenant_is_reported(string? tenant) =>
        Assert.Equal(
            AzureServicePrincipalValidator.Issue.TenantIdRequired,
            AzureServicePrincipalValidator.Validate(tenant, Client, Secret));

    [Theory]
    [InlineData("not-a-tenant")]
    [InlineData("https://contoso.onmicrosoft.com")]
    [InlineData("contoso.onmicrosoft.com/extra")]
    [InlineData("user@contoso.onmicrosoft.com")]
    [InlineData("contoso.")]
    [InlineData(".com")]
    public void A_malformed_tenant_is_reported(string tenant) =>
        Assert.Equal(
            AzureServicePrincipalValidator.Issue.TenantIdMalformed,
            AzureServicePrincipalValidator.Validate(tenant, Client, Secret));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_missing_client_id_is_reported(string? clientId) =>
        Assert.Equal(
            AzureServicePrincipalValidator.Issue.ClientIdRequired,
            AzureServicePrincipalValidator.Validate(Tenant, clientId, Secret));

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("contoso.onmicrosoft.com")]
    public void A_client_id_must_be_a_guid(string clientId) =>
        Assert.Equal(
            AzureServicePrincipalValidator.Issue.ClientIdMalformed,
            AzureServicePrincipalValidator.Validate(Tenant, clientId, Secret));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_missing_secret_is_reported(string? secret)
    {
        Assert.Equal(
            AzureServicePrincipalValidator.Issue.ClientSecretRequired,
            AzureServicePrincipalValidator.Validate(Tenant, Client, secret));
        Assert.False(AzureServicePrincipalValidator.IsComplete(Tenant, Client, secret));
    }

    [Fact]
    public void Every_issue_except_none_has_a_message()
    {
        foreach (var issue in Enum.GetValues<AzureServicePrincipalValidator.Issue>())
        {
            var message = AzureServicePrincipalValidator.Describe(issue);
            if (issue == AzureServicePrincipalValidator.Issue.None)
            {
                Assert.Null(message);
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(message));
            }
        }
    }

    [Fact]
    public void Azure_cli_is_the_default_auth_mode() =>
        Assert.Equal(AzureAuthMode.AzureCli, default(AzureAuthMode));
}
