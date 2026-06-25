namespace Scribe.Core.Cleanup;

/// <summary>
/// Where AI text cleanup runs. <see cref="FoundryLocal"/> uses an on-device Foundry Local model
/// (fully offline). <see cref="AzureFoundry"/> uses a model already deployed in the user's Azure
/// AI Foundry / Azure OpenAI account, reached with their Azure CLI sign-in (AAD token, no key).
/// </summary>
public enum CleanupProvider
{
    FoundryLocal = 0,
    AzureFoundry = 1,
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
    string? AzureApiKey = null)
{
    /// <summary>A disabled configuration (cleanup off, defaults elsewhere).</summary>
    public static CleanupOptions Disabled { get; } =
        new(false, CleanupProvider.FoundryLocal, CleanupModelCatalog.DefaultAlias, null, null);

    /// <summary>True when the selected provider has everything it needs to initialize.</summary>
    public bool IsActionable => Enabled && Provider switch
    {
        CleanupProvider.AzureFoundry =>
            !string.IsNullOrWhiteSpace(AzureEndpoint) && !string.IsNullOrWhiteSpace(AzureDeployment),
        _ => !string.IsNullOrWhiteSpace(FoundryModelAlias),
    };
}
