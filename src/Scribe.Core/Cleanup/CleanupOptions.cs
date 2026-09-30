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
/// <param name="PromptCaching">
/// <c>AppSettings.AiCleanupPromptCaching</c>: false makes every Microsoft Foundry request ask not to use the prompt cache
/// (<see cref="PromptCachePolicy"/>). Not a prompt field, so a change reconnects and probes again.
/// </param>
/// <param name="VocabularyMode">
/// How much of the vocabulary a dictation's requests carry (<see cref="CleanupVocabularyMode"/>). A prompt field: it
/// changes only the instructions an agent is built with.
/// </param>
/// <param name="LocalModelKeepAliveMinutes">
/// How long Ollama or LM Studio at its own address (<see cref="LocalAiServer.AppAt"/>) should keep the model in memory
/// after each request: <c>AppSettings.ReleaseModelsAfterIdleMinutes</c>, so the app frees the model on its own clock after
/// the idle time Scribe frees its speech models after (<see cref="LocalAiServer.KeepAliveMinutes"/>). Null or 0 sends
/// nothing and leaves it to the app: Ollama keeps the time a request last asked for, or its own default, and LM Studio an
/// hour for a model it loaded on demand, so nothing stays pinned after Scribe closes. Asked of each request, so, like a
/// prompt field, a change needs no reconnect; a shorter time, or one turned on, frees the model once, since a model
/// loaded under the old time can keep it until it is loaded again. The app passes it only for Ollama and LM Studio, so a
/// change never restarts another provider's setup.
/// </param>
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
    string? CopilotModel = null,
    bool PromptCaching = true,
    CleanupVocabularyMode VocabularyMode = CleanupVocabularyMode.All,
    int? LocalModelKeepAliveMinutes = null)
{
    /// <summary>A disabled configuration (cleanup off, defaults elsewhere).</summary>
    public static CleanupOptions Disabled { get; } =
        new(false, CleanupProvider.FoundryLocal, CleanupModelCatalog.DefaultAlias, null, null);

    /// <summary>
    /// True when <paramref name="other"/> is the same configuration apart from what the prompt says:
    /// the writing style, the glossary and how much of the vocabulary goes, the prompt style and either
    /// guardrail prompt. Those change the instructions an agent is built with, and nothing about the
    /// provider, model, endpoint or credentials it talks to, so a change confined to them needs no
    /// reconnect, no new readiness probe and no "cleanup is now running on" notice. How long a server on
    /// this PC keeps the model is asked of each request, so it counts with them.
    /// </summary>
    public bool MatchesIgnoringPrompt(CleanupOptions? other) =>
        other is not null && WithoutPrompt() == other.WithoutPrompt();

    private CleanupOptions WithoutPrompt() => this with
    {
        WritingStyle = null,
        Glossary = null,
        PromptStyle = CleanupPromptStyle.Auto,
        FrontierPrompt = null,
        LocalPrompt = null,
        VocabularyMode = CleanupVocabularyMode.All,
        LocalModelKeepAliveMinutes = null,
    };

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
