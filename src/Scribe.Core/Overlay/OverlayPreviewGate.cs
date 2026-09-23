namespace Scribe.Core.Overlay;

/// <summary>The part a command plays in a settings-window position preview.</summary>
public enum OverlayPreviewRole
{
    /// <summary>An engine command, not part of any preview.</summary>
    None,

    /// <summary>Moves the pill to the candidate anchor the preview demonstrates.</summary>
    Anchor,

    /// <summary>The preview's recording look and its synthetic level sweep.</summary>
    Step,

    /// <summary>Finishes the preview: hides the pill (or returns it to a state the engine keeps on screen) and restores the applied anchor.</summary>
    End,
}

/// <summary>What the consumer does with a command, as far as position previews are concerned.</summary>
public enum OverlayPreviewVerdict
{
    /// <summary>Carry the command out as usual.</summary>
    Deliver,

    /// <summary>
    /// The helper may still be showing a superseded preview's candidate anchor: write the applied anchor
    /// first, then carry the command out.
    /// </summary>
    RestoreAnchorThenDeliver,

    /// <summary>A step of a preview that something newer superseded: drop it.</summary>
    Drop,
}

/// <summary>
/// Keeps a position preview from overriding anything newer than itself. A preview queues its candidate
/// anchor, a recording look and a level sweep from a background task, then its end; any engine state
/// command or newer preview supersedes it. Each preview command carries the preview's generation and is
/// dropped once that generation is no longer current, and the first engine state command after a
/// superseded preview restores the applied anchor the preview moved away from. Live level meters and
/// control commands (keep-warm, release, exit) do not pass through it.
/// </summary>
/// <remarks>
/// <para>
/// Before this, the sweep checked its generation and then queued its hide, so a recording starting in
/// between got its RECORDING ahead of the preview's HIDE and recorded with no pill until processing; and a
/// preview superseded mid-sweep never restored the anchor, so the next recording showed at the previewed
/// position. Dropping stale steps alone would trade the first bug for the second, because the restore is
/// itself a preview step.
/// </para>
/// <para>
/// Threading: <see cref="BeginPreview"/>, <see cref="Supersede"/> and <see cref="IsCurrent"/> are called on
/// any thread. <see cref="OnCommand"/> belongs to the single command consumer, which sees commands in queue
/// order; staleness is judged when the consumer takes a command, not when it was queued, so a preview step
/// queued before a superseding command but taken after the supersede is dropped too. Restoring the anchor
/// when it was not actually moved costs one redundant POSITION line and is harmless.
/// </para>
/// </remarks>
public sealed class OverlayPreviewGate
{
    private long _generation;
    private bool _candidateAnchorShown;

    /// <summary>Starts a preview and returns its generation, superseding any earlier preview. Thread-safe.</summary>
    public long BeginPreview() => Interlocked.Increment(ref _generation);

    /// <summary>An engine command supersedes any running preview. Thread-safe. Call it before queuing the command.</summary>
    public void Supersede() => Interlocked.Increment(ref _generation);

    /// <summary>Whether the preview with <paramref name="generation"/> is still the newest thing asked of the pill. Thread-safe.</summary>
    public bool IsCurrent(long generation) => Interlocked.Read(ref _generation) == generation;

    /// <summary>
    /// Consumer only. Judges one command taken from the queue: <paramref name="role"/> and
    /// <paramref name="generation"/> are what it was queued with (generation is ignored for engine commands).
    /// </summary>
    public OverlayPreviewVerdict OnCommand(OverlayPreviewRole role, long generation)
    {
        if (role == OverlayPreviewRole.None)
        {
            if (!_candidateAnchorShown)
            {
                return OverlayPreviewVerdict.Deliver;
            }

            _candidateAnchorShown = false;
            return OverlayPreviewVerdict.RestoreAnchorThenDeliver;
        }

        if (!IsCurrent(generation))
        {
            return OverlayPreviewVerdict.Drop;
        }

        if (role == OverlayPreviewRole.Anchor)
        {
            _candidateAnchorShown = true;
        }
        else if (role == OverlayPreviewRole.End)
        {
            _candidateAnchorShown = false; // the end writes the applied anchor itself
        }

        return OverlayPreviewVerdict.Deliver;
    }
}
