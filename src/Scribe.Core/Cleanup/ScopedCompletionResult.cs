namespace Scribe.Core.Cleanup;

/// <summary>
/// What <see cref="ITextCleanupService.CompleteAsync(string, string, CleanupRecipient, Libraries.AiVocabularyScope, CancellationToken)"/>
/// did: <see cref="CompletionOutcome"/>'s cases, plus the one a library scope adds.
/// </summary>
public enum ScopedCompletionOutcome
{
    /// <summary>The model answered; <see cref="ScopedCompletionResult.Text"/> holds its answer.</summary>
    Completed,

    /// <summary>Nothing was sent: no model is ready.</summary>
    NotReady,

    /// <summary>Nothing was sent: the service now serves a different configuration from the recipient passed.</summary>
    RecipientChanged,

    /// <summary>
    /// A request was not handed over: the library vocabulary the completion carries is no longer permitted, or its content
    /// changed. Nothing was sent at all when <see cref="ScopedCompletionResult.RequestsHandedOver"/> is 0; otherwise an
    /// earlier request went, under the permission it was admitted with, and a later one was held back. The caller says the
    /// data changed and asks for the request to be made again (for the usage insight, "Usage changed while the insight
    /// was being prepared. Generate it again.").
    /// </summary>
    LibraryScopeNarrowed,

    /// <summary>The request was sent and failed, or the answer was empty.</summary>
    Failed,
}

/// <summary>The result of a one-off completion made under a library scope.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Text">The answer, set only when <see cref="ScopedCompletionOutcome.Completed"/>.</param>
/// <param name="RequestsHandedOver">
/// How many requests left the process through the admission point: over HTTP, the first attempt and each retry the client
/// made; for the GitHub Copilot runtime, a session's creation and its send, each on its own.
/// </param>
public sealed record ScopedCompletionResult(ScopedCompletionOutcome Outcome, string? Text = null, int RequestsHandedOver = 0)
{
    /// <summary>True when no request of the completion left the process.</summary>
    public bool NothingSent => RequestsHandedOver == 0 && Outcome != ScopedCompletionOutcome.Completed;
}
