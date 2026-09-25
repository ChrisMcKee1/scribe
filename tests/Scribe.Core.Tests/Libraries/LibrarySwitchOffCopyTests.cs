using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Xunit.Abstractions;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// The dictionary cleanup switches a library off only when that cannot change what dictation writes: it copies the
/// library's still-used rules into the dictionary, and keeps on, with nothing copied, any library whose spoken forms
/// overlap a rule that stays in effect, a rule copied from another library, or one another. Most cases compare finished
/// text before and after the switch through the real repository, composer and post-processor.
/// </summary>
public sealed class LibrarySwitchOffCopyTests(ITestOutputHelper output)
{
    // --- Switched off, with the rules it still uses copied ---

    [Fact]
    public void Switching_off_a_library_nothing_else_overlaps_copies_its_still_used_terms()
    {
        var alpha = Library("alpha", builtIn: false, ("acme", "Acme"));
        var beta = Library("beta", builtIn: false, ("kube", "Kubernetes"));

        var plan = PlanWithTicked([], [beta, alpha], [Usage(alpha, unused: 3)]);

        Assert.Equal(["acme=Acme"], Describe(plan.Copies));
        Assert.Empty(plan.KeptOn);
        string[] inputs = ["acme", "push acme and kube"];
        Assert.Equal(Dictated([], [alpha, beta], inputs), Dictated(plan.Copies, [beta], inputs));
    }

    [Fact]
    public void A_copy_keeps_the_written_form_and_word_boundary_and_is_on()
    {
        var team = new DictionaryLibrary("team-terms", "Team terms", "Custom", Description: null, BuiltIn: false,
            [new DictionaryEntry(0, "dot net", ".NET", WholeWord: false, Enabled: true)]);

        var copy = Assert.Single(PlanWithTicked([], [team], [Usage(team, unused: 1)]).Copies);

        Assert.Equal(new DictionaryEntry(0, "dot net", ".NET", WholeWord: false, Enabled: true), copy);
    }

    [Fact]
    public void Only_the_terms_the_review_kept_are_copied()
    {
        var team = Library("team-terms", builtIn: false, ("kube", "Kubernetes"), ("retro", "Retro"));
        var usage = new LibraryUsage(team.Id, team.Name, [team.Entries[1]], UnusedCount: 1, BuiltIn: false);

        Assert.Equal(["retro=Retro"], Describe(PlanWithTicked([], [team], [usage]).Copies));
    }

    [Fact]
    public void Only_the_row_dictation_applies_is_copied_when_a_library_repeats_a_spoken_form()
    {
        // A second row for the same spoken form never applies (the first wins), so keeping it keeps nothing dictation uses.
        var team = new DictionaryLibrary("team-terms", "Team terms", "Custom", Description: null, BuiltIn: false,
            [DictionaryEntry.New("acme", "Acme"), DictionaryEntry.New("acme", "ACME")]);
        var keepsTheSecondRow = new LibraryUsage(team.Id, team.Name, [team.Entries[1]], UnusedCount: 1, BuiltIn: false);

        Assert.Empty(PlanWithTicked([], [team], [keepsTheSecondRow]).Copies);
        Assert.Equal(["acme=Acme"], Describe(PlanWithTicked([], [team], [Usage(team, unused: 1)]).Copies));
    }

    [Fact]
    public void A_winner_the_scan_found_no_trace_of_is_dropped_and_no_later_library_is_copied_in_its_place()
    {
        // Both go off. Alpha's acme is today's rule but is not kept (unused); beta's is kept, but it is not what dictation
        // applies, and it goes off too, so nothing is left that could take over the text.
        var alpha = Library("alpha", builtIn: false, ("acme", "Acme"), ("kube", "Kubernetes"));
        var beta = Library("beta", builtIn: false, ("acme", "ACME"));
        var alphaKeepsKubeOnly = new LibraryUsage(alpha.Id, alpha.Name, [alpha.Entries[1]], UnusedCount: 1, BuiltIn: false);

        var plan = PlanWithTicked([], [alpha, beta], [alphaKeepsKubeOnly, Usage(beta, unused: 1)]);

        Assert.Equal(["kube=Kubernetes"], Describe(plan.Copies));
        Assert.Empty(plan.KeptOn);
    }

    [Fact]
    public void Libraries_switched_off_together_copy_their_terms_in_precedence_order()
    {
        // The review lists the library with the most unused terms first; the built-in precedes every custom library.
        var alpha = Library("alpha", builtIn: false, ("acme", "Acme"));
        var beta = Library("beta", builtIn: false, ("kube", "Kubernetes"));
        var github = Library("github", builtIn: true, ("get hub", "GitHub"));

        var plan = PlanWithTicked(
            [], [beta, github, alpha], [Usage(beta, unused: 40), Usage(alpha, unused: 12), Usage(github, unused: 3)]);

        Assert.Equal(["get hub=GitHub", "acme=Acme", "kube=Kubernetes"], Describe(plan.Copies));
        Assert.Empty(plan.KeptOn);
    }

    [Fact]
    public void Terms_of_one_library_that_contain_one_another_are_copied_together()
    {
        // A longer spoken form beside a shorter one inside it is common in a library. They match at different places, so
        // no order between them changes anything: the library is switched off with both copied.
        var team = Library("team", builtIn: false, ("get hub", "GitHub"), ("hub", "Hub"), ("unused term", "Unused Term"));
        var usage = UsageFromHistory([team], "GitHub is the Hub", "team");

        var plan = PlanWithTicked([], [team], [usage]);

        Assert.Equal(["get hub=GitHub", "hub=Hub"], Describe(plan.Copies));
        Assert.Empty(plan.KeptOn);
        string[] inputs = ["get hub", "hub", "the hub of get hub", "hubcap"];
        Assert.Equal(Dictated([], [team], inputs), Dictated(plan.Copies, [], inputs));
    }

    [Fact]
    public void A_listed_library_that_is_not_on_copies_nothing()
    {
        // Its rules do not apply today, so there is nothing of it for the dictionary to keep.
        var on = Library("zeta", builtIn: false, ("kube", "K8s"));
        var notOn = Library("alpha", builtIn: false, ("kube", "Kubernetes"), ("retro", "Retro"));

        var plan = PlanWithTicked([], [on], [Usage(notOn, unused: 1)]);

        Assert.Empty(plan.Copies);
        Assert.Empty(plan.KeptOn);
    }

    [Fact]
    public void A_library_whose_row_is_off_and_whose_id_no_ticked_row_carries_is_never_copied()
    {
        // zeta applies nowhere today, so switching alpha off takes nothing of zeta away, and zeta overlaps nothing.
        var alpha = Library("alpha", builtIn: false, ("acme", "Acme"));
        var zeta = Library("zeta", builtIn: false, ("acme", "Kubernetes"));

        var plan = LibrarySwitchOffCopy.Plan(
            [], [alpha, zeta],
            [new LibrarySwitchOffCopy.LibraryRow("alpha", BuiltIn: false, Enabled: true), new LibrarySwitchOffCopy.LibraryRow("zeta", BuiltIn: false, Enabled: false)],
            [Usage(alpha, unused: 3)]);

        Assert.Equal(["acme=Acme"], Describe(plan.Copies));
        Assert.Empty(plan.KeptOn);
    }

    [Fact]
    public void A_dictionary_row_that_is_off_blocks_its_copy_and_is_reported()
    {
        // The row is switched off, so it applies nothing and overlaps nothing in effect, but Save refuses a second row with
        // its spoken form: that term is reported instead of copied, and the library still goes off.
        var team = Library("team-terms", builtIn: false, ("north star", "North Star"), ("retro", "Retro"));
        LibrarySwitchOffCopy.Row[] rows =
        [
            new(" north star", "North star", WholeWord: true, Enabled: false),
            new(string.Empty, "Nothing", WholeWord: true, Enabled: true), new(null, null, WholeWord: true, Enabled: true),
        ];

        var plan = PlanWithTicked(rows, [team], [Usage(team, unused: 9)]);

        Assert.Equal(["retro=Retro"], Describe(plan.Copies));
        Assert.Equal(1, plan.Collided);
        Assert.Empty(plan.KeptOn);
    }

    // --- Kept on: its spoken forms overlap something that stays in effect ---

    [Fact]
    public void A_library_that_shares_a_spoken_form_with_one_that_stays_on_is_kept_on()
    {
        // Whichever of the two is switched off, and whether they write the form the same way or not: the rule that stays on
        // could take over the text, or be beaten by a copy, so nothing is copied and the library stays as it is.
        var alpha = Library("alpha", builtIn: false, ("acme", "Acme"));
        var sameText = Library("beta", builtIn: false, ("acme", "Acme"));
        var otherText = Library("beta", builtIn: false, ("acme", "ACME"));
        var anywhere = new DictionaryLibrary("beta", "beta", "Custom", Description: null, BuiltIn: false,
            [DictionaryEntry.New("acme", "Acme", wholeWord: false)]);

        foreach (var (beta, switching) in new[] { (otherText, otherText), (sameText, alpha), (otherText, alpha), (anywhere, alpha) })
        {
            DictionaryLibrary[] ticked = [alpha, beta];
            LibraryUsage[] asked = [Usage(switching, unused: 3)];

            var plan = PlanWithTicked([], ticked, asked);

            AssertKeptOnAndUnchanged(plan, switching.Id, [], ticked, asked, ["acme", "acmes", "use acme"]);
            Assert.Equal(1, Assert.Single(plan.KeptOn).OverlappingTerms);
        }
    }

    [Fact]
    public void An_unused_row_that_shares_a_spoken_form_with_a_library_that_stays_on_keeps_its_library_on()
    {
        // Alpha's acme is today's rule and the review found it unused, so switching alpha off would drop it, and "acme"
        // would stay as it is. But beta's acme, hidden behind it today, would take over and write ACME instead.
        var alpha = Library("alpha", builtIn: false, ("acme", "Acme"), ("kube", "Kubernetes"));
        var beta = Library("beta", builtIn: false, ("acme", "ACME"));
        LibraryUsage[] asked = [new(alpha.Id, alpha.Name, [alpha.Entries[1]], UnusedCount: 1, BuiltIn: false)];

        var plan = PlanWithTicked([], [alpha, beta], asked);

        AssertKeptOnAndUnchanged(plan, "alpha", [], [alpha, beta], asked, ["acme", "kube"], before: ["Acme", "Kubernetes"]);
        Assert.Equal(["ACME", "kube"], Dictated([], [beta], ["acme", "kube"]));
    }

    [Fact]
    public void A_spoken_form_inside_another_that_stays_on_keeps_its_library_on()
    {
        // Containment across libraries is refused too, not only the same spoken form: "hub" sits inside "get hub".
        var outer = Library("alpha", builtIn: false, ("get hub", "GitHub"));
        var inner = Library("beta", builtIn: false, ("hub", "Hub"), ("unused term", "Unused Term"));

        Assert.Equal(["beta"], PlanWithTicked([], [outer, inner], [Usage(inner, unused: 1)]).KeptOn.Select(k => k.Id));
        Assert.Equal(["alpha"], PlanWithTicked([], [outer, inner], [Usage(outer, unused: 1)]).KeptOn.Select(k => k.Id));
    }

    [Fact]
    public void A_dictionary_row_that_is_on_and_overlaps_keeps_the_library_on()
    {
        var team = Library("team-terms", builtIn: false, ("kube", "Kubernetes"), ("retro", "Retro"));

        var plan = PlanWithTicked([new("Kube ", "Kube", WholeWord: true, Enabled: true)], [team], [Usage(team, unused: 9)]);

        Assert.Empty(plan.Copies);
        Assert.Equal(0, plan.Collided);
        Assert.Equal(["team-terms"], plan.KeptOn.Select(k => k.Id));
    }

    [Fact]
    public void A_blocked_term_is_not_reported_for_a_library_that_stays_on()
    {
        var alpha = Library("alpha", builtIn: false, ("llm", "LLM"));
        var beta = Library("beta", builtIn: false, ("llm", "LLM"));

        var plan = PlanWithTicked([new("llm", "LLM", WholeWord: true, Enabled: false)], [alpha, beta], [Usage(alpha, unused: 2)]);

        Assert.Empty(plan.Copies);
        Assert.Equal(0, plan.Collided);
        Assert.Equal(["alpha"], plan.KeptOn.Select(k => k.Id));
    }

    [Fact]
    public void Keeping_one_library_on_can_keep_another_on()
    {
        // All three are switched off together. Alpha's "get hub" overlaps the copy github would make, so alpha stays on;
        // beta's "acme" then overlaps alpha, which stays in effect; and github's "get hub" overlaps alpha as well, although
        // nothing that was staying on overlapped it at first.
        var alpha = Library("alpha", builtIn: false, ("acme", "Acme"), ("get hub", "GitHub Enterprise"));
        var beta = Library("beta", builtIn: false, ("acme", "ACME"));
        var github = Library("github", builtIn: true, ("get hub", "GitHub"));
        DictionaryLibrary[] ticked = [beta, github, alpha];
        LibraryUsage[] asked = [Usage(beta, unused: 40), Usage(alpha, unused: 12), Usage(github, unused: 3)];

        var plan = PlanWithTicked([], ticked, asked);

        Assert.Equal(["github", "alpha", "beta"], plan.KeptOn.Select(k => k.Id));
        Assert.Empty(plan.Copies);
        string[] inputs = ["acme", "get hub"];
        Assert.Equal(Dictated([], ticked, inputs), Dictated(plan.Copies, StillOn(ticked, asked, plan), inputs));
    }

    [Fact]
    public void A_row_Save_would_store_differently_keeps_its_library_on()
    {
        // Dictated text is trimmed and its spaces collapsed before any rule runs, so a spoken form padded with spaces meets
        // only text with a space on each side; Save trims a copy's spoken form, so a copy would also rewrite "dot net" at
        // either end of a dictation. The loader and Save both trim, so such a row can only be built by hand.
        var team = new DictionaryLibrary("team-terms", "Team terms", "Custom", Description: null, BuiltIn: false,
            [new DictionaryEntry(0, "  dot net  ", ".NET", WholeWord: false, Enabled: true)]);
        LibraryUsage[] asked = [Usage(team, unused: 1)];

        var plan = PlanWithTicked([], [team], asked);

        AssertKeptOnAndUnchanged(plan, "team-terms", [], [team], asked, ["use dot net", "dot net"]);
    }

    [Fact]
    public void The_notice_names_the_libraries_kept_on_and_never_a_term()
    {
        static LibrarySwitchOffCopy.KeptOnLibrary Kept(string name) => new(name.ToLowerInvariant(), BuiltIn: false, name, OverlappingTerms: 2);

        Assert.Equal(string.Empty, LibrarySwitchOffCopy.DescribeKeptOn([]));
        Assert.Equal(
            "Kept on: \"Team terms\". Some of its terms overlap other terms dictation applies, so switching it off could "
                + "change what dictation writes.",
            LibrarySwitchOffCopy.DescribeKeptOn([Kept("Team terms")]));
        Assert.Equal(
            "Kept on: \"Team terms\", \"GitHub\" and \"Alpha\". Some of their terms overlap other terms dictation applies, "
                + "so switching them off could change what dictation writes.",
            LibrarySwitchOffCopy.DescribeKeptOn([Kept("Team terms"), Kept("GitHub"), Kept("Alpha")]));
    }

    // --- Twins: a built-in and a hand-placed file with the same id ---

    [Fact]
    public void Twins_that_share_an_id_are_told_apart_as_rows_and_go_off_together()
    {
        // Only the hand-placed file's row is unticked. The built-in's row stays ticked, so the shared id stays saved and
        // dictation keeps applying both: nothing goes off, nothing is copied, and nothing is kept on either.
        var builtIn = Library("github", builtIn: true, ("get hub", "GitHub"));
        var file = Library("github", builtIn: false, ("get hub", "GitHub"));

        var plan = PlanWithTicked([], [file, builtIn], [Usage(file, unused: 4)]);

        Assert.Empty(plan.Copies);
        Assert.Empty(plan.KeptOn);

        // Both rows unticked, the built-in's row unused: the built-in's rule is today's and is dropped. The file's identical
        // row is kept but never applied (the built-in's comes first), so it is not copied in its place, and it goes too.
        var builtInKeepsNothing = new LibraryUsage(builtIn.Id, builtIn.Name, [], UnusedCount: 1, BuiltIn: true);
        var both = PlanWithTicked([], [file, builtIn], [Usage(file, unused: 4), builtInKeepsNothing]);
        Assert.Empty(both.Copies);
        Assert.Empty(both.KeptOn);
    }

    // The next cases go through the real library service, which applies libraries by saved id, and Save's rule that an id
    // is saved while any row with it is ticked. A built-in and a hand-placed file with the same id are two rows of the
    // Libraries list but one id to dictation: both apply while either row is ticked, and both go off with the last one.

    private const string TwinFile =
        "# name: Team GitHub\npattern,replacement\nget hub,GitHub Enterprise\nkube,Kubernetes\nteam sync,Team Sync\n";

    // The same file without its unused row, for a history that mentions both of its rows.
    private const string FullyUsedTwinFile = "# name: Team GitHub\npattern,replacement\nget hub,GitHub Enterprise\nkube,Kubernetes\n";

    // A file whose spoken forms overlap none of the built-in's.
    private const string SeparateTwinFile =
        "# name: Team GitHub\npattern,replacement\nkube,Kubernetes\nnorth star,North Star\nteam sync,Team Sync\n";

    private static readonly string[] TwinInputs =
        ["get hub", "kube", "push get hub and kube", "git hub", "github", "north star", "team sync"];

    [Fact]
    public void Unticking_a_hand_placed_twin_whose_built_in_row_is_off_keeps_both_on_when_they_overlap()
    {
        // Grok's case. The built-in's row is unticked but the shared id is saved, so dictation applies it and it wins
        // "get hub"; the file's row is the one the cleanup unticks. After Save no row with the id would be ticked, so both
        // would go; the file's "get hub" overlaps the built-in's, so both stay on and the file's row stays ticked.
        var (before, after, plan) = SwitchOffThroughTheService(
            [("github.csv", TwinFile)],
            [("github", BuiltIn: true, Ticked: false), ("github", BuiltIn: false, Ticked: true)],
            switchOff: ("github", BuiltIn: false),
            transcript: "GitHub has the Kubernetes charts");

        Assert.Equal(["GitHub", "Kubernetes", "push GitHub and Kubernetes", "GitHub", "GitHub", "north star", "Team Sync"], before);
        Assert.Equal(before, after);
        Assert.Empty(plan.Copies);
        Assert.Equal([("github", false)], plan.KeptOn.Select(k => (k.Id, k.BuiltIn)));
    }

    [Fact]
    public void Unticking_a_built_in_whose_hand_placed_twin_row_is_off_keeps_both_on_when_they_overlap()
    {
        // The reverse, with a file the review found fully used: it has no verdict, so every row of it counts as kept.
        var (before, after, plan) = SwitchOffThroughTheService(
            [("github.csv", FullyUsedTwinFile)],
            [("github", BuiltIn: true, Ticked: true), ("github", BuiltIn: false, Ticked: false)],
            switchOff: ("github", BuiltIn: true),
            transcript: "GitHub Enterprise has the Kubernetes charts");

        Assert.Equal(["GitHub", "Kubernetes", "push GitHub and Kubernetes", "GitHub", "GitHub", "north star", "team sync"], before);
        Assert.Equal(before, after);
        Assert.Empty(plan.Copies);
        Assert.Equal([("github", true)], plan.KeptOn.Select(k => (k.Id, k.BuiltIn)));
    }

    [Fact]
    public void Unticking_a_hand_placed_twin_whose_built_in_row_is_off_takes_both_off_and_copies_what_each_still_uses()
    {
        // The same switch with a file that overlaps nothing of the built-in's: both go off, and the rules dictation applied
        // from each that the history shows in use are copied.
        var (before, after, plan) = SwitchOffThroughTheService(
            [("github.csv", SeparateTwinFile)],
            [("github", BuiltIn: true, Ticked: false), ("github", BuiltIn: false, Ticked: true)],
            switchOff: ("github", BuiltIn: false),
            transcript: "GitHub has the Kubernetes charts for North Star");

        Assert.Equal(["GitHub", "Kubernetes", "push GitHub and Kubernetes", "GitHub", "GitHub", "North Star", "Team Sync"], before);
        Assert.Empty(plan.KeptOn);
        AssertCopiesWhatEachTwinStillUses(plan);
        Assert.Equal(before[..^1], after[..^1]);
        Assert.Equal("team sync", after[^1]);
    }

    [Fact]
    public void Unticking_a_built_in_whose_hand_placed_twin_row_is_off_takes_both_off_and_copies_what_each_still_uses()
    {
        var (before, after, plan) = SwitchOffThroughTheService(
            [("github.csv", SeparateTwinFile)],
            [("github", BuiltIn: true, Ticked: true), ("github", BuiltIn: false, Ticked: false)],
            switchOff: ("github", BuiltIn: true),
            transcript: "GitHub has the Kubernetes charts for North Star");

        Assert.Equal(["GitHub", "Kubernetes", "push GitHub and Kubernetes", "GitHub", "GitHub", "North Star", "Team Sync"], before);
        Assert.Empty(plan.KeptOn);
        AssertCopiesWhatEachTwinStillUses(plan);
        Assert.Equal(before[..^1], after[..^1]);
        Assert.Equal("team sync", after[^1]);
    }

    // The built-in's "get hub" and the file's "kube" and "north star" are what dictation applied and the history shows in
    // use; the file's unused "team sync" is dropped, and so is a built-in row the history never shows. The built-in's other
    // rows the history mentions ("git hub" and "github" also write GitHub) are copied too, so no exact list is pinned here.
    private static void AssertCopiesWhatEachTwinStillUses(LibrarySwitchOffCopy.Result plan)
    {
        var copies = Describe(plan.Copies);
        Assert.Contains("get hub=GitHub", copies);
        Assert.Contains("kube=Kubernetes", copies);
        Assert.Contains("north star=North Star", copies);
        Assert.DoesNotContain("team sync=Team Sync", copies);
        Assert.DoesNotContain("github copilot=GitHub Copilot", copies);
    }

    [Fact]
    public void Unticking_a_hand_placed_twin_while_the_built_in_row_stays_ticked_leaves_both_on_and_copies_nothing()
    {
        var (before, after, plan) = SwitchOffThroughTheService(
            [("github.csv", TwinFile)],
            [("github", BuiltIn: true, Ticked: true), ("github", BuiltIn: false, Ticked: true)],
            switchOff: ("github", BuiltIn: false),
            transcript: "GitHub has the Kubernetes charts");

        Assert.Equal(before, after);
        Assert.Empty(plan.Copies);
        Assert.Empty(plan.KeptOn);
    }

    [Fact]
    public void Unticking_a_built_in_while_its_hand_placed_twin_row_stays_ticked_leaves_both_on_and_copies_nothing()
    {
        var (before, after, plan) = SwitchOffThroughTheService(
            [("github.csv", TwinFile)],
            [("github", BuiltIn: true, Ticked: true), ("github", BuiltIn: false, Ticked: true)],
            switchOff: ("github", BuiltIn: true),
            transcript: "GitHub has the Kubernetes charts");

        Assert.Equal(before, after);
        Assert.Empty(plan.Copies);
        Assert.Empty(plan.KeptOn);
    }

    // --- Round 3's cases. A spoken form's key (trimmed, OrdinalIgnoreCase) decides which row the composer keeps, but the
    // matcher is an invariant case-insensitive regular expression, which folds letters differently: it does not treat the
    // Greek final sigma as sigma, which OrdinalIgnoreCase does, and it treats the Kelvin sign (U+212A) as k, which
    // OrdinalIgnoreCase does not. Rule order breaks ties between rules that match the same text. Round 3 settled these by
    // running the matcher over each term's own spoken form; each library's spoken forms overlap another rule's, so each is
    // now kept on, and dictation writes exactly what it wrote. ---

    [Fact]
    public void Round_3_a_rule_behind_the_winner_whose_spoken_form_only_compares_equal()
    {
        // The composer keeps alpha's "ΟΣ" and drops beta's "ος" as the same spoken form, but alpha's rule rewrites "ΟΣ" and
        // "οσ" and leaves "ος", and beta's would do the opposite.
        var alpha = Library("alpha", builtIn: false, ("ΟΣ", "Expansion"), ("unused term", "Unused Term"));
        var beta = Library("beta", builtIn: false, ("ος", "Expansion"));
        LibraryUsage[] asked = [UsageFromHistory([alpha, beta], "Expansion was the word we needed", "alpha")];

        var plan = PlanWithTicked([], [alpha, beta], asked);

        AssertKeptOnAndUnchanged(plan, "alpha", [], [alpha, beta], asked, ["ΟΣ", "ος", "οσ", "το ΟΣ μας", "το ος μας"],
            before: ["Expansion", "ος", "Expansion", "το Expansion μας", "το ος μας"]);
    }

    [Fact]
    public void Round_3_an_identical_rule_behind_the_winner_that_another_rule_beats_to_the_text()
    {
        // Gamma writes exactly what alpha writes, but beta's Kelvin-sign rule, a spoken form of its own to the composer,
        // matches "k" too; with alpha off, beta's would come before gamma's.
        var alpha = Library("alpha", builtIn: false, ("k", "First"), ("unused term", "Unused Term"));
        var beta = Library("beta", builtIn: false, (Kelvin, "Second"));
        var gamma = Library("gamma", builtIn: false, ("k", "First"));
        LibraryUsage[] asked = [UsageFromHistory([alpha, beta, gamma], "First things first", "alpha")];

        var plan = PlanWithTicked([], [alpha, beta, gamma], asked);

        AssertKeptOnAndUnchanged(plan, "alpha", [], [alpha, beta, gamma], asked, ["k", "K", Kelvin, "plan k now"],
            before: ["First", "First", "First", "plan First now"]);
    }

    [Fact]
    public void Round_3_a_kept_term_that_an_earlier_library_rule_already_beats()
    {
        var aardvark = Library("aardvark", builtIn: false, (Kelvin, "Second"));
        var zebra = Library("zebra", builtIn: false, ("k", "First"), ("unused term", "Unused Term"));
        LibraryUsage[] asked = [UsageFromHistory([aardvark, zebra], "First things first", "zebra")];

        var plan = PlanWithTicked([], [aardvark, zebra], asked);

        AssertKeptOnAndUnchanged(plan, "zebra", [], [aardvark, zebra], asked, ["k", "K", Kelvin, "plan k now"],
            before: ["Second", "Second", "Second", "plan Second now"]);
    }

    [Fact]
    public void Round_3_a_kept_term_that_a_dictionary_row_already_beats()
    {
        // The dictionary is read sorted by spoken form, byte by byte in UTF-8, so a copy of "k" (0x6B) would sort before
        // the Kelvin-sign row (0xE2 0x84 0xAA) that writes "k" today.
        DictionaryEntry[] dictionary = [DictionaryEntry.New(Kelvin, "Dictionary")];
        var zebra = Library("zebra", builtIn: false, ("k", "First"), ("unused term", "Unused Term"));
        LibraryUsage[] asked = [UsageFromHistory([zebra], "First things first", "zebra")];

        var plan = PlanWithTicked(Rows(dictionary), [zebra], asked);

        AssertKeptOnAndUnchanged(plan, "zebra", dictionary, [zebra], asked, ["k", "K", Kelvin],
            before: ["Dictionary", "Dictionary", "Dictionary"]);
    }

    [Fact]
    public void Round_3_kept_terms_of_one_library_whose_copies_would_swap_places()
    {
        // Both rows are rules dictation compiles and both are kept, and the matcher reads them as one word. In the library
        // the Kelvin-sign row comes first and wins "k"; the dictionary's byte order would put "k" first.
        var team = Library("team", builtIn: false, (Kelvin, "Second"), ("k", "First"), ("unused term", "Unused Term"));
        LibraryUsage[] asked = [UsageFromHistory([team], "First and Second", "team")];

        var plan = PlanWithTicked([], [team], asked);

        AssertKeptOnAndUnchanged(plan, "team", [], [team], asked, ["k", "K", Kelvin], before: ["Second", "Second", "Second"]);
        Assert.Equal(2, Assert.Single(plan.KeptOn).OverlappingTerms);
    }

    [Fact]
    public void Round_3_three_kept_spellings_the_matcher_reads_as_one_word()
    {
        // Three spoken forms that are three keys to the composer (the Kelvin sign, k, and ß beside capital ẞ) but one word
        // to the matcher; the dictionary's byte order is the reverse of the rows' order.
        const string first = Kelvin + "\u1E9E", second = Kelvin + "ß", third = "k\u1E9E";
        var team = Library("team", builtIn: false, (first, "One"), (second, "Two"), (third, "Three"), ("unused term", "Unused Term"));
        LibraryUsage[] asked = [UsageFromHistory([team], "One Two Three", "team")];

        var plan = PlanWithTicked([], [team], asked);

        AssertKeptOnAndUnchanged(plan, "team", [], [team], asked, [first, second, third, "kß", "Kß"],
            before: ["One", "One", "One", "One", "One"]);
    }

    // --- Round 4, Astra's two cases: judging a rule on its own spoken form says nothing about the text around it. Neither
    // copying the rule nor leaving it out keeps what dictation writes, so the library stays on. ---

    [Fact]
    public void Round_4_case_1_a_substring_rule_that_ties_with_a_whole_word_rule_that_stays_on()
    {
        // Aardvark's Kelvin-sign rule (whole word) and zebra's "k" (anywhere in a word) are two keys to the composer and one
        // letter to the matcher. Standalone "k" ties and aardvark's rule comes first, so it is Second; inside a word only
        // zebra's rule can match, so "akb" is "aFirstb". With zebra off and nothing copied, "akb" loses its rule; a copy,
        // which goes ahead of every library rule, would turn "k" into First.
        var aardvark = Library("aardvark", builtIn: false, (Kelvin, "Second"));
        var zebraK = DictionaryEntry.New("k", "First", wholeWord: false);
        var zebra = new DictionaryLibrary("zebra", "zebra", "Custom", Description: null, BuiltIn: false,
            [zebraK, DictionaryEntry.New("unused term", "Unused Term")]);
        LibraryUsage[] asked = [UsageFromHistory([aardvark, zebra], "First things first", "zebra")];

        var plan = PlanWithTicked([], [aardvark, zebra], asked);

        string[] inputs = ["akb", "k", "K", Kelvin, "plan k now", "kk", "a k b k"];
        string[] before = ["aFirstb", "Second", "Second", "Second", "plan Second now", "FirstFirst", "a Second b Second"];
        AssertKeptOnAndUnchanged(plan, "zebra", [], [aardvark, zebra], asked, inputs, before);
        Assert.NotEqual(before, Dictated([], [aardvark], inputs));
        Assert.NotEqual(before, Dictated([zebraK], [aardvark], inputs));
    }

    [Fact]
    public void Round_4_case_2_a_copy_whose_expansion_guard_misses_the_canonical_text()
    {
        // Both rules write "New " and the Kelvin sign; aardvark's spoken form is the sign, zebra's is "k". The guard that
        // stops an expansion firing again inside text already in its written form compares with OrdinalIgnoreCase, which
        // does not fold the Kelvin sign into k, so zebra's rule has no guard and aardvark's has one. Aardvark's comes first
        // today, so the written form stays as it is; a copy of zebra's would go ahead of it and turn it into "New New K".
        const string canonical = "New " + Kelvin;
        var aardvark = Library("aardvark", builtIn: false, (Kelvin, canonical));
        var zebra = Library("zebra", builtIn: false, ("k", canonical), ("unused term", "Unused Term"));
        LibraryUsage[] asked = [UsageFromHistory([aardvark, zebra], $"the {canonical} release", "zebra")];

        var plan = PlanWithTicked([], [aardvark, zebra], asked);

        string[] inputs = [canonical, $"the {canonical} release", "k", Kelvin, "New K"];
        string[] before = [canonical, $"the {canonical} release", canonical, canonical, "New " + canonical];
        AssertKeptOnAndUnchanged(plan, "zebra", [], [aardvark, zebra], asked, inputs, before);
        Assert.NotEqual(before, Dictated([zebra.Entries[0]], [aardvark], inputs));
    }

    // --- The property ---

    [Fact]
    public void Across_random_libraries_the_switch_writes_exactly_what_it_promises_in_any_text()
    {
        // Families of spoken forms that meet the same text in different ways: one inside another ("hub" in "get hub", "k" in
        // "kube"), the Greek final sigma (one key with its capital and medial forms, another letter to the matcher), the
        // Kelvin sign and the capital sharp s (keys of their own, the same letters as k and ß to the matcher), dotted and
        // dotless i, the long s, and written forms that contain their own spoken form ("New York", "New K"), so the guard
        // against expanding text already in its written form takes part.
        (string Spoken, string[] Written)[][] families =
        [
            [("acme", ["Acme", "ACME"])],
            [("get hub", ["GitHub", "Get Hub"]), ("hub", ["Hub", "HUB"])],
            [("ΛΟΓΟΣ", ["Λόγος", "ΛΟΓΟΣ"]), ("λογος", ["Λόγος", "ΛΟΓΟΣ"]), ("λογοσ", ["Λόγος", "ΛΟΓΟΣ"])],
            [("k", ["First", "Second", "New K"]), ("K", ["First", "New K"]), (Kelvin, ["First", "Second", "New " + Kelvin])],
            [("york", ["New York", "York"]), ("new york", ["New York"])],
            [("strasse", ["Straße", "STRASSE"]), ("straße", ["Straße", "STRASSE"]), ("STRA\u1E9EE", ["Straße", "STRASSE"])],
            [("istanbul", ["Istanbul", "\u0130stanbul"]), ("\u0130stanbul", ["Istanbul", "\u0130stanbul"]), ("\u0131stanbul", ["Istanbul", "\u0130stanbul"])],
            [("sun", ["Sun", "SUN"]), ("\u017Fun", ["Sun", "SUN"])],
            [("kube", ["Kubernetes", "K8s"])],
            [("retro", ["Retro"])],
        ];
        (string Id, bool BuiltIn)[] ids =
        [
            ("github", true), ("ai-terminology", true), ("microsoft-azure", true), ("github", false),
            ("alpha", false), ("beta", false), ("team-terms", false), ("team-terms-2", false),
        ];

        // Texts to check, for each family: every spoken form as it is and in invariant lower and upper case, alone, inside a
        // word, repeated with and without a space and in a sentence, and every written form alone, repeated and in a
        // sentence, which is text already in canonical form.
        var probes = families.Select(family => family
            .SelectMany(form => new[] { form.Spoken, form.Spoken.ToLowerInvariant(), form.Spoken.ToUpperInvariant() }
                .SelectMany(v => new[] { v, $"a{v}b", $"{v} {v}", v + v, $"we said {v} today" })
                .Concat(form.Written.SelectMany(w => new[] { w, $"{w} {w}", $"we said {w} today" })))
            .Distinct(StringComparer.Ordinal)
            .ToArray()).ToArray();

        var random = new Random(4092026);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Count(string outcome, int by = 1) => counts[outcome] = counts.GetValueOrDefault(outcome) + by;

        for (var round = 0; round < 700; round++)
        {
            // Every library in the round is loaded and most rows are ticked. A library whose row is not ticked still applies
            // while a row with its id is, as the saved id says.
            var libraries = ids.Where(_ => random.Next(10) < 6).Select(id => RandomLibrary(id.Id, id.BuiltIn)).ToList();
            var ticked = libraries.Where(_ => random.Next(10) < 8).ToList();
            DictionaryLibrary AddLibrary(string id, bool builtIn)
            {
                var library = RandomLibrary(id, builtIn);
                libraries.Add(library);
                return library;
            }

            // Half the rounds load a built-in and a hand-placed file with the same id, one of them the row the cleanup
            // unticks and the other one's row already unticked, so both would go off through the shared id.
            DictionaryLibrary? collapsing = null;
            if (random.Next(2) == 0)
            {
                var twinId = random.Next(2) == 0 ? "github" : "ai-terminology";
                var twins = new[] { true, false }
                    .Select(builtIn => libraries.FirstOrDefault(l => l.Id == twinId && l.BuiltIn == builtIn) ?? AddLibrary(twinId, builtIn))
                    .ToArray();
                var pick = random.Next(2);
                collapsing = twins[pick];
                ticked.Remove(twins[1 - pick]);
                if (!ticked.Contains(collapsing))
                {
                    ticked.Add(collapsing);
                }
            }

            var listRows = libraries.Select(l => new LibrarySwitchOffCopy.LibraryRow(l.Id, l.BuiltIn, ticked.Contains(l))).ToList();

            // The review's verdicts for some of the libraries, and the ones picked to switch off, now and then one whose row
            // is not ticked (the plan must ignore it) or one for a library that is not loaded.
            var verdicts = libraries
                .Where(l => l == collapsing || random.Next(10) < 7)
                .Select(l => new LibraryUsage(
                    l.Id, l.Name, [.. l.EnabledEntries.Where(_ => random.Next(10) < 6)], random.Next(1, 50), l.BuiltIn))
                .ToList();
            var switching = verdicts.Where(u => (collapsing is not null && IsFor(u, collapsing)) || random.Next(10) < 5).ToList();
            if (random.Next(10) == 0)
            {
                var notOn = RandomLibrary("not-on", builtIn: false);
                switching.Add(new LibraryUsage(notOn.Id, notOn.Name, [.. notOn.EnabledEntries], 1, BuiltIn: false));
            }

            // The dictionary as Save would take it: no spoken form twice. Now and then it has a row switched off with the
            // spoken form of a row being switched off, which blocks that row's copy.
            var dictionary = new List<DictionaryEntry>();
            var dictionaryFamilies = new HashSet<int>();
            void AddToDictionary(DictionaryEntry entry)
            {
                if (!Saves([.. dictionary, entry]).HasDuplicate)
                {
                    dictionary.Add(entry);
                    dictionaryFamilies.Add(FamilyOf(entry.Pattern));
                }
            }

            while (random.Next(10) < 4)
            {
                var family = families[random.Next(families.Length)];
                AddToDictionary(RandomEntry(family[random.Next(family.Length)]) with { Enabled = random.Next(10) < 6 });
            }

            var switchedRows = switching.SelectMany(u => u.KeepTerms).ToList();
            if (switchedRows.Count > 0 && random.Next(10) < 4)
            {
                var row = switchedRows[random.Next(switchedRows.Count)];
                AddToDictionary(DictionaryEntry.New(row.Pattern, "Blocked", row.WholeWord) with { Enabled = false });
            }

            var plan = LibrarySwitchOffCopy.Plan(
                Rows(dictionary).OrderBy(_ => random.Next()),
                libraries.OrderBy(_ => random.Next()),
                listRows.OrderBy(_ => random.Next()),
                switching.OrderBy(_ => random.Next()),
                verdicts.OrderBy(_ => random.Next()));

            // What dictation applies, the way the library service reads the saved ids (every loaded library whose id a
            // ticked row carries), before the switch and after it, when every row picked is unticked except the ones the
            // plan keeps on.
            var asked = ticked.Where(l => switching.Any(u => IsFor(u, l))).ToList();
            var unticked = asked.Where(l => !plan.KeepsOn(l.Id, l.BuiltIn)).ToList();
            var today = Applied(libraries, ticked);
            var after = Applied(libraries, [.. ticked.Except(unticked)]);
            var off = LibraryPrecedence.Order(today.Where(l => !after.Contains(l)));
            Count("went off through a shared id", off.Count(l => !unticked.Contains(l)));
            Count("stayed on through a shared id", unticked.Count(after.Contains));
            Assert.All(plan.KeptOn, k => Assert.Contains(asked, l => l.Id == k.Id && l.BuiltIn == k.BuiltIn));

            // What the review kept of a library that goes off: its verdict, or every enabled row when it has none.
            bool Kept(DictionaryLibrary library, DictionaryEntry row) =>
                (verdicts.FirstOrDefault(u => IsFor(u, library))?.KeepTerms ?? [.. library.EnabledEntries]).Contains(row);
            bool Blocked(DictionaryEntry row) =>
                Saves([.. dictionary, DictionaryEntry.New(row.Pattern, row.Replacement, row.WholeWord)]).HasDuplicate;

            // The rows dictation compiles today, through the production composer, with the dictionary in the order the
            // repository reads it back.
            var compiled = DictionaryLibraryComposer.Merge(
                    Saves(dictionary).Entries.Where(e => e.Enabled).OrderBy(e => e.Pattern, DictionaryReadOrder.Pattern),
                    DictionaryLibraryComposer.ComposeLibraries(today))
                .ToHashSet<DictionaryEntry>(ReferenceEqualityComparer.Instance);

            // What the cleanup promises: a library that goes off keeps exactly the kept rows dictation compiles today that Save
            // can take, where they are; every other row of it goes; every other library stays as it is.
            DictionaryEntry[] Promised(DictionaryLibrary library) =>
                [.. library.Entries.Where(e => Kept(library, e) && compiled.Contains(e) && !Blocked(e))];
            var promised = today.Select(l => off.Contains(l) ? l with { Entries = Promised(l) } : l).ToList();

            // The copies are exactly those rows, as Save stores them (the repository sorts the dictionary, so their order
            // does not matter to dictation); the reported ones are exactly the kept rows dictation compiles today that Save
            // cannot take; and Save takes the copies.
            static string Key(DictionaryEntry e) => $"{e.Pattern}\u0001{e.Replacement}\u0001{e.WholeWord}\u0001{e.Enabled}";
            Assert.Equal(
                off.SelectMany(Promised).Select(e => new DictionaryEntry(0, e.Pattern.Trim(), e.Replacement, e.WholeWord, Enabled: true))
                    .Select(Key).Order(StringComparer.Ordinal),
                plan.Copies.Select(Key).Order(StringComparer.Ordinal));
            Assert.Equal(off.Sum(l => l.Entries.Count(e => Kept(l, e) && compiled.Contains(e) && Blocked(e))), plan.Collided);
            Assert.False(Saves([.. dictionary, .. plan.Copies]).HasDuplicate);
            Count("copied", plan.Copies.Count);
            Count("reported", plan.Collided);

            // Finished text, in every context, is exactly what was promised.
            var before = Dictation(dictionary, today);
            var intended = Dictation(dictionary, promised);
            var now = Dictation([.. dictionary, .. plan.Copies], after);
            var present = libraries.SelectMany(l => l.Entries).Select(e => FamilyOf(e.Pattern)).Concat(dictionaryFamilies).Where(f => f >= 0).ToHashSet();
            var pool = present.SelectMany(family => probes[family]).ToArray();
            for (var n = 0; n < 36 && pool.Length > 0; n++)
            {
                var probe = pool[random.Next(pool.Length)];
                var expected = intended(probe);
                var actual = now(probe);
                Assert.True(actual == expected, $"round {round}: '{probe}' should be '{expected}' and is '{actual}'.\n" +
                    Scenario(dictionary, libraries, listRows, switching, verdicts, plan));
                Count("checked: dictation writes what was promised");
                if (before(probe) != expected)
                {
                    Count("checked: the promise drops an unused rule here");
                }
            }

            foreach (var library in asked.Where(l => !plan.KeepsOn(l.Id, l.BuiltIn) && off.Contains(l)))
            {
                Count("switched off");
                if (Promised(library).Length > 0)
                {
                    Count("switched off: with copies");
                }
            }

            // Kept on only for a reason: a row of a library that would go off overlaps a rule in the dictionary or in
            // another library that is on, or two of its own copies read as one. Checked with the comparison the decision
            // describes (invariant upper and lower case, either containing the other, after the known specials), written
            // here independently of SpokenFormFold.
            foreach (var library in asked.Where(l => plan.KeepsOn(l.Id, l.BuiltIn)))
            {
                var dictionaryOn = Saves(dictionary).Entries.Where(e => e.Enabled).ToList();
                var group = today.Where(l => string.Equals(l.Id, library.Id, StringComparison.OrdinalIgnoreCase)).ToList();
                var reasons = new HashSet<string>();
                var onlyContainment = true;
                foreach (var member in group)
                {
                    var copies = Promised(member);
                    foreach (var row in member.EnabledEntries.Where(e => !string.IsNullOrWhiteSpace(e.Pattern)))
                    {
                        var dictionaryOverlaps = dictionaryOn.Where(e => LiteralOverlap(row.Pattern, e.Pattern)).ToList();
                        var libraryOverlaps = today.Where(l => l != member)
                            .SelectMany(l => l.EnabledEntries)
                            .Where(e => !string.IsNullOrWhiteSpace(e.Pattern) && LiteralOverlap(row.Pattern, e.Pattern))
                            .ToList();
                        if (dictionaryOverlaps.Count > 0)
                        {
                            reasons.Add("overlaps the dictionary");
                        }

                        if (libraryOverlaps.Count > 0)
                        {
                            reasons.Add("overlaps another library that is on");
                        }

                        onlyContainment &= dictionaryOverlaps.Concat(libraryOverlaps).All(e => !LiteralSame(row.Pattern, e.Pattern));
                        if (copies.Contains(row) && copies.Any(other => !ReferenceEquals(other, row) && LiteralSame(row.Pattern, other.Pattern)))
                        {
                            reasons.Add("two of its own copies read as one");
                            onlyContainment = false;
                        }
                    }
                }

                Assert.True(reasons.Count > 0, $"round {round}: {library.Id} was kept on with nothing overlapping it.\n" +
                    Scenario(dictionary, libraries, listRows, switching, verdicts, plan));
                Count("kept on");
                foreach (var reason in reasons)
                {
                    Count("kept on: " + reason);
                }

                if (onlyContainment)
                {
                    Count("kept on: only by containment");
                }

                if (group.Count > 1)
                {
                    Count("kept on through a shared id");
                }
            }
        }

        // The generator has to reach every outcome, or the property proves less than it claims.
        foreach (var (outcome, count) in counts)
        {
            output.WriteLine($"{count,7}  {outcome}");
        }

        string[] required =
        [
            "checked: dictation writes what was promised", "checked: the promise drops an unused rule here",
            "switched off", "switched off: with copies", "copied", "reported",
            "kept on", "kept on: overlaps the dictionary", "kept on: overlaps another library that is on",
            "kept on: two of its own copies read as one", "kept on: only by containment", "kept on through a shared id",
            "went off through a shared id", "stayed on through a shared id",
        ];
        Assert.All(required, outcome => Assert.True(
            counts.GetValueOrDefault(outcome) >= 20, $"'{outcome}' was reached {counts.GetValueOrDefault(outcome)} times"));

        int FamilyOf(string spoken) =>
            Array.FindIndex(families, family => family.Any(form => string.Equals(form.Spoken, spoken.Trim(), StringComparison.Ordinal)));

        DictionaryLibrary RandomLibrary(string id, bool builtIn)
        {
            var entries = new List<DictionaryEntry>();
            foreach (var form in families.Where(_ => random.Next(10) < 2).SelectMany(family => family).Where(_ => random.Next(10) < 7))
            {
                entries.Add(RandomEntry(form) with { Enabled = random.Next(10) < 9 });
                if (random.Next(20) == 0)
                {
                    // A second row for the same spoken form: dead code inside the library.
                    entries.Add(RandomEntry(form));
                }
            }

            return new DictionaryLibrary(id, id, builtIn ? "Built-in" : "Custom", Description: null, builtIn, entries);
        }

        DictionaryEntry RandomEntry((string Spoken, string[] Written) form) =>
            DictionaryEntry.New(form.Spoken, form.Written[random.Next(form.Written.Length)], wholeWord: random.Next(4) != 0);
    }

    // The comparison the decision describes, independent of SpokenFormFold: invariant upper and lower case of each spoken
    // form, after mapping the Kelvin sign, the final sigma, dotted and dotless i, the long s and capital sharp s to the
    // letters the matcher or the composer takes them for.
    private static string[] Foldings(string spoken)
    {
        var mapped = spoken.Trim().Replace(Kelvin, "k").Replace('ς', 'σ').Replace('\u0130', 'i').Replace('\u0131', 'i')
            .Replace('\u017F', 's').Replace('\u1E9E', 'ß');
        return [mapped.ToUpperInvariant(), mapped.ToLowerInvariant()];
    }

    private static bool LiteralOverlap(string a, string b) =>
        Foldings(a).Any(x => Foldings(b).Any(y => x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal)));

    private static bool LiteralSame(string a, string b) => Foldings(a).Any(x => Foldings(b).Contains(x, StringComparer.Ordinal));

    private static bool IsFor(LibraryUsage usage, DictionaryLibrary library) =>
        usage.Id == library.Id && usage.BuiltIn == library.BuiltIn;

    // The libraries dictation applies for these ticked rows, the way the library service reads the saved ids: every loaded
    // library whose id a ticked row carries, whatever its own row says.
    private static List<DictionaryLibrary> Applied(IEnumerable<DictionaryLibrary> loaded, IReadOnlyList<DictionaryLibrary> tickedRows) =>
        [.. loaded.Where(l => tickedRows.Any(t => string.Equals(t.Id, l.Id, StringComparison.OrdinalIgnoreCase)))];

    // The libraries dictation applies after the switch when each library passed has a ticked row of its own: every row
    // asked for is unticked except the ones the plan keeps on.
    private static List<DictionaryLibrary> StillOn(
        IReadOnlyList<DictionaryLibrary> ticked, IEnumerable<LibraryUsage> switching, LibrarySwitchOffCopy.Result plan) =>
        Applied(ticked, [.. ticked.Where(l => !switching.Any(u => IsFor(u, l)) || plan.KeepsOn(l.Id, l.BuiltIn))]);

    // The library asked for is kept on, nothing is copied, and dictation writes what it wrote, which is `before` when given.
    private static void AssertKeptOnAndUnchanged(
        LibrarySwitchOffCopy.Result plan,
        string id,
        IReadOnlyList<DictionaryEntry> dictionary,
        IReadOnlyList<DictionaryLibrary> ticked,
        IReadOnlyList<LibraryUsage> switching,
        string[] inputs,
        string[]? before = null)
    {
        Assert.Equal([id], plan.KeptOn.Select(k => k.Id));
        Assert.Empty(plan.Copies);
        Assert.Equal(0, plan.Collided);

        var today = Dictated(dictionary, ticked, inputs);
        if (before is not null)
        {
            Assert.Equal(before, today);
        }

        Assert.Equal(today, Dictated([.. dictionary, .. plan.Copies], StillOn(ticked, switching, plan), inputs));
    }

    // A failing round, in full, so it can be turned into a test of its own.
    private static string Scenario(
        IEnumerable<DictionaryEntry> dictionary,
        IEnumerable<DictionaryLibrary> libraries,
        IEnumerable<LibrarySwitchOffCopy.LibraryRow> listRows,
        IEnumerable<LibraryUsage> switching,
        IEnumerable<LibraryUsage> verdicts,
        LibrarySwitchOffCopy.Result plan)
    {
        static string Row(DictionaryEntry e) =>
            $"{Escape(e.Pattern)}={Escape(e.Replacement)}{(e.WholeWord ? string.Empty : " (substring)")}{(e.Enabled ? string.Empty : " (off)")}";
        static string Escape(string s) => string.Concat(s.Select(c => c < 128 ? c.ToString() : $"\\u{(int)c:X4}"));

        var text = new System.Text.StringBuilder();
        text.AppendLine("dictionary: " + string.Join(", ", dictionary.Select(Row)));
        foreach (var library in LibraryPrecedence.Order(libraries))
        {
            var tick = listRows.Any(r => r.Id == library.Id && r.BuiltIn == library.BuiltIn && r.Enabled) ? "ticked" : "not ticked";
            var picked = switching.Any(u => IsFor(u, library)) ? ", switched off" : string.Empty;
            var verdict = verdicts.FirstOrDefault(u => IsFor(u, library));
            text.AppendLine($"{library.Id} ({(library.BuiltIn ? "built-in" : "custom")}, {tick}{picked}{(verdict is null ? ", no verdict" : string.Empty)}): " +
                string.Join(", ", library.Entries.Select(e => Row(e) + (verdict is not null && verdict.KeepTerms.Contains(e) ? " (kept)" : string.Empty))));
        }

        text.AppendLine($"copies: {string.Join(", ", plan.Copies.Select(Row))}; collided {plan.Collided}; " +
            $"kept on: {string.Join(", ", plan.KeptOn.Select(k => $"{k.Id} ({(k.BuiltIn ? "built-in" : "custom")}, {k.OverlappingTerms})"))}");
        return text.ToString();
    }

    // What Save stores for these entries, and whether it would refuse them for a repeated spoken form.
    private static DictionaryEntryBuilder.Result Saves(IEnumerable<DictionaryEntry> entries) =>
        DictionaryEntryBuilder.Build([.. entries.Select(e => new DictionaryEntryBuilder.Row(0, e.Pattern, e.Replacement, e.WholeWord, e.Enabled))]);

    /// <summary>
    /// Finished text from the real post-processor over this dictionary and these libraries, with the dictionary stored as
    /// Save stores it and read back in the repository's order.
    /// </summary>
    private static Func<string, string> Dictation(IEnumerable<DictionaryEntry> dictionary, IEnumerable<DictionaryLibrary> libraries)
    {
        var processor = new TextPostProcessor(
            new StoredDictionary(Saves(dictionary).Entries), NullLogger<TextPostProcessor>.Instance, snippets: null,
            libraries: new ComposedLibraries([.. libraries]));
        return processor.Process;
    }

    /// <summary>
    /// A dictionary read back in the order <see cref="DictionaryRepository"/> returns it, which
    /// <c>DictionaryRepositoryOrderTests</c> pins against SQLite, without a database per round.
    /// </summary>
    private sealed class StoredDictionary(IReadOnlyList<DictionaryEntry> entries) : IDictionaryRepository
    {
        private readonly IReadOnlyList<DictionaryEntry> _all = [.. entries.OrderBy(e => e.Pattern, DictionaryReadOrder.Pattern)];

        public IReadOnlyList<DictionaryEntry> GetAll() => _all;

        public IReadOnlyList<DictionaryEntry> GetEnabled() => [.. _all.Where(e => e.Enabled)];

        public DictionaryEntry Add(DictionaryEntry entry) => throw new NotSupportedException();

        public IReadOnlyList<DictionaryEntry> AddRange(IReadOnlyList<DictionaryEntry> entries) => throw new NotSupportedException();

        public void Update(DictionaryEntry entry) => throw new NotSupportedException();

        public void Delete(long id) => throw new NotSupportedException();

        public void SaveAll(IReadOnlyList<DictionaryEntry> entries) => throw new NotSupportedException();

        public int SeedIfEmpty(IEnumerable<DictionaryEntry> entries) => throw new NotSupportedException();

        public int DisableUnmodifiedEntries(IEnumerable<DictionaryEntry> entries) => throw new NotSupportedException();
    }

    private static DictionaryLibrary Library(string id, bool builtIn, params (string Spoken, string Written)[] terms) =>
        new(id, id, builtIn ? "Built-in" : "Custom", Description: null, builtIn,
            [.. terms.Select(t => DictionaryEntry.New(t.Spoken, t.Written))]);

    // The Kelvin sign, U+212A: its own spoken form to the composer, the letter k to the matcher.
    private const string Kelvin = "\u212A";

    /// <summary>
    /// What dictation writes for each input: the dictionary stored as Save stores it (trimmed) in a real repository, which
    /// reads it back sorted by spoken form, the libraries through the real composition, and the real post-processor.
    /// </summary>
    private static string[] Dictated(IEnumerable<DictionaryEntry> dictionary, IEnumerable<DictionaryLibrary> libraries, IEnumerable<string> inputs)
    {
        using var database = ScribeDatabase.CreateInMemory();
        var repository = new DictionaryRepository(database);
        var saved = DictionaryEntryBuilder.Build(
            [.. dictionary.Select(e => new DictionaryEntryBuilder.Row(0, e.Pattern, e.Replacement, e.WholeWord, e.Enabled))]).Entries;
        if (saved.Count > 0)
        {
            repository.AddRange(saved);
        }

        var processor = new TextPostProcessor(
            repository, NullLogger<TextPostProcessor>.Instance, snippets: null, libraries: new ComposedLibraries([.. libraries]));
        return [.. inputs.Select(processor.Process)];
    }

    // The review's verdict on one library, from the real analyzer over a single dictation.
    private static LibraryUsage UsageFromHistory(IReadOnlyList<DictionaryLibrary> enabled, string transcript, string id) =>
        DictionaryUsageAnalyzer.Analyze([transcript], [], enabled, minimumTranscripts: 1, minimumWords: 1)
            .Libraries.Single(library => library.Id == id);

    /// <summary>
    /// The cleanup switching one row of the Libraries list off, end to end: custom files the real loader reads beside the
    /// shipped libraries, the list's rows saved as Save saves them (the ids of the ticked rows, which the real service
    /// applies to every loaded library with that id), the real review over what dictation applies, the plan, and Save
    /// storing the row unticked, unless the plan keeps its library on, and the copies in a real dictionary. Returns
    /// finished text before and after.
    /// </summary>
    private static (string[] Before, string[] After, LibrarySwitchOffCopy.Result Plan) SwitchOffThroughTheService(
        IReadOnlyList<(string FileName, string Csv)> customFiles,
        IReadOnlyList<(string Id, bool BuiltIn, bool Ticked)> rows,
        (string Id, bool BuiltIn) switchOff,
        string transcript)
    {
        var root = Path.Combine(Path.GetTempPath(), "scribe-switch-off-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        Directory.CreateDirectory(paths.LibrariesDir);
        foreach (var (fileName, csv) in customFiles)
        {
            File.WriteAllText(Path.Combine(paths.LibrariesDir, fileName), csv);
        }

        try
        {
            using var database = ScribeDatabase.CreateInMemory();
            var settings = new SettingsRepository(database);
            var dictionary = new DictionaryRepository(database);
            var service = new DictionaryLibraryService(paths, settings, NullLogger<DictionaryLibraryService>.Instance);

            void Save(IEnumerable<(string Id, bool BuiltIn, bool Ticked)> listRows)
            {
                var saved = AppSettings.CreateDefault();
                saved.EnabledDictionaryLibraryIds.Clear();
                saved.EnabledDictionaryLibraryIds.AddRange(listRows.Where(r => r.Ticked).Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase));
                settings.Save(saved);
            }

            string[] Dictate() =>
                [.. TwinInputs.Select(new TextPostProcessor(dictionary, NullLogger<TextPostProcessor>.Instance, snippets: null, libraries: service).Process)];

            Save(rows);
            var before = Dictate();

            var loaded = service.GetLibraries();
            var tickedIds = rows.Where(r => r.Ticked).Select(r => r.Id).ToList();
            var report = DictionaryUsageAnalyzer.Analyze(
                [transcript], [], LibraryPrecedence.Enabled(loaded, tickedIds), minimumTranscripts: 1, minimumWords: 1);
            var selected = report.Libraries.Where(u => u.BuiltIn == switchOff.BuiltIn && u.Id == switchOff.Id).ToList();
            Assert.Single(selected);

            var plan = LibrarySwitchOffCopy.Plan(
                [], loaded, [.. rows.Select(r => new LibrarySwitchOffCopy.LibraryRow(r.Id, r.BuiltIn, r.Ticked))], selected, report.Libraries);

            Save(rows.Select(r => r.BuiltIn == switchOff.BuiltIn && r.Id == switchOff.Id && !plan.KeepsOn(r.Id, r.BuiltIn) ? r with { Ticked = false } : r));
            if (plan.Copies.Count > 0)
            {
                dictionary.AddRange(Saves(plan.Copies).Entries);
            }

            return (before, Dictate(), plan);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a leftover temp folder is harmless.
            }
        }
    }

    private static IReadOnlyList<LibrarySwitchOffCopy.Row> Rows(IEnumerable<DictionaryEntry> dictionary) =>
        [.. dictionary.Select(e => new LibrarySwitchOffCopy.Row(e.Pattern, e.Replacement, e.WholeWord, e.Enabled))];

    // The plan when every library passed is on through a ticked row of its own and nothing else is loaded.
    private static LibrarySwitchOffCopy.Result PlanWithTicked(
        IEnumerable<LibrarySwitchOffCopy.Row> rows, IEnumerable<DictionaryLibrary> ticked, IEnumerable<LibraryUsage> switchingOff)
    {
        var libraries = ticked.ToList();
        return LibrarySwitchOffCopy.Plan(
            rows, libraries, [.. libraries.Select(l => new LibrarySwitchOffCopy.LibraryRow(l.Id, l.BuiltIn, Enabled: true))], switchingOff);
    }

    /// <summary>The enabled libraries as the real service hands them to dictation: through the composer.</summary>
    private sealed class ComposedLibraries(IReadOnlyList<DictionaryLibrary> enabled) : IDictionaryLibraryService
    {
        public IReadOnlyList<DictionaryLibrary> GetLibraries() => LibraryPrecedence.Order(enabled);

        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries() => DictionaryLibraryComposer.ComposeLibraries(enabled);

        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries(IReadOnlyCollection<string> enabledIds) =>
            DictionaryLibraryComposer.ComposeLibraries(LibraryPrecedence.Enabled(enabled, enabledIds));

        public DictionaryLibrary Import(string csv, string? suggestedName) => throw new NotSupportedException();

        public void Remove(string id) => throw new NotSupportedException();
    }

    // The review keeps every term of these small libraries, as it keeps any term it finds a trace of.
    private static LibraryUsage Usage(DictionaryLibrary library, int unused) =>
        new(library.Id, library.Name, [.. library.EnabledEntries], unused, library.BuiltIn);

    private static IReadOnlyList<string> Describe(IEnumerable<DictionaryEntry> copies) =>
        copies.Select(e => $"{e.Pattern}={e.Replacement}").ToList();
}
