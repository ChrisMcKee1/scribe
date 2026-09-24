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
/// <para>Not thread-safe: the window uses it on its UI thread only.</para>
/// </remarks>
public sealed class AzureSignInAttempts
{
    private int _current;

    /// <summary>A check is running and owns the page; the action button waits for it.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>Starts a check, retiring the one before it, and returns its attempt number.</summary>
    public int Begin()
    {
        IsBusy = true;
        return ++_current;
    }

    /// <summary>Whether <paramref name="attempt"/> may still apply its result.</summary>
    public bool IsCurrent(int attempt) => attempt == _current;

    /// <summary>Retires the running check, or the result already applied, and ends the busy state.</summary>
    public void Retire()
    {
        _current++;
        IsBusy = false;
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
}
