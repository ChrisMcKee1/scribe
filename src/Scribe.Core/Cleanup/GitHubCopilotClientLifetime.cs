using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Cleanup;

/// <summary>
/// The service's owner of its shared Copilot client. Referenced only from the Copilot initialization path, so the
/// service's fields and method signatures can keep their SDK-free disposable handle. Agents still receive the client.
/// </summary>
/// <remarks>
/// Closes operation admission immediately, then waits at most two seconds for admitted SDK work to drain, two for
/// graceful stop, two for force stop when needed, and two for final SDK disposal: eight seconds of waits in total.
/// A failed drain leaves the client untouched for process exit, rather than allowing an admitted SDK call to restart a
/// client already stopped. This bounds the owner's wait, not the runtime's eventual exit or data deletion.
/// Pending SDK tasks remain observed and keep the resources they still use until their own cleanup ends.
/// SDK 1.0.14's DisposeAsync calls StopAsync, and StopAsync has no overall deadline. ForceStopAsync is its supported
/// fallback, but cannot take a connection graceful stop already claimed: CleanupConnectionAsync clears the client's
/// connection task before awaiting cleanup. Such cleanup stays with the first call, including its native waits.
/// </remarks>
internal sealed class GitHubCopilotClientLifetime : IAsyncDisposable
{
    internal static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan ShutdownWaitBound = DrainTimeout + StepTimeout * 3;

    private readonly Func<Task> _stop;
    private readonly Func<Task> _forceStop;
    private readonly Func<ValueTask> _dispose;
    private readonly ILogger? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Lazy<Task> _shutdown;
    private readonly CleanupOperationTracker _operations = new();

    internal int ActiveOperations => _operations.ActiveCount;
    internal bool IsClosing => _operations.IsClosed;

    internal GitHubCopilotClientLifetime(CopilotClient client, ILogger? logger = null, TimeProvider? timeProvider = null)
        : this(client.StopAsync, client.ForceStopAsync, client.DisposeAsync, logger, timeProvider)
    {
    }

    // The delegates pin phase ordering, timeouts and late failures without a process or real provider.
    internal GitHubCopilotClientLifetime(
        Func<Task> stop,
        Func<Task> forceStop,
        Func<ValueTask> dispose,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(forceStop);
        ArgumentNullException.ThrowIfNull(dispose);
        _stop = stop;
        _forceStop = forceStop;
        _dispose = dispose;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _shutdown = new Lazy<Task>(() => Task.Run(ShutdownAsync));
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            // Close on the requesting thread, not the eventual worker. A queued cleanup cannot be admitted after this.
            _operations.Close();
            return new ValueTask(_shutdown.Value);
        }
        catch (Exception ex)
        {
            TryLogFailure("schedule", FailureShape.Describe(ex));
            return ValueTask.CompletedTask;
        }
    }

    private async Task ShutdownAsync()
    {
        var draining = _operations.Close();
        if (!draining.IsCompletedSuccessfully)
        {
            try
            {
                await draining.WaitAsync(DrainTimeout, _timeProvider, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                TryLogFailure("drain-kept-for-process-exit", FailureShape.Describe(ex));
                // A timeout of the caller is not the end of SDK work: EnsureConnectedAsync can still start a child.
                // Never stop or dispose beneath that work. This is the service's existing LeftToProcessExit policy.
                return;
            }
        }

        if (!await RunStepAsync(_stop, "stop").ConfigureAwait(false))
        {
            await RunStepAsync(_forceStop, "force").ConfigureAwait(false);
        }

        // Dispose marks the SDK client disposed. Do not call it first: it would enter its own unbounded StopAsync.
        await RunStepAsync(() => _dispose().AsTask(), "dispose").ConfigureAwait(false);
    }

    internal Task RunAsync(Func<Task> start)
    {
        var lease = _operations.TryEnter();
        if (lease is null)
        {
            return Task.FromException(new ObjectDisposedException(nameof(GitHubCopilotClientLifetime)));
        }

        try
        {
            return AwaitOwnedAsync(start(), lease);
        }
        catch (Exception ex)
        {
            lease.Dispose();
            return Task.FromException(ex);
        }
    }

    internal Task<T> RunAsync<T>(Func<Task<T>> start)
    {
        var lease = _operations.TryEnter();
        if (lease is null)
        {
            return Task.FromException<T>(new ObjectDisposedException(nameof(GitHubCopilotClientLifetime)));
        }

        try
        {
            return AwaitOwnedAsync(start(), lease);
        }
        catch (Exception ex)
        {
            lease.Dispose();
            return Task.FromException<T>(ex);
        }
    }

    private static async Task AwaitOwnedAsync(Task work, CleanupOperationTracker.Lease lease)
    {
        using (lease)
        {
            await work.ConfigureAwait(false);
        }
    }

    private static async Task<T> AwaitOwnedAsync<T>(Task<T> work, CleanupOperationTracker.Lease lease)
    {
        using (lease)
        {
            return await work.ConfigureAwait(false);
        }
    }

    private async Task<bool> RunStepAsync(Func<Task> start, string step)
    {
        Task? work = null;
        try
        {
            using var bound = new CancellationTokenSource(StepTimeout, _timeProvider);
            // SDK methods also do synchronous work before their first await. That must not block the disposing caller
            // or prevent the step's timer from bounding its wait.
            work = Task.Run(start);
            await work.WaitAsync(bound.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            TryLogFailure(step, FailureShape.Describe(ex));
            if (work is not null && !work.IsCompleted)
            {
                ObserveLateFailure(work, step);
            }
            else if (work is not null)
            {
                _ = work.Exception;
            }

            return false;
        }
    }

    private void ObserveLateFailure(Task work, string step) =>
        _ = work.ContinueWith(
            completed => TryLogLateFailure(step, FailureShape.Describe(completed.Exception)),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void TryLogFailure(string step, string shape)
    {
        try
        {
            _logger?.LogWarning("Copilot client shutdown {Step} did not finish successfully: {FailureShape}.", step, shape);
        }
        catch (Exception)
        {
            // Diagnostics cannot stop the force fallback or final disposal.
        }
    }

    private void TryLogLateFailure(string step, string shape)
    {
        try
        {
            _logger?.LogWarning("Copilot client shutdown {Step} completed late with a failure: {FailureShape}.", step, shape);
        }
        catch (Exception)
        {
            // Observing a late SDK failure must not create an unobserved failure of its own.
        }
    }
}
