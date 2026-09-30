namespace Scribe.Core.Cleanup;

/// <summary>
/// Whether an OpenAI-compatible address is a server on this PC, such as Ollama, LM Studio or llama.cpp's server
/// answering on <c>localhost</c>. Such a server runs a model on this PC's own hardware, so AI cleanup treats it as it
/// treats Foundry Local: the short instructions and their glossary budget, a low temperature, and thinking turned off.
/// </summary>
/// <remarks>
/// Settings has always told the user that Automatic "uses short instructions for models on this PC", yet only Foundry
/// Local got them. A local server got the detailed instructions and the whole glossary, which with the two word packs a
/// fresh install turns on came to about 7,700 tokens against Ollama's default 4,096-token context: Ollama cut the prompt
/// to its last 2,050 tokens, dropping every instruction, and small models answered with summaries and action plans.
/// Only loopback counts. A server elsewhere on the network is not "on this PC", may run a large model, and keeps the
/// detailed instructions unless the user picks the short ones.
/// </remarks>
public static class LocalAiServer
{
    /// <summary>
    /// How long after a server on this PC last answered Scribe skips readying it at the start of a dictation
    /// (<see cref="AdmittedCleanup.Prewarm"/>): within this the model is still loaded and its instructions cached.
    /// </summary>
    public const int PrewarmAfterIdleSeconds = 30;

    /// <summary>True for an http or https address whose host is this PC: <c>localhost</c>, <c>127.0.0.0/8</c> or <c>::1</c>.</summary>
    public static bool IsOnThisPc(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        return uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when cleanup runs on a server on this PC through the OpenAI-compatible provider.</summary>
    public static bool Serves(CleanupProvider provider, string? customEndpoint) =>
        provider == CleanupProvider.OpenAiCompatible && IsOnThisPc(customEndpoint);

    /// <summary>Ollama's OpenAI-compatible address as it installs, the one Settings saves for Ollama.</summary>
    public const string OllamaAddress = "http://localhost:11434/v1";

    /// <summary>LM Studio's OpenAI-compatible address as it installs, the one Settings saves for LM Studio.</summary>
    public const string LmStudioAddress = "http://localhost:1234/v1";

    /// <summary>
    /// Which app answers at <paramref name="endpoint"/>, judged only from the exact address each installs with: http, this
    /// PC's own name (<c>localhost</c>, <c>127.0.0.1</c> or <c>::1</c>), Ollama's port 11434 or LM Studio's 1234, and the
    /// path <c>/v1</c>. Anything else, another port, a path, a query, https or a name on the network, is
    /// <see cref="LocalServerApp.None"/>: a port alone does not prove which app is listening, so a server the user set up
    /// by hand stays "another AI service" and Scribe never manages its models.
    /// </summary>
    public static LocalServerApp AppAt(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath.TrimEnd('/') != "/v1")
        {
            return LocalServerApp.None;
        }

        var host = uri.IdnHost;
        var ownName = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            host == "127.0.0.1" ||
            host is "[::1]" or "::1";
        if (!ownName)
        {
            return LocalServerApp.None;
        }

        return uri.Port switch
        {
            11434 => LocalServerApp.Ollama,
            1234 => LocalServerApp.LmStudio,
            _ => LocalServerApp.None,
        };
    }

    /// <summary>The address Settings saves for <paramref name="app"/>; null for <see cref="LocalServerApp.None"/>.</summary>
    public static string? AddressOf(LocalServerApp app) => app switch
    {
        LocalServerApp.Ollama => OllamaAddress,
        LocalServerApp.LmStudio => LmStudioAddress,
        _ => null,
    };

    /// <summary>The app that serves cleanup when it runs on Ollama or LM Studio at its own address (<see cref="AppAt"/>).</summary>
    public static LocalServerApp AppServing(CleanupProvider provider, string? customEndpoint) =>
        provider == CleanupProvider.OpenAiCompatible ? AppAt(customEndpoint) : LocalServerApp.None;

    /// <summary>
    /// What <see cref="CleanupOptions.LocalModelKeepAliveMinutes"/> carries for this provider and address: the idle time
    /// the user chose (<c>AppSettings.ReleaseModelsAfterIdleMinutes</c>) for Ollama and LM Studio at their own address, which
    /// every request then asks the app to keep the model for, and null for anything else, so a change of the idle time
    /// never touches another provider's setup. The same for dictation and Test connection: a model Test connection loads
    /// is given back on the same clock.
    /// </summary>
    public static int? KeepAliveMinutes(CleanupProvider provider, string? customEndpoint, int idleMinutes) =>
        AppServing(provider, customEndpoint) == LocalServerApp.None ? null : idleMinutes;
}

/// <summary>An app on this PC that serves AI models and that Scribe knows how to ask about, and free, its models.</summary>
public enum LocalServerApp
{
    /// <summary>Not an app Scribe recognizes: another AI service, even one on this PC.</summary>
    None = 0,

    /// <summary>Ollama, at <see cref="LocalAiServer.OllamaAddress"/>.</summary>
    Ollama = 1,

    /// <summary>LM Studio, at <see cref="LocalAiServer.LmStudioAddress"/>.</summary>
    LmStudio = 2,
}
