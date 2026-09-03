namespace Scribe.Core.Cleanup;

/// <summary>
/// Where AI text cleanup runs. <see cref="FoundryLocal"/> uses an on-device Foundry Local model
/// (fully offline). <see cref="AzureFoundry"/> uses a model already deployed in the user's Azure
/// AI Foundry / Azure OpenAI account, reached with their Azure CLI sign-in (AAD token, no key).
/// <see cref="OpenAiCompatible"/> is bring-your-own-endpoint: any server speaking the OpenAI chat
/// protocol: Ollama, LM Studio, vLLM, OpenRouter, or a direct OpenAI key.
/// </summary>
public enum CleanupProvider
{
    FoundryLocal = 0,
    AzureFoundry = 1,
    OpenAiCompatible = 2,

    /*
     * The user's own GitHub Copilot licence, through Agent Framework's Copilot backend.
     *
     * Unlike every other provider this one has no endpoint and no key: it drives an authenticated
     * Copilot CLI on this machine, so the models on offer are whichever ones the signed-in GitHub
     * account is entitled to. That also makes it the one provider with a dependency Scribe cannot
     * satisfy on its own, which is why Settings detects the CLI before offering it.
     */
    GitHubCopilot = 3,
}

/// <summary>
/// Which guardrail preamble drives AI cleanup. Both preambles carry the same rules; the
/// <see cref="Local"/> one is terser and more directive because small on-device models follow short,
/// explicit instructions (and a worked example) more reliably than long nuanced prose, while the
/// <see cref="Frontier"/> one is the golden-suite-tuned prompt capable cloud models score best on.
/// <see cref="Auto"/> (the default) picks by provider so users get a sensible default without losing
/// the ability to force either prompt.
/// </summary>
public enum CleanupPromptStyle
{
    Auto = 0,
    Frontier = 1,
    Local = 2,
}

/// <summary>
/// Immutable snapshot of the cleanup configuration handed to <see cref="ITextCleanupService"/>.
/// Carries everything both providers need so the service can (re)build its chat client whenever
/// the user changes the toggle, the provider, the local model, or the Azure deployment.
/// </summary>
public sealed record CleanupOptions(
    bool Enabled,
    CleanupProvider Provider,
    string FoundryModelAlias,
    string? AzureEndpoint,
    string? AzureDeployment,
    string? AzureApiKey = null,
    string? AzureTenantId = null,
    string? WritingStyle = null,
    string? Glossary = null,
    string? CustomEndpoint = null,
    string? CustomModel = null,
    string? CustomApiKey = null,
    CleanupPromptStyle PromptStyle = CleanupPromptStyle.Auto,
    string? FrontierPrompt = null,
    string? LocalPrompt = null,
    string? AzureSubscriptionId = null,
    Settings.AzureAuthMode AzureAuthMode = Settings.AzureAuthMode.AzureCli,
    string? AzureClientId = null,
    string? AzureClientSecret = null,
    string? CopilotModel = null)
{
    /// <summary>A disabled configuration (cleanup off, defaults elsewhere).</summary>
    public static CleanupOptions Disabled { get; } =
        new(false, CleanupProvider.FoundryLocal, CleanupModelCatalog.DefaultAlias, null, null);

    /// <summary>True when the selected provider has everything it needs to initialize.</summary>
    public bool IsActionable => Enabled && Provider switch
    {
        CleanupProvider.AzureFoundry =>
            !string.IsNullOrWhiteSpace(AzureEndpoint) && !string.IsNullOrWhiteSpace(AzureDeployment),
        // The API key stays optional: local servers (Ollama, LM Studio) don't need one.
        CleanupProvider.OpenAiCompatible =>
            !string.IsNullOrWhiteSpace(CustomEndpoint) && !string.IsNullOrWhiteSpace(CustomModel),
        // No endpoint, no key, and the model is optional: blank means "whatever the Copilot CLI
        // defaults to for this account", which is a working configuration rather than a missing one.
        CleanupProvider.GitHubCopilot => true,
        _ => !string.IsNullOrWhiteSpace(FoundryModelAlias),
    };
}
