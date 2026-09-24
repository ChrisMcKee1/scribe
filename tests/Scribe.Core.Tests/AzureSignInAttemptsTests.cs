using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class AzureSignInAttemptsTests
{
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
