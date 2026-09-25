using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// The dictionary cleanup over the library editor's model (W1b contracts 3.3.6, acceptance C-19): the shipped rules of
/// rounds 5 and 6, kept exactly, fed from the draft's enabled state by logical id, the draft composition's winners and
/// physical file names. <c>LibrarySwitchOffCopyTests</c> pins the same rules over 0.4.3's model.
/// </summary>
public sealed class LibrarySwitchOffCopyW1bTests
{
    private static readonly LibraryContent GitHub = BuiltInLibrary("github", Shipped("get hub", "GitHub"), Shipped("octo cat", "Octocat"));
    private static readonly LibraryContent Twin = CustomLibrary("custom-github", Custom("nightjar", "Nightjar"), Custom("codename", "Codename"));

    [Fact]
    public void A_remapped_twin_goes_off_alone_while_its_built_in_stays_on()
    {
        var draft = Draft(1, State(enabled: ["github", "custom-github"]), Draft(GitHub), Draft(Twin, fileName: "github.csv"));
        var composition = Preview(draft);
        var usage = UsageOf(composition, "custom-github", keep: ["nightjar"]);

        var plan = LibrarySwitchOffCopy.Plan([], composition, [usage]);

        Assert.Empty(plan.KeptOn);
        Assert.Equal(["nightjar=Nightjar"], Describe(plan.Copies));
        AssertDictationUnchanged(draft, [], [usage], plan, ["get hub nightjar octo cat"], expectedBefore: ["GitHub Nightjar Octocat"]);

        // 0.4.3's model cannot do this: the twin and the built-in share the saved id "github", so unticking the twin while
        // the built-in's row stays ticked switches nothing off.
        var legacyTwin = new DictionaryLibrary("github", "Twin", "Custom", null, false, [.. Twin.Rows.Select(r => r.Values.ToEntry())]);
        var legacyBuiltIn = new DictionaryLibrary("github", "GitHub", "Microsoft", null, true, [.. GitHub.Rows.Select(r => r.Values.ToEntry())]);
        var legacy = LibrarySwitchOffCopy.Plan(
            [], [legacyBuiltIn, legacyTwin],
            [new LibrarySwitchOffCopy.LibraryRow("github", true, true), new LibrarySwitchOffCopy.LibraryRow("github", false, true)],
            [new LibraryUsage("github", "Twin", [legacyTwin.Entries[0]], UnusedCount: 1, BuiltIn: false)]);
        Assert.Empty(legacy.Copies);
    }

    [Fact]
    public void A_built_in_goes_off_alone_while_its_remapped_twin_stays_on()
    {
        var draft = Draft(1, State(enabled: ["github", "custom-github"]), Draft(GitHub), Draft(Twin, fileName: "github.csv"));
        var composition = Preview(draft);
        var usage = UsageOf(composition, "github", keep: ["get hub"]);

        var plan = LibrarySwitchOffCopy.Plan([], composition, [usage]);

        Assert.Empty(plan.KeptOn);
        Assert.Equal(["get hub=GitHub"], Describe(plan.Copies));
        AssertDictationUnchanged(draft, [], [usage], plan, ["get hub nightjar codename"], expectedBefore: ["GitHub Nightjar Codename"]);
    }

    [Fact]
    public void Asking_for_a_twin_leaves_its_built_in_in_effect_for_every_other_library()
    {
        // The built-in github is not asked for, so its "get hub" stays in effect, and zeta's "hub", which it contains, must
        // keep zeta on. Matched by saved id, as 0.4.3 matched them, the twin would take github off with it and let zeta go.
        var zeta = CustomLibrary("zeta", Custom("hub", "Hub"), Custom("zeta only", "Zeta"));
        var draft = Draft(1, State(enabled: ["github", "custom-github", "zeta"]), Draft(GitHub), Draft(Twin, fileName: "github.csv"), Draft(zeta));
        var composition = Preview(draft);
        LibraryUsage[] asked = [UsageOf(composition, "custom-github", keep: ["nightjar"]), UsageOf(composition, "zeta", keep: ["zeta only"])];

        var plan = LibrarySwitchOffCopy.Plan([], composition, asked);

        Assert.Equal(["zeta"], plan.KeptOn.Select(k => k.Id));
        Assert.Equal(["nightjar=Nightjar"], Describe(plan.Copies));
        AssertDictationUnchanged(draft, [], asked, plan, ["get hub nightjar hub zeta only"], expectedBefore: ["GitHub Nightjar Hub Zeta"]);
    }

    [Fact]
    public void An_authored_built_in_row_that_a_going_row_meets_keeps_that_library_on()
    {
        // The staying built-in's added "kilo" is a rule dictation applies; the going custom "k" sits inside it.
        var metric = BuiltInLibrary("github", Shipped("get hub", "GitHub"), Added("kilo", "kilogram"));
        var team = CustomLibrary("team", Custom("k", "K", wholeWord: false), Custom("unused", "Unused"));
        var draft = Draft(1, State(enabled: ["github", "team"]), Draft(metric), Draft(team));
        var composition = Preview(draft);
        var usage = UsageOf(composition, "team", keep: ["k"]);

        var plan = LibrarySwitchOffCopy.Plan([], composition, [usage]);

        Assert.Equal(["team"], plan.KeptOn.Select(k => k.Id));
        Assert.Empty(plan.Copies);
        AssertDictationUnchanged(draft, [], [usage], plan, ["a kilo of k"], expectedBefore: ["a kilogram of K"]);

        // Fed the shipped built-in without the user's row, the plan would miss "kilo" and switch team off.
        var shippedOnly = new DictionaryLibrary("github", "GitHub", "Microsoft", null, true, [DictionaryEntry.New("get hub", "GitHub")]);
        var teamLibrary = new DictionaryLibrary("team", "Team", "Custom", null, false, [.. team.Rows.Select(r => r.Values.ToEntry())]);
        var unadapted = LibrarySwitchOffCopy.Plan(
            [], [shippedOnly, teamLibrary],
            [new LibrarySwitchOffCopy.LibraryRow("github", true, true), new LibrarySwitchOffCopy.LibraryRow("team", false, true)],
            [new LibraryUsage("team", "Team", [teamLibrary.Entries[0]], UnusedCount: 1, BuiltIn: false)]);
        Assert.Empty(unadapted.KeptOn);
    }

    [Fact]
    public void An_active_legacy_marked_row_keeps_either_side_on()
    {
        var team = CustomLibrary("team", Custom("get hub", "GitHub Enterprise"), Custom("helm", "Helm"));
        var state = State(enabled: ["github", "team"], markers: [("team", "get hub")]);
        var draft = Draft(1, state, Draft(GitHub), Draft(team));
        var composition = Preview(draft);
        Assert.True(composition.AnyLegacyMarkerActive);
        Assert.Equal("GitHub", Winner(composition, "get hub"));

        // The built-in's row is the rule dictation applies; the marked row stays in effect beside its copy.
        var builtIn = LibrarySwitchOffCopy.Plan([], composition, [UsageOf(composition, "github", keep: ["get hub", "octo cat"])]);
        Assert.Equal(["github"], builtIn.KeptOn.Select(k => k.Id));
        Assert.Empty(builtIn.Copies);

        // The marked row is not the rule, so it goes, beside the built-in's rule that stays.
        var custom = LibrarySwitchOffCopy.Plan([], composition, [UsageOf(composition, "team", keep: ["get hub", "helm"])]);
        Assert.Equal(["team"], custom.KeptOn.Select(k => k.Id));
        Assert.Empty(custom.Copies);
    }

    [Fact]
    public void Libraries_rank_by_their_physical_file_name_as_dictation_composes_them()
    {
        // A16: epsilon.csv and the hand-placed github.csv. Copies come out in the order dictation composes the libraries,
        // and the spoken form both supply is epsilon's, as dictation writes it, before and after.
        var epsilon = CustomLibrary("epsilon", Custom("alpha term", "Alpha"), Custom("project token", "Epsilon"));
        var twin = CustomLibrary("custom-github", Custom("zeta term", "Zeta"), Custom("project token", "Twin"));
        var draft = Draft(1, State(enabled: ["epsilon", "custom-github"]), Draft(twin, fileName: "github.csv"), Draft(epsilon));
        var composition = Preview(draft);
        Assert.Equal("Epsilon", Winner(composition, "project token"));

        LibraryUsage[] asked =
        [
            UsageOf(composition, "custom-github", keep: ["zeta term", "project token"]),
            UsageOf(composition, "epsilon", keep: ["alpha term", "project token"]),
        ];
        var plan = LibrarySwitchOffCopy.Plan([], composition, asked);

        // Both still use their shared spoken form, so both stay on, named in physical order; nothing changes what dictation
        // writes.
        Assert.Equal(["epsilon", "custom-github"], plan.KeptOn.Select(k => k.Id));
        AssertDictationUnchanged(draft, [], asked, plan, ["project token alpha term zeta term"], expectedBefore: ["Epsilon Alpha Zeta"]);

        // Without the shared form, both go, and their copies follow the physical names: epsilon.csv before github.csv.
        var apart = Draft(2, draft.LocalState,
            Draft(CustomLibrary("custom-github", Custom("zeta term", "Zeta")), fileName: "github.csv"),
            Draft(CustomLibrary("epsilon", Custom("alpha term", "Alpha"))));
        var apartComposition = Preview(apart);
        LibraryUsage[] apartAsked = [UsageOf(apartComposition, "custom-github", keep: ["zeta term"]), UsageOf(apartComposition, "epsilon", keep: ["alpha term"])];
        Assert.Equal(["alpha term=Alpha", "zeta term=Zeta"], Describe(LibrarySwitchOffCopy.Plan([], apartComposition, apartAsked).Copies));
    }

    [Theory]
    [InlineData("kilo beside k")]
    [InlineData("ab beside bc")]
    [InlineData("the push through ab, bc and cd")]
    public void A_row_that_goes_and_meets_a_rule_that_stays_keeps_its_library_on(string scenario)
    {
        var (libraries, keep, inputs, before) = scenario switch
        {
            "kilo beside k" => (
                new[] { CustomLibrary("team", Custom("kilo", "kilogram"), Custom("k", "K", wholeWord: false)) },
                new[] { "k" }, new[] { "kilo", "k", "a kilo of k" }, new[] { "kilogram", "K", "a kilogram of K" }),
            "ab beside bc" => (
                new[] { CustomLibrary("team", Custom("ab", "Y", wholeWord: false), Custom("bc", "X", wholeWord: false)) },
                new[] { "bc" }, new[] { "abc", "bc", "ab", "abcabc" }, new[] { "Yc", "X", "Y", "YcYc" }),
            _ => (
                new[]
                {
                    CustomLibrary("team", Custom("ab", "Y", wholeWord: false), Custom("cd", "Z", wholeWord: false)),
                    CustomLibrary("zeta", Custom("bc", "X", wholeWord: false)),
                },
                new[] { "cd" }, new[] { "abcd", "bcd", "cd", "ab" }, new[] { "YZ", "Xd", "Z", "Y" }),
        };
        var draft = Draft(1, State(enabled: libraries.Select(l => l.Id)), [.. libraries.Select(l => Draft(l))]);
        var composition = Preview(draft);
        LibraryUsage[] asked = [UsageOf(composition, "team", keep)];

        var plan = LibrarySwitchOffCopy.Plan([], composition, asked);

        Assert.Equal(["team"], plan.KeptOn.Select(k => k.Id));
        Assert.Empty(plan.Copies);
        AssertDictationUnchanged(draft, [], asked, plan, inputs, before);
    }

    [Fact]
    public void A_row_whose_guard_held_text_a_dictionary_row_runs_into_keeps_its_library_on()
    {
        // Round 6 (Astra A4): "c#" writes "C#-code"; the dictionary removes "-" and writes "Sharp" for "#-code".
        var team = CustomLibrary("team", Custom("c#", "C#-code"));
        DictionaryEntry[] dictionary = [DictionaryEntry.New("-", "", wholeWord: false), DictionaryEntry.New("#-code", "Sharp", wholeWord: false)];
        var draft = Draft(1, State(enabled: ["team"]), Draft(team));
        var composition = Preview(draft, dictionary);
        LibraryUsage[] asked = [UsageOf(composition, "team", keep: [])];

        var plan = LibrarySwitchOffCopy.Plan(Rows(dictionary), composition, asked);

        Assert.Equal(["team"], plan.KeptOn.Select(k => k.Id));
        AssertDictationUnchanged(draft, dictionary, asked, plan, ["c#-code", "c#", "#-code", "c# code"], ["c#code", "C#-code", "Sharp", "C#-code code"]);
        Assert.Equal(["cSharp", "c#", "Sharp", "c# code"], Dictation.Write(dictionary, [], ["c#-code", "c#", "#-code", "c# code"]));
    }

    [Fact]
    public void A_blocked_copy_counts_as_a_row_that_goes()
    {
        // "bc" is used, but a dictionary row that is off already has it, so it cannot be copied; it goes, and it runs into
        // the copy of "ab".
        var team = CustomLibrary("team", Custom("ab", "Y", wholeWord: false), Custom("bc", "X", wholeWord: false));
        DictionaryEntry[] dictionary = [new DictionaryEntry(0, "bc", "Off", false, Enabled: false)];
        var draft = Draft(1, State(enabled: ["team"]), Draft(team));
        var composition = Preview(draft, dictionary);
        LibraryUsage[] asked = [UsageOf(composition, "team", keep: ["ab", "bc"])];

        var plan = LibrarySwitchOffCopy.Plan(Rows(dictionary), composition, asked);

        Assert.Equal(["team"], plan.KeptOn.Select(k => k.Id));
        Assert.Empty(plan.Copies);
        Assert.Equal(0, plan.Collided);
    }

    [Fact]
    public void Two_copies_of_one_library_that_meet_still_both_copy()
    {
        var team = CustomLibrary("team", Custom("ab", "Y", wholeWord: false), Custom("bc", "X", wholeWord: false));
        var draft = Draft(1, State(enabled: ["team"]), Draft(team));
        var composition = Preview(draft);
        LibraryUsage[] asked = [UsageOf(composition, "team", keep: ["ab", "bc"])];

        var plan = LibrarySwitchOffCopy.Plan([], composition, asked);

        Assert.Empty(plan.KeptOn);
        Assert.Equal(["ab=Y", "bc=X"], Describe(plan.Copies));
        AssertDictationUnchanged(draft, [], asked, plan, ["abc", "bc", "ab"], ["Yc", "X", "Y"]);
    }

    [Fact]
    public void With_the_default_AI_libraries_on_every_other_shipped_library_offered_stays_on()
    {
        // Today's limitation, pinned until the per-term cleanup (S10): every row of every other shipped library meets a
        // row of ai-model-names or ai-terminology, so none of them can go, whether or not a term of it is in use.
        var builtIns = BuiltInDictionaryLibraries.All
            .Select(library => new LibraryContent(library.Id, true, library.Name, library.Category, library.Description,
                [.. library.Entries.Select(entry => Shipped(entry.Pattern, entry.Replacement, entry.WholeWord))]))
            .ToList();
        var draft = Draft(1, State(enabled: builtIns.Select(l => l.Id)), [.. builtIns.Select(l => Draft(l))]);
        var composition = Preview(draft);
        var others = builtIns.Where(l => !AppSettings.DefaultLibraryIds.Contains(l.Id, StringComparer.OrdinalIgnoreCase)).ToList();

        foreach (var keepOne in new[] { false, true })
        {
            var asked = others.Select(l => UsageOf(composition, l.Id, keep: keepOne ? [l.Rows[0].Values.Spoken] : [])).ToList();

            var plan = LibrarySwitchOffCopy.Plan([], composition, asked);

            Assert.Equal(others.Select(l => l.Id), plan.KeptOn.Select(k => k.Id));
            Assert.Empty(plan.Copies);
        }
    }

    // --- Helpers ---

    private static LibraryComposition Preview(LibraryDraft draft, IReadOnlyList<DictionaryEntry>? dictionary = null) =>
        LibraryComposition.Preview(draft, dictionary ?? [], new GlossaryBudget(80));

    // The review's verdict on one library in use: the named spoken forms kept, the rest unused.
    private static LibraryUsage UsageOf(LibraryComposition composition, string id, IReadOnlyList<string> keep)
    {
        var library = composition.EnabledLibraries.Single(l => l.Id == id);
        var kept = library.EnabledEntries.Where(e => keep.Contains(e.Pattern, StringComparer.OrdinalIgnoreCase)).ToList();
        return new LibraryUsage(library.Id, library.Name, kept, library.EnabledEntryCount - kept.Count, library.BuiltIn) { FileName = library.FileName };
    }

    private static IReadOnlyList<LibrarySwitchOffCopy.Row> Rows(IEnumerable<DictionaryEntry> dictionary) =>
        [.. dictionary.Select(e => new LibrarySwitchOffCopy.Row(e.Pattern, e.Replacement, e.WholeWord, e.Enabled))];

    private static IReadOnlyList<string> Describe(IEnumerable<DictionaryEntry> copies) =>
        [.. copies.Select(e => $"{e.Pattern}={e.Replacement}")];

    // What dictation writes before the switch and after it, when the workspace switches off exactly the libraries asked
    // for that the plan does not keep on and the copies join the dictionary: the same text, every time.
    private static void AssertDictationUnchanged(
        LibraryDraft draft,
        IReadOnlyList<DictionaryEntry> dictionary,
        IReadOnlyList<LibraryUsage> asked,
        LibrarySwitchOffCopy.Result plan,
        IReadOnlyList<string> inputs,
        IReadOnlyList<string> expectedBefore)
    {
        var before = Dictation.Write(dictionary, Preview(draft, dictionary).LibraryEntries, inputs);
        Assert.Equal(expectedBefore, before);

        var switchedOff = asked.Where(u => !plan.KeepsOn(u.Id, u.BuiltIn)).Select(u => u.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var state = draft.LocalState;
        var after = new LibraryDraft(draft.Revision + 1, draft.BaseGeneration, draft.Libraries,
            LibraryLocalState.Create(state.EnabledIds.Where(id => !switchedOff.Contains(id)), state.LegacyEnabledIds, state.AiPermissions,
                state.LegacyMarkers, state.AiUpgradeNotice, state.Health, state.AcceptedContent, state.AiPermissionsLost),
            draft.RecentlyDeleted);
        Assert.Equal(before, Dictation.Write([.. dictionary, .. plan.Copies], Preview(after, dictionary).LibraryEntries, inputs));

        // Nothing is ever copied from a library the plan keeps on.
        foreach (var copy in plan.Copies)
        {
            var source = Preview(draft, dictionary).Rules.Single(rule => rule.Key == Key(copy.Pattern)).LibraryId;
            Assert.DoesNotContain(plan.KeptOn, kept => kept.Id == source);
        }
    }
}
