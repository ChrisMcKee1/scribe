using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// Turns a sample of recent dictation into dictionary suggestions with the help of the user's
/// configured AI model. Where <see cref="DictionarySuggestionMiner"/> only spots repeated, already
/// correctly spelled jargon, this asks the model to reason about the surrounding context and how each
/// term is <i>spoken</i> versus how it should be <i>written</i> (phrases speech recognition or an
/// earlier rewrite got wrong in context, acronyms said letter by letter, phonetic mishears, casing),
/// which is exactly what a spoken-form to written-form dictionary needs. Prompt building
/// and response parsing are pure and testable; the model call itself is made by the caller through
/// <c>ITextCleanupService.CompleteAsync</c>, which dash-normalizes its answer before returning, so
/// suggested written forms arrive free of em/en dashes. That only ever rewrites the value inside a
/// JSON string, never a structural character, so parsing here is unaffected.
/// </summary>
public static class AiDictionarySuggester
{
    /// <summary>Default cap on characters of history sent to the model, to bound cost and latency.</summary>
    public const int DefaultMaxSampleChars = 6000;

    /// <summary>Default cap on suggestions accepted from a single response.</summary>
    public const int DefaultMaxSuggestions = 25;

    /// <summary>Cap on suggestions accepted when scanning full history and vocabulary in a large context window.</summary>
    public const int FullContextMaxSuggestions = 100;

    /// <summary>Maximum output token budget reserved when scanning full history in a large context window.</summary>
    public const int FullContextMaxOutputTokens = 8192;

    // A single oversized term is data, not a rule; cap it so one bad row can't bloat the grid.
    private const int MaxTermChars = 80;
    private const int MaxScanCandidates = 256;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// The system prompt that instructs the model to emit spoken/written pairs as JSON. Public so the
    /// caller passes it straight to <c>CompleteAsync</c> and so it can be asserted in tests.
    /// </summary>
    public const string SystemPrompt =
        "You help build a personal dictation dictionary. You are given a sample of text that a " +
        "speech-to-text system produced from one user's recent dictation. Read the surrounding sentence, " +
        "topic, and nearby dictations carefully: just because a phrase was transcribed or rewritten into " +
        "valid words does not mean it was written as the right term. Find the technical terms, product " +
        "names, brand names, acronyms, commands, and jargon in the sample that speech-to-text or an " +
        "earlier rewrite got wrong in context, or is likely to spell wrong, mis-case, or mishear.\n\n" +
        "For each such term (a short word or phrase, never a full sentence), output two fields:\n" +
        "- \"written\": the correct way to write the term in context (correct spelling, casing, punctuation, hyphenation).\n" +
        "- \"spoken\": how that term came out in the sample when misheard or mis-rewritten, or how it most " +
        "likely comes out of a speech-to-text system when the user says it out loud, written in plain " +
        "lowercase the way a recognizer transcribes the sound. For an acronym said letter by letter, use " +
        "single spaced letters (for example \"a p i m\" for APIM). For a term said as a word, use its " +
        "phonetic lowercase spelling (for example \"cube control\" for kubectl, \"post gres\" for " +
        "PostgreSQL). For a term that is only mis-cased, use the lowercase of the written form.\n\n" +
        "Rules:\n" +
        "- Only include terms that actually appear in the sample (either as the spoken mishearing or the written term).\n" +
        "- Prefer high-value, unambiguous terms backed by the surrounding context. Skip ordinary English " +
        "words, full sentences, and anything you are unsure about.\n" +
        "- If existing vocabulary rules are listed below, use them as supporting evidence for the user's " +
        "domain to infer what a failed or mis-rewritten dictation in the sample was meant to say. Do not " +
        "assume every existing rule is accurate: if an existing rule maps a spoken form to the wrong written " +
        "term or casing in context, include that spoken form with the corrected written form so the " +
        "dictionary can self-heal; skip only spoken forms whose existing written replacement is already right.\n" +
        "- \"spoken\" must be lowercase, and \"spoken\" must not be identical to \"written\".\n" +
        "- Return at most 25 items, fewer is fine.\n" +
        "- Output ONLY a JSON array of objects, each with exactly the keys \"spoken\" and \"written\". " +
        "No commentary, no code fences, no other text.\n\n" +
        "Example output:\n" +
        "[{\"spoken\":\"a p i m\",\"written\":\"APIM\"},{\"spoken\":\"cosmos db\",\"written\":\"Cosmos DB\"}," +
        "{\"spoken\":\"post gres\",\"written\":\"PostgreSQL\"}]";

    /// <summary>True when <paramref name="systemPrompt"/> is the Learn from history dictionary suggestion prompt.</summary>
    public static bool IsDictionarySuggestionPrompt(string? systemPrompt) =>
        systemPrompt is not null &&
        systemPrompt.AsSpan().TrimStart().StartsWith("You help build a personal dictation dictionary.", StringComparison.Ordinal);

    /// <summary>
    /// A full-context Learn from history request fitted into a model's context window: the system prompt
    /// (with the user's dictionary and permitted word packs), the user message (all distinct history that
    /// fits in the remaining context window), and the suggestion cap.
    /// </summary>
    public sealed record FullContextRequest(
        string SystemPrompt,
        string UserMessage,
        int MaxSuggestions,
        int HistoryEntriesIncluded,
        int VocabularyTermsIncluded);

    /// <summary>
    /// Builds a full-context Learn from history request that fits the user's personal dictionary, all
    /// AI-permitted word packs, and every distinct history entry that fits into <paramref name="contextTokens"/>.
    /// </summary>
    public static FullContextRequest BuildFullContextRequest(
        IEnumerable<HistoryEntry> entries,
        IReadOnlyList<DictionaryEntry> dictionaryEntries,
        IReadOnlyList<DictionaryEntry> wordPackEntries,
        int contextTokens)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(dictionaryEntries);
        ArgumentNullException.ThrowIfNull(wordPackEntries);

        var budgetContext = contextTokens > 0 ? ContextBudget.Sanitize(contextTokens) : ContextBudget.AssumedContextTokens;
        var maxSuggestions = budgetContext >= 32768 ? FullContextMaxSuggestions : DefaultMaxSuggestions;
        var outputCeiling = budgetContext >= 32768
            ? Math.Clamp(budgetContext / 8, 2048, FullContextMaxOutputTokens)
            : 2048;

        var basePrompt = maxSuggestions == DefaultMaxSuggestions
            ? SystemPrompt
            : SystemPrompt.Replace(
                "Return at most 25 items, fewer is fine.",
                $"Return at most {maxSuggestions} items, fewer is fine.",
                StringComparison.Ordinal);

        var personal = dictionaryEntries
            .Where(e => e is { Enabled: true } && !string.IsNullOrWhiteSpace(e.Pattern))
            .OrderBy(e => e.Pattern, SqliteBinaryCollation.Instance)
            .ToList();
        var composedVocabulary = CleanupPrompt.ComposeVocabulary(personal, wordPackEntries);
        var glossaryLines = CleanupPrompt.GlossaryLines(composedVocabulary);

        var totalAvailableTokens = Math.Max(0, ContextBudget.VocabularyTokensFor(budgetContext, basePrompt, outputCeiling));
        var minHistoryReserve = Math.Min(
            Math.Max(1, totalAvailableTokens / 2),
            TokenEstimate.Transcript(new string('a', DefaultMaxSampleChars)));
        var vocabularyBudget = Math.Max(0, totalAvailableTokens - minHistoryReserve);
        var fittedVocabulary = glossaryLines.Count == 0
            ? []
            : CleanupPrompt.TakeWhileFits(
                glossaryLines,
                (long)vocabularyBudget - CleanupPrompt.GlossaryHeaderTokens,
                int.MaxValue);

        var fullSystemPrompt = fittedVocabulary.Count == 0
            ? basePrompt
            : string.Concat(basePrompt, "\n\n", CleanupPrompt.RenderGlossary(fittedVocabulary));

        var historyTokenBudget = Math.Max(0, ContextBudget.VocabularyTokensFor(budgetContext, fullSystemPrompt, outputCeiling));
        var (userMessage, historyCount) = BuildHistorySampleForTokens(entries, historyTokenBudget);
        return new FullContextRequest(
            fullSystemPrompt,
            userMessage,
            maxSuggestions,
            historyCount,
            fittedVocabulary.Count);
    }

    private static (string Sample, int IncludedCount) BuildHistorySampleForTokens(
        IEnumerable<HistoryEntry> entries,
        int maxTokens)
    {
        if (maxTokens <= 0)
        {
            return (string.Empty, 0);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        var count = 0;
        var usedTokens = TokenEstimate.ShortTextAllowance;
        foreach (var entry in entries)
        {
            var text = entry?.Text?.Trim();
            if (string.IsNullOrEmpty(text) || !seen.Add(text))
            {
                continue;
            }

            var lineTokens = TokenEstimate.Prose(text) + (sb.Length > 0 ? 1 : 0);
            if (sb.Length == 0 && usedTokens + lineTokens > maxTokens)
            {
                var maxChars = Math.Max(1, (int)((maxTokens - TokenEstimate.ShortTextAllowance) * TokenEstimate.ProseCharsPerToken));
                return (text.Length <= maxChars ? text : text[..maxChars], 1);
            }

            if (usedTokens + lineTokens > maxTokens)
            {
                break;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(text);
            usedTokens += lineTokens;
            count++;
        }

        return (sb.ToString(), count);
    }

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

        var knownWrittenBySpoken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in existing)
        {
            if (entry is not null && !string.IsNullOrWhiteSpace(entry.Pattern))
            {
                var pattern = Normalize(entry.Pattern);
                if (pattern.Length > 0)
                {
                    knownWrittenBySpoken.TryAdd(pattern, Normalize(entry.Replacement));
                }
            }
        }

        var raw = TryDeserializeSuggestions(response);
        if (raw is null || raw.Count == 0)
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

            // Identical spoken/written is a no-op; a term already in the dictionary with the same written
            // replacement is a duplicate (whereas a different written replacement self-heals the rule); a
            // spoken form already accepted in this batch is a repeat.
            if (string.Equals(spoken, written, StringComparison.Ordinal) ||
                (knownWrittenBySpoken.TryGetValue(spoken, out var existingWritten) &&
                 string.Equals(existingWritten, written, StringComparison.Ordinal)) ||
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

    // Pulls the first valid JSON array of suggestions out of the response, ignoring any prose, code
    // fences, or think-block wrapping (including brackets like "[1]" inside preamble prose or before a
    // bare "</think>"), and falls back to collecting individual {"spoken":...,"written":...} objects when
    // a model emits objects without a complete outer array.
    private static List<RawSuggestion>? TryDeserializeSuggestions(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        var cleaned = StripThinking(response);
        var candidatesChecked = 0;
        for (var searchFrom = 0; searchFrom < cleaned.Length && candidatesChecked < MaxScanCandidates;)
        {
            var start = cleaned.IndexOf('[', searchFrom);
            if (start < 0)
            {
                break;
            }

            candidatesChecked++;
            var candidate = ExtractBalancedSlice(cleaned, start, '[', ']');
            if (candidate is not null)
            {
                try
                {
                    var list = JsonSerializer.Deserialize<List<RawSuggestion>>(candidate, JsonOptions);
                    if (list is not null &&
                        list.Any(item => item is not null && (item.Spoken is not null || item.Written is not null)))
                    {
                        return list;
                    }
                }
                catch (JsonException)
                {
                    // A bracketed note in prose (e.g. "[1]"); keep scanning for the real JSON array.
                }
            }

            searchFrom = start + 1;
        }

        List<RawSuggestion>? collected = null;
        candidatesChecked = 0;
        for (var searchFrom = 0; searchFrom < cleaned.Length && candidatesChecked < MaxScanCandidates;)
        {
            var start = cleaned.IndexOf('{', searchFrom);
            if (start < 0)
            {
                break;
            }

            candidatesChecked++;
            var candidate = ExtractBalancedSlice(cleaned, start, '{', '}');
            if (candidate is not null)
            {
                try
                {
                    if (JsonSerializer.Deserialize<RawSuggestion>(candidate, JsonOptions) is { } single &&
                        (single.Spoken is not null || single.Written is not null))
                    {
                        collected ??= [];
                        collected.Add(single);
                        searchFrom = start + candidate.Length;
                        continue;
                    }
                }
                catch (JsonException)
                {
                }
            }

            searchFrom = start + 1;
        }

        return collected;
    }

    private static string StripThinking(string response)
    {
        var lastClose = response.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        return lastClose >= 0 ? response[(lastClose + "</think>".Length)..] : response;
    }

    private static string? ExtractBalancedSlice(string response, int start, char open, char close)
    {
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

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == open)
            {
                depth++;
            }
            else if (ch == close)
            {
                depth--;
                if (depth == 0)
                {
                    return response[start..(i + 1)];
                }
            }
        }

        return null;
    }

    // Flattens control characters (which could smuggle extra lines) to spaces, collapses runs of
    // whitespace, and trims; dictionary data, never prompt instructions.
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
