using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// The dictionary cleanup copies a library's still-used terms into the dictionary before switching the library off.
/// Each copy must be the rule dictation applies today, so the libraries are taken in precedence order, never in the
/// order the review lists them (most unused terms first), and a library that stays on keeps what it supplies.
/// </summary>
public sealed class LibrarySwitchOffCopyTests
{
    [Fact]
    public void Libraries_switched_off_together_copy_the_term_dictation_applies_whatever_order_the_review_lists_them()
    {
        // The built-in precedes every custom library and "alpha.csv" precedes "zeta.csv", so today dictation writes
        // GitHub for "get hub" and Kubernetes for "kube". The review lists the library with the most unused terms first.
        var github = Library("github", builtIn: true, ("get hub", "GitHub"));
        var alpha = Library("alpha", builtIn: false, ("kube", "Kubernetes"), ("get hub", "GitHub Enterprise"));
        var zeta = Library("zeta", builtIn: false, ("kube", "K8s"));

        var plan = LibrarySwitchOffCopy.Plan(
            [], [zeta, github, alpha], [Usage(zeta, unused: 40), Usage(alpha, unused: 12), Usage(github, unused: 3)]);

        Assert.Equal(["get hub=GitHub", "kube=Kubernetes"], Describe(plan.Copies));
        Assert.Equal(0, plan.Collided);
    }

    [Fact]
    public void A_library_that_stays_on_and_comes_earlier_keeps_its_spoken_forms()
    {
        // The built-in stays on and precedes the custom library, so its "llm" is what dictation writes and keeps
        // writing; a copy of the custom version in the dictionary would override it.
        var staying = Library("ai-terminology", builtIn: true, ("llm", "LLM"));
        var team = Library("team-terms", builtIn: false, ("llm", "large language model"), ("north star", "North Star"));

        var plan = LibrarySwitchOffCopy.Plan([], [team, staying], [Usage(team, unused: 5)]);

        Assert.Equal(["north star=North Star"], Describe(plan.Copies));
        Assert.Equal(0, plan.Collided);
    }

    [Fact]
    public void A_library_that_stays_on_but_comes_later_does_not_block_the_copy()
    {
        var github = Library("github", builtIn: true, ("get hub", "GitHub"));
        var later = Library("team-terms", builtIn: false, ("get hub", "GitHub Enterprise"));

        var plan = LibrarySwitchOffCopy.Plan([], [later, github], [Usage(github, unused: 50)]);

        Assert.Equal(["get hub=GitHub"], Describe(plan.Copies));
    }

    [Fact]
    public void A_dictionary_row_that_is_on_keeps_the_spoken_form_and_one_that_is_off_is_reported()
    {
        var team = Library("team-terms", builtIn: false, ("kube", "Kubernetes"), ("north star", "North Star"), ("retro", "Retro"));
        LibrarySwitchOffCopy.Row[] rows =
            [new("Kube ", Enabled: true), new(" north star", Enabled: false), new(string.Empty, Enabled: true), new(null, Enabled: true)];

        var plan = LibrarySwitchOffCopy.Plan(rows, [team], [Usage(team, unused: 9)]);

        Assert.Equal(["retro=Retro"], Describe(plan.Copies));
        Assert.Equal(1, plan.Collided);
    }

    [Fact]
    public void A_row_off_in_the_dictionary_is_not_reported_when_a_library_that_stays_on_still_supplies_the_term()
    {
        var staying = Library("ai-terminology", builtIn: true, ("llm", "LLM"));
        var team = Library("team-terms", builtIn: false, ("llm", "LLM"));

        var plan = LibrarySwitchOffCopy.Plan([new("llm", Enabled: false)], [staying, team], [Usage(team, unused: 2)]);

        Assert.Empty(plan.Copies);
        Assert.Equal(0, plan.Collided);
    }

    [Fact]
    public void A_copy_keeps_the_written_form_and_word_boundary_trims_the_spoken_form_and_is_on()
    {
        var team = new DictionaryLibrary("team-terms", "Team terms", "Custom", Description: null, BuiltIn: false,
            [new DictionaryEntry(0, "  dot net  ", " .NET", WholeWord: false, Enabled: true)]);

        var copy = Assert.Single(LibrarySwitchOffCopy.Plan([], [team], [Usage(team, unused: 1)]).Copies);

        Assert.Equal(new DictionaryEntry(0, "dot net", " .NET", WholeWord: false, Enabled: true), copy);
    }

    [Fact]
    public void Only_the_terms_the_review_kept_are_copied()
    {
        var team = Library("team-terms", builtIn: false, ("kube", "Kubernetes"), ("retro", "Retro"));
        var usage = new LibraryUsage(team.Id, team.Name, [team.Entries[1]], UnusedCount: 1, BuiltIn: false);

        Assert.Equal(["retro=Retro"], Describe(LibrarySwitchOffCopy.Plan([], [team], [usage]).Copies));
    }

    [Fact]
    public void A_built_in_and_a_hand_placed_file_that_share_an_id_are_told_apart()
    {
        // Only the hand-placed file is switched off. The built-in stays on and comes first, so it keeps "get hub".
        var builtIn = Library("github", builtIn: true, ("get hub", "GitHub"));
        var file = Library("github", builtIn: false, ("get hub", "GitHub Enterprise"));

        var plan = LibrarySwitchOffCopy.Plan([], [file, builtIn], [Usage(file, unused: 4)]);

        Assert.Empty(plan.Copies);
    }

    [Fact]
    public void A_listed_library_that_is_not_on_is_handled_after_the_ones_that_are()
    {
        var on = Library("zeta", builtIn: false, ("kube", "K8s"));
        var notOn = Library("alpha", builtIn: false, ("kube", "Kubernetes"));

        var plan = LibrarySwitchOffCopy.Plan([], [on], [Usage(notOn, unused: 1), Usage(on, unused: 1)]);

        Assert.Equal(["kube=K8s"], Describe(plan.Copies));
    }

    private static DictionaryLibrary Library(string id, bool builtIn, params (string Spoken, string Written)[] terms) =>
        new(id, id, builtIn ? "Built-in" : "Custom", Description: null, builtIn,
            [.. terms.Select(t => DictionaryEntry.New(t.Spoken, t.Written))]);

    // The review keeps every term of these small libraries, as it keeps any term it finds a trace of.
    private static LibraryUsage Usage(DictionaryLibrary library, int unused) =>
        new(library.Id, library.Name, [.. library.EnabledEntries], unused, library.BuiltIn);

    private static IReadOnlyList<string> Describe(IEnumerable<DictionaryEntry> copies) =>
        copies.Select(e => $"{e.Pattern}={e.Replacement}").ToList();
}
