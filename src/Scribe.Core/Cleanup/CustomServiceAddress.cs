namespace Scribe.Core.Cleanup;

/// <summary>
/// What another AI service's address says about how to reach it.
/// </summary>
/// <remarks>
/// The OpenAI SDK adds the API's path to the address it is given (<c>/chat/completions</c>, or <c>/responses</c>), so an
/// address pasted with that path already on it was sent to <c>.../chat/completions/chat/completions</c> and answered 404.
/// An address that ends in an API's path is now taken as that API, with the path taken off for the SDK (the query kept),
/// and the API chosen in Settings applies only to an address that names none. Ollama and LM Studio at their own addresses
/// (<see cref="LocalAiServer.AppAt"/>, which an address with a path never is) always take Chat Completions: it is the API
/// Scribe manages them through (the context size, the idle time, the readying request).
/// </remarks>
public static class CustomServiceAddress
{
    private const string ChatCompletionsPath = "/chat/completions";
    private const string ResponsesPath = "/responses";
    private const string CompletionsPath = "/completions";

    /// <summary>The API <paramref name="address"/>'s path names, any case, with or without a final slash; null when none.</summary>
    public static CustomApiStyle? NamedStyle(string? address) =>
        Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) ? Split(uri).Named : null;

    /// <summary>The address requests are built on: <paramref name="address"/> without the API path it names, if any.</summary>
    public static Uri BaseAddress(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return Split(address).Base;
    }

    /// <summary>
    /// True when <paramref name="address"/> ends in <c>/completions</c> without <c>/chat</c> before it: the older
    /// Completions API, which Scribe does not use.
    /// </summary>
    public static bool NamesOldCompletions(string? address) =>
        Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) &&
        Path(uri) is var path &&
        path.EndsWith(CompletionsPath, StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(ChatCompletionsPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The API cleanup reaches the service with: Chat Completions for Ollama or LM Studio at its own address; otherwise the
    /// one the address names, else <paramref name="chosen"/>.
    /// </summary>
    public static CustomApiStyle Effective(CleanupProvider provider, string? address, CustomApiStyle chosen) =>
        LocalAiServer.AppServing(provider, address) != LocalServerApp.None
            ? CustomApiStyle.ChatCompletions
            : NamedStyle(address) ?? (chosen == CustomApiStyle.Responses ? CustomApiStyle.Responses : CustomApiStyle.ChatCompletions);

    private static (Uri Base, CustomApiStyle? Named) Split(Uri address)
    {
        var path = Path(address);
        foreach (var (suffix, style) in NamedPaths)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return (new UriBuilder(address) { Path = path[..^suffix.Length] }.Uri, style);
            }
        }

        return (address, null);
    }

    private static readonly (string Suffix, CustomApiStyle Style)[] NamedPaths =
    [
        (ChatCompletionsPath, CustomApiStyle.ChatCompletions),
        (ResponsesPath, CustomApiStyle.Responses),
    ];

    // The path without its final slash.
    private static string Path(Uri address) => address.AbsolutePath.TrimEnd('/');
}
