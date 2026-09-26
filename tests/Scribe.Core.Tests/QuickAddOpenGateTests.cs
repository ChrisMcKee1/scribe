using Scribe.Core.Settings;
using HangGuard = Scribe.Core.Tests.Concurrency.HangGuard;

namespace Scribe.Core.Tests;

public sealed class QuickAddOpenGateTests
{
    // A hang guard, never the verdict: both requests are certain to finish once the held open is released.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Concurrent_requests_await_the_same_held_open()
    {
        var gate = new QuickAddOpenGate();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        var coalesced = 0;

        var first = gate.RunAsync(async () =>
        {
            opens++;
            await release.Task;
        });
        try
        {
            var second = gate.RunAsync(
                () =>
                {
                    opens++;
                    return Task.CompletedTask;
                },
                () =>
                {
                    coalesced++;
                    return Task.CompletedTask;
                });

            await Task.Delay(50);
            Assert.Equal(1, opens);

            release.SetResult();
            Assert.True(
                await HangGuard.Completes(Task.WhenAll(first, second), Bound),
                "The held open, or the request coalesced behind it, never finished once the open was released.");
        }
        finally
        {
            // The first open holds until the test lets it go, and is let go on every way out (stream TR round 7).
            release.TrySetResult();
        }

        Assert.Equal(1, opens);
        Assert.Equal(1, coalesced);
    }

    [Fact]
    public async Task Completed_open_allows_the_next_request_to_start()
    {
        var gate = new QuickAddOpenGate();
        var opens = 0;

        await gate.RunAsync(() =>
        {
            opens++;
            return Task.CompletedTask;
        });
        await gate.RunAsync(() =>
        {
            opens++;
            return Task.CompletedTask;
        });

        Assert.Equal(2, opens);
    }
}