using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.PostProcessing;

/// <inheritdoc cref="ITextPostProcessor"/>
public sealed partial class TextPostProcessor : ITextPostProcessor
{
    private readonly IDictionaryRepository _dictionary;
    private readonly ILogger<TextPostProcessor> _logger;
    private readonly object _gate = new();

    private CompiledRule[] _rules = [];
    private bool _loaded;

    public TextPostProcessor(IDictionaryRepository dictionary, ILogger<TextPostProcessor> logger)
    {
        _dictionary = dictionary;
        _logger = logger;
    }

    public string Process(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        EnsureLoaded();

        var rules = Volatile.Read(ref _rules);
        foreach (var rule in rules)
            text = rule.Apply(text);

        return NormalizeWhitespace(text);
    }

    public void Reload()
    {
        lock (_gate)
        {
            _rules = Build(_dictionary.GetEnabled());
            _loaded = true;
        }
    }

    private void EnsureLoaded()
    {
        if (Volatile.Read(ref _loaded)) return;
        lock (_gate)
        {
            if (_loaded) return;
            _rules = Build(_dictionary.GetEnabled());
            _loaded = true;
        }
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

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex HorizontalWhitespace();

    [GeneratedRegex(@"[ \t]+([,.!?;:])")]
    private static partial Regex SpaceBeforePunctuation();

    /// <summary>A single dictionary substitution, pre-compiled for reuse across captures.</summary>
    private sealed class CompiledRule
    {
        private readonly Regex _regex;
        private readonly string _replacement;
        private readonly bool _replacementContainsPattern;

        public CompiledRule(DictionaryEntry entry)
        {
            _replacement = entry.Replacement;
            var escaped = Regex.Escape(entry.Pattern);
            var pattern = entry.WholeWord ? $@"\b{escaped}\b" : escaped;
            _regex = new Regex(pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            // Only an expansion whose replacement is strictly longer than its pattern AND embeds that
            // pattern (e.g. "york" -> "New York") can double-fire: when AI cleanup is enabled the
            // glossary biases the model to emit the canonical form first, then this deterministic
            // stage — which always runs last — would expand the embedded pattern again ("New York" ->
            // "New New York"). A same-length entry is a pure casing/punctuation fix ("azure" ->
            // "Azure", "sherpa onnx" -> "sherpa-onnx"); it must keep the plain fast-path replace so the
            // fix actually applies, so the length guard is essential here, not just an optimization.
            _replacementContainsPattern =
                !string.IsNullOrEmpty(entry.Pattern) &&
                _replacement.Length > entry.Pattern.Length &&
                _replacement.Contains(entry.Pattern, StringComparison.OrdinalIgnoreCase);
        }

        // MatchEvaluator avoids $-substitution surprises in user-supplied replacement text.
        public string Apply(string text)
        {
            if (!_replacementContainsPattern)
            {
                return _regex.Replace(text, _ => _replacement);
            }

            // Idempotency guard for the combination with AI cleanup: leave a pattern match that is
            // already part of an existing occurrence of the replacement, so applying the rule to text
            // the AI already canonicalized is a no-op rather than a second expansion ("New York" must
            // not become "New New York"). Precompute the replacement spans once so each match is a
            // cheap membership check instead of rescanning the whole input per match.
            var canonicalStarts = CollectReplacementStarts(text);
            if (canonicalStarts.Count == 0)
            {
                return _regex.Replace(text, _ => _replacement);
            }

            return _regex.Replace(text, m =>
                IsInsideAnyReplacement(canonicalStarts, m.Index, m.Length) ? m.Value : _replacement);
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
