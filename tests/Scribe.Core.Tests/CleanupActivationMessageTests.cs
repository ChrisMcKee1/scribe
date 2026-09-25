using Scribe.Core.Cleanup;
using Xunit;

namespace Scribe.Core.Tests;

public class CleanupActivationMessageTests
{
    private static CleanupOptions Local(string alias) =>
        new(true, CleanupProvider.FoundryLocal, alias, null, null);

    private static CleanupOptions Azure(string? endpoint, string? deployment) =>
        new(true, CleanupProvider.AzureFoundry, "unused", endpoint, deployment);

    private static CleanupOptions Custom(string? endpoint, string? model) =>
        new(true, CleanupProvider.OpenAiCompatible, "unused", null, null, CustomEndpoint: endpoint, CustomModel: model);

    [Fact]
    public void ForReady_names_the_on_device_model()
    {
        Assert.Equal(
            "AI cleanup is on. Scribe uses qwen3-1.7b on this PC. Your text stays on this PC.",
            CleanupActivationMessage.ForReady(Local("qwen3-1.7b")));
    }

    [Fact]
    public void ForReady_names_the_azure_deployment()
    {
        Assert.Equal(
            "AI cleanup is on. Scribe uses gpt-5.6-terra in Microsoft Foundry. Your text goes to your Azure resource.",
            CleanupActivationMessage.ForReady(Azure("https://example.services.ai.azure.com", "gpt-5.6-terra")));
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", "Ollama")]
    [InlineData("http://127.0.0.1:11434/v1", "Ollama")]
    [InlineData("http://localhost:1234/v1", "LM Studio")]
    public void ForReady_recognises_well_known_local_servers(string endpoint, string expected)
    {
        Assert.Equal(
            $"AI cleanup is on. Scribe uses llama3 in {expected}. Your text stays on this PC.",
            CleanupActivationMessage.ForReady(Custom(endpoint, "llama3")));
    }

    [Fact]
    public void ForReady_falls_back_to_the_port_for_an_unknown_local_server()
    {
        Assert.Equal(
            "AI cleanup is on. Scribe uses llama3 in a server on this PC (port 8080). Your text stays on this PC.",
            CleanupActivationMessage.ForReady(Custom("http://localhost:8080/v1", "llama3")));
    }

    [Fact]
    public void ForReady_uses_the_host_for_a_remote_endpoint()
    {
        Assert.Equal(
            "AI cleanup is on. Scribe uses llama3 at openrouter.ai. Your text goes to openrouter.ai.",
            CleanupActivationMessage.ForReady(Custom("https://openrouter.ai/api/v1", "llama3")));
    }

    [Fact]
    public void ForReady_echoes_an_unparsable_endpoint_rather_than_inventing_a_name()
    {
        Assert.Equal(
            "AI cleanup is on. Scribe uses llama3 at not a url. Your text goes to not a url.",
            CleanupActivationMessage.ForReady(Custom("not a url", "llama3")));
    }

    [Fact]
    public void ForReady_is_silent_when_cleanup_is_disabled()
    {
        Assert.Null(CleanupActivationMessage.ForReady(CleanupOptions.Disabled));
        Assert.Null(CleanupActivationMessage.ForReady(null));
    }

    [Fact]
    public void ForReady_is_silent_for_a_configuration_that_cannot_run()
    {
        // Announcing "running on Microsoft Foundry" with no deployment would claim a swap that the
        // service will immediately report as unavailable.
        Assert.Null(CleanupActivationMessage.ForReady(Azure("https://example.services.ai.azure.com", null)));
        Assert.Null(CleanupActivationMessage.ForReady(Custom("http://localhost:11434/v1", null)));
    }

    [Fact]
    public void Copilot_is_announced()
    {
        var options = new CleanupOptions(true, CleanupProvider.GitHubCopilot, "unused", null, null, CopilotModel: "gpt-5.4");

        Assert.Equal("AI cleanup is on. Scribe uses GitHub Copilot with gpt-5.4. Your text goes to GitHub.", CleanupActivationMessage.ForReady(options));
        Assert.Equal("AI cleanup is on. Scribe uses GitHub Copilot. Your text goes to GitHub.", CleanupActivationMessage.ForReady(options with { CopilotModel = null }));
    }

    [Fact]
    public void ForDisabled_announces_only_a_disabled_configuration()
    {
        Assert.Equal(
            "AI cleanup is off. Scribe types what it hears, with your dictionary and snippets.",
            CleanupActivationMessage.ForDisabled(CleanupOptions.Disabled));
        Assert.Null(CleanupActivationMessage.ForDisabled(Local("qwen3-1.7b")));
        Assert.Null(CleanupActivationMessage.ForDisabled(null));
    }
}
