namespace Scribe.Core.Libraries;

/// <summary>What one run of the library janitor removed, as counts.</summary>
/// <param name="RecentlyDeletedRemoved">Recently deleted entries removed after their retention.</param>
/// <param name="QuarantinedRemoved">Set-aside manifests removed with their own journal files after the quarantine.</param>
/// <param name="Failed">Removals that failed, retried at the next run.</param>
internal readonly record struct LibraryJanitorResult(int RecentlyDeletedRemoved, int QuarantinedRemoved, int Failed);

/// <summary>
/// The library storage's retention step (contract 3.1.6), which storage maintenance runs as a light step with its own
/// clock (pattern P-10): it retries an unresolved committed manifest and every held one, removes Recently deleted entries
/// older than <see cref="LibraryLimits.RecentlyDeletedRetentionDays"/> by the stamp in their names, and removes each
/// set-aside manifest older than <see cref="LibraryLimits.QuarantineRetentionDays"/> by its set-aside stamp, together
/// with its own journal files, the manifest last.
/// </summary>
/// <remarks>
/// It never removes a library file (a target, a kept version, a set-aside edits document, a previous copy or a Recently
/// deleted entry a manifest names), nothing a live or pending manifest names, no entry whose stamp cannot be parsed or
/// lies in the future, a held manifest (which has no set-aside stamp), and nothing at all while the session runs on
/// defaults. Loading never purges: only this step does. Never throws, as a maintenance step must not. It also carries the
/// bounded retry after a hold-back (<see cref="LibraryRecoveryRetry"/>): storage maintenance connects its trigger when it
/// takes the janitor and stops the retry at shutdown, and the library service reports every publication.
/// </remarks>
internal sealed class LibraryJanitor
{
    private readonly Func<DateTimeOffset, LibraryJanitorResult> _run;

    internal LibraryJanitor(Func<DateTimeOffset, LibraryJanitorResult> run, TimeProvider time)
    {
        _run = run;
        Retry = new LibraryRecoveryRetry(time);
    }

    /// <summary>The retry after a hold-back, on the library service's clock.</summary>
    internal LibraryRecoveryRetry Retry { get; }

    internal LibraryJanitorResult Run(DateTimeOffset nowUtc)
    {
        try
        {
            return _run(nowUtc);
        }
        catch (Exception)
        {
            // A maintenance step never throws (contract 7.1); the next run retries whatever this one did not finish.
            return new LibraryJanitorResult(0, 0, 1);
        }
    }

    /// <summary>
    /// Whether an entry deleted at <paramref name="deletedUtc"/> is past its retention at <paramref name="nowUtc"/>. A stamp
    /// in the future (a clock set back since, or a hand-made name) is never expired.
    /// </summary>
    internal static bool RecentlyDeletedExpired(DateTimeOffset deletedUtc, DateTimeOffset nowUtc) =>
        deletedUtc <= nowUtc && nowUtc - deletedUtc >= TimeSpan.FromDays(LibraryLimits.RecentlyDeletedRetentionDays);

    /// <summary>Whether a manifest set aside at <paramref name="setAsideUtc"/> is past its quarantine at <paramref name="nowUtc"/>.</summary>
    internal static bool QuarantineExpired(DateTimeOffset setAsideUtc, DateTimeOffset nowUtc) =>
        setAsideUtc <= nowUtc && nowUtc - setAsideUtc >= TimeSpan.FromDays(LibraryLimits.QuarantineRetentionDays);
}
