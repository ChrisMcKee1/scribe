namespace Scribe.Core.Cleanup;

/// <summary>
/// One cleanup explanation in the two forms the app needs.
/// </summary>
/// <remarks>
/// <see cref="Diagnostic"/> is the only form that may reach the log, an Activity tag, the overlay or
/// any exported diagnostics: a fixed phrase that can carry the provider, an HTTP status, an SDK error
/// code and a model identifier, and never a URL, host, port, deployment or resource name, or text an
/// endpoint returned. <see cref="Display"/> is for the settings window, where naming the host the user
/// typed or quoting the endpoint's own explanation is what makes the message actionable. Keeping them
/// as separate values rather than redacting one sentence means nothing has to recognise a hostname in
/// free-form prose to stay safe.
/// </remarks>
internal readonly record struct CleanupReason(string Diagnostic, string Display)
{
    /// <summary>A fixed phrase that is already safe to show and to log.</summary>
    public static CleanupReason Same(string text) => new(text, text);
}
