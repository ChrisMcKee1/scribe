using Scribe.Core.Cleanup;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class AzureCliSignInTests
{
    private static readonly AzureSignInStatus SignedOut = new(false, null);
    private static readonly AzureSignInStatus SignedIn = new(true, "user@contoso.com", "72f988bf-0000-0000-0000-000000000000");

    // The reported race: "Sign in & find models" starts a check, the user edits the tenant while the token request is
    // out, and the retired check used to open a browser sign-in for the new tenant anyway.
    [Fact]
    public async Task A_check_retired_by_a_tenant_edit_does_not_start_a_browser_sign_in()
    {
        var steps = new Steps();
        var probe = steps.ThenGatedProbe();

        var running = AzureCliSignIn.RunAsync(steps, allowInteractiveLogin: true, () => steps.Current);
        await probe.Started;
        steps.Current = false;
        probe.Release(SignedOut);
        var result = await running;

        Assert.Equal(AzureCliSignIn.Outcome.Retired, result.Outcome);
        Assert.Equal(["installed", "probe"], steps.Calls);
    }

    [Fact]
    public async Task A_check_retired_before_azure_cli_answers_does_not_report_it_missing()
    {
        var steps = new Steps();
        var installed = steps.GateInstalled();

        var running = AzureCliSignIn.RunAsync(steps, allowInteractiveLogin: true, () => steps.Current);
        await installed.Started;
        steps.Current = false;
        installed.Release(false);
        var result = await running;

        Assert.Equal(AzureCliSignIn.Outcome.Retired, result.Outcome);
        Assert.Equal(["installed"], steps.Calls);
    }

    [Fact]
    public async Task A_check_retired_while_checking_the_saved_subscription_leaves_it_selected()
    {
        var steps = new Steps { HasSelectedSubscription = true };
        var subscription = steps.GateSubscriptionCheck();

        var running = AzureCliSignIn.RunAsync(steps, allowInteractiveLogin: true, () => steps.Current);
        await subscription.Started;
        steps.Current = false;
        subscription.Release(true);
        var result = await running;

        Assert.Equal(AzureCliSignIn.Outcome.Retired, result.Outcome);
        Assert.Equal(["installed", "subscription"], steps.Calls);
    }

    [Fact]
    public async Task A_sign_in_retired_while_the_browser_is_open_changes_nothing_after_it()
    {
        var steps = new Steps { HasSelectedSubscription = true };
        steps.ThenProbe(SignedOut);
        var login = steps.GateLogin();

        var running = AzureCliSignIn.RunAsync(steps, allowInteractiveLogin: true, () => steps.Current);
        await login.Started;
        steps.Current = false;
        login.Release((true, string.Empty));
        var result = await running;

        Assert.Equal(AzureCliSignIn.Outcome.Retired, result.Outcome);
        Assert.Equal(["installed", "subscription", "probe", "report", "login"], steps.Calls);
    }

    [Fact]
    public async Task A_check_after_sign_in_that_is_retired_does_not_clear_the_subscription()
    {
        var steps = new Steps { HasSelectedSubscription = true };
        steps.ThenProbe(SignedOut);
        var recheck = steps.ThenGatedProbe();

        var running = AzureCliSignIn.RunAsync(steps, allowInteractiveLogin: true, () => steps.Current);
        await recheck.Started;
        steps.Current = false;
        recheck.Release(SignedOut);
        var result = await running;

        Assert.Equal(AzureCliSignIn.Outcome.Retired, result.Outcome);
        Assert.Equal(["installed", "subscription", "probe", "report", "login", "probe"], steps.Calls);
    }

    [Fact]
    public async Task The_last_check_after_the_subscription_gave_way_is_retired_too()
    {
        var steps = new Steps { HasSelectedSubscription = true };
        steps.ThenProbe(SignedOut);
        steps.ThenProbe(SignedOut);
        var last = steps.ThenGatedProbe();

        var running = AzureCliSignIn.RunAsync(steps, allowInteractiveLogin: true, () => steps.Current);
        await last.Started;
        steps.Current = false;
        last.Release(SignedIn);
        var result = await running;

        Assert.Equal(AzureCliSignIn.Outcome.Retired, result.Outcome);
        Assert.Equal(["installed", "subscription", "probe", "report", "login", "probe", "clear", "probe"], steps.Calls);
    }

    [Fact]
    public async Task An_existing_sign_in_needs_no_browser()
    {
        var steps = new Steps();
        steps.ThenProbe(SignedIn);

        var result = await AzureCliSignIn.RunAsync(steps, allowInteractiveLogin: true, () => steps.Current);

        Assert.Equal(AzureCliSignIn.Outcome.SignedIn, result.Outcome);
        Assert.Same(SignedIn, result.Status);
        Assert.Equal(["installed", "probe"], steps.Calls);
    }

    [Fact]
    public async Task The_automatic_check_never_opens_a_browser()
    {
        var steps = new Steps();
        steps.ThenProbe(SignedOut);

        var result = await AzureCliSignIn.RunAsync(steps, allowInteractiveLogin: false, () => steps.Current);

        Assert.Equal(AzureCliSignIn.Outcome.NotSignedIn, result.Outcome);
        Assert.Equal(["installed", "probe"], steps.Calls);
    }

    [Fact]
    public async Task A_browser_sign_in_is_followed_by_a_fresh_check()
    {
        var steps = new Steps();
        steps.ThenProbe(SignedOut);
        steps.ThenProbe(SignedIn);

        var result = await AzureCliSignIn.RunAsync(steps, allowInteractiveLogin: true, () => steps.Current);

        Assert.Equal(AzureCliSignIn.Outcome.SignedIn, result.Outcome);
        Assert.Same(SignedIn, result.Status);
        Assert.Equal(["installed", "probe", "report", "login", "probe"], steps.Calls);
    }

    [Fact]
    public async Task A_failed_browser_sign_in_reports_why()
    {
        var steps = new Steps { LoginResult = (false, "Azure sign-in was cancelled.") };
        steps.ThenProbe(SignedOut);

        var result = await AzureCliSignIn.RunAsync(steps, allowInteractiveLogin: true, () => steps.Current);

        Assert.Equal(AzureCliSignIn.Outcome.LoginFailed, result.Outcome);
        Assert.Equal("Azure sign-in was cancelled.", result.Message);
        Assert.Equal(["installed", "probe", "report", "login"], steps.Calls);
    }

    [Fact]
    public async Task A_subscription_the_new_sign_in_cannot_use_gives_way_to_it()
    {
        var steps = new Steps { HasSelectedSubscription = true };
        steps.ThenProbe(SignedOut);
        steps.ThenProbe(SignedOut);
        steps.ThenProbe(SignedIn);

        var result = await AzureCliSignIn.RunAsync(steps, allowInteractiveLogin: true, () => steps.Current);

        Assert.Equal(AzureCliSignIn.Outcome.SignedIn, result.Outcome);
        Assert.Equal(["installed", "subscription", "probe", "report", "login", "probe", "clear", "probe"], steps.Calls);
    }

    [Fact]
    public async Task Still_signed_out_after_the_browser_is_reported_as_such()
    {
        var steps = new Steps();
        steps.ThenProbe(SignedOut);
        steps.ThenProbe(SignedOut);

        var result = await AzureCliSignIn.RunAsync(steps, allowInteractiveLogin: true, () => steps.Current);

        Assert.Equal(AzureCliSignIn.Outcome.NotSignedIn, result.Outcome);
        Assert.Equal(["installed", "probe", "report", "login", "probe"], steps.Calls);
    }

    [Fact]
    public async Task Missing_azure_cli_stops_before_any_check()
    {
        var steps = new Steps { Installed = false };

        var result = await AzureCliSignIn.RunAsync(steps, allowInteractiveLogin: true, () => steps.Current);

        Assert.Equal(AzureCliSignIn.Outcome.CliMissing, result.Outcome);
        Assert.Equal(["installed"], steps.Calls);
    }

    [Fact]
    public async Task A_saved_subscription_the_account_no_longer_sees_is_cleared_before_the_check()
    {
        var steps = new Steps { HasSelectedSubscription = true, SubscriptionUnavailable = true };
        steps.ThenProbe(SignedIn);

        var result = await AzureCliSignIn.RunAsync(steps, allowInteractiveLogin: true, () => steps.Current);

        Assert.Equal(AzureCliSignIn.Outcome.SignedIn, result.Outcome);
        Assert.Equal(["installed", "subscription", "clear", "probe"], steps.Calls);
    }

    /// <summary>Holds one step open until the test releases it, so the attempt can be retired mid-wait.</summary>
    private sealed class Gate<T>
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<T> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task<T> Wait()
        {
            _started.TrySetResult();
            return _result.Task;
        }

        public void Release(T value) => _result.TrySetResult(value);
    }

    private sealed class Steps : IAzureCliSignInSteps
    {
        private readonly Queue<Func<Task<AzureSignInStatus>>> _probes = new();
        private Gate<bool>? _installed;
        private Gate<bool>? _subscription;
        private Gate<(bool Ok, string Message)>? _login;

        public bool Current { get; set; } = true;

        public bool Installed { get; init; } = true;

        public bool HasSelectedSubscription { get; set; }

        public bool SubscriptionUnavailable { get; init; }

        public (bool Ok, string Message) LoginResult { get; init; } = (true, string.Empty);

        public List<string> Calls { get; } = [];

        public void ThenProbe(AzureSignInStatus status) => _probes.Enqueue(() => Task.FromResult(status));

        public Gate<AzureSignInStatus> ThenGatedProbe()
        {
            var gate = new Gate<AzureSignInStatus>();
            _probes.Enqueue(gate.Wait);
            return gate;
        }

        public Gate<bool> GateInstalled() => _installed = new Gate<bool>();

        public Gate<bool> GateSubscriptionCheck() => _subscription = new Gate<bool>();

        public Gate<(bool Ok, string Message)> GateLogin() => _login = new Gate<(bool Ok, string Message)>();

        public Task<bool> IsCliInstalledAsync()
        {
            Calls.Add("installed");
            return _installed?.Wait() ?? Task.FromResult(Installed);
        }

        public Task<bool> IsSelectedSubscriptionUnavailableAsync()
        {
            Calls.Add("subscription");
            return _subscription?.Wait() ?? Task.FromResult(SubscriptionUnavailable);
        }

        public void ClearSelectedSubscription()
        {
            Calls.Add("clear");
            HasSelectedSubscription = false;
        }

        public Task<AzureSignInStatus> ProbeAsync()
        {
            Calls.Add("probe");
            return _probes.Count > 0
                ? _probes.Dequeue()()
                : throw new InvalidOperationException("The sequence checked the sign-in more often than the test expected.");
        }

        public void ReportBrowserSignIn() => Calls.Add("report");

        public Task<(bool Ok, string Message)> LoginAsync()
        {
            Calls.Add("login");
            return _login?.Wait() ?? Task.FromResult(LoginResult);
        }
    }
}
