namespace Scribe.Core.Overlay;

/// <summary>
/// Decides when the overlay helper process has sat idle long enough to be suspended, and makes that
/// decision safe against a command racing it. One of the two policies <see cref="OverlayHelperLifetime"/>
/// combines; the client never calls it directly.
/// </summary>
/// <remarks>
/// <para>
/// The helper owns its idle lifetime. Every state command restarts the idle period, and the period is
/// the keep-warm setting that also governs the speech models (zero or less never suspends). Suspending
/// on the speech models' release instead let a stale release end the pill of a recording that had
/// started in the meantime, and never fired at all when no model was resident, so a helper started by
/// a position preview stayed up for the life of the process.
/// </para>
/// <para>
/// Threading: producers stamp each command with <see cref="NoteCommandIssued"/> before queuing it, from
/// any thread. Every other member belongs to the single command consumer, which is also the only thread
/// that ends the helper, so the checks in <see cref="TryCommitSuspend"/> and <see cref="TryCommitRelease"/>
/// and the teardown that follows them are serialized with command processing. A command stamped before
/// the check vetoes the suspend; one stamped after it is processed after the teardown and relaunches the
/// helper through the normal path.
/// </para>
/// <para>Deterministic: callers pass a monotonic millisecond clock reading; nothing here reads a clock.</para>
/// </remarks>
internal sealed class OverlayIdleDeadline
{
    private long _issued;
    private long _armedStamp;
    private long _lastActivityMs;
    private bool _armed;

    /// <summary>
    /// Stamps a command that is about to be queued. Thread-safe. Call it before the enqueue, so a commit
    /// that runs while the command is still on its way already sees it.
    /// </summary>
    public long NoteCommandIssued() => Interlocked.Increment(ref _issued);

    /// <summary>
    /// Consumer only. Restarts the idle period because the command carrying <paramref name="stamp"/> was
    /// taken from the queue at <paramref name="nowMs"/>.
    /// </summary>
    public void OnCommandProcessed(long nowMs, long stamp)
    {
        _armed = true;
        _lastActivityMs = nowMs;
        NoteStampSettled(stamp);
    }

    /// <summary>
    /// Consumer only. Accounts for a stamped command that was taken from the queue without restarting the
    /// idle period (a superseded preview step, a release request), so its stamp does not read as a command
    /// still on its way at the next commit.
    /// </summary>
    public void NoteStampSettled(long stamp)
    {
        // Two producers can stamp in one order and enqueue in the other, so keep the newest stamp seen
        // rather than whichever was taken last.
        if (stamp > _armedStamp)
        {
            _armedStamp = stamp;
        }
    }

    /// <summary>
    /// Consumer only. When the deadline falls due for an idle period of <paramref name="idleMs"/>, or
    /// <c>null</c> when nothing is armed or the period is zero or less (never suspend). The period is
    /// passed on every call so a changed setting also applies to a deadline that is already armed.
    /// </summary>
    public long? DueAtMs(long idleMs) => _armed && idleMs > 0 ? _lastActivityMs + idleMs : null;

    /// <summary>
    /// Consumer only, at the commit point immediately before the helper is ended. Returns <c>true</c>
    /// exactly when the helper should be suspended now: the deadline is due, no command has been stamped
    /// since it was armed, and the latest state does not keep the pill on screen. A <c>true</c> result
    /// disarms the deadline until the next command.
    /// </summary>
    public bool TryCommitSuspend(long nowMs, long idleMs, OverlayDemand latest)
    {
        if (DueAtMs(idleMs) is not { } dueMs || nowMs < dueMs)
        {
            return false;
        }

        var issued = Interlocked.Read(ref _issued);
        if (issued != _armedStamp)
        {
            // A command arrived after the deadline was armed, and processing it re-arms the deadline.
            // Until then, look again only after another full period, so the consumer never spins on a
            // producer that has stamped its command but not queued it yet.
            _armedStamp = issued;
            _lastActivityMs = nowMs;
            return false;
        }

        if (latest == OverlayDemand.Sustained)
        {
            // A recording or processing pill is in use with no new command, which is what a long
            // recording looks like. Check again after another full period.
            _lastActivityMs = nowMs;
            return false;
        }

        _armed = false;
        return true;
    }

    /// <summary>
    /// Consumer only. The immediate form of <see cref="TryCommitSuspend"/> for a release request stamped
    /// <paramref name="stamp"/> (dictation was paused): the helper may be ended now when no command was
    /// stamped after the request and the latest state does not keep the pill on screen. That is the same
    /// validation the idle commit runs, so a request can never end the helper of a recording started after
    /// it. A <c>true</c> result disarms the deadline until the next command.
    /// </summary>
    public bool TryCommitRelease(long stamp, OverlayDemand latest)
    {
        NoteStampSettled(stamp);
        if (Interlocked.Read(ref _issued) != stamp || latest == OverlayDemand.Sustained)
        {
            return false; // the newer command re-arms the deadline when it is processed
        }

        _armed = false;
        return true;
    }

    /// <summary>Consumer only. Disarms the deadline until the next processed command.</summary>
    public void Disarm() => _armed = false;
}
