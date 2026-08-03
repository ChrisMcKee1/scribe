using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public class DictionaryUsageAnalyzerTests
{
    private static readonly IReadOnlyList<DictionaryLibrary> NoLibraries = [];

    /// <summary>
    /// Builds a corpus that clears the evidence bar without the test having to care what the bar is.
    /// Filler is deliberately bland so it cannot accidentally supply evidence for a term under test.
    /// </summary>
    private static List<string> Corpus(params string[] lines)
    {
        var transcripts = new List<string>(lines);
        while (transcripts.Count < DictionaryUsageAnalyzer.MinimumTranscripts
            || transcripts.Sum(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)
                < DictionaryUsageAnalyzer.MinimumWords)
        {
            transcripts.Add(string.Join(' ', Enumerable.Repeat("the meeting went well today", 20)));
        }

        return transcripts;
    }

    private static DictionaryLibrary Library(string id, string name, params DictionaryEntry[] entries) =>
        new(id, name, "Test", null, BuiltIn: true, entries);

    // --- The inversion trap ----------------------------------------------------------------

    /// <summary>
    /// The whole feature turns on this case. History stores what was typed, which is post-dictionary,
    /// so a rule that works has already erased its own pattern from the record. Judging on the
    /// pattern alone would delete the hardest-working rules first.
    /// </summary>
    [Fact]
    public void A_working_rule_is_kept_even_though_its_pattern_never_appears()
    {
        var entry = new DictionaryEntry(1, "co pilot", "Copilot");
        var report = DictionaryUsageAnalyzer.Analyze(
            Corpus("Copilot wrote the change for me", "I asked Copilot again"),
            [entry],
            NoLibraries);

        Assert.True(report.HasEnoughEvidence);
        Assert.Empty(report.UnusedEntries);
    }

    [Fact]
    public void A_term_with_no_trace_in_either_direction_is_flagged()
    {
        var entry = new DictionaryEntry(1, "kubernetes", "Kubernetes");
        var report = DictionaryUsageAnalyzer.Analyze(Corpus("nothing relevant here"), [entry], NoLibraries);

        var flagged = Assert.Single(report.UnusedEntries);
        Assert.Equal("kubernetes", flagged.Entry.Pattern);
    }

    /// <summary>A pattern still being spoken means the rule is live even if the fix never lands.</summary>
    [Fact]
    public void A_term_whose_spoken_form_still_appears_is_kept()
    {
        var entry = new DictionaryEntry(1, "azure", "Azure", WholeWord: true, Enabled: false);
        var report = DictionaryUsageAnalyzer.Analyze(Corpus("we deployed to azure"), [entry], NoLibraries);

        Assert.Empty(report.UnusedEntries);
    }

    // --- Matcher parity --------------------------------------------------------------------

    /// <summary>
    /// The written form must be searched without word boundaries even when the rule itself is
    /// whole-word. The matcher only bounds the pattern, and TextPostProcessor tightens whitespace
    /// before punctuation, so "comma" to "," lands as "hello, world" where the comma follows a word
    /// character. Bounding the search would call a rule that fires constantly dead.
    /// </summary>
    [Fact]
    public void A_punctuation_replacement_is_found_even_though_boundaries_would_reject_it()
    {
        var entry = new DictionaryEntry(1, "comma", ",");

        var report = DictionaryUsageAnalyzer.Analyze(Corpus("hello, world"), [entry], NoLibraries);

        Assert.Empty(report.UnusedEntries);
    }

    /// <summary>The pattern side keeps the matcher's boundaries, or the verdict is not the matcher's.</summary>
    [Fact]
    public void Whole_word_patterns_are_not_kept_alive_by_a_substring()
    {
        var wholeWord = new DictionaryEntry(1, "ai", "Artificial intelligence");
        var substring = new DictionaryEntry(2, "ai", "Artificial intelligence", WholeWord: false);

        var corpus = Corpus("this said nothing important");

        Assert.Single(DictionaryUsageAnalyzer.Analyze(corpus, [wholeWord], NoLibraries).UnusedEntries);
        Assert.Empty(DictionaryUsageAnalyzer.Analyze(corpus, [substring], NoLibraries).UnusedEntries);
    }

    [Fact]
    public void Evidence_matching_ignores_case()
    {
        var entry = new DictionaryEntry(1, "GITHUB", "GitHub");
        var report = DictionaryUsageAnalyzer.Analyze(Corpus("pushed it to github"), [entry], NoLibraries);

        Assert.Empty(report.UnusedEntries);
    }

    // --- Unmeasurable rules ----------------------------------------------------------------

    /// <summary>
    /// A removal rule leaves identical evidence whether it fires or not: it deletes its own pattern
    /// and has no written form to look for. It is also skipped when the glossary is built, so
    /// retiring one saves nothing. Proposing it would be a pure risk with no payoff.
    /// </summary>
    [Fact]
    public void A_removal_rule_is_never_proposed_because_it_cannot_be_judged()
    {
        var fired = new DictionaryEntry(1, "um", "");
        var never = new DictionaryEntry(2, "erm", "");

        var report = DictionaryUsageAnalyzer.Analyze(Corpus("so anyway"), [fired, never], NoLibraries);

        Assert.Empty(report.UnusedEntries);
        Assert.Equal(0, report.TermsExamined);
    }

    [Fact]
    public void A_removal_rule_inside_a_library_is_preserved_rather_than_counted_against_it()
    {
        var library = Library(
            "fillers",
            "Fillers",
            new DictionaryEntry(0, "um", ""),
            new DictionaryEntry(0, "kubernetes", "Kubernetes"));

        var report = DictionaryUsageAnalyzer.Analyze(Corpus("nothing relevant"), [], [library]);

        var usage = Assert.Single(report.Libraries);
        Assert.Equal(1, usage.UnusedCount);
        Assert.False(usage.EntirelyUnused);
        Assert.Contains(usage.KeepTerms, t => t.Pattern == "um");
    }

    // --- Entries that cannot have shaped history -------------------------------------------

    /// <summary>
    /// An unsaved grid row cannot have influenced the history being searched, so judging it would let
    /// the scan offer to delete an entry the user typed seconds earlier.
    /// </summary>
    [Fact]
    public void An_unsaved_entry_is_never_judged_against_history_that_predates_it()
    {
        var report = DictionaryUsageAnalyzer.Analyze(
            Corpus("nothing relevant here"),
            [new DictionaryEntry(0, "kubernetes", "Kubernetes")],
            NoLibraries);

        Assert.Empty(report.UnusedEntries);
        Assert.Equal(0, report.TermsExamined);
    }

    [Fact]
    public void Blank_patterns_are_skipped_rather_than_flagged()
    {
        var report = DictionaryUsageAnalyzer.Analyze(
            Corpus("some ordinary text"),
            [new DictionaryEntry(1, "   ", "something")],
            NoLibraries);

        Assert.Empty(report.UnusedEntries);
        Assert.Equal(0, report.TermsExamined);
    }

    // --- The evidence bar ------------------------------------------------------------------

    /// <summary>
    /// Telling a new user to delete their dictionary because they have barely dictated is the worst
    /// possible first impression, and the verdict genuinely is not supportable on a tiny sample.
    /// </summary>
    [Fact]
    public void A_corpus_below_the_evidence_bar_reports_nothing_actionable()
    {
        var report = DictionaryUsageAnalyzer.Analyze(
            ["a short dictation", "another short one"],
            [new DictionaryEntry(1, "kubernetes", "Kubernetes")],
            NoLibraries);

        Assert.False(report.HasEnoughEvidence);
        Assert.False(report.HasFindings);
        Assert.Empty(report.UnusedEntries);
        Assert.Contains("Not enough dictation history", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Enough dictations but almost no words is still not a vocabulary sample.</summary>
    [Fact]
    public void Many_tiny_dictations_do_not_clear_the_evidence_bar()
    {
        var transcripts = Enumerable.Repeat("yes", DictionaryUsageAnalyzer.MinimumTranscripts * 2).ToList();

        Assert.False(DictionaryUsageAnalyzer.Analyze(transcripts, [], NoLibraries).HasEnoughEvidence);
    }

    [Fact]
    public void Blank_transcripts_do_not_count_towards_the_evidence_bar()
    {
        var report = DictionaryUsageAnalyzer.Analyze(Enumerable.Repeat("   ", 500).ToList(), [], NoLibraries);

        Assert.False(report.HasEnoughEvidence);
        Assert.Equal(0, report.TranscriptsScanned);
    }

    // --- Libraries -------------------------------------------------------------------------

    /// <summary>
    /// A partly used library is the common case for a shipped pack, and it has to be actionable or
    /// the feature cannot do the job it exists for. The terms that still work come back as KeepTerms
    /// so switching the library off does not quietly break them.
    /// </summary>
    [Fact]
    public void A_partly_used_library_is_actionable_and_reports_what_must_be_preserved()
    {
        var library = Library(
            "ai-terminology",
            "AI terminology",
            new DictionaryEntry(0, "co pilot", "Copilot"),
            new DictionaryEntry(0, "kubernetes", "Kubernetes"),
            new DictionaryEntry(0, "voir dire", "voir dire"));

        var report = DictionaryUsageAnalyzer.Analyze(Corpus("Copilot again"), [], [library]);

        var usage = Assert.Single(report.Libraries);
        Assert.True(usage.Actionable);
        Assert.False(usage.EntirelyUnused);
        Assert.Equal(2, usage.UnusedCount);
        Assert.Equal(3, usage.TermCount);
        Assert.Equal(["co pilot"], usage.KeepTerms.Select(t => t.Pattern));
    }

    [Fact]
    public void A_library_with_no_live_terms_is_reported_as_entirely_unused()
    {
        var library = Library(
            "legal",
            "Legal",
            new DictionaryEntry(0, "voir dire", "voir dire"),
            new DictionaryEntry(0, "sub poena", "subpoena"));

        var report = DictionaryUsageAnalyzer.Analyze(Corpus("nothing legal here"), [], [library]);

        var usage = Assert.Single(report.Libraries);
        Assert.True(usage.EntirelyUnused);
        Assert.Empty(usage.KeepTerms);
        Assert.True(report.HasFindings);
    }

    /// <summary>A fully used library has nothing to gain, so it must not clutter the review list.</summary>
    [Fact]
    public void A_fully_used_library_is_not_offered()
    {
        var library = Library("ai", "AI", new DictionaryEntry(0, "co pilot", "Copilot"));

        var report = DictionaryUsageAnalyzer.Analyze(Corpus("Copilot again"), [], [library]);

        Assert.Empty(report.Libraries);
        Assert.False(report.HasFindings);
    }

    /// <summary>
    /// Library rows have no database id and cannot be turned off or deleted one at a time, so listing
    /// them individually would be a list of things the user cannot act on.
    /// </summary>
    [Fact]
    public void Library_terms_are_never_listed_as_individually_actionable()
    {
        var library = Library("legal", "Legal", new DictionaryEntry(0, "voir dire", "voir dire"));

        var report = DictionaryUsageAnalyzer.Analyze(Corpus("nothing legal here"), [], [library]);

        Assert.Empty(report.UnusedEntries);
        Assert.Equal(1, report.TermsExamined);
    }

    /// <summary>A disabled library term is already inert, so it cannot count against its library.</summary>
    [Fact]
    public void Disabled_library_terms_are_not_scored()
    {
        var library = Library(
            "legal",
            "Legal",
            new DictionaryEntry(0, "voir dire", "voir dire", WholeWord: true, Enabled: false),
            new DictionaryEntry(0, "sub poena", "subpoena"));

        var report = DictionaryUsageAnalyzer.Analyze(Corpus("we filed the subpoena"), [], [library]);

        Assert.Empty(report.Libraries);
    }

    // --- Corpus handling -------------------------------------------------------------------

    /// <summary>
    /// Transcripts are joined into one corpus for speed; the join must not let a phrase match across
    /// the seam between two unrelated dictations.
    /// </summary>
    [Fact]
    public void Evidence_does_not_leak_across_two_dictations()
    {
        var entry = new DictionaryEntry(1, "is ready", "is ready");

        var report = DictionaryUsageAnalyzer.Analyze(
            Corpus("the release is", "ready to ship"),
            [entry],
            NoLibraries);

        Assert.Single(report.UnusedEntries);
    }

    // --- Reporting -------------------------------------------------------------------------

    [Fact]
    public void A_clean_dictionary_says_so_plainly()
    {
        var report = DictionaryUsageAnalyzer.Analyze(
            Corpus("Copilot wrote it"),
            [new DictionaryEntry(1, "co pilot", "Copilot")],
            NoLibraries);

        Assert.False(report.HasFindings);
        Assert.Contains("Nothing to clean up", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Findings_are_listed_alphabetically_so_the_review_list_is_predictable()
    {
        DictionaryEntry[] entries =
        [
            new(1, "zulu", "Zulu"),
            new(2, "alpha", "Alpha"),
            new(3, "mike", "Mike"),
        ];

        var report = DictionaryUsageAnalyzer.Analyze(Corpus("unrelated content"), entries, NoLibraries);

        Assert.Equal(["alpha", "mike", "zulu"], report.UnusedEntries.Select(u => u.Entry.Pattern));
    }

    /// <summary>
    /// The glossary cap is only worth mentioning when the dictionary is big enough for it to bite;
    /// quoting a limit to someone nowhere near it is noise.
    /// </summary>
    [Fact]
    public void The_glossary_cap_is_only_mentioned_when_the_dictionary_exceeds_it()
    {
        var small = DictionaryUsageAnalyzer.Analyze(
            Corpus("unrelated content"),
            [new DictionaryEntry(1, "kubernetes", "Kubernetes")],
            NoLibraries);

        Assert.DoesNotContain("frees room", small.Summary, StringComparison.OrdinalIgnoreCase);

        var many = Enumerable.Range(1, 200)
            .Select(i => new DictionaryEntry(i, $"term number {i}", $"Term{i}"))
            .ToList();

        var large = DictionaryUsageAnalyzer.Analyze(Corpus("unrelated content"), many, NoLibraries);

        Assert.Contains("frees room", large.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evidence_counts_both_directions_for_a_live_term()
    {
        var usage = DictionaryUsageAnalyzer.Score(
            "Copilot and co pilot and Copilot",
            new DictionaryEntry(1, "co pilot", "Copilot"));

        Assert.False(usage.Unused);
        Assert.Equal(1, usage.PatternHits);
        Assert.Equal(2, usage.ReplacementHits);
    }
}
