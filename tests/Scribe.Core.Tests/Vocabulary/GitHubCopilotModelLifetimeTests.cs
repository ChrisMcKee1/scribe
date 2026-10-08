using Scribe.Core.Cleanup;
using Scribe.Core.Tests.StorageTime;

namespace Scribe.Core.Tests.Vocabulary;

public sealed class GitHubCopilotModelLifetimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_model_list_keeps_its_answer_and_releases_its_owned_loopback_client()
    {
        await using var runtime = new FakeCopilotRuntime();
        var clock = new ModelClock();

        var models = await GitHubCopilotModels.ListForRuntimeAsync(runtime.Url, clock).WaitAsync(Bound);

        var model = Assert.Single(models);
        Assert.Equal("cleanup-model", model.Id);
        Assert.Equal("Cleanup model", model.Name);
        Assert.Equal(["low"], model.SupportedReasoningEfforts);
        Assert.Equal("low", model.DefaultReasoningEffort);
        Assert.Single(runtime.Requests, request => request.Method == "connect");
        Assert.Single(runtime.Requests, request => request.Method == "models.list");
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Contains(clock.Timers, timer => timer.InitialDueTime == GitHubCopilotClientLifetime.StepTimeout);
    }

    [Fact]
    public async Task A_cancelled_model_list_stays_empty_and_never_reconnects_during_owner_cleanup()
    {
        await using var runtime = new FakeCopilotRuntime
        {
            BeforeAnswer = (request, token) =>
                request.Method == "models.list" ? Task.Delay(Timeout.InfiniteTimeSpan, token) : Task.CompletedTask,
        };
        using var cancellation = new CancellationTokenSource();
        var requested = runtime.Next(request => request.Method == "models.list");
        var clock = new ModelClock();

        var listing = GitHubCopilotModels.ListForRuntimeAsync(runtime.Url, clock, cancellation.Token);
        await requested.WaitAsync(Bound);
        await cancellation.CancelAsync();

        Assert.Empty(await listing.WaitAsync(Bound));
        Assert.Single(runtime.Requests, request => request.Method == "connect");
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
    }

    private sealed class ModelClock : TimeProvider
    {
        private readonly List<ObservedTimer> _timers = [];

        public IReadOnlyList<ObservedTimer> Timers
        {
            get
            {
                lock (_timers)
                {
                    return [.. _timers];
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            timer.Change(dueTime, period);
            lock (_timers)
            {
                _timers.Add(new ObservedTimer(timer, dueTime));
            }

            return timer;
        }
    }

    private sealed record ObservedTimer(ManualTimer Timer, TimeSpan InitialDueTime)
    {
        public bool Disposed => Timer.Disposed;
    }
}
