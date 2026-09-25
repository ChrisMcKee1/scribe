using Scribe.Core.Cleanup;

namespace Scribe.Core.Settings;

/// <summary>
/// Whether sending recent dictation history to the AI provider needs the user's go-ahead first.
/// </summary>
public static class AiRequestConsent
{
    /// <summary>
    /// True unless both the provider cleanup is serving (<paramref name="recipient"/>, the only place the
    /// request can go) and the saved provider (what the page describes) run on this PC. Neither alone is
    /// enough: a Save that failed leaves the window holding a provider that was picked but never applied,
    /// while cleanup keeps serving the old one, and a Save that succeeded is applied before cleanup is ready
    /// on it. Asking once too often costs a click; asking once too rarely sends history unasked.
    /// </summary>
    public static bool IsNeeded(CleanupRecipient recipient, CleanupProvider savedProvider)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        return !recipient.IsOnDevice || savedProvider != CleanupProvider.FoundryLocal;
    }
}
