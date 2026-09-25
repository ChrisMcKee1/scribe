namespace Scribe.Core.Cleanup;

/// <summary>
/// Who a request made through <see cref="ITextCleanupService.CompleteAsync"/> reaches: the configuration the
/// service is serving, identified by everything that decides where a request goes (provider, endpoint,
/// deployment or model, credentials) and by nothing the prompt says. <see cref="ITextCleanupService.Recipient"/>
/// hands one out while cleanup is ready; a caller that asks the user before sending passes it back, and the
/// service sends only while it is still serving exactly that configuration. A Save or a tray change in between
/// then cannot redirect what the user agreed to send.
/// </summary>
/// <remarks>
/// Only the service creates one, so a caller cannot describe a recipient it did not get from the service.
/// Equality is by configuration: a prompt-only change (a dictionary term, the writing style) keeps the
/// recipient, because the request still goes to the same place.
/// </remarks>
public sealed class CleanupRecipient
{
    private readonly CleanupOptions _configuration;

    internal CleanupRecipient(CleanupOptions configuration) => _configuration = configuration;

    /// <summary>The provider the request goes to.</summary>
    public CleanupProvider Provider => _configuration.Provider;

    /// <summary>True when the request stays on this PC (Foundry Local).</summary>
    public bool IsOnDevice => Provider == CleanupProvider.FoundryLocal;

    /// <summary>Whether a service serving <paramref name="serving"/> is this recipient.</summary>
    internal bool Matches(CleanupOptions serving) => _configuration.MatchesIgnoringPrompt(serving);
}

/// <summary>What <see cref="ITextCleanupService.CompleteAsync"/> did.</summary>
public enum CompletionOutcome
{
    /// <summary>The model answered; <see cref="CompletionResult.Text"/> holds its answer.</summary>
    Completed,

    /// <summary>Nothing was sent: no model is ready.</summary>
    NotReady,

    /// <summary>
    /// Nothing was sent: the service is now serving a different configuration from the recipient the caller
    /// passed, so the request would have gone somewhere the user did not agree to.
    /// </summary>
    RecipientChanged,

    /// <summary>The request was sent and failed, or the answer was empty.</summary>
    Failed,
}

/// <summary>The result of a one-off completion. <see cref="Text"/> is set only when it completed.</summary>
public sealed record CompletionResult(CompletionOutcome Outcome, string? Text = null)
{
    internal static CompletionResult NotReady { get; } = new(CompletionOutcome.NotReady);

    internal static CompletionResult RecipientChanged { get; } = new(CompletionOutcome.RecipientChanged);

    internal static CompletionResult Failed { get; } = new(CompletionOutcome.Failed);

    /// <summary>True when nothing was sent because of the recipient check or readiness.</summary>
    public bool NothingSent => Outcome is CompletionOutcome.NotReady or CompletionOutcome.RecipientChanged;
}
