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
                $"AI cleanup is running on this device with {Describe(options.FoundryModelAlias)}.",
            CleanupProvider.AzureFoundry =>
                $"AI cleanup is running on Microsoft Foundry with {Describe(options.AzureDeployment)}.",
            CleanupProvider.OpenAiCompatible =>
                $"AI cleanup is running on {DescribeHost(options.CustomEndpoint)} with {Describe(options.CustomModel)}.",
            _ => null,
        };
    }

    /// <summary>Message for cleanup being switched off, or null when it was not a deliberate disable.</summary>
    public static string? ForDisabled(CleanupOptions? options) =>
        options is not null && !options.Enabled ? "AI cleanup is off. Dictations are inserted as transcribed." : null;

    private static string Describe(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "the selected model" : name.Trim();

    /// <summary>
    /// Host of a bring-your-own endpoint, so the notification can say "Ollama" or "LM Studio"
    /// rather than repeating a URL the user already typed. Falls back to the raw value when it is
    /// not a parsable URL, because echoing what the user entered beats inventing a name.
    /// </summary>
    private static string DescribeHost(string? endpoint)
    {
        var value = endpoint?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return "your OpenAI-compatible endpoint";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return value;
        }

        // Port is the only reliable local-server signal: Ollama and LM Studio both bind loopback,
        // so the host alone ("localhost") would not tell the two apart.
        var isLoopback = uri.IsLoopback ||
            string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        if (isLoopback)
        {
            return uri.Port switch
            {
                11434 => "Ollama",
                1234 => "LM Studio",
                _ => $"your local server on port {uri.Port}",
            };
        }

        return uri.Host;
    }
}
