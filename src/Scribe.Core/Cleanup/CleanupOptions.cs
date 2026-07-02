namespace Scribe.Core.Cleanup;

/// <summary>
/// Where AI text cleanup runs. <see cref="FoundryLocal"/> uses an on-device Foundry Local model
/// (fully offline). <see cref="AzureFoundry"/> uses a model already deployed in the user's Azure
/// AI Foundry / Azure OpenAI account, reached with their Azure CLI sign-in (AAD token, no key).
/// <see cref="OpenAiCompatible"/> is bring-your-own-endpoint: any server speaking the OpenAI chat
/// protocol — Ollama, LM Studio, vLLM, OpenRouter, or a direct OpenAI key.
/// </summary>
public enum CleanupProvider
{
    FoundryLocal = 0,
    AzureFoundry = 1,
    OpenAiCompatible = 2,
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
    string? CustomApiKey = null)
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
        _ => !string.IsNullOrWhiteSpace(FoundryModelAlias),
    };
}
