namespace Scribe.Core.Lifecycle;

/// <summary>How one <see cref="IdleModelRelease.Run"/> ended.</summary>
public enum IdleReleaseOutcome
{
    /// <summary>The owner was busy or closing when the release tried to claim it; nothing ran.</summary>
    NotIdle,

    /// <summary>Nothing was resident, so there was nothing to unload (idle-only buffers were still released).</summary>
    NothingResident,

    /// <summary>
    /// The models were unloaded, but activity began before the collection, so the collection and the announcement
    /// were both skipped.
    /// </summary>
    UnloadedThenActivityResumed,

    /// <summary>
    /// The models were unloaded and memory was compacted, but activity began before the announcement, so it was
    /// skipped.
    /// </summary>
    CompactedThenActivityResumed,

    /// <summary>The models were unloaded, memory was compacted and the release was announced.</summary>
    Released,
}

/// <summary>
/// The ordering contract of an idle model release, independent of what is being released.
/// </summary>
/// <remarks>
/// <para>
/// The release is claimed under the owner's lock and every later step runs outside it, because unloading native models
/// and a blocking, compacting collection must never sit inside a lock the hotkey path needs. Unloading is safe even if a
/// dictation starts meanwhile: the speech services load and use their models under their own gates, so that dictation
/// simply reloads them. The collection and the announcement are not safe to run on behalf of a dictation that is
/// already underway, so the claim is re-validated under the owner's lock before each of them.
/// </para>
/// <para>
/// What no lock can close is activity that begins just after a check has passed. A collection that has started runs
/// to completion, suspending every managed thread (the hotkey hook's included) for its duration, and an announcement
/// can still race the very start of a dictation, so its consumers must treat it as informational only.
/// </para>
/// </remarks>
public static class IdleModelRelease
{
    /// <param name="tryClaim">
    /// Under the owner's lock: returns the owner's current activity epoch when it is idle and not closing, else null.
    /// </param>
    /// <param name="isStillIdle">
    /// Under the owner's lock: true while no activity has begun since the claim and the owner is not closing.
    /// </param>
    /// <param name="anythingResident">Whether any model is loaded at all.</param>
    /// <param name="unload">Unloads the models. Must be safe against a concurrent use, which reloads on demand.</param>
    /// <param name="compact">The blocking collection that returns freed memory to the OS.</param>
    /// <param name="announce">Tells listeners the release happened.</param>
    /// <param name="releaseRetained">
    /// Releases idle-only memory that is not a model, such as a reusable capture buffer. Runs after every successful
    /// claim, whether or not a model is resident, and after the unload but before the collection when one is, so that
    /// collection can return it too. Like <paramref name="unload"/>, it must be safe against a dictation starting
    /// concurrently.
    /// </param>
    public static IdleReleaseOutcome Run(
        Func<long?> tryClaim,
        Func<long, bool> isStillIdle,
        Func<bool> anythingResident,
        Action unload,
        Action compact,
        Action announce,
        Action? releaseRetained = null)
    {
        ArgumentNullException.ThrowIfNull(tryClaim);
        ArgumentNullException.ThrowIfNull(isStillIdle);
        ArgumentNullException.ThrowIfNull(anythingResident);
        ArgumentNullException.ThrowIfNull(unload);
        ArgumentNullException.ThrowIfNull(compact);
        ArgumentNullException.ThrowIfNull(announce);

        if (tryClaim() is not { } claim)
        {
            return IdleReleaseOutcome.NotIdle;
        }

        if (!anythingResident())
        {
            releaseRetained?.Invoke();
            return IdleReleaseOutcome.NothingResident;
        }

        unload();
        releaseRetained?.Invoke();
        if (!isStillIdle(claim))
        {
            return IdleReleaseOutcome.UnloadedThenActivityResumed;
        }

        compact();
        if (!isStillIdle(claim))
        {
            return IdleReleaseOutcome.CompactedThenActivityResumed;
        }

        announce();
        return IdleReleaseOutcome.Released;
    }
}
