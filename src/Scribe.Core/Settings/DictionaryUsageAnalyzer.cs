using System.Text.RegularExpressions;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Settings;

/// <summary>
/// Finds dictionary terms the user has no evidence of ever needing, so a dictionary that has
/// accumulated speculative entries and unused shipped libraries can be pruned back.
/// </summary>
/// <remarks>
/// <para>
/// This exists because dead terms are not free. <see cref="Cleanup.CleanupPrompt"/> renders the
/// enabled dictionary into the AI cleanup system prompt on every single dictation, capped at
/// <see cref="Cleanup.CleanupPrompt.MaxGlossaryTermsLocal"/> terms for on-device models. Past that
/// cap, terms the user never says actively displace the ones they do.
/// </para>
/// <para>
/// <b>The inversion trap.</b> History stores the text that was actually typed, which is
/// <i>post</i>-dictionary. So a rule that does its job rewrites its own pattern out of the record:
/// asking "does this pattern appear in history?" answers <i>no</i> for precisely the
/// hardest-working rules. Deleting on that signal would delete the most valuable entries first.
/// The only honest signal is that <b>neither</b> the spoken form <b>nor</b> the written form has
/// ever appeared, which means the term is simply not in this user's vocabulary.
/// </para>
/// <para>
/// Every ambiguity resolves towards keeping a term. A false "still in use" costs the user one
/// glossary slot; a false "dead" costs them a rule they were relying on.
/// </para>
/// </remarks>
public static class DictionaryUsageAnalyzer
{
    /// <summary>Dictations required before an "unused" verdict means anything.</summary>
    public const int MinimumTranscripts = 25;

    /// <summary>
    /// Words required alongside the dictation count. Twenty-five two-word dictations are not a
    /// vocabulary sample, and without this a new user would be told to delete their whole dictionary.
    /// </summary>
    public const int MinimumWords = 1_500;

    private static readonly Regex WordLike = new(@"[\p{L}\p{N}][\p{L}\p{N}'’\-]*", RegexOptions.Compiled);

    /// <summary>
    /// Scores every term against the dictation corpus. Base entries are reported individually
    /// because they can be turned off or deleted; library terms are only ever reported at the
    /// library level, because a shipped library's rows have no database identity and can be
    /// switched off only as a unit.
    /// </summary>
    public static DictionaryUsageReport Analyze(
        IReadOnlyList<string> transcripts,
        IReadOnlyList<DictionaryEntry> baseEntries,
        IReadOnlyList<DictionaryLibrary> enabledLibraries,
        int minimumTranscripts = MinimumTranscripts,
        int minimumWords = MinimumWords)
    {
        ArgumentNullException.ThrowIfNull(transcripts);
        ArgumentNullException.ThrowIfNull(baseEntries);
        ArgumentNullException.ThrowIfNull(enabledLibraries);

        var usable = transcripts.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var words = usable.Sum(t => WordLike.Matches(t).Count);

        // Id 0 means the row exists only in the settings grid and has never been saved, so it cannot
        // possibly have shaped the history being searched. Judging it would let the scan offer to
        // delete an entry the user added seconds ago.
        var candidates = baseEntries.Where(e => e.Id != 0 && IsMeasurable(e)).ToList();
        var examined = candidates.Count
            + enabledLibraries.Sum(l => l.EnabledEntries.Count(IsMeasurable));

        if (usable.Count < minimumTranscripts || words < minimumWords)
        {
            return new DictionaryUsageReport(
                HasEnoughEvidence: false,
                TranscriptsScanned: usable.Count,
                WordsScanned: words,
                TermsExamined: examined,
                UnusedEntries: [],
                Libraries: [],
                Summary: "Not enough dictation history yet to safely recommend a cleanup. You have "
                    + $"{usable.Count:N0} of {minimumTranscripts:N0} dictations and about {words:N0} of "
                    + $"{minimumWords:N0} words. Keep dictating and run this again. You can still turn "
                    + "libraries off by hand on the Libraries page.");
        }

        // One corpus, joined on newlines. A dictionary pattern can never usefully contain a newline
        // (the matcher's input preserves CR/LF), so joining cannot invent a match that spans two
        // unrelated dictations.
        var corpus = string.Join('\n', usable);

        var unused = candidates
            .Select(entry => Score(corpus, entry))
            .Where(u => u.Unused)
            .OrderBy(u => u.Entry.Pattern, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var libraries = enabledLibraries
            .Select(library => ScoreLibrary(corpus, library))
            .Where(l => l.Actionable)
            .OrderByDescending(l => l.UnusedCount)
            .ToList();

        return new DictionaryUsageReport(
            HasEnoughEvidence: true,
            TranscriptsScanned: usable.Count,
            WordsScanned: words,
            TermsExamined: examined,
            UnusedEntries: unused,
            Libraries: libraries,
            Summary: Describe(unused.Count, libraries, usable.Count, examined));
    }

    /// <summary>
    /// Whether a term can be judged at all.
    /// </summary>
    /// <remarks>
    /// A removal rule (<c>"um" -> ""</c>) is genuinely <b>unmeasurable</b>. When it fires it deletes
    /// its own pattern from the stored text, and it has no written form to look for instead, so a
    /// working rule and a dead one leave byte-identical evidence. These are also free: an entry with
    /// an empty replacement is skipped when the glossary is built, so retiring one saves nothing.
    /// Unmeasurable and worthless to remove means it must never be proposed.
    /// </remarks>
    public static bool IsMeasurable(DictionaryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return !string.IsNullOrWhiteSpace(entry.Pattern) && !string.IsNullOrWhiteSpace(entry.Replacement);
    }

    /// <summary>Counts the evidence for one term in both directions.</summary>
    public static TermUsage Score(string corpus, DictionaryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // The pattern is searched exactly as the matcher compiles it, so "would this ever have
        // matched" is answered by the real rules rather than an approximation of them.
        var patternHits = Count(corpus, entry.Pattern, entry.WholeWord);

        // The replacement deliberately does NOT inherit the pattern's word-boundary flag. The
        // matcher only ever applies boundaries to the pattern, and the written form can land
        // somewhere boundaries would reject: "comma" -> "," produces "hello, world", where the comma
        // follows a word character. Searching that with boundaries would report a rule that fires
        // constantly as dead.
        var replacementHits = string.IsNullOrWhiteSpace(entry.Replacement)
            ? 0
            : Count(corpus, entry.Replacement, wholeWord: false);

        return new TermUsage(entry, patternHits, replacementHits);
    }

    private static LibraryUsage ScoreLibrary(string corpus, DictionaryLibrary library)
    {
        var keep = new List<DictionaryEntry>();
        var unused = 0;

        foreach (var term in library.EnabledEntries.Where(e => !string.IsNullOrWhiteSpace(e.Pattern)))
        {
            // An unmeasurable term is always kept. It cannot be shown to be dead, and if the library
            // is switched off it has to survive as one of the preserved entries or the user silently
            // loses a rule this scan was never able to judge.
            if (!IsMeasurable(term) || !Score(corpus, term).Unused)
            {
                keep.Add(term);
            }
            else
            {
                unused++;
            }
        }

        return new LibraryUsage(library.Id, library.Name, keep, unused);
    }

    private static int Count(string corpus, string term, bool wholeWord)
    {
        var trimmed = (term ?? string.Empty).Trim();
        if (trimmed.Length == 0 || string.IsNullOrEmpty(corpus))
        {
            return 0;
        }

        // Mirrors TextPostProcessor.CompiledRule. Diverging here would report a term as dead that
        // the matcher can still fire, which is the one error this feature must not make.
        var escaped = Regex.Escape(trimmed);
        var pattern = wholeWord ? $@"(?<!\w){escaped}(?!\w)" : escaped;
        return Regex.Count(corpus, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string Describe(
        int unusedCount,
        IReadOnlyList<LibraryUsage> libraries,
        int transcripts,
        int examined)
    {
        var libraryTerms = libraries.Sum(l => l.UnusedCount);
        if (unusedCount == 0 && libraryTerms == 0)
        {
            return $"Every term in your dictionary turned up in your last {transcripts:N0} dictations. "
                + "Nothing to clean up.";
        }

        var parts = new List<string>();
        if (unusedCount > 0)
        {
            parts.Add($"{unusedCount:N0} of your own {(unusedCount == 1 ? "entry" : "entries")}");
        }

        if (libraries.Count > 0)
        {
            parts.Add($"{libraryTerms:N0} {(libraryTerms == 1 ? "term" : "terms")} across "
                + $"{libraries.Count:N0} {(libraries.Count == 1 ? "library" : "libraries")}");
        }

        var headline = $"Checked {examined:N0} terms against your last {transcripts:N0} dictations. "
            + $"{string.Join(" and ", parts)} did not appear.";

        // The glossary cap only bites once the dictionary is bigger than it, so the number is only
        // worth raising when it is actually costing the user something.
        return examined > Cleanup.CleanupPrompt.MaxGlossaryTermsLocal
            ? headline + " Turning them off frees room in the vocabulary list Scribe sends to a local "
                + $"AI model, which fits {Cleanup.CleanupPrompt.MaxGlossaryTermsLocal} terms."
            : headline;
    }
}

/// <summary>Evidence gathered for a single dictionary term.</summary>
/// <param name="Entry">The term as stored.</param>
/// <param name="PatternHits">Times the spoken form appears in history.</param>
/// <param name="ReplacementHits">Times the written form appears in history.</param>
public sealed record TermUsage(DictionaryEntry Entry, int PatternHits, int ReplacementHits)
{
    /// <summary>
    /// No trace of the term in either direction. A rule that fires erases its own pattern from the
    /// stored text, so the written form has to be checked too or working rules look dead.
    /// </summary>
    public bool Unused => PatternHits == 0 && ReplacementHits == 0;
}

/// <summary>How much of an enabled library the user's history justifies.</summary>
/// <param name="Id">Library id, as stored in <c>EnabledDictionaryLibraryIds</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="KeepTerms">
/// Terms that matched, plus any that could not be judged. If the library is switched off these have
/// to be carried over into the user's own dictionary or the user silently loses working rules.
/// </param>
/// <param name="UnusedCount">Terms with no trace in history. This is the size of the win.</param>
public sealed record LibraryUsage(
    string Id,
    string Name,
    IReadOnlyList<DictionaryEntry> KeepTerms,
    int UnusedCount)
{
    /// <summary>Terms in the library, ignoring ones disabled inside the library itself.</summary>
    public int TermCount => KeepTerms.Count + UnusedCount;

    /// <summary>Nothing in the library has ever been said or written.</summary>
    public bool EntirelyUnused => UnusedCount > 0 && KeepTerms.Count == 0;

    /// <summary>There is something to gain by switching this library off.</summary>
    public bool Actionable => UnusedCount > 0;
}

/// <summary>The result of scanning dictation history for dictionary decay.</summary>
public sealed record DictionaryUsageReport(
    bool HasEnoughEvidence,
    int TranscriptsScanned,
    int WordsScanned,
    int TermsExamined,
    IReadOnlyList<TermUsage> UnusedEntries,
    IReadOnlyList<LibraryUsage> Libraries,
    string Summary)
{
    /// <summary>Whether there is anything for the user to act on.</summary>
    public bool HasFindings => UnusedEntries.Count > 0 || Libraries.Count > 0;
}
