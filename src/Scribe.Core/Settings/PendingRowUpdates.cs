namespace Scribe.Core.Settings;

/// <summary>
/// Optimistic per-row updates whose writes run off the UI thread, such as a history rating.
/// </summary>
/// <remarks>
/// <para>
/// The row shows the new value as soon as it is clicked, and a second click on the same row is
/// ignored until the write finishes, so two writes for one row can never race each other to the
/// database in the wrong order.
/// </para>
/// <para>
/// Rows are identified by a stable key, never a list position: the list can be reloaded while a
/// write is running, and a reload that read storage before the write landed would otherwise flash
/// the old value back. <see cref="Resolve"/> gives such a row the pending value instead. A reload
/// that only publishes after the write has completed is no longer covered here, so the owner
/// restarts it (see <see cref="SettingsSectionLoad.Invalidate"/>).
/// </para>
/// <para>
/// Not thread-safe by design: the Settings window drives it from its dispatcher thread.
/// </para>
/// </remarks>
public sealed class PendingRowUpdates<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, (TValue Previous, TValue Next)> _pending = new();

    public int Count => _pending.Count;

    /// <summary>Starts an update for one row. False when that row already has one running.</summary>
    public bool TryBegin(TKey key, TValue previous, TValue next) => _pending.TryAdd(key, (previous, next));

    public bool IsPending(TKey key) => _pending.ContainsKey(key);

    /// <summary>The value a freshly loaded row should show while its update is still running.</summary>
    public TValue Resolve(TKey key, TValue loaded) =>
        _pending.TryGetValue(key, out var update) ? update.Next : loaded;

    /// <summary>
    /// Ends the update for <paramref name="key"/> and returns the value the row should show: the
    /// new value when the write succeeded, the value from before the click when it failed.
    /// </summary>
    public TValue Complete(TKey key, bool succeeded)
    {
        if (!_pending.Remove(key, out var update))
        {
            throw new InvalidOperationException("No update is pending for that row.");
        }

        return succeeded ? update.Next : update.Previous;
    }
}
