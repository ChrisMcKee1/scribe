using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// The dictionary cleanup and AI permission (plan 3.7, acceptance C-10): switching a library off copies the terms it
/// still uses into the dictionary, which every AI cleanup request carries, so a library kept from AI cleanup offers
/// nothing for copying, and the report says so.
/// </summary>
public sealed class DictionaryUsageAnalyzerLibraryTests
{
    [Fact]
    public void A_library_kept_from_AI_cleanup_offers_no_term_for_copying_and_the_report_says_so()
    {
        var privateLibrary = new PostProcessing.DictionaryLibrary("private", "Private", "Custom", null, false,
            [DictionaryEntry.New("nightjar", "Nightjar"), DictionaryEntry.New("never said", "Never Said")]);
        var team = new PostProcessing.DictionaryLibrary("team", "Team", "Custom", null, false,
            [DictionaryEntry.New("kube", "Kubernetes"), DictionaryEntry.New("zzz", "Zzz")]);
        string[] history = ["the nightjar build shipped on kube today"];

        var report = DictionaryUsageAnalyzer.Analyze(
            history, [], [privateLibrary, team], new HashSet<string> { "PRIVATE" }, minimumTranscripts: 1, minimumWords: 1);

        var kept = report.Libraries.Single(l => l.Id == "private");
        Assert.True(kept.AiExcluded);
        Assert.Equal([privateLibrary.Entries[0]], kept.KeepTerms);
        Assert.Empty(kept.CopyTerms);
        Assert.Equal(1, kept.UnusedCount);
        var permitted = report.Libraries.Single(l => l.Id == "team");
        Assert.False(permitted.AiExcluded);
        Assert.Equal(permitted.KeepTerms, permitted.CopyTerms);
        Assert.EndsWith(
            "1 library is kept from AI cleanup, so the terms it still uses are not copied into your dictionary, which AI " +
            "cleanup always receives. Switching it off stops applying those terms.",
            report.Summary, StringComparison.Ordinal);

        // Without the set, nothing changes from before.
        var before = DictionaryUsageAnalyzer.Analyze(history, [], [privateLibrary, team], minimumTranscripts: 1, minimumWords: 1);
        Assert.All(before.Libraries, l => Assert.False(l.AiExcluded));
        Assert.DoesNotContain("kept from AI cleanup", before.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_off_a_library_kept_from_AI_cleanup_copies_none_of_its_terms_into_the_dictionary()
    {
        var team = CustomLibrary("team", Custom("kube", "Kubernetes"), Custom("zzz", "Zzz"));
        var privateLibrary = CustomLibrary("private", Custom("nightjar", "Nightjar"), Custom("never said", "Never Said"));
        var catalog = Catalog(
            State(enabled: ["team", "private"], ai: [("team", true), ("private", false)], accepted: [("team", H1), ("private", H2)]),
            Committed(team, H1), Committed(privateLibrary, H2));
        var composition = LibraryComposition.Preview(
            Draft(1, catalog.LocalState, Draft(team), Draft(privateLibrary)), [], new GlossaryBudget(80));
        Assert.Equal(["private"], composition.AiExcludedLibraryIds);

        var report = DictionaryUsageAnalyzer.Analyze(
            ["nightjar and kube"], [], composition.EnabledLibraries, composition.AiExcludedLibraryIds, minimumTranscripts: 1, minimumWords: 1);
        var plan = LibrarySwitchOffCopy.Plan([], composition, report.Libraries);

        Assert.Empty(plan.KeptOn);
        Assert.Equal(["kube"], plan.Copies.Select(e => e.Pattern));
    }
}
