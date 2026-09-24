using Scribe.Core.Cleanup;

namespace Scribe.Core.Settings;

/// <summary>
/// The window's side of <see cref="AzureCliSignIn.RunAsync"/>. Each step reads the page as it is when it runs.
/// </summary>
public interface IAzureCliSignInSteps
{
    /// <summary>Whether Azure CLI is installed on this PC.</summary>
    Task<bool> IsCliInstalledAsync();

    /// <summary>Whether a subscription is selected. Its tenant then takes precedence over the tenant box.</summary>
    bool HasSelectedSubscription { get; }

    /// <summary>Whether the selected subscription is missing from what Azure CLI's account can now see.</summary>
    Task<bool> IsSelectedSubscriptionUnavailableAsync();

    /// <summary>Goes back to all subscriptions.</summary>
    void ClearSelectedSubscription();

    /// <summary>Checks the existing Azure CLI sign-in without prompting.</summary>
    Task<AzureSignInStatus> ProbeAsync();

    /// <summary>Tells the user that a browser sign-in is about to open.</summary>
    void ReportBrowserSignIn();

    /// <summary>Opens the browser sign-in (az login).</summary>
    Task<(bool Ok, string Message)> LoginAsync();
}

/// <summary>
/// The Azure CLI sign-in sequence behind Settings' "Sign in &amp; find models" and its automatic check: is Azure
/// CLI installed, can its account still see the saved subscription, is it signed in, and, when allowed, a browser
/// sign-in followed by a fresh check.
/// </summary>
/// <remarks>
/// <para>
/// Every step waits on something slow (az, a token request, a browser), and the user can retire the attempt
/// during any of those waits by editing the tenant or switching the sign-in method. A retired attempt must not act
/// on what it learned: it would overwrite the newer status, clear a subscription, or open a browser sign-in for a
/// tenant the user has just changed, whose result the page can no longer use. So the sequence asks whether it is
/// still current after every wait, before it changes anything.
/// </para>
/// <para>
/// Every await resumes on the caller's context, because the steps read and change the window.
/// </para>
/// </remarks>
public static class AzureCliSignIn
{
    public enum Outcome
    {
        /// <summary>The attempt was retired during a wait, and nothing was changed after that point.</summary>
        Retired,

        /// <summary>Azure CLI is not installed.</summary>
        CliMissing,

        /// <summary>The browser sign-in failed or was cancelled; <see cref="Result.Message"/> says why.</summary>
        LoginFailed,

        /// <summary>Not signed in, either without a browser sign-in or despite one.</summary>
        NotSignedIn,

        /// <summary>Signed in; <see cref="Result.Status"/> identifies the account.</summary>
        SignedIn,
    }

    public readonly record struct Result(Outcome Outcome, AzureSignInStatus? Status = null, string? Message = null);

    /// <param name="steps">The window's steps.</param>
    /// <param name="allowInteractiveLogin">A browser sign-in may open when the check finds no sign-in.</param>
    /// <param name="isCurrent">Whether this attempt still owns the page, asked after every wait.</param>
    public static async Task<Result> RunAsync(
        IAzureCliSignInSteps steps,
        bool allowInteractiveLogin,
        Func<bool> isCurrent)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(isCurrent);

        var installed = await steps.IsCliInstalledAsync();
        if (!isCurrent())
        {
            return new Result(Outcome.Retired);
        }

        if (!installed)
        {
            return new Result(Outcome.CliMissing);
        }

        if (steps.HasSelectedSubscription)
        {
            var unavailable = await steps.IsSelectedSubscriptionUnavailableAsync();
            if (!isCurrent())
            {
                return new Result(Outcome.Retired);
            }

            if (unavailable)
            {
                steps.ClearSelectedSubscription();
            }
        }

        var status = await steps.ProbeAsync();
        if (!isCurrent())
        {
            return new Result(Outcome.Retired);
        }

        if (status.IsSignedIn || !allowInteractiveLogin)
        {
            return Finish(status);
        }

        steps.ReportBrowserSignIn();
        var (ok, message) = await steps.LoginAsync();
        if (!isCurrent())
        {
            return new Result(Outcome.Retired);
        }

        if (!ok)
        {
            return new Result(Outcome.LoginFailed, Message: message);
        }

        status = await steps.ProbeAsync();
        if (!isCurrent())
        {
            return new Result(Outcome.Retired);
        }

        // The browser can sign in to an account that cannot see the saved subscription; its tenant would then keep
        // the check failing, so it gives way to the account that just signed in.
        if (!status.IsSignedIn && steps.HasSelectedSubscription)
        {
            steps.ClearSelectedSubscription();
            status = await steps.ProbeAsync();
            if (!isCurrent())
            {
                return new Result(Outcome.Retired);
            }
        }

        return Finish(status);
    }

    private static Result Finish(AzureSignInStatus status) =>
        new(status.IsSignedIn ? Outcome.SignedIn : Outcome.NotSignedIn, status);
}
