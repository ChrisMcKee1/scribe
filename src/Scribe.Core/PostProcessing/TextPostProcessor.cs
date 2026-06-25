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

        public CompiledRule(DictionaryEntry entry)
        {
            _replacement = entry.Replacement;
            var escaped = Regex.Escape(entry.Pattern);
            var pattern = entry.WholeWord ? $@"\b{escaped}\b" : escaped;
            _regex = new Regex(pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // MatchEvaluator avoids $-substitution surprises in user-supplied replacement text.
        public string Apply(string text) => _regex.Replace(text, _ => _replacement);
    }
}
