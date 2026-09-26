namespace Scribe.Core.Cleanup;

public enum CleanupTestOutcome
{
    Connected,
    Failed,
    Cancelled,
    NotApplicable,
}

public sealed record CleanupTestResult(
    CleanupTestOutcome Outcome,
    CleanupRecipient Recipient,
    string? SafeReason = null,
    string? DisplayDetail = null)
{
    public static CleanupTestResult Connected(CleanupRecipient recipient) =>
        new(CleanupTestOutcome.Connected, recipient);

    internal static CleanupTestResult Failed(CleanupRecipient recipient, CleanupReason reason) =>
        new(CleanupTestOutcome.Failed, recipient, reason.Diagnostic, reason.Display);

    public static CleanupTestResult Cancelled(CleanupRecipient recipient) =>
        new(CleanupTestOutcome.Cancelled, recipient);

    public static CleanupTestResult NotApplicable(CleanupRecipient recipient) =>
        new(CleanupTestOutcome.NotApplicable, recipient);
}
