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
    private readonly Action<string?>? _prewarm;

    internal AdmittedCleanup(
        CleanupVocabulary vocabulary,
        Func<string, CancellationToken, string?, Task<CleanupResult>> clean,
        Action<string?>? prewarm = null)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(clean);
        Vocabulary = vocabulary;
        _clean = clean;
        _prewarm = prewarm;
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

    /// <summary>
    /// Readies the model for this dictation while it is still being spoken, when AI cleanup runs on a server on this PC
    /// (Ollama, LM Studio): the dictation's own instructions and vocabulary go out with no text and a one-token ceiling,
    /// so a model the server unloaded while idle is loaded, and those instructions cached, before the words arrive.
    /// Returns at once and never throws; does nothing for any other provider, while cleanup is not ready, or when the model
    /// answered moments ago. <paramref name="writingStyleOverride"/> is the style the dictation will be cleaned with.
    /// </summary>
    public void Prewarm(string? writingStyleOverride = null) => _prewarm?.Invoke(writingStyleOverride);
}
