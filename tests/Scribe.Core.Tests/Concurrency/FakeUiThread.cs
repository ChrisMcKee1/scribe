using System.Collections.Concurrent;

namespace Scribe.Core.Tests.Concurrency;

/// <summary>
/// A stand-in for the WPF dispatcher: one thread that runs posted work in the order it was posted, and runs nothing else
/// while one item is running, exactly as the UI thread runs nothing posted while it works through the app's exit.
/// </summary>
internal sealed class FakeUiThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly ConcurrentQueue<Exception> _faults = new();
    private readonly Thread _thread;

    public FakeUiThread()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "fake UI thread" };
        _thread.Start();
    }

    public Thread Thread => _thread;

    public bool IsCurrent => Thread.CurrentThread == _thread;

    /// <summary>Exceptions thrown by posted work, which the pump keeps running past.</summary>
    public IReadOnlyList<Exception> Faults => [.. _faults];

    /// <summary>Queues work and returns at once, like Dispatcher.BeginInvoke.</summary>
    public void Post(Action work) => _queue.Add(work);

    /// <summary>Queues work and waits for it to have run, like Dispatcher.Invoke from another thread.</summary>
    public void Invoke(Action work)
    {
        using var done = new ManualResetEventSlim();
        _queue.Add(() =>
        {
            try
            {
                work();
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
    }

    /// <summary>
    /// Returns once everything posted before this call has run, failing loudly instead of hanging if the thread is stuck.
    /// </summary>
    public void Drain()
    {
        using var reached = new ManualResetEventSlim();
        _queue.Add(reached.Set);
        if (!reached.Wait(BlockedThreads.SafetyTimeout))
        {
            throw new TimeoutException("The fake UI thread never reached the end of its queue.");
        }
    }

    public void Dispose() => _queue.CompleteAdding();

    private void Pump()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                _faults.Enqueue(ex);
            }
        }
    }
}
