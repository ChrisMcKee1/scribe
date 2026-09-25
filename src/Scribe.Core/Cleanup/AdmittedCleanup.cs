namespace Scribe.Core.Cleanup;

/// <summary>
/// One dictation's AI cleanup, admitted with the vocabulary that dictation took when it started
/// (<see cref="ITextCleanupService.Admit"/>): its requests carry exactly that vocabulary's glossary, whatever newer
/// vocabulary has been published since, and each attempt is handed over only while the library scope that vocabulary was
/// cut by is still permitted. An attempt that is not handed over is not sent; its text stays as dictated and local rules
/// finish it, with no failure reported for a permission change.
/// </summary>
public sealed class AdmittedCleanup
{
    private readonly Func<string, CancellationToken, string?, Task<CleanupResult>> _clean;

    internal AdmittedCleanup(CleanupVocabulary vocabulary, Func<string, CancellationToken, string?, Task<CleanupResult>> clean)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(clean);
        Vocabulary = vocabulary;
        _clean = clean;
    }

    /// <summary>The vocabulary every request of this cleanup carries and is judged by.</summary>
    public CleanupVocabulary Vocabulary { get; }

    /// <summary>
    /// <see cref="ITextCleanupService.CleanAsync(string, CancellationToken, string?)"/> with this vocabulary instead of
    /// whatever the service was configured with. Never throws for a content failure.
    /// </summary>
    public Task<CleanupResult> CleanAsync(
        string text, CancellationToken cancellationToken = default, string? writingStyleOverride = null) =>
        _clean(text, cancellationToken, writingStyleOverride);
}
