namespace Scribe.Core.Cleanup;

/// <summary>
/// Builds the one-line notification shown after the user changes AI cleanup settings.
/// <para>
/// Switching provider already takes effect on save, but it did so silently, so there was no way to
/// tell a successful swap from a setting that had not applied. Users reasonably concluded a restart
/// was required and could not tell which provider their dictations were actually going to. Naming
/// the provider and the model is what makes the swap observable.
/// </para>
/// </summary>
public static class CleanupActivationMessage
{
    /// <summary>
    /// Message for a cleanup configuration that just became ready, or null when there is nothing
    /// worth announcing (cleanup switched off, or a configuration too incomplete to run).
    /// </summary>
    public static string? ForReady(CleanupOptions? options)
    {
        if (options is null || !options.Enabled || !options.IsActionable)
        {
            return null;
        }

        return options.Provider switch
        {
            CleanupProvider.FoundryLocal =>
                $"AI cleanup is on. Scribe uses {Describe(options.FoundryModelAlias)} on this PC. Your text stays on this PC.",
            CleanupProvider.AzureFoundry =>
                $"AI cleanup is on. Scribe uses {Describe(options.AzureDeployment)} in Microsoft Foundry. Your text goes to your Azure resource.",
            CleanupProvider.OpenAiCompatible =>
                $"AI cleanup is on. {CustomEndpointBody(options)}",
            CleanupProvider.GitHubCopilot => $"AI cleanup is on. Scribe uses GitHub Copilot{CopilotModel(options.CopilotModel)}. Your text goes to GitHub.",
            _ => null,
        };
    }

    /// <summary>Message for cleanup being switched off, or null when it was not a deliberate disable.</summary>
    public static string? ForDisabled(CleanupOptions? options) =>
        options is not null && !options.Enabled ? "AI cleanup is off. Scribe types what it hears, with your dictionary and snippets." : null;


    private static string CustomEndpointBody(CleanupOptions options)
    {
        var endpoint = CleanupEndpointDescription.For(options.CustomEndpoint);
        var model = Describe(options.CustomModel);
        return endpoint.IsOnThisPc
            ? $"Scribe uses {model} in {endpoint.ActivationName}. Your text stays on this PC."
            : $"Scribe uses {model} at {endpoint.ActivationName}. Your text goes to {endpoint.ActivationName}.";
    }

    public static string TryDictationPhrase(CleanupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return "AI cleanup: off.";
        }

        return options.Provider switch
        {
            CleanupProvider.FoundryLocal =>
                $"AI cleanup: on, {StripRecommendation(CleanupModelCatalog.Resolve(options.FoundryModelAlias).DisplayName)} on this PC.",
            CleanupProvider.AzureFoundry =>
                $"AI cleanup: on, {Describe(options.AzureDeployment)} in Microsoft Foundry.",
            CleanupProvider.GitHubCopilot => string.IsNullOrWhiteSpace(options.CopilotModel)
                ? "AI cleanup: on, GitHub Copilot."
                : $"AI cleanup: on, GitHub Copilot with {options.CopilotModel.Trim()}.",
            CleanupProvider.OpenAiCompatible =>
                CustomTryDictationPhrase(options),
            _ => "AI cleanup: on, the selected model.",
        };
    }

    private static string CustomTryDictationPhrase(CleanupOptions options)
    {
        var endpoint = CleanupEndpointDescription.For(options.CustomEndpoint);
        var model = Describe(options.CustomModel);
        return endpoint.TryDictationPreposition switch
        {
            CleanupEndpointPreposition.In => $"AI cleanup: on, {model} in {endpoint.TryDictationName}.",
            CleanupEndpointPreposition.OnThisPc => $"AI cleanup: on, {model} on this PC.",
            _ => $"AI cleanup: on, {model} at {endpoint.TryDictationName}.",
        };
    }

    private static string CopilotModel(string? model) => string.IsNullOrWhiteSpace(model) ? string.Empty : $" with {model.Trim()}";

    private static string Describe(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "the selected model" : name.Trim();

    private static string StripRecommendation(string displayName)
    {
        const string suffix = " (recommended)";
        return displayName.EndsWith(suffix, StringComparison.Ordinal)
            ? displayName[..^suffix.Length]
            : displayName;
    }
}

public enum CleanupEndpointPreposition
{
    In,
    OnThisPc,
    At,
}

public sealed record CleanupEndpointDescription(
    string ActivationName,
    bool IsOnThisPc,
    string TryDictationName,
    CleanupEndpointPreposition TryDictationPreposition)
{
    public static CleanupEndpointDescription For(string? endpoint)
    {
        var value = endpoint?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return new("the server you entered", false, "the server you entered", CleanupEndpointPreposition.At);
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return new(value, false, value, CleanupEndpointPreposition.At);
        }

        var isLoopback = uri.IsLoopback ||
            string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        if (!isLoopback)
        {
            return new(uri.Host, false, uri.Host, CleanupEndpointPreposition.At);
        }

        return uri.Port switch
        {
            11434 => new("Ollama", true, "Ollama", CleanupEndpointPreposition.In),
            1234 => new("LM Studio", true, "LM Studio", CleanupEndpointPreposition.In),
            _ => new($"a server on this PC (port {uri.Port})", true, "this PC", CleanupEndpointPreposition.OnThisPc),
        };
    }
}
