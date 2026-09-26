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

    /// <summary>
    /// No build answered the request by its deadline (<see cref="VocabularyPublisher.RefreshDeadline"/>): a library or
    /// dictionary read that does not return, so the caller is released without it and the change is stored but not in use
    /// yet. The build goes on; if it returns it publishes in order like any other, and a later dictation gets it, but this
    /// answer stands.
    /// </summary>
    TimedOut,
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
/// (<see cref="VocabularyRefreshOutcome.NotApplied"/>, <see cref="VocabularyRefreshOutcome.TimedOut"/>, or
/// <see cref="VocabularyRefreshOutcome.Stopped"/>), and when a Settings save's draft moved on while it waited. Every place
/// that reports a stored vocabulary change (a Settings save, quick add, learning from history, the Usage page's Add)
/// reports it as in effect only after awaiting <see cref="VocabularyRefreshOutcome.Applied"/>, and otherwise with this.
/// </summary>
public static class VocabularyNotice
{
    /// <summary>
    /// What a Settings save says when the window's draft moved on while it waited for what it stored to come into use
    /// (<see cref="StoredChangeOutcome.ChangedWhileSaving"/>): that change is not in what was stored, so the window stays
    /// open with it rather than close over it, and the next save stores it.
    /// </summary>
    public const string SettingsChangedWhileSaving =
        "Settings saved, but something in this window changed while saving. Save again to keep that change.";

    /// <summary>
    /// <paramref name="saved"/>, what was stored as a clause without a closing stop (such as "Settings saved"), then that
    /// dictation is not using the change yet and keeps its previous vocabulary. True whichever way the build fell short: it
    /// could not read the dictionary (a later build, which the next change or a restart asks for, loads it), or it had not
    /// returned by its deadline (it loads the change if it returns).
    /// </summary>
    public static string SavedButNotApplied(string saved)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saved);
        return saved.TrimEnd() +
            ", but dictation isn't using the change yet and keeps its previous vocabulary until it can load it.";
    }
}
