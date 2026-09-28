namespace Scribe.Core.Persistence;

/// <summary>
/// Runs an action at most once, and holds every other caller until that run has returned. The run counts from the moment
/// it starts: an action that throws is not run again, and the callers after it go on without it, as the SQLite provider's
/// initialization always has (<see cref="ScribeDatabase"/>).
/// </summary>
internal sealed class RunOnce
{
    private readonly Lock _gate = new();
    private bool _started;

    /// <summary>Whether the action has been started (and, once this returns to another caller, has returned).</summary>
    internal bool Started
    {
        get
        {
            lock (_gate)
            {
                return _started;
            }
        }
    }

    internal void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            action();
        }
    }
}
