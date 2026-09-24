namespace Scribe.Core.Settings;

/// <summary>
/// Which Azure check owns Settings' Microsoft Foundry page, and whether it is still running: the Azure CLI
/// sign-in, or a service principal or API key verification.
/// </summary>
/// <remarks>
/// <para>
/// A check takes an attempt number when it starts and applies its result only while that attempt is current.
/// Editing what it checks (the tenant, a service principal field, the API key details, the sign-in method)
/// retires it, and so does starting another check.
/// </para>
/// <para>
/// A retired check must not touch the page, its busy state included, so it cannot end the busy state it began.
/// Retiring ends it instead. When every caller had to remember that for itself, editing a service principal field
/// during a verification did not, and Verify stayed disabled until the sign-in method was switched.
/// </para>
/// <para>
/// Retiring also cancels the check's work (<see cref="CancellationOf"/>). Ignoring a retired check's result is not
/// enough: its az login kept running inside Azure CLI's single gate (AzureCliProcessCoordinator) for up to five
/// minutes, and every later check, cleanup's token requests included, waited behind it.
/// </para>
/// <para>Not thread-safe: the window uses it on its UI thread only.</para>
/// </remarks>
public sealed class AzureSignInAttempts
{
    private static readonly CancellationToken Retired = new(canceled: true);

    private int _current;
    private CancellationTokenSource _work = new();

    /// <summary>A check is running and owns the page; the action button waits for it.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>Starts a check, retiring and cancelling the one before it, and returns its attempt number.</summary>
    public int Begin()
    {
        var attempt = ++_current;
        IsBusy = true;
        CancelRetiredWork();
        return attempt;
    }

    /// <summary>Whether <paramref name="attempt"/> may still apply its result.</summary>
    public bool IsCurrent(int attempt) => attempt == _current;

    /// <summary>
    /// The token for <paramref name="attempt"/>'s work, cancelled once the attempt is retired. An attempt that is
    /// already retired gets a cancelled token.
    /// </summary>
    public CancellationToken CancellationOf(int attempt) => IsCurrent(attempt) ? _work.Token : Retired;

    /// <summary>
    /// Retires the running check, or the result already applied, cancels its work and ends the busy state.
    /// </summary>
    public void Retire()
    {
        _current++;
        IsBusy = false;
        CancelRetiredWork();
    }

    /// <summary>Ends the busy state of <paramref name="attempt"/> if it is still current.</summary>
    /// <returns>Whether it was current. A retired attempt changes nothing.</returns>
    public bool Finish(int attempt)
    {
        if (!IsCurrent(attempt))
        {
            return false;
        }

        IsBusy = false;
        return true;
    }

    // Called after the attempt number has moved on, so anything a cancellation callback reaches already sees the
    // work as retired. CancelAsync marks the token cancelled at once but runs the callbacks, which end the waits and
    // lead to the az process tree being killed, on the thread pool rather than inside the UI handler that retired it.
    private void CancelRetiredWork()
    {
        var retired = _work;
        _work = new CancellationTokenSource();
        _ = retired.CancelAsync();
    }
}
