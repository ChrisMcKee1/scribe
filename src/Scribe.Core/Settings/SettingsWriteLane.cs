using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Settings;

/// <summary>
/// Saves small settings changes, such as the tray's AI cleanup switch, on a worker thread, one at a time and in the
/// order they were asked for, and hands each stored result back on the owner's thread.
/// </summary>
/// <remarks>
/// <para>
/// A settings write can wait seconds for the database behind another connection's write, and the owner is the UI
/// thread, which must not freeze for that. Each change goes through <see cref="ISettingsRepository.Update"/>, so it
/// can never put back a stale copy of fields it did not touch.
/// </para>
/// <para>
/// Changes are written strictly one after another in submission order, and each result is posted to the owner before
/// the next change starts, so results reach the owner in the order the database produced them. The Settings window
/// still saves its whole document on the owner's thread, outside this lane. When it saves and applies a document while
/// a change here is on its way (<see cref="NoteExternalApply"/>), which of the two writes reached the database last is
/// unknown, so the lane reads the stored document again and hands back that instead: handing back its own copy could
/// undo the window's save. A result is therefore never older than a document the owner applied before receiving it.
/// </para>
/// <para>
/// A change submitted with a revision is never written over a newer choice either. When the window's save accounted
/// for it (the window had shown it, or the user set the switch after it) and committed first, the change is superseded
/// inside its own write transaction, and only the stored settings come back, marked so. Without the check, a change
/// that waited seconds for the database would land after the user's later save and silently undo it.
/// </para>
/// <para>
/// <see cref="Submit"/>, <see cref="NoteExternalApply"/> and every callback run on the owner's thread, the callbacks
/// through the post delegate. <see cref="Close"/> and <see cref="WaitForIdle"/> may be called from any thread.
/// Callbacks never throw into the owner: a result callback that throws is reported through the failure callback, and
/// a failure callback that throws is swallowed.
/// </para>
/// </remarks>
public sealed class SettingsWriteLane
{
    private readonly ISettingsRepository _repository;
    private readonly Action<Action> _postToOwner;
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;
    private bool _closed;

    // Owner thread only.
    private long _externalApplies;

    /// <param name="repository">Where the settings live.</param>
    /// <param name="postToOwner">Queues a callback onto the owner's thread without waiting for it to run.</param>
    public SettingsWriteLane(ISettingsRepository repository, Action<Action> postToOwner)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(postToOwner);
        _repository = repository;
        _postToOwner = postToOwner;
    }

    /// <summary>
    /// Queues <paramref name="mutate"/> as one atomic change of the stored settings. <paramref name="onSaved"/> then
    /// receives the settings as stored, which can include other writers' changes; <paramref name="onFailed"/> receives
    /// why nothing was saved, or why the stored result could not be handed back. Returns false, and queues nothing,
    /// once the lane is closed.
    /// </summary>
    public bool Submit(Action<AppSettings> mutate, Action<AppSettings> onSaved, Action<Exception> onFailed)
    {
        ArgumentNullException.ThrowIfNull(onSaved);
        return Queue(mutate, revision: null, (stored, _) => onSaved(stored), onFailed);
    }

    /// <summary>
    /// Queues a change asked for outside the settings window, with the revision it took when it was asked for, as one
    /// atomic change of the stored settings, unless a whole-document save that accounts for it commits first
    /// (<see cref="ISettingsRepository.Update(Action{AppSettings}, long, out bool)"/>). <paramref name="onStored"/> then
    /// receives the settings as stored, handed back the same way, and true when the change was superseded and never
    /// written. Returns false, and queues nothing, once the lane is closed.
    /// </summary>
    public bool Submit(
        Action<AppSettings> mutate, long revision, Action<AppSettings, bool> onStored, Action<Exception> onFailed) =>
        Queue(mutate, revision, onStored, onFailed);

    private bool Queue(
        Action<AppSettings> mutate, long? revision, Action<AppSettings, bool> onStored, Action<Exception> onFailed)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ArgumentNullException.ThrowIfNull(onStored);
        ArgumentNullException.ThrowIfNull(onFailed);

        var baseline = _externalApplies;
        return Enqueue(() =>
        {
            AppSettings stored;
            var superseded = false;
            try
            {
                stored = revision is { } asked
                    ? _repository.Update(mutate, asked, out superseded)
                    : _repository.Update(mutate);
            }
            catch (Exception ex)
            {
                Post(() => Fail(onFailed, ex));
                return;
            }

            Post(() => Deliver(stored, superseded, baseline, onStored, onFailed));
        });
    }

    /// <summary>
    /// Records that another writer has just saved and applied the whole settings document on the owner's thread.
    /// </summary>
    public void NoteExternalApply() => _externalApplies++;

    /// <summary>
    /// Stops admitting changes and stops handing back results, because the owner is going away. A write already under
    /// way still finishes, so a change the user made just before quitting is not lost. Idempotent.
    /// </summary>
    public void Close()
    {
        lock (_sync)
        {
            _closed = true;
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for every admitted change to finish writing.</summary>
    public bool WaitForIdle(TimeSpan timeout)
    {
        Task tail;
        lock (_sync)
        {
            tail = _tail;
        }

        return tail.Wait(timeout);
    }

    private bool IsClosed
    {
        get { lock (_sync) { return _closed; } }
    }

    private bool Enqueue(Action work)
    {
        lock (_sync)
        {
            if (_closed)
            {
                return false;
            }

            // Every step catches its own failures, so the chain itself never faults and one failed change can never
            // stop the ones queued behind it.
            _tail = _tail.ContinueWith(
                static (_, state) =>
                {
                    try
                    {
                        ((Action)state!)();
                    }
                    catch
                    {
                        // Each step reports its own failure; this only keeps the chain alive for the next one.
                    }
                },
                work,
                CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach,
                TaskScheduler.Default);
            return true;
        }
    }

    private void Deliver(
        AppSettings stored, bool superseded, long baseline, Action<AppSettings, bool> onStored, Action<Exception> onFailed)
    {
        if (IsClosed)
        {
            return;
        }

        if (baseline != _externalApplies)
        {
            var rereadBaseline = _externalApplies;
            Enqueue(() => Reread(superseded, rereadBaseline, onStored, onFailed));
            return;
        }

        try
        {
            onStored(stored, superseded);
        }
        catch (Exception ex)
        {
            Fail(onFailed, ex);
        }
    }

    private void Reread(bool superseded, long baseline, Action<AppSettings, bool> onStored, Action<Exception> onFailed)
    {
        AppSettings stored;
        try
        {
            stored = _repository.Load();
            if (_repository.LastLoadFailed)
            {
                // Load falls back to defaults for an unreadable document, and handing those back would apply them.
                throw new InvalidOperationException("The stored settings could not be read.");
            }
        }
        catch (Exception ex)
        {
            Post(() => Fail(onFailed, ex));
            return;
        }

        Post(() => Deliver(stored, superseded, baseline, onStored, onFailed));
    }

    private void Fail(Action<Exception> onFailed, Exception error)
    {
        if (IsClosed)
        {
            return;
        }

        try
        {
            onFailed(error);
        }
        catch
        {
            // Nothing is left to report to; the owner's own failure path failed.
        }
    }

    private void Post(Action callback)
    {
        try
        {
            _postToOwner(callback);
        }
        catch
        {
            // The owner's thread is gone, so there is nobody left to hand the result to.
        }
    }
}
