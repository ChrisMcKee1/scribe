using Scribe.Core.Cleanup;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// A discovered deployment must offer the Foundry <b>project</b> endpoint, which is the shape
/// Microsoft documents for a Foundry project and the one the portal shows. Cleanup takes only its host
/// and sends inference to the account's <c>/openai/v1/</c> endpoint
/// (<c>AzureOpenAIResponsesClientFactory.GetV1Endpoint</c>), so the saved shape decides nothing about
/// routing. The project data plane is Entra-only, so key auth has to keep getting the account endpoint.
/// </summary>
public class AzureFoundryDeploymentEndpointTests
{
    private static AzureFoundryDeployment Deployment(string? projectEndpoint) =>
        new(
            SubscriptionId: "sub",
            SubscriptionName: "Sub",
            TenantId: "tenant",
            ResourceGroup: "rg",
            AccountName: "acct",
            Kind: "AIServices",
            Endpoint: "https://acct.cognitiveservices.azure.com/",
            DeploymentName: "gpt-5.6-terra",
            ModelName: "gpt-5.6-terra",
            ModelVersion: null,
            Location: "southcentralus",
            ProjectEndpoint: projectEndpoint,
            ProjectName: projectEndpoint is null ? null : "proj");

    [Fact]
    public void Project_endpoint_is_preferred_when_present()
    {
        var d = Deployment("https://acct.services.ai.azure.com/api/projects/proj");

        Assert.Equal("https://acct.services.ai.azure.com/api/projects/proj", d.PreferredEndpoint);
        Assert.Equal("https://acct.services.ai.azure.com/api/projects/proj", d.EndpointFor(usingApiKey: false));
    }

    [Fact]
    public void Api_key_auth_falls_back_to_the_account_endpoint()
    {
        // The Foundry project data plane rejects keys, so offering the project URL alongside a key
        // would configure a combination that cannot authenticate.
        var d = Deployment("https://acct.services.ai.azure.com/api/projects/proj");

        Assert.Equal("https://acct.cognitiveservices.azure.com/", d.EndpointFor(usingApiKey: true));
    }

    [Fact]
    public void An_account_without_projects_keeps_the_account_endpoint()
    {
        var d = Deployment(null);

        Assert.Equal("https://acct.cognitiveservices.azure.com/", d.PreferredEndpoint);
        Assert.Equal("https://acct.cognitiveservices.azure.com/", d.EndpointFor(usingApiKey: false));
        Assert.Equal("https://acct.cognitiveservices.azure.com/", d.EndpointFor(usingApiKey: true));
    }

    [Fact]
    public void The_project_endpoint_keeps_the_project_path_shape()
    {
        // Settings fills in and saves this URL. Cleanup normalizes either shape to account inference,
        // so this pins what is shown and saved, not how a request is routed.
        var d = Deployment("https://acct.services.ai.azure.com/api/projects/proj");

        Assert.Contains("/api/projects/", d.PreferredEndpoint, StringComparison.OrdinalIgnoreCase);
    }
}
