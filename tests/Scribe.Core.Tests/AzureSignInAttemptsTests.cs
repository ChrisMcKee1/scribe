using Scribe.Core.Cleanup;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class AzureSignInAttemptsTests
{
    // A hang guard, never the verdict: every wait below is for something certain to happen.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public void A_retired_attempt_is_cancelled_and_a_new_one_is_not()
    {
        var attempts = new AzureSignInAttempts();
        var first = attempts.Begin();
        var firstWork = attempts.CancellationOf(first);
        Assert.False(firstWork.IsCancellationRequested);

        attempts.Retire();

        Assert.True(firstWork.IsCancellationRequested);
        Assert.True(attempts.CancellationOf(first).IsCancellationRequested);

        var second = attempts.Begin();
        Assert.False(attempts.CancellationOf(second).IsCancellationRequested);
        Assert.True(attempts.CancellationOf(first).IsCancellationRequested);
    }

    [Fact]
    public void Starting_a_check_cancels_the_one_before_it()
    {
        var attempts = new AzureSignInAttempts();
        var first = attempts.Begin();
        var firstWork = attempts.CancellationOf(first);

        var second = attempts.Begin();

        Assert.True(firstWork.IsCancellationRequested);
        Assert.False(attempts.CancellationOf(second).IsCancellationRequested);
    }

    [Fact]
    public void Finishing_a_check_does_not_cancel_it()
    {
        var attempts = new AzureSignInAttempts();
        var attempt = attempts.Begin();
        var work = attempts.CancellationOf(attempt);

        attempts.Finish(attempt);

        Assert.False(work.IsCancellationRequested);
    }

    // O1: az login runs inside Azure CLI's single gate, which every token request waits on, cleanup's included. A
    // login that outlived its retired attempt made every later check time out until its browser window closed.
    // The gate here is the real one, and the login is shaped as the window's (the attempt's token, linked, with the
    // five-minute limit on top).
    [Fact]
    public async Task Retiring_an_attempt_ends_its_login_so_the_next_check_gets_the_Azure_CLI_gate()
    {
        var attempts = new AzureSignInAttempts();
        var attempt = attempts.Begin();
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var login = CancellationTokenSource.CreateLinkedTokenSource(attempts.CancellationOf(attempt));
        login.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            Func<CancellationToken, Task<string>> signIn = async token =>
            {
                holding.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "signed in";
            };
            var running = AzureCliProcessCoordinator.RunAsync(signIn, login.Token);
            await holding.Task.WaitAsync(Bound);

            attempts.Retire();

            // The next check waits on the same gate. Its limit is a hang guard, far under the login's five minutes, so a
            // login the retirement failed to end still fails the test.
            using var check = new CancellationTokenSource(Bound);
            var next = await AzureCliProcessCoordinator.RunAsync(_ => Task.FromResult("checked"), check.Token);

            Assert.Equal("checked", next);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
        finally
        {
            // Releases the shared gate even when an assertion failed, so no other test waits behind it.
            login.Cancel();
        }
    }

    [Fact]
    public void A_check_keeps_the_page_busy_until_it_finishes()
    {
        var attempts = new AzureSignInAttempts();

        var attempt = attempts.Begin();

        Assert.True(attempts.IsBusy);
        Assert.True(attempts.IsCurrent(attempt));
        Assert.True(attempts.Finish(attempt));
        Assert.False(attempts.IsBusy);
    }

    // The reported defect: editing a service principal field during a verification retired it, and the retired
    // verification, which may no longer touch the page, was the only thing that would have ended the busy state.
    [Fact]
    public void Retiring_a_running_check_ends_its_busy_state()
    {
        var attempts = new AzureSignInAttempts();
        var verification = attempts.Begin();

        attempts.Retire();

        Assert.False(attempts.IsBusy);
        Assert.False(attempts.IsCurrent(verification));

        // The retired verification returns later; its result and its cleanup change nothing.
        Assert.False(attempts.Finish(verification));
        Assert.False(attempts.IsBusy);
    }

    [Fact]
    public void A_retired_check_cannot_end_the_busy_state_of_a_newer_one()
    {
        var attempts = new AzureSignInAttempts();
        var retired = attempts.Begin();
        attempts.Retire();
        var newer = attempts.Begin();

        Assert.False(attempts.Finish(retired));
        Assert.True(attempts.IsBusy);
        Assert.True(attempts.IsCurrent(newer));

        Assert.True(attempts.Finish(newer));
        Assert.False(attempts.IsBusy);
    }

    [Fact]
    public void Starting_a_check_retires_the_one_before_it()
    {
        var attempts = new AzureSignInAttempts();
        var first = attempts.Begin();
        var second = attempts.Begin();

        Assert.False(attempts.IsCurrent(first));
        Assert.False(attempts.Finish(first));
        Assert.True(attempts.IsBusy);

        Assert.True(attempts.Finish(second));
        Assert.False(attempts.IsBusy);
    }

    [Fact]
    public void Retiring_also_retires_a_result_already_applied()
    {
        var attempts = new AzureSignInAttempts();
        var attempt = attempts.Begin();
        attempts.Finish(attempt);

        attempts.Retire();

        // The sign-in lists models after it finishes, and only while it is still current.
        Assert.False(attempts.IsCurrent(attempt));
        Assert.False(attempts.IsBusy);
    }

    [Fact]
    public void Retiring_with_nothing_running_is_harmless()
    {
        var attempts = new AzureSignInAttempts();

        attempts.Retire();
        attempts.Retire();

        Assert.False(attempts.IsBusy);
        var attempt = attempts.Begin();
        Assert.True(attempts.IsBusy);
        Assert.True(attempts.Finish(attempt));
        Assert.False(attempts.IsBusy);
    }
}
