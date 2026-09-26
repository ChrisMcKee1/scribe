namespace Scribe.Core.TextInjection;

/// <summary>
/// How Unicode typing is batched and spaced for a target: code units per SendInput call, the settle between calls, and
/// whether a batch backs up to a word boundary.
/// <para>
/// Every target but a Remote Desktop or virtual machine client keeps the pace every release has used
/// (<see cref="Local"/>). A remote client (<see cref="RemoteClientProcesses"/>) sends each injected keystroke on to the
/// remote session's input stack, where it is replayed at whatever pace it arrived: the user's 0.4.3 log shows 184
/// characters, 368 events, typed into msrdc in 99 ms, in four calls of up to 100 events each, and the remote input
/// stack wedged about a second later. Nothing documents a rate a remote session can take, so the remote pace is chosen
/// to stay far below that burst while keeping a long dictation brisk: at most 16 code units per call (32 events, plus two
/// for each Shift+Enter), a third of a local batch, with 20 ms between calls, four times the local settle and more than a
/// 60 Hz frame, so the session's input and its screen updates each get a turn between batches. Word boundaries are not
/// sought: a batch that small cuts words wherever it ends, and backing up could double the number of batches and so the
/// time. Surrogate pairs and CRLF pairs still never straddle two calls (<c>TextInjector.ChunkLength</c>). The extra time
/// is the settles, measured over the same text (<c>TypingPaceTests</c>): 11 of 20 ms for 184 characters (220 ms, against
/// 3 of 5 ms, 15 ms, locally) and 47 for 766 (940 ms, against 75 ms). The events sent, and the time SendInput itself
/// takes, are the same either way.
/// </para>
/// </summary>
internal readonly record struct TypingPace(int BatchUnits, int SettleMs, bool PreferWordBoundary, bool PacedForRemoteSession)
{
    /// <summary>The pace of every target that is not a remote client, unchanged.</summary>
    public static TypingPace Local { get; } =
        new(TextInjector.UnicodeChunkChars, TextInjector.InterChunkSettleMs, PreferWordBoundary: true, PacedForRemoteSession: false);

    /// <summary>The pace of a Remote Desktop or virtual machine client.</summary>
    public static TypingPace RemoteSession { get; } =
        new(TextInjector.RemoteChunkChars, TextInjector.RemoteSettleMs, PreferWordBoundary: false, PacedForRemoteSession: true);

    /// <summary>The pace for the process that owned the focused window when the dictation started.</summary>
    public static TypingPace For(string? targetProcessName) =>
        RemoteClientProcesses.IsRemoteClient(targetProcessName) ? RemoteSession : Local;
}
