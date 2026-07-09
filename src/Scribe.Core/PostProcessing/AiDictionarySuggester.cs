using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scribe.Core.Models;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// Turns a sample of recent dictation into dictionary suggestions with the help of the user's
/// configured AI model. Where <see cref="DictionarySuggestionMiner"/> only spots repeated, already
/// correctly spelled jargon, this asks the model to reason about how each term is <i>spoken</i>
/// versus how it should be <i>written</i> (acronyms said letter by letter, phonetic mishears,
/// casing), which is exactly what a spoken-form to written-form dictionary needs. Prompt building
/// and response parsing are pure and testable; the model call itself is made by the caller through
/// <c>ITextCleanupService.CompleteAsync</c>.
/// </summary>
public static class AiDictionarySuggester
{
    /// <summary>Default cap on characters of history sent to the model, to bound cost and latency.</summary>
    public const int DefaultMaxSampleChars = 6000;

    /// <summary>Default cap on suggestions accepted from a single response.</summary>
    public const int DefaultMaxSuggestions = 25;

    // A single oversized term is data, not a rule; cap it so one bad row can't bloat the grid.
    private const int MaxTermChars = 80;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The system prompt that instructs the model to emit spoken/written pairs as JSON. Public so the
    /// caller passes it straight to <c>CompleteAsync</c> and so it can be asserted in tests.
    /// </summary>
    public const string SystemPrompt =
        "You help build a personal dictation dictionary. You are given a sample of text that a " +
        "speech-to-text system produced from one user's recent dictation. Find the technical terms, " +
        "product names, brand names, acronyms, commands, and jargon in the sample that a speech-to-text " +
        "system is likely to spell wrong, mis-case, or mishear.\n\n" +
        "For each such term, output two fields:\n" +
        "- \"written\": the correct way to write it (correct spelling, casing, punctuation, hyphenation).\n" +
        "- \"spoken\": how that term most likely comes out of a speech-to-text system when the user says " +
        "it out loud, written in plain lowercase the way a recognizer transcribes the sound. For an " +
        "acronym said letter by letter, use single spaced letters (for example \"a p i m\" for APIM). " +
        "For a term said as a word, use its phonetic lowercase spelling (for example \"cube control\" " +
        "for kubectl, \"post gres\" for PostgreSQL). For a term that is only mis-cased, use the lowercase " +
        "of the written form.\n\n" +
        "Rules:\n" +
        "- Only include terms that actually appear in the sample.\n" +
        "- Prefer high-value, unambiguous terms. Skip ordinary English words and anything you are unsure about.\n" +
        "- \"spoken\" must be lowercase, and \"spoken\" must not be identical to \"written\".\n" +
        "- Return at most 25 items, fewer is fine.\n" +
        "- Output ONLY a JSON array of objects, each with exactly the keys \"spoken\" and \"written\". " +
        "No commentary, no code fences, no other text.\n\n" +
        "Example output:\n" +
        "[{\"spoken\":\"a p i m\",\"written\":\"APIM\"},{\"spoken\":\"cosmos db\",\"written\":\"Cosmos DB\"}," +
        "{\"spoken\":\"post gres\",\"written\":\"PostgreSQL\"}]";

    /// <summary>
    /// Builds the user message: a newline-separated sample of the most recent distinct dictations,
    /// bounded to <paramref name="maxChars"/>. Duplicates are dropped so repeated phrases don't crowd
    /// out variety. Returns an empty string when there is nothing to learn from.
    /// </summary>
    public static string BuildHistorySample(IEnumerable<HistoryEntry> entries, int maxChars = DefaultMaxSampleChars)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            var text = entry?.Text?.Trim();
            if (string.IsNullOrEmpty(text) || !seen.Add(text))
            {
                continue;
            }

            if (sb.Length == 0 && text.Length >= maxChars)
            {
                // A single dictation already fills the budget: take a prefix so the model still gets data.
                return text[..maxChars];
            }

            if (sb.Length + text.Length + 1 > maxChars)
            {
                break;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(text);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses the model's response into dictionary entries (spoken form to written form), tolerating
    /// prose, code fences, or think-blocks around the JSON array. Entries are cleaned, de-duplicated by
    /// spoken form, filtered against <paramref name="existing"/> (so a term already in the dictionary is
    /// never re-suggested), and capped at <paramref name="maxSuggestions"/>. Never throws; a response
    /// that cannot be parsed yields an empty list.
    /// </summary>
    public static IReadOnlyList<DictionaryEntry> ParseSuggestions(
        string? response,
        IEnumerable<DictionaryEntry> existing,
        int maxSuggestions = DefaultMaxSuggestions)
    {
        ArgumentNullException.ThrowIfNull(existing);

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in existing)
        {
            if (!string.IsNullOrWhiteSpace(entry.Pattern))
            {
                known.Add(entry.Pattern.Trim());
            }
        }

        var json = ExtractJsonArray(response);
        if (json is null)
        {
            return [];
        }

        List<RawSuggestion>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<List<RawSuggestion>>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return [];
        }

        if (raw is null)
        {
            return [];
        }

        var results = new List<DictionaryEntry>();
        var takenSpoken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in raw)
        {
            var spoken = Normalize(item?.Spoken);
            var written = Normalize(item?.Written);
            if (spoken.Length == 0 || written.Length == 0 ||
                spoken.Length > MaxTermChars || written.Length > MaxTermChars)
            {
                continue;
            }

            // Identical spoken/written is a no-op; a term already in the dictionary is a duplicate; a
            // spoken form already accepted in this batch is a repeat.
            if (string.Equals(spoken, written, StringComparison.Ordinal) ||
                known.Contains(spoken) ||
                !takenSpoken.Add(spoken))
            {
                continue;
            }

            results.Add(new DictionaryEntry(0, spoken, written));
            if (results.Count >= maxSuggestions)
            {
                break;
            }
        }

        return results;
    }

    // Pulls the first JSON array out of the response, ignoring any prose, code fences, or think-block
    // wrapping the model may add. Balanced-bracket scan (not just last ']') so trailing chatter after
    // the array doesn't swallow unrelated brackets.
    private static string? ExtractJsonArray(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        var start = response.IndexOf('[');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < response.Length; i++)
        {
            var ch = response[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    break;
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    if (depth == 0)
                    {
                        return response[start..(i + 1)];
                    }

                    break;
            }
        }

        return null;
    }

    // Flattens control characters (which could smuggle extra lines) to spaces, collapses runs of
    // whitespace, and trims — dictionary data, never prompt instructions.
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value)
        {
            var c = char.IsControl(ch) || ch == '\t' ? ' ' : ch;
            if (c == ' ')
            {
                if (lastWasSpace)
                {
                    continue;
                }

                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    private sealed record RawSuggestion(
        [property: JsonPropertyName("spoken")] string? Spoken,
        [property: JsonPropertyName("written")] string? Written);
}
