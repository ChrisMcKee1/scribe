using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class QuickAddOpenGateTests
{
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
        await Task.WhenAll(first, second);

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