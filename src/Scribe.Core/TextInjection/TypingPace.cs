namespace Scribe.Core.TextInjection;

/// <summary>
/// How Unicode typing is batched and spaced for a target: code units per SendInput call, the settle between calls, and
/// whether a batch backs up to a word boundary.
/// <para>
/// With the precise-local-settle experiment off, every target but a Remote Desktop or virtual machine client keeps the
/// requested pace every release has used (<see cref="Local"/>). A remote client
/// (<see cref="RemoteClientProcesses"/>) sends each injected keystroke on to the remote session's input stack,
/// where it is replayed at whatever pace it arrived: the user's 0.4.3 log shows 184
/// characters, 368 events, typed into msrdc in 99 ms, in four calls of up to 100 events each, and the remote input
/// stack wedged about a second later. Nothing documents a rate a remote session can take, so the remote pace is chosen
/// to stay far below that burst while keeping a long dictation brisk: at most 16 code units per call
/// (32 events, plus two for each Shift+Enter), a third of a local batch, requesting a 20 ms wait between calls.
/// That is four times the local request. It gives the session an opportunity to process input, not an
/// acknowledgment that it did. Word boundaries are not sought: small batches already cut words, and backing up
/// could double the batch count and time. Surrogate pairs and CRLF pairs never straddle two calls
/// (<c>TextInjector.ChunkLength</c>).
/// <c>TypingPaceTests</c> sums requested waits over fixed text: 220 ms for 184 characters (15 ms locally) and 940 ms
/// for 766 (75 ms locally). These are not wall-clock measurements: actual waits depend on Windows timer resolution
/// and scheduling, and SendInput and the target's processing add time. The events are identical; their elapsed
/// delivery time is not promised to be identical.
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
