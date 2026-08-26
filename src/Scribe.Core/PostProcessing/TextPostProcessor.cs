using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.PostProcessing;

/// <inheritdoc cref="ITextPostProcessor"/>
public sealed partial class TextPostProcessor : ITextPostProcessor
{
    private readonly IDictionaryRepository _dictionary;
    private readonly ISnippetRepository? _snippets;
    private readonly IDictionaryLibraryService? _libraries;
    private readonly ILogger<TextPostProcessor> _logger;
    private readonly object _gate = new();

    private CompiledRule[] _rules = [];
    private SnippetRule[] _snippetRules = [];
    private bool _loaded;

    public TextPostProcessor(
        IDictionaryRepository dictionary,
        ILogger<TextPostProcessor> logger,
        ISnippetRepository? snippets = null,
        IDictionaryLibraryService? libraries = null)
    {
        _dictionary = dictionary;
        _snippets = snippets;
        _libraries = libraries;
        _logger = logger;
    }

    public string Process(string text) => ProcessDetailed(text).Text;

    public TextPostProcessingResult ProcessDetailed(string text, string? sourceText = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TextPostProcessingResult(string.Empty, []);
        }

        EnsureLoaded();

        // Normalize only the dictated source. Snippet templates are literal user content and may
        // intentionally contain tabs, indentation, aligned columns, or repeated spaces.
        text = NormalizeWhitespace(text);

        // Snippets expand first so their templates then benefit from dictionary canonicalization.
        // Each phase matches its original input once; generated text is never fed back through later
        // rules in the same phase.
        var snippetRules = Volatile.Read(ref _snippetRules);
        var snippetApplications = new List<TextReplacement>();
        text = ApplySinglePass(
            text,
            snippetRules.SelectMany((rule, order) => rule.Find(text, order)),
            snippetApplications,
            TextReplacementKind.Snippet);

        var rules = Volatile.Read(ref _rules);
        var replacements = new List<TextReplacement>();
        text = ApplySinglePass(
            text,
            rules.SelectMany((rule, order) => rule.Find(text, order)),
            replacements);

        var canonicalSnippets = snippetApplications.Select(application =>
        {
            var canonical = ApplySinglePass(
                application.Replacement,
                rules.SelectMany((rule, order) => rule.Find(application.Replacement, order)));
            return application with { Length = canonical.Length, Replacement = canonical };
        });
        AddLocatedReplacements(text, canonicalSnippets, replacements, replaceOverlaps: true);

        if (!string.IsNullOrWhiteSpace(sourceText))
        {
            var source = NormalizeWhitespace(sourceText);
            var glossaryApplications = new List<TextReplacement>();
            _ = ApplySinglePass(
                source,
                rules.SelectMany((rule, order) => rule.Find(source, order)),
                glossaryApplications);
            // ponytail: ordered text search covers cleanup canonicalization; add token alignment only
            // if models start reordering repeated glossary terms enough to make this misleading.
            AddLocatedReplacements(text, glossaryApplications, replacements, replaceOverlaps: false);
        }

        return new TextPostProcessingResult(
            text,
            replacements.OrderBy(replacement => replacement.Start).ToList());
    }

    public void Reload()
    {
        lock (_gate)
        {
            _rules = BuildRules();
            _snippetRules = BuildSnippets();
            _loaded = true;
        }
    }

    private void EnsureLoaded()
    {
        if (Volatile.Read(ref _loaded)) return;
        lock (_gate)
        {
            if (_loaded) return;
            _rules = BuildRules();
            _snippetRules = BuildSnippets();
            _loaded = true;
        }
    }

    // The effective rule set = the user's base dictionary plus any enabled libraries, de-duplicated
    // with the base winning on conflict. Libraries are optional and best-effort: a failure to load
    // them must never cost the user their base dictionary.
    private CompiledRule[] BuildRules()
    {
        var baseEntries = _dictionary.GetEnabled();
        var libraryEntries = SafeLibraryEntries();
        var effective = libraryEntries.Count == 0
            ? baseEntries
            : DictionaryLibraryComposer.Merge(baseEntries, libraryEntries);
        return Build(effective);
    }

    private IReadOnlyList<DictionaryEntry> SafeLibraryEntries()
    {
        if (_libraries is null)
        {
            return [];
        }

        try
        {
            return _libraries.GetEnabledLibraryEntries();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load enabled dictionary libraries; using the base dictionary only.");
            return [];
        }
    }

    private SnippetRule[] BuildSnippets()
    {
        if (_snippets is null)
        {
            return [];
        }

        var rules = new List<SnippetRule>();
        foreach (var snippet in _snippets.GetEnabled())
        {
            if (string.IsNullOrWhiteSpace(snippet.Phrase) || string.IsNullOrEmpty(snippet.Template))
            {
                continue;
            }

            try
            {
                rules.Add(new SnippetRule(snippet));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping invalid snippet {Id} ('{Phrase}').",
                    snippet.Id, snippet.Phrase);
            }
        }

        _logger.LogDebug("Post-processor loaded {Count} snippet(s).", rules.Count);
        return rules.ToArray();
    }

    private CompiledRule[] Build(IReadOnlyList<DictionaryEntry> entries)
    {
        var rules = new List<CompiledRule>(entries.Count);
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Pattern)) continue;
            try
            {
                rules.Add(new CompiledRule(entry));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping invalid dictionary entry {Id} ('{Pattern}').",
                    entry.Id, entry.Pattern);
            }
        }

        _logger.LogDebug("Post-processor loaded {Count} dictionary rule(s).", rules.Count);
        return rules.ToArray();
    }

    private static string NormalizeWhitespace(string text)
    {
        text = HorizontalWhitespace().Replace(text, " ");
        text = SpaceBeforePunctuation().Replace(text, "$1");
        return text.Trim();
    }

    /// <inheritdoc />
    public SelectionVocabularyResult ProcessSelection(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new SelectionVocabularyResult(text ?? string.Empty, []);
        }

        EnsureLoaded();

        // No NormalizeWhitespace and no snippet phase: see the interface remarks. The user is looking
        // at this text in their own document, so its indentation and blank lines are content.
        var rules = Volatile.Read(ref _rules);
        if (rules.Length == 0)
        {
            return new SelectionVocabularyResult(text, []);
        }

        var protectedSpans = ProtectedSpans(text);
        var replacements = new List<TextReplacement>();
        var result = ApplySinglePass(
            text,
            rules
                .SelectMany((rule, order) => rule.Find(text, order))
                .Where(candidate => !IsProtected(protectedSpans, candidate.Index, candidate.Length)),
            replacements);

        return new SelectionVocabularyResult(result, replacements);
    }

    // Regions a vocabulary rule must never touch. Rewriting a term inside a URL breaks the link, and
    // rewriting one inside a backtick span or a file path corrupts something the user wrote precisely.
    private static List<(int Start, int End)> ProtectedSpans(string text)
    {
        var spans = new List<(int Start, int End)>();
        foreach (Match match in SelectionProtectedRegion().Matches(text))
        {
            spans.Add((match.Index, match.Index + match.Length));
        }

        return spans;
    }

    private static bool IsProtected(List<(int Start, int End)> spans, int index, int length)
    {
        var end = index + length;
        foreach (var (start, spanEnd) in spans)
        {
            if (index < spanEnd && end > start)
            {
                return true;
            }
        }

        return false;
    }

    // Backtick spans (including fenced blocks), URLs, and Windows or POSIX paths.
    [GeneratedRegex(
        @"`[^`]*`|```[\s\S]*?```|\b[a-z][a-z0-9+.-]*://\S+|(?:[A-Za-z]:\\|\\\\|\./|/)[^\s""'<>|]+",
        RegexOptions.IgnoreCase)]
    private static partial Regex SelectionProtectedRegion();

    /// <summary>
    /// Runs one dictionary rule over already-finalized text, using the exact pipeline a live
    /// dictation would take: the same normalization, the same compiled matcher, and the same
    /// single-pass application including its double-expansion guard.
    ///
    /// This exists so the quick add popup can repair the transcript a correction came from. It
    /// deliberately calls the real implementation rather than reproducing it, because a private copy
    /// of the matcher drifts silently: it would hand the user a "corrected" transcript that disagrees
    /// with what their very next dictation actually produces, which is worse than not repairing at
    /// all. Only the one rule is applied, since the text has already been through every other rule.
    /// </summary>
    public static string ApplyRule(string? text, DictionaryEntry? entry)
    {
        if (string.IsNullOrEmpty(text) || entry is null || string.IsNullOrWhiteSpace(entry.Pattern))
        {
            return text ?? string.Empty;
        }

        CompiledRule rule;
        try
        {
            rule = new CompiledRule(entry with { Pattern = entry.Pattern.Trim() });
        }
        catch (ArgumentException)
        {
            // Mirrors BuildRules: an entry the matcher refuses to compile simply never applies.
            return text;
        }

        var normalized = NormalizeWhitespace(text);
        return ApplySinglePass(normalized, rule.Find(normalized, 0));
    }

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex HorizontalWhitespace();

    [GeneratedRegex(@"[ \t]+([,.!?;:])")]
    private static partial Regex SpaceBeforePunctuation();

    private static string ApplySinglePass(
        string text,
        IEnumerable<ReplacementCandidate> candidates,
        List<TextReplacement>? replacements = null,
        TextReplacementKind kind = TextReplacementKind.Dictionary)
    {
        var selected = candidates
            .OrderBy(candidate => candidate.Index)
            .ThenByDescending(candidate => candidate.Length)
            .ThenBy(candidate => candidate.RuleOrder)
            .ToList();
        if (selected.Count == 0)
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        var position = 0;
        foreach (var candidate in selected)
        {
            if (candidate.Index < position)
            {
                continue;
            }

            var prefixLength = candidate.Index - position;
            if (prefixLength > 0 &&
                char.IsWhiteSpace(text[candidate.Index - 1]) &&
                candidate.Replacement.Length > 0 &&
                candidate.Replacement.All(IsTightPunctuation))
            {
                prefixLength--;
            }

            builder.Append(text, position, prefixLength);
            var replacementStart = builder.Length;
            builder.Append(candidate.Replacement);
            if (replacements is not null &&
                !string.Equals(candidate.Original, candidate.Replacement, StringComparison.Ordinal))
            {
                replacements.Add(new TextReplacement(
                    replacementStart,
                    candidate.Replacement.Length,
                    candidate.Pattern,
                    candidate.Replacement,
                    kind));
            }
            position = candidate.Index + candidate.Length;
        }

        builder.Append(text, position, text.Length - position);
        return builder.ToString();
    }

    private static void AddLocatedReplacements(
        string text,
        IEnumerable<TextReplacement> traces,
        List<TextReplacement> replacements,
        bool replaceOverlaps)
    {
        var searchStart = 0;
        foreach (var trace in traces)
        {
            if (trace.Replacement.Length == 0)
            {
                continue;
            }

            var index = text.IndexOf(trace.Replacement, searchStart, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                index = text.IndexOf(trace.Replacement, StringComparison.OrdinalIgnoreCase);
            }
            if (index < 0)
            {
                continue;
            }

            var end = index + trace.Replacement.Length;
            var overlaps = replacements
                .Where(existing => existing.Start < end && index < existing.Start + existing.Length)
                .ToList();
            if (overlaps.Count > 0)
            {
                if (!replaceOverlaps)
                {
                    continue;
                }
                replacements.RemoveAll(overlaps.Contains);
            }

            replacements.Add(trace with { Start = index, Length = trace.Replacement.Length });
            searchStart = end;
        }
    }

    private static bool IsTightPunctuation(char value) => value is ',' or '.' or '!' or '?' or ';' or ':';

    private sealed record ReplacementCandidate(
        int Index,
        int Length,
        string Replacement,
        int RuleOrder,
        string Pattern,
        string Original);

    /// <summary>
    /// A voice-snippet expansion: the spoken trigger phrase, matched whole, case-insensitively,
    /// and tolerant of the trailing punctuation AI cleanup adds ("Insert my standup update."),
    /// is replaced by the saved template. A MatchEvaluator supplies the template so user text
    /// can never trigger $-substitution.
    /// </summary>
    private sealed class SnippetRule
    {
        private readonly Regex _regex;
        private readonly string _phrase;
        private readonly string _template;

        public SnippetRule(Snippet snippet)
        {
            _phrase = snippet.Phrase;
            _template = snippet.Template;
            var escaped = Regex.Escape(snippet.Phrase.Trim());
            _regex = new Regex($@"(?<!\w){escaped}(?!\w)[.!?,;:]?",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public IEnumerable<ReplacementCandidate> Find(string text, int order) =>
            _regex.Matches(text).Select(match =>
                new ReplacementCandidate(match.Index, match.Length, _template, order, _phrase, match.Value));
    }

    /// <summary>A single dictionary substitution, pre-compiled for reuse across captures.</summary>
    private sealed class CompiledRule
    {
        private readonly Regex _regex;
        private readonly string _pattern;
        private readonly string _replacement;
        private readonly bool _replacementContainsPattern;

        public CompiledRule(DictionaryEntry entry)
        {
            _pattern = entry.Pattern;
            _replacement = entry.Replacement;
            var escaped = Regex.Escape(entry.Pattern);
            var pattern = entry.WholeWord ? $@"(?<!\w){escaped}(?!\w)" : escaped;
            _regex = new Regex(pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            // Only an expansion whose replacement is strictly longer than its pattern AND embeds that
            // pattern (e.g. "york" -> "New York") can double-fire: when AI cleanup is enabled the
            // glossary biases the model to emit the canonical form first, then this deterministic
            // stage, which always runs last, would expand the embedded pattern again ("New York" ->
            // "New New York"). A same-length entry is a pure casing/punctuation fix ("azure" ->
            // "Azure", "sherpa onnx" -> "sherpa-onnx"); it must keep the plain fast-path replace so the
            // fix actually applies, so the length guard is essential here, not just an optimization.
            _replacementContainsPattern =
                !string.IsNullOrEmpty(entry.Pattern) &&
                _replacement.Length > entry.Pattern.Length &&
                _replacement.Contains(entry.Pattern, StringComparison.OrdinalIgnoreCase);
        }

        // MatchEvaluator avoids $-substitution surprises in user-supplied replacement text.
        public IEnumerable<ReplacementCandidate> Find(string text, int order)
        {
            var canonicalStarts = _replacementContainsPattern ? CollectReplacementStarts(text) : [];
            foreach (Match match in _regex.Matches(text))
            {
                var replacement = canonicalStarts.Count > 0 &&
                    IsInsideAnyReplacement(canonicalStarts, match.Index, match.Length)
                    ? match.Value
                    : _replacement;
                yield return new ReplacementCandidate(
                    match.Index,
                    match.Length,
                    replacement,
                    order,
                    _pattern,
                    match.Value);
            }
        }

        // Ascending start offsets of every existing occurrence of the replacement. Case-insensitive
        // because the AI may emit a different casing than the canonical form; that casing is left as-is
        // (never corrupted into a double expansion), which is preferable to a risky span rewrite.
        private List<int> CollectReplacementStarts(string text)
        {
            var starts = new List<int>();
            var from = 0;
            while (from <= text.Length - _replacement.Length)
            {
                var idx = text.IndexOf(_replacement, from, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    break;
                }

                starts.Add(idx);
                from = idx + 1; // allow overlapping occurrences
            }

            return starts;
        }

        private bool IsInsideAnyReplacement(List<int> starts, int matchStart, int matchLength)
        {
            var matchEnd = matchStart + matchLength;
            foreach (var idx in starts)
            {
                if (idx > matchStart)
                {
                    break; // ascending: no later occurrence can contain this match
                }

                if (matchEnd <= idx + _replacement.Length)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
