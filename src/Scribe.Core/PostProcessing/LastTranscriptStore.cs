using Scribe.Core.Models;

namespace Scribe.Core.PostProcessing;

/// <summary>Keeps the last finalized dictation available for explicit clipboard recovery.</summary>
public sealed class LastTranscriptStore
{
    private readonly object _gate = new();
    private string? _text;

    public void Set(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_gate)
        {
            _text = text;
        }
    }

    public string? Get(IEnumerable<HistoryEntry>? fallbackHistory = null)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(_text))
            {
                return _text;
            }
        }

        return fallbackHistory?
            .Select(entry => entry.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
    }
}