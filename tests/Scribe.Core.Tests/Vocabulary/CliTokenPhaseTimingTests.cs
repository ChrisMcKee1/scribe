using Azure.Core;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// PLAT-R-02: the Azure CLI token phase is recorded when each part of it ends, however it ends, on the admission of the flow
/// that waited: a wait for the gate by admission or before it, a CLI call with a token or not, and a wait for a token
/// another request was already acquiring. So a dictation that timed out in the queue shows that time as queue time, a
/// request that never reached the CLI shows no call, and a phase that never ran is not a measured zero. The credentials'
/// own behaviour (the gate, the call, cancellation) is unchanged. Synthetic credentials only; the process-wide Azure CLI
/// gate is never held (the credential's test gate stands in for it).
/// </summary>
public sealed class CliTokenPhaseTimingTests
{
    private static readonly TokenRequestContext Context = new(["https://ai.azure.com/.default"]);

    [Fact]
    public async Task A_wait_that_ends_by_admission_records_the_wait_and_the_finished_call()
    {
        var timings = NewTimings();
        var credential = new SerializedAzureCliCredential(new ScriptedCli(), new SemaphoreSlim(1, 1));

        using (Admission(timings).Enter())
        {
            await credential.GetTokenAsync(Context, CancellationToken.None);
        }

        var phase = timings.Read();
        Assert.Equal(1, phase.GateWaits);
        Assert.Equal(0, phase.GateUnadmitted);
        Assert.Equal(1, phase.TokenCalls);
        Assert.Equal(0, phase.TokenUnfinished);
        Assert.Equal(0, phase.SharedWaits);
    }

    [Fact]
    public async Task A_wait_cancelled_before_admission_records_the_queue_time_and_no_call()
    {
        var timings = NewTimings();
        var gate = new SemaphoreSlim(0, 1); // Settings' sign-in holds the Azure CLI gate
        var cli = new ScriptedCli();
        var credential = new SerializedAzureCliCredential(cli, gate);
        using var cancel = new CancellationTokenSource();

        Task<AccessToken> waiting;
        using (Admission(timings).Enter())
        {
            waiting = credential.GetTokenAsync(Context, cancel.Token).AsTask();
        }

        Assert.False(waiting.IsCompleted);
        Assert.Equal(0, timings.Read().GateWaits); // not ended yet, so nothing is claimed
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        var phase = timings.Read();
        Assert.Equal(0, cli.Calls);
        Assert.Equal(1, phase.GateWaits);
        Assert.Equal(1, phase.GateUnadmitted);
        Assert.True(phase.GateTicks > 0, "The time spent in the queue was recorded as zero.");
        Assert.Equal(0, phase.TokenCalls);
        Assert.Equal(0, phase.TokenTicks);
        Assert.Equal(0, gate.CurrentCount); // the gate is left exactly as it was: still held
    }

    [Fact]
    public async Task A_failure_inside_the_call_records_an_unfinished_call_and_the_failure_is_the_callers()
    {
        var timings = NewTimings();
        var failure = new InvalidOperationException("synthetic az failure");
        var credential = new SerializedAzureCliCredential(new ScriptedCli { Failure = failure }, new SemaphoreSlim(1, 1));

        Exception? thrown;
        using (Admission(timings).Enter())
        {
            thrown = await Record.ExceptionAsync(async () => await credential.GetTokenAsync(Context, CancellationToken.None));
        }

        Assert.Same(failure, thrown);
        var phase = timings.Read();
        Assert.Equal(1, phase.GateWaits);
        Assert.Equal(0, phase.GateUnadmitted);
        Assert.Equal(1, phase.TokenCalls);
        Assert.Equal(1, phase.TokenUnfinished);
    }

    [Fact]
    public async Task A_cancelled_request_sharing_a_cached_acquisition_records_its_wait_as_shared_and_no_call_of_its_own()
    {
        var first = NewTimings();
        var second = NewTimings();
        var cli = new ScriptedCli { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var credential = new CachingCliTokenCredential(
            new SerializedAzureCliCredential(cli, new SemaphoreSlim(1, 1)),
            invalidationVersion: () => 0,
            sharedWait: static (wait, received) => CleanupAdmission.Current?.Timings?.AddSharedTokenWait(wait, received));
        using var cancelSecond = new CancellationTokenSource();

        Task<AccessToken> starting;
        using (Admission(first).Enter())
        {
            starting = credential.GetTokenAsync(Context, CancellationToken.None).AsTask();
        }

        Task<AccessToken> joining;
        using (Admission(second).Enter())
        {
            joining = credential.GetTokenAsync(Context, cancelSecond.Token).AsTask();
        }

        await cancelSecond.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joining);

        // The request that joined waited on the other's acquisition: its time is a shared wait that ended without the
        // token, and it made no call and waited at no gate of its own.
        var joined = second.Read();
        Assert.Equal(1, joined.SharedWaits);
        Assert.Equal(1, joined.SharedUnreceived);
        Assert.True(joined.SharedTicks > 0);
        Assert.Equal(0, joined.GateWaits);
        Assert.Equal(0, joined.TokenCalls);

        // The request that started it keeps its own numbers: the gate it passed, then the call once it ends.
        Assert.Equal(1, first.Read().GateWaits);
        Assert.Equal(0, first.Read().TokenCalls);
        cli.Hold.SetResult();
        await starting;
        Assert.Equal(1, first.Read().TokenCalls);
        Assert.Equal(0, first.Read().TokenUnfinished);
        Assert.Equal(0, first.Read().SharedWaits);
        Assert.Equal(1, cli.Calls);
    }

    [Fact]
    public void The_factory_s_cache_records_shared_waits_on_the_waiting_request()
    {
        var factory = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.Core", "Cleanup", "AzureCredentialFactory.cs"));

        Assert.Contains("new CachingCliTokenCredential(Build(normalized), sharedWait: RecordSharedTokenWait)", factory, StringComparison.Ordinal);
        Assert.Contains("CleanupAdmission.Current?.Timings?.AddSharedTokenWait(wait, received)", factory, StringComparison.Ordinal);
    }

    private static CleanupPhaseTimings NewTimings() => new(capturePhases: false, TimeSpan.Zero);

    private static CleanupAdmission Admission(CleanupPhaseTimings timings) =>
        new(CleanupRequestKind.Dictation, AiVocabularyScope.None, null) { Timings = timings };

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    // Stands in for az: answers, fails, or holds until the test lets it go.
    private sealed class ScriptedCli : TokenCredential
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Exception? Failure { get; init; }

        public TaskCompletionSource? Hold { get; init; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }

            if (Failure is { } failure)
            {
                throw failure;
            }

            return new AccessToken("synthetic-token", DateTimeOffset.UtcNow.AddHours(1));
        }
    }
}
