namespace Scribe.Core.Vocabulary;

/// <summary>What became of a request for a new vocabulary generation (<see cref="VocabularyPublisher.RefreshAsync"/>).</summary>
public enum VocabularyRefreshOutcome
{
    /// <summary>
    /// A generation built from inputs read after the request was made is published: every dictation admitted from now on
    /// takes it or a newer one, so a change stored before the request is in effect.
    /// </summary>
    Applied,

    /// <summary>
    /// The build that answered the request could not read the personal dictionary, so dictation keeps the generation it
    /// had, which predates the request: the change is stored but not in effect until a later build succeeds (the next
    /// change asks for one, and so does a restart).
    /// </summary>
    NotApplied,

    /// <summary>The publisher was disposed, because Scribe is closing, before a build answered the request.</summary>
    Stopped,
}

/// <summary>
/// The answer to a request for a new vocabulary generation: its outcome, and the generation in use when it was answered,
/// the one a dictation admitted at that moment is given.
/// </summary>
/// <param name="Outcome">Whether the change the request was made for is in effect.</param>
/// <param name="Generation">
/// For <see cref="VocabularyRefreshOutcome.Applied"/>, the generation built for the request (or a newer one, once later
/// requests are answered); otherwise the generation dictation keeps.
/// </param>
public sealed record VocabularyRefresh(VocabularyRefreshOutcome Outcome, VocabularyGeneration Generation)
{
    /// <summary>True when the change the request was made for is in effect.</summary>
    public bool Applied => Outcome == VocabularyRefreshOutcome.Applied;
}

/// <summary>
/// What the shell says when a change was stored but dictation cannot use it yet
/// (<see cref="VocabularyRefreshOutcome.NotApplied"/>, or <see cref="VocabularyRefreshOutcome.Stopped"/>). Every place that
/// reports a stored vocabulary change (a Settings save, quick add, learning from history, the Usage page's Add) reports it
/// as in effect only after awaiting <see cref="VocabularyRefreshOutcome.Applied"/>, and otherwise with this.
/// </summary>
public static class VocabularyNotice
{
    /// <summary>
    /// <paramref name="saved"/>, what was stored as a clause without a closing stop (such as "Settings saved"), then that
    /// dictation keeps its previous vocabulary and until when.
    /// </summary>
    public static string SavedButNotApplied(string saved)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saved);
        return saved.TrimEnd() +
            ", but dictation couldn't load the change yet and keeps its previous vocabulary until the next change or a restart.";
    }
}
