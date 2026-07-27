using Scribe.Core.Settings;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

public sealed class AzureSettingsAccessTests
{
    [Fact]
    public void Signed_out_state_hides_discovery_and_configuration()
    {
        var state = AzureSettingsAccess.Resolve(
            cliInstalled: true,
            signedIn: false,
            manualConfigurationRequested: false,
            hasApiKey: false);

        Assert.False(state.ShowCliSetup);
        Assert.False(state.ShowDiscovery);
        Assert.False(state.ShowConfiguration);
        Assert.True(state.ShowManualConfigurationAction);
        Assert.True(state.CanStartSignIn);
        Assert.False(state.HasUsableAuthentication);
    }

    [Fact]
    public void Missing_cli_keeps_manual_configuration_available()
    {
        var state = AzureSettingsAccess.Resolve(
            cliInstalled: false,
            signedIn: false,
            manualConfigurationRequested: true,
            hasApiKey: false);

        Assert.True(state.ShowCliSetup);
        Assert.False(state.ShowDiscovery);
        Assert.True(state.ShowConfiguration);
        Assert.False(state.ShowManualConfigurationAction);
        Assert.False(state.CanStartSignIn);
    }

    [Fact]
    public void Verified_sign_in_reveals_discovery_and_configuration()
    {
        var state = AzureSettingsAccess.Resolve(
            cliInstalled: true,
            signedIn: true,
            manualConfigurationRequested: false,
            hasApiKey: false);

        Assert.True(state.ShowDiscovery);
        Assert.True(state.ShowConfiguration);
        Assert.False(state.ShowManualConfigurationAction);
        Assert.True(state.HasUsableAuthentication);
    }

    [Fact]
    public void Saved_api_key_reveals_only_manual_configuration()
    {
        var state = AzureSettingsAccess.Resolve(
            cliInstalled: false,
            signedIn: false,
            manualConfigurationRequested: false,
            hasApiKey: true);

        Assert.False(state.ShowDiscovery);
        Assert.True(state.ShowConfiguration);
        Assert.True(state.HasUsableAuthentication);
    }

    [Theory]
    [InlineData(false, true, false, null, null, null, AzureSettingsAccess.ValidationIssue.None)]
    [InlineData(true, false, false, null, null, null, AzureSettingsAccess.ValidationIssue.None)]
    [InlineData(true, true, false, null, null, null, AzureSettingsAccess.ValidationIssue.AuthenticationRequired)]
    [InlineData(true, true, false, "key", null, "deployment", AzureSettingsAccess.ValidationIssue.EndpointRequired)]
    [InlineData(true, true, true, null, "https://example.test", null, AzureSettingsAccess.ValidationIssue.DeploymentRequired)]
    [InlineData(true, true, true, null, "https://example.test", "deployment", AzureSettingsAccess.ValidationIssue.None)]
    public void Cleanup_validation_requires_real_auth_and_target(
        bool enabled,
        bool usesAzureProvider,
        bool signedIn,
        string? apiKey,
        string? endpoint,
        string? deployment,
        AzureSettingsAccess.ValidationIssue expected)
    {
        var issue = AzureSettingsAccess.ValidateCleanup(
            enabled,
            usesAzureProvider,
            signedIn,
            apiKey,
            endpoint,
            deployment);

        Assert.Equal(expected, issue);
    }

    [Fact]
    public void Subscription_selection_ignores_a_saved_subscription_from_another_verified_tenant()
    {
        var subscriptions = new[]
        {
            new AzureSubscription(
                "d3adbeef-0000-4000-8000-000000000001",
                "Other tenant",
                "aaaaaaaa-0000-4000-8000-000000000001",
                IsDefault: true),
            new AzureSubscription(
                "d3adbeef-0000-4000-8000-000000000002",
                "Verified tenant",
                "bbbbbbbb-0000-4000-8000-000000000002",
                IsDefault: true),
        };

        var selected = AzureSubscriptionSelection.ChooseInitialSubscriptionId(
            subscriptions,
            currentSubscriptionId: subscriptions[0].Id,
            verifiedTenantId: subscriptions[1].TenantId);

        Assert.Equal(subscriptions[1].Id, selected);
    }

    [Fact]
    public void Selected_deployment_identity_wins_when_browsing_all_subscriptions()
    {
        var deployment = new AzureFoundryDeployment(
            SubscriptionId: "d3adbeef-0000-4000-8000-000000000002",
            SubscriptionName: "Verified tenant",
            TenantId: "bbbbbbbb-0000-4000-8000-000000000002",
            ResourceGroup: "rg",
            AccountName: "foundry",
            Kind: "AIServices",
            Endpoint: "https://example.test/",
            DeploymentName: "cleanup",
            ModelName: "gpt-test",
            ModelVersion: null,
            Location: "eastus");

        var selected = AzureSubscriptionSelection.ResolveAuthenticationSubscription(
            deployment,
            selectedFilter: null,
            endpoint: deployment.Endpoint,
            deploymentName: deployment.DeploymentName);

        Assert.NotNull(selected);
        Assert.Equal(deployment.SubscriptionId, selected.Id);
        Assert.Equal(deployment.TenantId, selected.TenantId);
    }

    [Fact]
    public void Manually_edited_target_does_not_reuse_stale_deployment_identity()
    {
        var deployment = new AzureFoundryDeployment(
            SubscriptionId: "d3adbeef-0000-4000-8000-000000000002",
            SubscriptionName: "Verified tenant",
            TenantId: "bbbbbbbb-0000-4000-8000-000000000002",
            ResourceGroup: "rg",
            AccountName: "foundry",
            Kind: "AIServices",
            Endpoint: "https://example.test/",
            DeploymentName: "cleanup",
            ModelName: "gpt-test",
            ModelVersion: null,
            Location: "eastus");

        var selected = AzureSubscriptionSelection.ResolveAuthenticationSubscription(
            deployment,
            selectedFilter: null,
            endpoint: "https://manual.example.test/",
            deploymentName: deployment.DeploymentName);

        Assert.Null(selected);
    }

    [Fact]
    public void Selected_subscription_tenant_overrides_the_manual_tenant()
    {
        var tenant = AzureSubscriptionSelection.ResolveTenantId(
            subscriptionId: "d3adbeef-0000-4000-8000-000000000002",
            subscriptionTenantId: "bbbbbbbb-0000-4000-8000-000000000002",
            manualTenantId: "aaaaaaaa-0000-4000-8000-000000000001");

        Assert.Equal("bbbbbbbb-0000-4000-8000-000000000002", tenant);
    }

    private const string Tenant = "6e898202-3a97-48e6-9eb2-71fd5fe7de39";
    private const string Client = "11111111-2222-3333-4444-555555555555";
    private const string Secret = "a-client-secret-value";

    [Fact]
    public void Service_principal_mode_hides_the_cli_setup_and_sign_in_affordances()
    {
        var state = AzureSettingsAccess.Resolve(
            cliInstalled: false,
            signedIn: false,
            manualConfigurationRequested: false,
            hasApiKey: false,
            authMode: AzureAuthMode.ServicePrincipal);

        // The CLI is irrelevant to an app registration, so prompting to install it would be a lie.
        Assert.False(state.ShowCliSetup);
        Assert.False(state.CanStartSignIn);
        Assert.True(state.ShowServicePrincipalFields);
        Assert.False(state.ShowDiscovery);
        Assert.False(state.HasUsableAuthentication);
    }

    [Fact]
    public void A_complete_service_principal_enables_verification()
    {
        var incomplete = AzureSettingsAccess.Resolve(
            cliInstalled: true,
            signedIn: false,
            manualConfigurationRequested: false,
            hasApiKey: false,
            authMode: AzureAuthMode.ServicePrincipal,
            servicePrincipalComplete: false);
        Assert.False(incomplete.CanStartSignIn);

        var complete = AzureSettingsAccess.Resolve(
            cliInstalled: true,
            signedIn: false,
            manualConfigurationRequested: false,
            hasApiKey: false,
            authMode: AzureAuthMode.ServicePrincipal,
            servicePrincipalComplete: true);
        Assert.True(complete.CanStartSignIn);
    }

    [Fact]
    public void A_verified_service_principal_reveals_configuration_but_not_discovery()
    {
        var state = AzureSettingsAccess.Resolve(
            cliInstalled: false,
            signedIn: true,
            manualConfigurationRequested: false,
            hasApiKey: false,
            authMode: AzureAuthMode.ServicePrincipal,
            servicePrincipalComplete: true);

        // Discovery would need Reader across the subscription; inference only needs a role on the
        // one resource. This mode deliberately asks for the smaller grant.
        Assert.False(state.ShowDiscovery);
        Assert.True(state.ShowConfiguration);
        Assert.True(state.HasUsableAuthentication);
        Assert.True(state.ShowServicePrincipalFields);
    }

    [Fact]
    public void Azure_cli_mode_never_shows_the_service_principal_fields()
    {
        var state = AzureSettingsAccess.Resolve(
            cliInstalled: true,
            signedIn: true,
            manualConfigurationRequested: false,
            hasApiKey: false);

        Assert.False(state.ShowServicePrincipalFields);
    }

    [Fact]
    public void An_incomplete_service_principal_blocks_saving()
    {
        var issue = AzureSettingsAccess.ValidateCleanup(
            enabled: true,
            usesAzureProvider: true,
            signedIn: false,
            apiKey: null,
            endpoint: "https://example.test",
            deployment: "cleanup",
            authMode: AzureAuthMode.ServicePrincipal,
            tenantId: Tenant,
            clientId: Client,
            clientSecret: null);

        Assert.Equal(AzureSettingsAccess.ValidationIssue.ServicePrincipalIncomplete, issue);
    }

    [Fact]
    public void A_complete_service_principal_still_requires_an_endpoint_and_deployment()
    {
        Assert.Equal(
            AzureSettingsAccess.ValidationIssue.EndpointRequired,
            AzureSettingsAccess.ValidateCleanup(
                enabled: true,
                usesAzureProvider: true,
                signedIn: true,
                apiKey: null,
                endpoint: null,
                deployment: "cleanup",
                authMode: AzureAuthMode.ServicePrincipal,
                tenantId: Tenant,
                clientId: Client,
                clientSecret: Secret));

        Assert.Equal(
            AzureSettingsAccess.ValidationIssue.None,
            AzureSettingsAccess.ValidateCleanup(
                enabled: true,
                usesAzureProvider: true,
                signedIn: true,
                apiKey: null,
                endpoint: "https://example.test",
                deployment: "cleanup",
                authMode: AzureAuthMode.ServicePrincipal,
                tenantId: Tenant,
                clientId: Client,
                clientSecret: Secret));
    }

    [Fact]
    public void An_api_key_bypasses_the_service_principal_requirement()
    {
        // The key path never touches Entra, so half-entered app registration details are not a
        // blocker; failing here would strand a user who deliberately chose key auth.
        var issue = AzureSettingsAccess.ValidateCleanup(
            enabled: true,
            usesAzureProvider: true,
            signedIn: false,
            apiKey: "a-key",
            endpoint: "https://example.test",
            deployment: "cleanup",
            authMode: AzureAuthMode.ServicePrincipal,
            tenantId: null,
            clientId: null,
            clientSecret: null);

        Assert.Equal(AzureSettingsAccess.ValidationIssue.None, issue);
    }

    [Fact]
    public void A_service_principal_is_only_built_from_complete_details_in_the_matching_mode()
    {
        Assert.NotNull(AzureServicePrincipal.TryCreate(
            AzureAuthMode.ServicePrincipal, Tenant, Client, Secret));

        // Complete details but CLI mode: the user chose the CLI, so the app registration must not
        // silently take over.
        Assert.Null(AzureServicePrincipal.TryCreate(
            AzureAuthMode.AzureCli, Tenant, Client, Secret));

        Assert.Null(AzureServicePrincipal.TryCreate(
            AzureAuthMode.ServicePrincipal, Tenant, Client, null));
    }

    [Fact]
    public void A_created_service_principal_trims_its_identifiers_but_never_its_secret()
    {
        var principal = AzureServicePrincipal.TryCreate(
            AzureAuthMode.ServicePrincipal, $"  {Tenant} ", $" {Client}  ", "  spaced-secret  ");

        Assert.NotNull(principal);
        Assert.Equal(Tenant, principal.TenantId);
        Assert.Equal(Client, principal.ClientId);
        Assert.Equal("  spaced-secret  ", principal.ClientSecret);
    }
}
