using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Covers the save-time check that tells a user when a dictionary entry is already handled by a
/// library they have switched on. The distinction that matters: an entry producing the same output
/// is clutter and can go, while one producing different output is a deliberate override and must be
/// kept. Deleting the second kind would silently change what the user's dictation says.
/// </summary>
public sealed class DictionaryLibraryOverlapTests
{
    private static DictionaryEntry E(string pattern, string replacement, bool wholeWord = true, bool enabled = true) =>
        DictionaryEntry.New(pattern, replacement) with { WholeWord = wholeWord, Enabled = enabled };

    [Fact]
    public void An_entry_matching_the_library_exactly_is_redundant()
    {
        var report = DictionaryLibraryOverlapAnalyzer.Analyze(
            [E("azure", "Azure")], [E("azure", "Azure")]);

        Assert.Equal(1, report.RedundantCount);
        Assert.Equal(0, report.OverrideCount);
    }

    [Fact]
    public void An_entry_writing_the_same_word_differently_is_an_override_not_clutter()
    {
        // The motivating case. A library maps "v s" to Visual Studio; a user who dictates about
        // sports or litigation means "versus". Removing that entry would corrupt their output.
        var report = DictionaryLibraryOverlapAnalyzer.Analyze(
            [E("v s", "versus")], [E("v s", "Visual Studio")]);

        Assert.Equal(0, report.RedundantCount);
        Assert.Equal(1, report.OverrideCount);

        var overlap = report.Overrides.Single();
        Assert.Equal("versus", overlap.Replacement);
        Assert.Equal("Visual Studio", overlap.LibraryReplacement);
    }

    [Fact]
    public void Casing_alone_is_enough_to_make_it_an_override()
    {
        // Casing is the entire point of most entries, so "gpt" -> "gpt" is a real disagreement with
        // a library that says "GPT", not an equivalent restatement of it.
        var report = DictionaryLibraryOverlapAnalyzer.Analyze(
            [E("gpt", "gpt")], [E("gpt", "GPT")]);

        Assert.Equal(DictionaryOverlapKind.Override, report.Overlaps.Single().Kind);
    }

    [Fact]
    public void A_different_word_boundary_setting_is_an_override()
    {
        // Same replacement applied on different boundaries is not the same replacement: one fires
        // inside longer words and the other does not.
        var report = DictionaryLibraryOverlapAnalyzer.Analyze(
            [E("api", "API", wholeWord: false)], [E("api", "API", wholeWord: true)]);

        Assert.Equal(DictionaryOverlapKind.Override, report.Overlaps.Single().Kind);
    }

    [Fact]
    public void Pattern_matching_is_case_insensitive_and_trimmed()
    {
        // Matches how the post-processor and the glossary treat patterns, so the warning fires in
        // exactly the cases where the two layers would actually collide.
        var report = DictionaryLibraryOverlapAnalyzer.Analyze(
            [E("  Azure  ", "Azure")], [E("azure", "Azure")]);

        Assert.Equal(1, report.RedundantCount);
    }

    [Fact]
    public void Disabled_entries_are_ignored_on_both_sides()
    {
        // A disabled entry produces no output, so it can neither duplicate nor override anything.
        Assert.False(DictionaryLibraryOverlapAnalyzer
            .Analyze([E("azure", "Azure", enabled: false)], [E("azure", "Azure")]).HasAny);

        Assert.False(DictionaryLibraryOverlapAnalyzer
            .Analyze([E("azure", "Azure")], [E("azure", "Azure", enabled: false)]).HasAny);
    }

    [Fact]
    public void Entries_no_library_covers_are_not_reported()
    {
        var report = DictionaryLibraryOverlapAnalyzer.Analyze(
            [E("pekg", "pEKG"), E("mckee", "McKee")], [E("azure", "Azure")]);

        Assert.False(report.HasAny);
    }

    [Fact]
    public void Null_inputs_produce_an_empty_report_rather_than_throwing()
    {
        Assert.False(DictionaryLibraryOverlapAnalyzer.Analyze(null, [E("a", "A")]).HasAny);
        Assert.False(DictionaryLibraryOverlapAnalyzer.Analyze([E("a", "A")], null).HasAny);
    }

    [Fact]
    public void RemoveRedundant_drops_only_the_redundant_entries()
    {
        var personal = new[]
        {
            E("azure", "Azure"),      // redundant
            E("v s", "versus"),       // override, must survive
            E("pekg", "pEKG"),        // personal, must survive
        };
        var library = new[] { E("azure", "Azure"), E("v s", "Visual Studio") };

        var report = DictionaryLibraryOverlapAnalyzer.Analyze(personal, library);
        var kept = DictionaryLibraryOverlapAnalyzer.RemoveRedundant(personal, report);

        Assert.Equal(2, kept.Count);
        Assert.DoesNotContain(kept, e => e.Pattern == "azure");
        Assert.Contains(kept, e => e.Pattern == "v s");
        Assert.Contains(kept, e => e.Pattern == "pekg");
    }

    [Fact]
    public void RemoveRedundant_returns_the_original_list_when_there_is_nothing_to_drop()
    {
        var personal = new[] { E("pekg", "pEKG") };
        var report = DictionaryLibraryOverlapAnalyzer.Analyze(personal, [E("azure", "Azure")]);

        Assert.Same(personal, DictionaryLibraryOverlapAnalyzer.RemoveRedundant(personal, report));
    }

    [Fact]
    public void The_library_id_is_reported_so_the_message_can_name_the_source()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["azure"] = "microsoft-azure",
        };

        var report = DictionaryLibraryOverlapAnalyzer.Analyze(
            [E("azure", "Azure")], [E("azure", "Azure")], map);

        Assert.Equal("microsoft-azure", report.Overlaps.Single().LibraryId);
    }

    [Fact]
    public void It_finds_real_overlap_against_the_shipped_libraries()
    {
        // End-to-end against actual shipped data, so a future library edit that changes one of these
        // replacements shows up here rather than silently altering the warning users see.
        var shipped = BuiltInDictionaryLibraries.All
            .Where(l => l.Id is "microsoft-azure" or "ai-terminology")
            .SelectMany(l => l.Entries)
            .ToList();

        var report = DictionaryLibraryOverlapAnalyzer.Analyze(
            [E("azure", "Azure"), E("l l m", "LLM"), E("pekg", "pEKG")], shipped);

        Assert.Equal(2, report.RedundantCount);
        Assert.DoesNotContain(report.Overlaps, o => o.Pattern == "pekg");
    }
}
