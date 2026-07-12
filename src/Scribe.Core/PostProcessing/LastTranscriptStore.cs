using Scribe.Core.Models;

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

    // Most recent first. A plain list is fine at this size: inserts shift at most Capacity items.
    private readonly List<string> _entries = new(Capacity);

    public void Set(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_gate)
        {
            // Re-dictating identical text must not burn ring slots on adjacent duplicates: the
            // transcript is already recoverable at the top of the list, so keep it there and
            // preserve the older, distinct entries beneath it.
            if (_entries.Count > 0 && string.Equals(_entries[0], text, StringComparison.Ordinal))
            {
                return;
            }

            _entries.Insert(0, text);
            if (_entries.Count > Capacity)
            {
                _entries.RemoveAt(_entries.Count - 1);
            }
        }
    }

    public string? Get(IEnumerable<HistoryEntry>? fallbackHistory = null)
    {
        lock (_gate)
        {
            if (_entries.Count > 0)
            {
                return _entries[0];
            }
        }

        return fallbackHistory?
            .Select(entry => entry.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
    }

    /// <summary>
    /// Returns an immutable snapshot of the retained transcripts, most recent first.
    /// The snapshot never changes after it is returned, even if more dictations arrive.
    /// </summary>
    public IReadOnlyList<string> GetRecent()
    {
        lock (_gate)
        {
            return _entries.ToArray();
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
