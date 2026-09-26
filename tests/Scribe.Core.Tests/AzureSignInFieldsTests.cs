using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class AzureSignInFieldsTests
{
    [Fact]
    public void An_api_key_stores_only_its_key()
    {
        var stored = AzureSignInFields.For(AzureAuthMode.AzureCli, apiKeySelected: true, "cli-tenant", "sp-tenant", "client", "secret", " key ");
        Assert.Equal(new AzureSignInFields.Stored(null, null, null, "key"), stored);
    }

    [Fact]
    public void Azure_cli_stores_only_its_tenant()
    {
        var stored = AzureSignInFields.For(AzureAuthMode.AzureCli, apiKeySelected: false, " cli-tenant ", "sp-tenant", "client", "secret", "key");
        Assert.Equal(new AzureSignInFields.Stored("cli-tenant", null, null, null), stored);
    }

    [Fact]
    public void A_service_principal_stores_its_own_tenant_and_app_registration()
    {
        var stored = AzureSignInFields.For(AzureAuthMode.ServicePrincipal, apiKeySelected: false, "cli-tenant", "sp-tenant", "client", "secret", "key");
        Assert.Equal(new AzureSignInFields.Stored("sp-tenant", "client", "secret", null), stored);
    }

    [Fact]
    public void Blank_fields_store_nothing()
    {
        var stored = AzureSignInFields.For(AzureAuthMode.ServicePrincipal, apiKeySelected: false, null, "  ", string.Empty, " ", null);
        Assert.Equal(new AzureSignInFields.Stored(null, null, null, null), stored);
    }
}
