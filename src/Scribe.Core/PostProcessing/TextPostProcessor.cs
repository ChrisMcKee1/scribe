using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
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

    // The rules the last Build compiled, by all a CompiledRule reads of its entry. A rule never changes and its Regex is
    // safe to share, so the next generation takes an unchanged rule from here instead of compiling it again. Keyed by
    // content, never by generation; each Build replaces it whole, so it holds only the newest rules.
    private Dictionary<RuleKey, CompiledRule>? _compiled;

    // Two flags because a caller that brings its own dictionary rules (a vocabulary generation) needs only the snippets:
    // loading this post-processor's own rules would read the libraries for nothing.
    private bool _rulesLoaded;
    private bool _snippetsLoaded;
    private long _reloads;
    private long _regexRuns;

    // The library selection of the settings dictation runs on, as the last Reload(ids) named it. Null until an owner
    // names one. Read and written under _gate.
    private string[]? _libraryIds;

    public TextPostProcessor(
        IDictionaryRepository dictionary,
        ILogger<TextPostProcessor> logger,
        ISnippetRepository? snippets = null,
        IDictionaryLibraryService? libraries = null,
        PerfFlags? perfFlags = null)
    {
        _dictionary = dictionary;
        _snippets = snippets;
        _libraries = libraries;
        _logger = logger;
        UseMatcherPrefilter = perfFlags?.IsOn(PerfFlags.MatcherPrefilter) ?? false;
    }

    public string Process(string text) => ProcessDetailed(text).Text;

    public event Action<long>? Reloaded;

    /// <summary>
    /// <see cref="PerfFlags.MatcherPrefilter"/>: a dictionary rule whose spoken form is ASCII is skipped, without running its
    /// regex, when an ordinal ignore-case search does not find that spoken form in a text <see cref="OrdinalPrefilter"/>
    /// judges sound; every other rule, and every rule the search finds, runs its regex exactly as before, so the regex still
    /// decides every match. Off (the default) runs every rule's regex.
    /// </summary>
    internal bool UseMatcherPrefilter { get; }

    /// <summary>
    /// Test seam: how many dictionary rules' regular expressions this post-processor's passes have run, the work
    /// <see cref="PerfFlags.MatcherPrefilter"/> exists to cut. Each call adds its passes' count once, so a pass does no
    /// extra work per rule.
    /// </summary>
    internal long RegexRuns => Interlocked.Read(ref _regexRuns);

    /// <summary>
    /// When true (the default), a source identical to the text is not normalized a second time, and
    /// a normalized source identical to the dictionary pass input is not rescanned: the source pass
    /// projects the traces the dictionary pass already recorded. False runs the original algorithm
    /// unchanged. Internal so the differential tests and the benchmark baseline arm can use that as
    /// the reference; the results are identical either way.
    /// </summary>
    internal bool ReuseIdenticalSourceScan { get; init; } = true;

    public TextPostProcessingResult ProcessDetailed(string text, string? sourceText = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TextPostProcessingResult(string.Empty, []);
        }

        EnsureLoaded();
        return ProcessWith(text, sourceText, Volatile.Read(ref _rules));
    }

    public TextPostProcessingResult ProcessDetailed(string text, string? sourceText, CompiledDictionaryRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TextPostProcessingResult(string.Empty, []);
        }

        EnsureSnippetsLoaded();
        return ProcessWith(text, sourceText, rules.Rules);
    }

    private TextPostProcessingResult ProcessWith(string text, string? sourceText, CompiledRule[] rules)
    {
        // Normalize only dictated text. Snippet templates are literal user content and may
        // intentionally contain tabs, indentation, aligned columns, or repeated spaces.
        var normalized = NormalizeWhitespace(text);

        // NormalizeWhitespace is pure, so a source that is the very string being processed (AI
        // cleanup off or skipped) normalizes to the same value without a second pair of regex passes.
        var reuse = ReuseIdenticalSourceScan;
        var source = string.IsNullOrWhiteSpace(sourceText)
            ? null
            : reuse && string.Equals(sourceText, text, StringComparison.Ordinal)
                ? normalized
                : NormalizeWhitespace(sourceText);

        // Snippets expand first so their templates then benefit from dictionary canonicalization.
        // Each phase matches its original input once; generated text is never fed back through later
        // rules in the same phase.
        var snippetRules = Volatile.Read(ref _snippetRules);
        var snippetApplications = new List<TextReplacement>();
        var dictionaryInput = ApplySinglePass(
            normalized,
            Candidates(snippetRules, normalized),
            snippetApplications,
            TextReplacementKind.Snippet);

        var replacements = new List<TextReplacement>();
        var output = ApplySinglePass(
            dictionaryInput,
            Candidates(rules, dictionaryInput, UseMatcherPrefilter, out var regexRuns),
            replacements);

        // The source pass below scans with this same rule snapshot. Matching is a pure function of
        // (input, rules), and both passes record through the same ApplySinglePass with the same kind
        // and the same no-op filter, so a source equal to the dictionary input would record exactly
        // these traces. Copy them now: the snippet projection below edits `replacements` in place.
        var sourceTraces = reuse &&
            source is not null &&
            string.Equals(source, dictionaryInput, StringComparison.Ordinal)
                ? replacements.ToArray()
                : null;

        // Each snippet template's pass adds its count to the seam itself: capturing the local counter would grow this
        // lambda's closure, which every call allocates, by one field.
        var canonicalSnippets = snippetApplications.Select(application =>
        {
            var canonical = ApplySinglePass(
                application.Replacement,
                Candidates(rules, application.Replacement, UseMatcherPrefilter, out var snippetRuns));
            Interlocked.Add(ref _regexRuns, snippetRuns);
            return application with { Length = canonical.Length, Replacement = canonical };
        });
        AddLocatedReplacements(output, canonicalSnippets, replacements, replaceOverlaps: true);

        if (source is not null)
        {
            var sourceRuns = 0;
            IReadOnlyList<TextReplacement> glossaryApplications =
                sourceTraces is not null ? sourceTraces : ScanSource(source, rules, UseMatcherPrefilter, out sourceRuns);
            regexRuns += sourceRuns;
            // ponytail: ordered text search covers cleanup canonicalization; add token alignment only
            // if models start reordering repeated glossary terms enough to make this misleading.
            AddLocatedReplacements(output, glossaryApplications, replacements, replaceOverlaps: false);
        }

        Interlocked.Add(ref _regexRuns, regexRuns);
        return new TextPostProcessingResult(
            output,
            replacements.OrderBy(replacement => replacement.Start).ToList());
    }

    private static List<TextReplacement> ScanSource(string source, CompiledRule[] rules, bool prefilter, out int regexRuns)
    {
        var glossaryApplications = new List<TextReplacement>();
        _ = ApplySinglePass(
            source,
            Candidates(rules, source, prefilter, out regexRuns),
            glossaryApplications);
        return glossaryApplications;
    }

    // Null when nothing matched, so a pass over rules that do not match allocates nothing. With the prefilter, whether an
    // ordinal search may rule an ASCII spoken form out of this text is judged once, here (OrdinalPrefilter).
    internal static List<ReplacementCandidate>? Candidates(CompiledRule[] rules, string text, bool prefilter = false) =>
        Candidates(rules, text, prefilter, out _);

    // regexRuns: how many of the rules ran their regular expression over the text; a rule the prefilter ruled out ran none.
    internal static List<ReplacementCandidate>? Candidates(CompiledRule[] rules, string text, bool prefilter, out int regexRuns)
    {
        var skipUnfound = prefilter && OrdinalPrefilter.IsSound(text);
        List<ReplacementCandidate>? candidates = null;
        var runs = 0;
        for (var order = 0; order < rules.Length; order++)
        {
            if (rules[order].AddCandidates(text, order, skipUnfound, ref candidates))
            {
                runs++;
            }
        }

        regexRuns = runs;
        return candidates;
    }

    private static List<ReplacementCandidate>? Candidates(SnippetRule[] rules, string text)
    {
        List<ReplacementCandidate>? candidates = null;
        for (var order = 0; order < rules.Length; order++)
        {
            rules[order].AddCandidates(text, order, ref candidates);
        }

        return candidates;
    }

    public void Reload()
    {
        long reload;
        lock (_gate)
        {
            _rules = BuildRules();
            _snippetRules = BuildSnippets();
            Volatile.Write(ref _rulesLoaded, true);
            Volatile.Write(ref _snippetsLoaded, true);
            reload = ++_reloads;
        }

        RaiseReloaded(reload);
    }

    public void Reload(IReadOnlyCollection<string> enabledLibraryIds)
    {
        ArgumentNullException.ThrowIfNull(enabledLibraryIds);
        long reload;
        lock (_gate)
        {
            // A copy, so a caller changing its own list later cannot change the selection the rules were built from.
            _libraryIds = [.. enabledLibraryIds];
            _rules = BuildRules();
            _snippetRules = BuildSnippets();
            Volatile.Write(ref _rulesLoaded, true);
            Volatile.Write(ref _snippetsLoaded, true);
            reload = ++_reloads;
        }

        RaiseReloaded(reload);
    }

    public void ReloadSnippets()
    {
        lock (_gate)
        {
            _snippetRules = BuildSnippets();
            Volatile.Write(ref _snippetsLoaded, true);
        }
    }

    public CompiledDictionaryRules Compile(
        IReadOnlyList<DictionaryEntry> dictionary, IReadOnlyList<DictionaryEntry> libraryEntries)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentNullException.ThrowIfNull(libraryEntries);

        // Exactly BuildRules' merge, so a generation's rules and this post-processor's own agree for the same entries.
        var effective = libraryEntries.Count == 0
            ? dictionary
            : DictionaryLibraryComposer.Merge(dictionary, libraryEntries);
        return new CompiledDictionaryRules(Build(effective));
    }

    // Outside _gate, so a subscriber that compiles rules of its own can never wait on this post-processor's lock.
    private void RaiseReloaded(long reload) =>
        ResilientEvent.InvokeAll(Reloaded, reload, ex =>
            _logger.LogWarning("A post-processor reload subscriber failed ({Failure}).", FailureShape.Describe(ex)));

    private void EnsureLoaded()
    {
        if (Volatile.Read(ref _rulesLoaded) && Volatile.Read(ref _snippetsLoaded)) return;
        lock (_gate)
        {
            if (!_rulesLoaded)
            {
                _rules = BuildRules();
                Volatile.Write(ref _rulesLoaded, true);
            }

            if (!_snippetsLoaded)
            {
                _snippetRules = BuildSnippets();
                Volatile.Write(ref _snippetsLoaded, true);
            }
        }
    }

    private void EnsureSnippetsLoaded()
    {
        if (Volatile.Read(ref _snippetsLoaded)) return;
        lock (_gate)
        {
            if (_snippetsLoaded) return;
            _snippetRules = BuildSnippets();
            Volatile.Write(ref _snippetsLoaded, true);
        }
    }

    // The effective rule set = the user's base dictionary plus any enabled libraries, de-duplicated
    // with the base winning on conflict. Libraries are optional and best-effort: a failure to load
    // them must never cost the user their base dictionary. Caller holds _gate.
    private CompiledRule[] BuildRules()
    {
        var baseEntries = _dictionary.GetEnabled();
        var libraryEntries = SafeLibraryEntries();
        var effective = libraryEntries.Count == 0
            ? baseEntries
            : DictionaryLibraryComposer.Merge(baseEntries, libraryEntries);
        return Build(effective);
    }

    // The selection dictation named, never a fresh read of the stored document once one was named: that document may
    // have turned unreadable, and the defaults standing in for it are not the user's selection. Caller holds _gate.
    private IReadOnlyList<DictionaryEntry> SafeLibraryEntries()
    {
        if (_libraries is null)
        {
            return [];
        }

        try
        {
            return _libraryIds is { } ids
                ? _libraries.GetEnabledLibraryEntries(ids)
                : _libraries.GetEnabledLibraryEntries();
        }
        catch (Exception ex)
        {
            // By shape: a library file's I/O failure names its path, and the path is a slug of the library's name.
            _logger.LogWarning(
                "Failed to load enabled dictionary libraries; using the base dictionary only ({Failure}).",
                FailureShape.Describe(ex));
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
                LogSkippedSnippet(_logger, snippet, ex);
            }
        }

        _logger.LogDebug("Post-processor loaded {Count} snippet(s).", rules.Count);
        return rules.ToArray();
    }

    private CompiledRule[] Build(IReadOnlyList<DictionaryEntry> entries)
    {
        var previous = Volatile.Read(ref _compiled);
        var next = new Dictionary<RuleKey, CompiledRule>(entries.Count);
        var rules = new List<CompiledRule>(entries.Count);
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Pattern)) continue;
            var key = new RuleKey(entry.Pattern, entry.Replacement, entry.WholeWord);
            if (!next.TryGetValue(key, out var rule))
            {
                if (previous is null || !previous.TryGetValue(key, out rule))
                {
                    // Never kept when it fails, so a rejected entry is logged at every build, as before.
                    try
                    {
                        rule = new CompiledRule(entry);
                    }
                    catch (Exception ex)
                    {
                        LogSkippedEntry(_logger, entry, ex);
                        continue;
                    }
                }

                next[key] = rule;
            }

            rules.Add(rule);
        }

        Volatile.Write(ref _compiled, next);
        _logger.LogDebug("Post-processor loaded {Count} dictionary rule(s).", rules.Count);
        return rules.ToArray();
    }

    private readonly record struct RuleKey(string Pattern, string Replacement, bool WholeWord);

    // A trigger phrase and a dictionary pattern are the user's own words, and a regex error message
    // quotes the pattern it rejected. So the warning carries the id, the length and the exception
    // type only: never the text, and never the exception object whose message would repeat it.
    internal static void LogSkippedSnippet(ILogger logger, Snippet snippet, Exception exception) =>
        logger.LogWarning(
            "Skipping invalid snippet {Id} (trigger phrase of {PhraseLength} characters): {ExceptionType}.",
            snippet.Id, snippet.Phrase?.Length ?? 0, exception.GetType().Name);

    internal static void LogSkippedEntry(ILogger logger, DictionaryEntry entry, Exception exception) =>
        logger.LogWarning(
            "Skipping invalid dictionary entry {Id} (pattern of {PatternLength} characters): {ExceptionType}.",
            entry.Id, entry.Pattern?.Length ?? 0, exception.GetType().Name);

    private static string NormalizeWhitespace(string text)
    {
        text = HorizontalWhitespace().Replace(text, " ");
        text = SpaceBeforePunctuation().Replace(text, "$1");
        return text.Trim();
    }

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
        List<ReplacementCandidate>? candidates = null;
        _ = rule.AddCandidates(normalized, 0, skipUnfound: false, ref candidates);
        return ApplySinglePass(normalized, candidates);
    }

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex HorizontalWhitespace();

    // The space before punctuation goes only when the mark ends a word. A mark followed by a letter or
    // digit starts one (".NET", ".gitignore", ".5"), and a cleanup model answering "we use .NET" must
    // not come out as "we use.NET", which also stops the dictionary's own ".net maui" rule from matching.
    [GeneratedRegex(@"[ \t]+([,.!?;:])(?![\p{L}\p{N}])")]
    private static partial Regex SpaceBeforePunctuation();

    private static string ApplySinglePass(
        string text,
        List<ReplacementCandidate>? candidates,
        List<TextReplacement>? replacements = null,
        TextReplacementKind kind = TextReplacementKind.Dictionary)
    {
        if (candidates is not { Count: > 0 })
        {
            return text;
        }

        var selected = candidates
            .OrderBy(candidate => candidate.Index)
            .ThenByDescending(candidate => candidate.Length)
            .ThenBy(candidate => candidate.RuleOrder)
            .ToList();

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

    internal sealed record ReplacementCandidate(
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

        public void AddCandidates(string text, int order, ref List<ReplacementCandidate>? candidates)
        {
            foreach (var match in _regex.EnumerateMatches(text))
            {
                (candidates ??= []).Add(new ReplacementCandidate(
                    match.Index,
                    match.Length,
                    _template,
                    order,
                    _phrase,
                    text.Substring(match.Index, match.Length)));
            }
        }
    }

    /// <summary>
    /// The options every dictionary rule's regular expression is built with. <see cref="SpokenFormFold"/> reads the
    /// matcher's case equivalence from the regex engine with these same options, so the two cannot drift apart.
    /// </summary>
    internal const RegexOptions DictionaryMatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>A single dictionary substitution, pre-compiled for reuse across captures.</summary>
    internal sealed class CompiledRule
    {
        private readonly Regex _regex;
        private readonly string _pattern;
        private readonly string _replacement;
        private readonly bool _replacementContainsPattern;

        // Whether the prefilter may judge this rule: its spoken form, as Regex.Escape saw it, is all ASCII. A function of the
        // entry alone, so a rule TX-8 reuses across generations keeps the right value.
        private readonly bool _asciiPattern;

        public CompiledRule(DictionaryEntry entry)
        {
            _pattern = entry.Pattern;
            _replacement = entry.Replacement;
            _asciiPattern = System.Text.Ascii.IsValid(entry.Pattern);
            var escaped = Regex.Escape(entry.Pattern);
            var pattern = entry.WholeWord ? $@"(?<!\w){escaped}(?!\w)" : escaped;
            _regex = new Regex(pattern, DictionaryMatchOptions);

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

        // The replacement is inserted as given, so user text can never trigger $-substitution.
        // skipUnfound: the caller found the text sound for OrdinalPrefilter and MatcherPrefilter is on. An ASCII spoken form
        // an ordinal ignore-case search does not find then has no regex match (OrdinalPrefilter's remarks), so its regex is not
        // run; one the search finds runs its regex, which decides its matches as always. Returns whether the regex ran.
        public bool AddCandidates(string text, int order, bool skipUnfound, ref List<ReplacementCandidate>? candidates)
        {
            if (skipUnfound && _asciiPattern && !OrdinalPrefilter.MayMatch(text, _pattern))
            {
                return false;
            }

            List<int>? canonicalStarts = null;
            foreach (var match in _regex.EnumerateMatches(text))
            {
                // The starts depend on the text alone, so they are found at this rule's first match, not for every rule.
                if (_replacementContainsPattern)
                {
                    canonicalStarts ??= CollectReplacementStarts(text);
                }

                var original = text.Substring(match.Index, match.Length);
                var replacement = canonicalStarts is { Count: > 0 } &&
                    IsInsideAnyReplacement(canonicalStarts, match.Index, match.Length)
                    ? original
                    : _replacement;
                (candidates ??= []).Add(new ReplacementCandidate(
                    match.Index,
                    match.Length,
                    replacement,
                    order,
                    _pattern,
                    original));
            }

            return true;
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
