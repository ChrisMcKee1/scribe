namespace Scribe.Core.Vocabulary;

/// <summary>How a stored change ended once the answer of the vocabulary generation it asked for was in.</summary>
public enum StoredChangeOutcome
{
    /// <summary>In use, and the draft is still what was stored: the save may be reported complete, and may close.</summary>
    InEffect,

    /// <summary>
    /// In use, but the draft moved on while the answer was awaited: something changed that is not in what was stored. The
    /// caller must neither report the save complete nor close over it; the change stays in the draft, unsaved, for the
    /// next save.
    /// </summary>
    ChangedWhileSaving,

    /// <summary>
    /// Stored but not in use yet: the build could not read the dictionary, was not built by its deadline, or Scribe is
    /// closing (<see cref="VocabularyRefresh.Applied"/> is false). The caller stays open, whatever the draft did.
    /// </summary>
    NotInUseYet,
}

/// <summary>
/// The wait between a stored change and the vocabulary generation that puts it into use, for a caller that stays editable
/// while it waits: the Settings window, whose Save stores its document and then awaits the answer of the generation its
/// application asked for. An edit made during that wait is not in what was stored, so closing when the answer comes would
/// lose it without a word. The caller hands over its draft when it starts waiting, with nothing since it read what it
/// stored that could have taken an edit, and the draft is read again once the answer is in: a draft that moved on is
/// reported, never saved (<see cref="StoredChangeOutcome.ChangedWhileSaving"/>).
/// </summary>
/// <remarks>
/// The draft is a value that differs whenever what the caller would store differs (the window hashes its editors and rows).
/// Both reads run on the caller's thread: the first in <see cref="Watch"/>, the second after the answer, on the caller's
/// synchronization context (the dispatcher, the only thread that may read the window's controls). A read that throws
/// counts as a change, so a draft that cannot be compared never lets the caller close.
/// </remarks>
public sealed class StoredChangeAcknowledgement
{
    private readonly Task<VocabularyRefresh> _answer;
    private readonly Func<string> _draft;
    private readonly string? _stored;

    private StoredChangeAcknowledgement(Task<VocabularyRefresh> answer, Func<string> draft, string? stored)
    {
        _answer = answer;
        _draft = draft;
        _stored = stored;
    }

    /// <summary>Starts the wait, reading <paramref name="draft"/> now, as stored, to compare once the answer is in.</summary>
    /// <param name="answer">The answer of the generation the stored change asked for, which the publisher bounds.</param>
    /// <param name="draft">Reads the caller's draft, now and once the answer is in, on the caller's thread.</param>
    public static StoredChangeAcknowledgement Watch(Task<VocabularyRefresh> answer, Func<string> draft)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(draft);
        return new StoredChangeAcknowledgement(answer, draft, TryRead(draft));
    }

    /// <summary>
    /// Awaits the answer, asynchronously, resuming on the caller's context, then decides: not in use yet; in use with the
    /// draft moved on since <see cref="Watch"/>; or in use. A draft changed and changed back in the meantime is in use: what
    /// the caller would store is what it stored.
    /// </summary>
    public async Task<StoredChangeOutcome> CompleteAsync()
    {
        // No ConfigureAwait(false): the draft is read on the caller's context, where its controls live.
        var refresh = await _answer;
        if (!refresh.Applied)
        {
            return StoredChangeOutcome.NotInUseYet;
        }

        return _stored is { } stored && TryRead(_draft) is { } now && string.Equals(stored, now, StringComparison.Ordinal)
            ? StoredChangeOutcome.InEffect
            : StoredChangeOutcome.ChangedWhileSaving;
    }

    private static string? TryRead(Func<string> draft)
    {
        try
        {
            return draft();
        }
        catch
        {
            // Treated as a change: the caller stays open rather than close over a draft it could not compare.
            return null;
        }
    }
}
