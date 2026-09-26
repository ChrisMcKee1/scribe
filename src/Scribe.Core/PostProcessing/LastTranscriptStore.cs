using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// Keeps the last few finalized dictations available for explicit clipboard recovery.
/// A bounded ring of <see cref="Capacity"/> transcripts, most recent first, so a dictation
/// lost to a failed injection (or overwritten clipboard) stays recoverable from the tray.
/// </summary>
public sealed class LastTranscriptStore
{
    /// <summary>How many finalized transcripts are retained for recovery.</summary>
    public const int Capacity = 5;

    /// <summary>Preview length budget for the tray submenu, including the trailing ellipsis.</summary>
    public const int PreviewLength = 42;

    private readonly object _gate = new();
    private readonly Func<long> _currentRevision;

    public LastTranscriptStore()
        : this(() => 0)
    {
    }

    public LastTranscriptStore(HistoryDeletionNotifier deletionNotifier)
        : this(() => deletionNotifier.Revision)
    {
    }

    internal LastTranscriptStore(Func<long> currentRevision)
    {
        _currentRevision = currentRevision;
    }

    public sealed record RetainedTranscript(Guid Id, string HistoryText, string Text, DateTimeOffset TimestampUtc, long AddedAtRevision);

    private sealed record Slot(Guid Id, string HistoryText, string Current, DateTimeOffset TimestampUtc, long AddedAtRevision);

    // Most recent first. A plain list is fine at this size: inserts shift at most Capacity items.
    private readonly List<Slot> _entries = new(Capacity);

    public void Set(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_gate)
        {
            // Re-dictating identical text still represents a new capture. Keep one top slot for adjacent
            // duplicates, but refresh its deletion revision and timestamp so a late history notice for the
            // earlier capture cannot remove the new tray copy.
            if (_entries.Count > 0 && string.Equals(_entries[0].Current, text, StringComparison.Ordinal))
            {
                _entries[0] = new Slot(Guid.NewGuid(), text, text, DateTimeOffset.UtcNow, _currentRevision());
                return;
            }

            _entries.Insert(0, new Slot(Guid.NewGuid(), text, text, DateTimeOffset.UtcNow, _currentRevision()));
            if (_entries.Count > Capacity)
            {
                _entries.RemoveAt(_entries.Count - 1);
            }
        }
    }

    /// <summary>
    /// Rewrites the retained transcripts that exactly match <paramref name="original"/>.
    ///
    /// Keyed by content rather than by position on purpose: a dictation finishing while the quick
    /// add popup is open shifts every index in the ring, and an index-based update would then
    /// silently overwrite somebody else's transcript. If the original has already been evicted,
    /// nothing happens.
    ///
    /// Every match is rewritten, not just the first. The ring can legitimately hold the same text
    /// twice (Set only collapses an immediate repeat), and identical text deserves an identical
    /// correction, so rewriting one and leaving its twin stale would just look broken.
    ///
    /// This is deliberately an update rather than an insert, and it never removes an entry. Pushing
    /// the corrected text on as a new entry, or dropping one that a correction made redundant, would
    /// cost the user a recovery slot as the price of a spelling fix.
    /// </summary>
    /// <returns>True when at least one transcript was rewritten.</returns>
    public bool Update(string? original, string? updated)
    {
        // An entirely emptied transcript is rejected rather than stored: there would be nothing left
        // to recover, and removing the slot instead would break the no-eviction guarantee above.
        if (string.IsNullOrWhiteSpace(original)
            || string.IsNullOrWhiteSpace(updated)
            || string.Equals(original, updated, StringComparison.Ordinal))
        {
            return false;
        }

        lock (_gate)
        {
            var changed = false;
            for (var i = 0; i < _entries.Count; i++)
            {
                if (string.Equals(_entries[i].Current, original, StringComparison.Ordinal))
                {
                    _entries[i] = _entries[i] with { Current = updated };
                    changed = true;
                }
            }

            return changed;
        }
    }

    /// <summary>
    /// Fills an empty ring from durable history so the transcripts a user can act on are the same
    /// ones that can be repaired. Without this, everything shown after a restart comes from history
    /// while <see cref="Update"/> searches an empty ring, so a correction would appear to save and
    /// then quietly fail to fix the dictation it came from.
    ///
    /// Live dictations already in the ring stay newest, and seeded entries fill the older slots without duplicates.
    /// </summary>
    public void Seed(IEnumerable<string>? transcripts)
    {
        if (transcripts is null)
        {
            return;
        }

        lock (_gate)
        {
            var liveCount = _entries.Count;
            foreach (var text in transcripts)
            {
                if (string.IsNullOrWhiteSpace(text) || _entries.Count >= Capacity)
                {
                    continue;
                }

                if (_entries.Take(liveCount).Any(entry => string.Equals(entry.Current, text, StringComparison.Ordinal) || string.Equals(entry.HistoryText, text, StringComparison.Ordinal)))
                {
                    continue;
                }

                _entries.Add(new Slot(Guid.NewGuid(), text, text, DateTimeOffset.UtcNow, _currentRevision()));
            }
        }
    }

    public bool SeedHistory(IEnumerable<HistoryEntry>? entries, long capturedRevision, Func<long> currentRevision)
    {
        if (entries is null)
        {
            return true;
        }

        lock (_gate)
        {
            if (currentRevision() != capturedRevision)
            {
                return false;
            }

            var liveCount = _entries.Count;
            foreach (var history in entries)
            {
                if (string.IsNullOrWhiteSpace(history.Text) || _entries.Count >= Capacity)
                {
                    continue;
                }

                if (_entries.Take(liveCount).Any(entry => string.Equals(entry.Current, history.Text, StringComparison.Ordinal) || string.Equals(entry.HistoryText, history.Text, StringComparison.Ordinal)))
                {
                    continue;
                }

                _entries.Add(new Slot(Guid.NewGuid(), history.Text, history.Text, history.TimestampUtc, capturedRevision));
            }

            return true;
        }
    }

    public string? Get(IEnumerable<HistoryEntry>? fallbackHistory = null)
    {
        lock (_gate)
        {
            if (_entries.Count > 0)
            {
                return _entries[0].Current;
            }
        }

        return fallbackHistory?
            .Select(entry => entry.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
    }

    public Guid? CurrentId()
    {
        lock (_gate)
        {
            return _entries.Count == 0 ? null : _entries[0].Id;
        }
    }

    public string? Get(Guid id)
    {
        lock (_gate)
        {
            return _entries.FirstOrDefault(entry => entry.Id == id)?.Current;
        }
    }


    public IReadOnlyList<Guid> ApplyDeletion(HistoryDeletion deletion)
    {
        lock (_gate)
        {
            var removed = _entries
                .Where(entry => HistoryDeletionNotifier.Covers(deletion, entry.HistoryText, entry.TimestampUtc, entry.AddedAtRevision))
                .Select(entry => entry.Id)
                .ToArray();
            _entries.RemoveAll(entry => removed.Contains(entry.Id));
            return removed;
        }
    }

    /// <summary>Removes every retained transcript.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    /// <summary>Removes every retained copy of one transcript.</summary>
    public IReadOnlyList<Guid> Forget(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return ApplyDeletion(new HistoryDeletion(HistoryDeletionKind.Entry, new HistoryEntry(0, DateTimeOffset.UtcNow, text, 0, 0), Revision: long.MaxValue));
    }

    public IReadOnlyList<Guid> ForgetOlderThan(DateTimeOffset cutoffUtc) =>
        ApplyDeletion(new HistoryDeletion(HistoryDeletionKind.OlderThan, CutoffUtc: cutoffUtc));

    /// <summary>
    /// Returns an immutable snapshot of the retained transcripts, most recent first.
    /// The snapshot never changes after it is returned, even if more dictations arrive.
    /// </summary>
    public IReadOnlyList<string> GetRecent()
    {
        lock (_gate)
        {
            return _entries.Select(entry => entry.Current).ToArray();
        }
    }

    public IReadOnlyList<RetainedTranscript> GetRecentEntries()
    {
        lock (_gate)
        {
            return _entries.Select(entry => new RetainedTranscript(entry.Id, entry.HistoryText, entry.Current, entry.TimestampUtc, entry.AddedAtRevision)).ToArray();
        }
    }

    /// <summary>
    /// Renders a transcript as a single-line menu preview: all whitespace runs (including line
    /// breaks) collapse to single spaces, the result is trimmed, and anything longer than
    /// <paramref name="maxLength"/> is truncated so the ellipsis fits inside the budget.
    /// </summary>
    public static string FormatPreview(string? text, int maxLength = PreviewLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Split on any whitespace and rejoin: collapses CRLF, tabs and double spaces in one pass
        // so multi-paragraph dictations render as a single readable menu row.
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length <= maxLength)
        {
            return collapsed;
        }

        // Never cut between the halves of a surrogate pair (emoji in a dictation): a trailing lone
        // high surrogate is invalid UTF-16 and renders as a broken glyph in the menu header.
        var cut = maxLength - 1;
        if (char.IsHighSurrogate(collapsed[cut - 1]))
        {
            cut--;
        }

        return collapsed[..cut] + '…';
    }
}
