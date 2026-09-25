using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// Decision 1's tiers and legacy markers (W1b contracts 3.3.2): which row supplies each spoken form, and what the page
/// says about every row.
/// </summary>
public sealed class LibraryCompositionTests
{
    // --- Tiers ---

    [Fact]
    public void Authored_rows_beat_shipped_rows_and_built_ins_lead_inside_each_tier()
    {
        var terminology = BuiltInLibrary("ai-terminology", Shipped("shared", "Terminology"), Shipped("both shipped", "First"));
        var github = BuiltInLibrary("github",
            Shipped("both shipped", "Second"), Edited("edited", "Old", "edited", "GitHub Edit"), Shipped("custom wins", "Shipped"));
        var alpha = CustomLibrary("alpha", Custom("edited", "Alpha"), Custom("custom wins", "Alpha"), Custom("shared", "Alpha"));
        var beta = CustomLibrary("beta", Custom("custom wins", "Beta"));
        var composition = Compose(Catalog(
            State(enabled: ["ai-terminology", "github", "alpha", "beta"], accepted: [("alpha", H1), ("beta", H2)]),
            Committed(beta, H2), Committed(alpha, H1), Committed(github), Committed(terminology)));

        Assert.Equal("Alpha", Winner(composition, "custom wins"));      // authored beats shipped
        Assert.Equal("Alpha", Winner(composition, "shared"));
        Assert.Equal("GitHub Edit", Winner(composition, "edited"));     // a built-in leads inside the authored tier
        Assert.Equal("First", Winner(composition, "both shipped"));     // the frozen built-in order inside the shipped tier
        Assert.Equal(RuleTier.Authored, composition.Rules.Single(r => r.Key == Key("edited")).Tier);
        Assert.Equal(RuleTier.Shipped, composition.Rules.Single(r => r.Key == Key("both shipped")).Tier);

        // Composition order: the authored tier first (built-ins, then custom files by name), then the shipped tier.
        Assert.Equal(
            ["edited", "custom wins", "shared", "both shipped"],
            composition.Rules.Select(rule => rule.Key.Value));
        Assert.Equal(composition.Rules.Select(r => r.Entry), composition.LibraryEntries);
    }

    [Fact]
    public void Turned_off_rows_and_libraries_that_are_off_paused_or_pending_deletion_drop_out_before_winners_are_chosen()
    {
        var github = BuiltInLibrary("github", Shipped("get hub", "GitHub"), Shipped("kube", "Kubernetes"), Shipped("paused", "Shipped"));
        var off = CustomLibrary("aaa-off", Custom("get hub", "Off Library"));
        var rowOff = CustomLibrary("bbb-row-off", Custom("kube", "K8s", enabled: false));
        var paused = CustomLibrary("ccc-paused", Custom("paused", "Paused"));
        var state = State(
            enabled: ["github", "bbb-row-off", "ccc-paused"],
            accepted: [("aaa-off", H1), ("bbb-row-off", H2), ("ccc-paused", H3)]);

        var composition = Compose(Catalog(
            state, Committed(github), Committed(off, H1), Committed(rowOff, H2), Committed(paused, H3, state: LibraryFileState.Unreadable)));

        Assert.Equal("GitHub", Winner(composition, "get hub"));
        Assert.Equal("Kubernetes", Winner(composition, "kube"));
        Assert.Equal("Shipped", Winner(composition, "paused"));
        Assert.Equal(["github", "bbb-row-off"], composition.EnabledLibraries.Select(l => l.Id));

        var draft = Draft(3, State(enabled: ["github", "aaa-off"]), Draft(github), Draft(off, pendingDelete: true));
        Assert.Equal("GitHub", Winner(LibraryComposition.Preview(draft, [], new GlossaryBudget(80)), "get hub"));
    }

    // --- Legacy markers (decision 3, C-3) ---

    [Fact]
    public void An_active_legacy_marker_keeps_the_built_in_result_and_Use_my_spelling_flips_only_that_row()
    {
        var github = BuiltInLibrary("github", Shipped("get hub", "GitHub"), Shipped("copilot", "GitHub Copilot"));
        var team = CustomLibrary("team", Custom("get hub", "GitHub Enterprise"), Custom("copilot", "Copilot"));
        LibraryCatalog WithMarkers(params (string, string)[] markers) => Catalog(
            State(enabled: ["github", "team"], markers: markers, accepted: [("team", H1)]), Committed(github), Committed(team, H1));

        var both = Compose(WithMarkers(("team", "get hub"), ("team", "copilot")));
        Assert.Equal("GitHub", Winner(both, "get hub"));
        Assert.Equal("GitHub Copilot", Winner(both, "copilot"));
        Assert.True(both.AnyLegacyMarkerActive);
        Assert.True(both.StatusOf("team", Key("get hub")).LegacyMarkerActive);
        Assert.Equal(TermMarker.Check, both.StatusOf("team", Key("get hub")).Marker);

        // Use my spelling on "get hub" removes that marker only.
        var one = Compose(WithMarkers(("team", "copilot")));
        Assert.Equal("GitHub Enterprise", Winner(one, "get hub"));
        Assert.Equal("team", WinnerLibrary(one, "get hub"));
        Assert.Equal("GitHub Copilot", Winner(one, "copilot"));
        Assert.Equal(TermWinner.ThisRow, one.StatusOf("team", Key("get hub")).Winner);
        Assert.Equal(TermMarker.Check, one.StatusOf("team", Key("copilot")).Marker);
    }

    [Fact]
    public void A_turned_off_built_in_deactivates_its_markers()
    {
        var github = BuiltInLibrary("github", Shipped("get hub", "GitHub"));
        var team = CustomLibrary("team", Custom("get hub", "GitHub Enterprise"));
        var composition = Compose(Catalog(
            State(enabled: ["team"], markers: [("team", "get hub")], accepted: [("team", H1)]), Committed(github), Committed(team, H1)));

        Assert.Equal("GitHub Enterprise", Winner(composition, "get hub"));
        Assert.False(composition.AnyLegacyMarkerActive);
        Assert.False(composition.StatusOf("team", Key("get hub")).LegacyMarkerActive);
    }

    [Fact]
    public void A_marker_is_inactive_where_the_built_in_gives_the_same_result_and_word_boundaries_count_as_a_difference()
    {
        var github = BuiltInLibrary("github", Shipped("get hub", "GitHub"), Shipped("hub", "Hub"));
        var team = CustomLibrary("team", Custom("get hub", "GitHub"), Custom("hub", "Hub", wholeWord: false));
        var composition = Compose(Catalog(
            State(enabled: ["github", "team"], markers: [("team", "get hub"), ("team", "hub")], accepted: [("team", H1)]),
            Committed(github), Committed(team, H1)));

        // The same result: the authored row supplies it, which changes no output.
        Assert.Equal("team", WinnerLibrary(composition, "get hub"));
        Assert.False(composition.StatusOf("team", Key("get hub")).LegacyMarkerActive);

        // Whole word differs: the built-in's result stands (decision 3).
        Assert.Equal("github", WinnerLibrary(composition, "hub"));
        Assert.True(composition.StatusOf("team", Key("hub")).LegacyMarkerActive);
    }

    [Fact]
    public void A_marker_only_counts_while_its_library_is_in_use()
    {
        var github = BuiltInLibrary("github", Shipped("get hub", "GitHub"));
        var team = CustomLibrary("team", Custom("get hub", "GitHub Enterprise"));
        var composition = Compose(Catalog(
            State(enabled: ["github"], markers: [("team", "get hub")], accepted: [("team", H1)]), Committed(github), Committed(team, H1)));

        Assert.False(composition.AnyLegacyMarkerActive);
        Assert.False(composition.StatusOf("team", Key("get hub")).LegacyMarkerActive);
    }

    [Fact]
    public void Content_the_state_never_accepted_composes_with_the_markers_the_upgrade_would_give_it_and_no_AI_permission()
    {
        // The stored marker was for other content; the file now holds rows that contradict the built-in differently.
        var github = BuiltInLibrary("github", Shipped("get hub", "GitHub"), Shipped("kube", "Kubernetes"));
        var team = CustomLibrary("team", Custom("get hub", "GitHub Enterprise"), Custom("kube", "K8s"), Custom("helm", "Helm"));
        var catalog = Catalog(
            State(enabled: ["github", "team"], ai: [("team", true)], markers: [("team", "copilot")], accepted: [("team", H1)]),
            Committed(github), Committed(team, H2));

        var composition = Compose(catalog);

        Assert.Equal("GitHub", Winner(composition, "get hub"));
        Assert.Equal("Kubernetes", Winner(composition, "kube"));
        Assert.Equal("Helm", Winner(composition, "helm"));
        Assert.DoesNotContain(composition.AiLibraryEntries, entry => entry.Replacement == "Helm");
        Assert.Contains("team", composition.AiExcludedLibraryIds);
    }

    // --- The key (A10, C-12b) ---

    [Theory]
    [InlineData("get  hub")]
    [InlineData("get\thub")]
    [InlineData("get\u00A0hub")]
    [InlineData("get\u3000hub")]
    [InlineData("get\r\nhub")]
    public void A_legacy_row_with_irregular_spacing_stays_its_own_term_and_never_takes_the_rule_of_a_row_that_matches(string legacy)
    {
        var alpha = CustomLibrary("alpha", Custom(legacy, "Alpha"));
        var beta = CustomLibrary("beta", Custom("get hub", "Beta"));
        var composition = Compose(Catalog(
            State(enabled: ["alpha", "beta"], accepted: [("alpha", H1), ("beta", H2)]), Committed(alpha, H1), Committed(beta, H2)));

        Assert.Equal(2, composition.Rules.Count);
        Assert.Equal("Beta", Winner(composition, "get hub"));
        Assert.Equal(["i pushed to Beta"], Dictation.Write([], composition.LibraryEntries, ["i pushed to get hub"]));

        // Once the user edits Alpha's row, the editor commits it in normal form, and precedence decides.
        var edited = CustomLibrary("alpha", Custom(LibraryTermKey.Normalize(legacy), "Alpha"));
        var afterEdit = Compose(Catalog(
            State(enabled: ["alpha", "beta"], accepted: [("alpha", H3), ("beta", H2)]), Committed(edited, H3), Committed(beta, H2)));
        Assert.Single(afterEdit.Rules);
        Assert.Equal(["i pushed to Alpha"], Dictation.Write([], afterEdit.LibraryEntries, ["i pushed to get hub"]));
    }

    [Fact]
    public void A_built_in_row_the_user_renamed_competes_as_its_new_spoken_form_and_the_old_one_falls_to_the_next_source()
    {
        // Astra's question 2 (C-18).
        var github = BuiltInLibrary("github", Edited("get hub", "GitHub", "git hub", "GitHub"));
        var beta = CustomLibrary("beta", Custom("get hub", "Get Hub Beta"), Custom("git hub", "Git Hub Beta"));
        var composition = Compose(Catalog(
            State(enabled: ["github", "beta"], accepted: [("beta", H1)]), Committed(github), Committed(beta, H1)));

        Assert.Equal("Get Hub Beta", Winner(composition, "get hub"));
        Assert.Equal("GitHub", Winner(composition, "git hub"));
        Assert.Equal("github", WinnerLibrary(composition, "git hub"));

        // The row is found by its identity, the shipped spoken form, and reports what it competes as.
        var status = composition.StatusOf("github", Key("get hub"));
        Assert.Equal(TermWinner.ThisRow, status.Winner);
        Assert.Equal(TermMarker.Changed, status.Marker);
        Assert.Equal(["beta"], status.DifferentResultIn);
    }

    // --- Statuses (C-13) ---

    [Fact]
    public void Each_marker_has_its_rule_and_the_most_urgent_one_shows()
    {
        var shipped = new TermValues("copilot", "Copilot");
        var yours = new TermValues("copilot", "GitHub Copilot");
        var review = new TermReview(yours, shipped with { Written = "Microsoft Copilot" }, TermFields.Written);
        var reviewed = new LibraryRow(Key("copilot"), yours, TermOrigin.Edited, shipped,
            new BuiltInTermEdit(Key("copilot"), BuiltInTermIntent.Edited, shipped, yours), review);
        var github = BuiltInLibrary("github",
            reviewed, Shipped("get hub", "GitHub"), Edited("actions", "Actions", "actions", "GitHub Actions"),
            TurnedOff("gist", "Gist"), Shipped("same", "Same"), Shipped("unique off", "Unique"));
        var team = CustomLibrary("team",
            Custom("get hub", "GitHub Enterprise"), Custom("kube", "K8s"), Custom("gist", "Gist Team"), Custom("um", ""),
            Custom("same", "Same"), Custom("mine", "Mine"), Custom("dropped", "Dropped", enabled: false));
        var zeta = CustomLibrary("zeta", Custom("kube", "Kubernetes"), Custom("same", "Same"), Custom("mine", "Other"));
        var composition = Compose(
            Catalog(
                State(
                    enabled: ["github", "team", "zeta"],
                    markers: [("team", "get hub")],
                    accepted: [("team", H1), ("zeta", H2)]),
                Committed(github), Committed(team, H1), Committed(zeta, H2)),
            DictionaryEntry.New("mine", "Dictionary Mine"));

        Assert.Equal(TermMarker.Update, composition.StatusOf("github", Key("copilot")).Marker);
        Assert.Same(review, composition.StatusOf("github", Key("copilot")).Review);
        Assert.Equal(TermMarker.Check, composition.StatusOf("team", Key("get hub")).Marker);
        Assert.Equal(TermMarker.NotUsed, composition.StatusOf("zeta", Key("kube")).Marker);   // another library, differently
        Assert.Equal(TermMarker.NotUsed, composition.StatusOf("team", Key("mine")).Marker);   // the dictionary, differently
        Assert.Equal(TermMarker.OffHere, composition.StatusOf("github", Key("gist")).Marker);
        Assert.Equal(TermMarker.Removes, composition.StatusOf("team", Key("um")).Marker);
        Assert.Equal(TermMarker.Changed, composition.StatusOf("github", Key("actions")).Marker);

        // Identical results elsewhere stay silent and are listed: the shipped "same" loses to the authored copy.
        var same = composition.StatusOf("github", Key("same"));
        Assert.Equal(TermMarker.None, same.Marker);
        Assert.Equal(TermWinner.OtherLibrary, same.Winner);
        Assert.Equal("team", same.WinningLibraryId);
        Assert.Equal(["team", "zeta"], same.SameResultIn);
        Assert.Empty(same.DifferentResultIn);

        var winner = composition.StatusOf("team", Key("kube"));
        Assert.Equal(TermWinner.ThisRow, winner.Winner);
        Assert.Equal(TermMarker.None, winner.Marker);
        Assert.Equal(["zeta"], winner.DifferentResultIn);

        var dictionary = composition.StatusOf("zeta", Key("mine"));
        Assert.Equal(TermWinner.Dictionary, dictionary.Winner);
        Assert.Null(dictionary.WinningLibraryId);
        Assert.Equal("Dictionary Mine", dictionary.WinningEntry?.Replacement);

        // A turned-off row nobody else applies says nothing about being off elsewhere.
        var dropped = composition.StatusOf("team", Key("dropped"));
        Assert.Equal(TermWinner.None, dropped.Winner);
        Assert.Equal(TermMarker.None, dropped.Marker);
        Assert.Equal(GlossaryInclusion.NotApplied, dropped.Glossary);

        // An unknown library or row has no winner and no marker.
        Assert.Equal(TermWinner.None, composition.StatusOf("missing", Key("kube")).Winner);
        Assert.Equal(TermWinner.None, composition.StatusOf("team", Key("missing")).Winner);
    }

    [Fact]
    public void With_nothing_edited_the_shipped_built_ins_raise_no_marker()
    {
        // The 181 spoken forms shipped libraries share give identical results, so they stay silent.
        var builtIns = BuiltInDictionaryLibraries.All
            .Select(library => new LibraryContent(library.Id, true, library.Name, library.Category, library.Description,
                [.. library.Entries.Select(entry => Shipped(entry.Pattern, entry.Replacement, entry.WholeWord))]))
            .ToList();
        var composition = Compose(Catalog(
            State(enabled: builtIns.Select(l => l.Id)), [.. builtIns.Select(content => Committed(content))]));

        var shared = builtIns.SelectMany(l => l.Rows.Select(r => r.Key)).GroupBy(k => k).Count(g => g.Count() > 1);
        Assert.Equal(181, shared);
        foreach (var library in builtIns)
        {
            foreach (var row in library.Rows)
            {
                var status = composition.StatusOf(library.Id, row.Key);
                Assert.True(status.Marker == TermMarker.None, $"{library.Id}: {row.Key.Value} shows {status.Marker}");
                Assert.Empty(status.DifferentResultIn);
            }
        }
    }

    // --- Badges and the Save prompt (C-8) ---

    [Fact]
    public void The_Save_prompt_and_the_badges_name_the_library_that_supplies_the_rule()
    {
        // W1a's disclosed quirk: an earlier library listing a spoken form only in a turned-off row took the name.
        var v2 = CustomLibrary("team-terms-2", Custom("pipeline", "Pipeline", enabled: false));
        var v1 = CustomLibrary("team-terms", Custom("pipeline", "Pipelines"));
        v2 = v2 with { Name = "Team terms v2" };
        v1 = v1 with { Name = "Team terms" };
        var composition = Compose(Catalog(
            State(enabled: ["team-terms-2", "team-terms"], accepted: [("team-terms-2", H1), ("team-terms", H2)]),
            Committed(v2, H1), Committed(v1, H2)));
        DictionaryEntry[] personal = [DictionaryEntry.New("pipeline", "Pipelines")];

        var overlap = Assert.Single(composition.OverlapReport(personal).Overlaps);
        Assert.Equal(DictionaryOverlapKind.Redundant, overlap.Kind);
        Assert.Equal("Team terms", overlap.LibraryId);
        Assert.Equal("Team terms", composition.Coverage()["PIPELINE"].LibraryName);
        Assert.Equal("team-terms.csv", composition.Coverage()["pipeline"].FileName);

        // The analyzer over 0.4.3's model names it the same way now.
        var libraries = new[]
        {
            new DictionaryLibrary("team-terms-2", "Team terms v2", "Custom", null, false, [new DictionaryEntry(0, "pipeline", "Pipeline", true, false)]),
            new DictionaryLibrary("team-terms", "Team terms", "Custom", null, false, [DictionaryEntry.New("pipeline", "Pipelines")]),
        };
        var legacy = Assert.Single(DictionaryLibraryOverlapAnalyzer.AnalyzeEnabledLibraries(personal, libraries, ["team-terms", "team-terms-2"]).Overlaps);
        Assert.Equal("Team terms", legacy.LibraryId);
    }

    // --- The Show filter ---

    [Fact]
    public void The_Show_filter_keeps_the_rows_it_names_in_saved_order()
    {
        var shipped = new TermValues("copilot", "Copilot");
        var review = new TermReview(shipped with { Written = "Mine" }, shipped with { Written = "New" }, TermFields.Written);
        var github = BuiltInLibrary("github",
            Shipped("get hub", "GitHub"), TurnedOff("gist", "Gist"), Added("octo", "Octocat"),
            new LibraryRow(Key("copilot"), shipped with { Written = "Mine" }, TermOrigin.Edited, shipped,
                new BuiltInTermEdit(Key("copilot"), BuiltInTermIntent.Edited, shipped, shipped with { Written = "Mine" }), review));
        var team = CustomLibrary("team", Custom("get hub", "GitHub Enterprise"), Custom("kube", "K8s", enabled: false));
        var composition = Compose(Catalog(
            State(enabled: ["github", "team"], markers: [("team", "get hub")], accepted: [("team", H1)]),
            Committed(github), Committed(team, H1)));

        Assert.Equal(["get hub", "gist", "octo", "copilot"], composition.Filter("github", TermFilter.All).Select(k => k.Value));
        Assert.Equal(["gist", "octo", "copilot"], composition.Filter("github", TermFilter.ChangedByYou).Select(k => k.Value));
        Assert.Equal(["gist"], composition.Filter("github", TermFilter.TurnedOff).Select(k => k.Value));
        Assert.Equal(["copilot"], composition.Filter("github", TermFilter.NeedsAttention).Select(k => k.Value));
        Assert.Equal(["get hub"], composition.Filter("team", TermFilter.NeedsAttention).Select(k => k.Value));
        Assert.Equal(["kube"], composition.Filter("team", TermFilter.TurnedOff).Select(k => k.Value));
        Assert.Empty(composition.Filter("missing", TermFilter.All));
    }

    // --- Previews and physical names ---

    [Fact]
    public void A_preview_names_its_revision_and_ranks_a_remapped_file_by_its_physical_name()
    {
        // A16: epsilon.csv and a hand-placed github.csv both supply "project token"; 0.4.3 writes Epsilon.
        var epsilon = CustomLibrary("epsilon", Custom("project token", "Epsilon"));
        var twin = CustomLibrary("custom-github", Custom("project token", "Twin"));
        var state = State(enabled: ["epsilon", "custom-github"], accepted: [("epsilon", H1), ("custom-github", H2)]);

        var committed = Compose(Catalog(7, state, Committed(twin, H2, fileName: "github.csv"), Committed(epsilon, H1)));
        var preview = LibraryComposition.Preview(
            Draft(12, state, Draft(twin, fileName: "github.csv"), Draft(epsilon)), [], new GlossaryBudget(80));
        var byLogicalId = Compose(Catalog(7, state, Committed(twin, H2, fileName: "custom-github.csv"), Committed(epsilon, H1)));

        Assert.False(committed.IsPreview);
        Assert.Equal(7, committed.Basis);
        Assert.True(preview.IsPreview);
        Assert.Equal(12, preview.Basis);
        Assert.Equal("Epsilon", Winner(committed, "project token"));
        Assert.Equal("Epsilon", Winner(preview, "project token"));
        Assert.Equal("Twin", Winner(byLogicalId, "project token"));
        Assert.Equal(["epsilon", "custom-github"], committed.EnabledLibraries.Select(l => l.Id));
        Assert.Equal("github.csv", committed.EnabledLibraries[1].FileName);
    }

    [Fact]
    public void A_preview_takes_AI_permission_from_the_drafts_choices()
    {
        // Round 2, part 3: judged against the draft's base catalog, where team's file is accepted as it stands.
        var team = CustomLibrary("team", Custom("kube", "K8s"));
        var created = CustomLibrary("custom-new", Custom("helm", "Helm"));
        var state = State(enabled: ["team", "custom-new"], ai: [("team", true), ("custom-new", false)], accepted: [("team", H1)]);
        var committed = Catalog(state, Committed(team, H1));
        var preview = LibraryComposition.Preview(
            Draft(4, state, Draft(team), Draft(created, LibraryOrigin.Created)), committed, [], new GlossaryBudget(80));

        Assert.Equal(["K8s"], preview.AiLibraryEntries.Select(e => e.Replacement));
        Assert.Equal(["custom-new"], preview.AiExcludedLibraryIds);
        Assert.Equal(GlossaryInclusion.NotPermitted, preview.StatusOf("custom-new", Key("helm")).Glossary);
    }

    // --- The content check in a preview (round 2, part 3: Astra's review of D) ---

    [Theory]
    [InlineData("AI box", false)]
    [InlineData("enabled box", false)]
    [InlineData("AI box", true)]
    [InlineData("enabled box", true)]
    public void A_library_whose_only_draft_change_is_its_AI_box_or_enabled_box_is_not_permitted_while_its_committed_content_is_not_accepted(
        string change, bool builtIn)
    {
        // The committed file (H2) is not the content the state accepted (H1): replaced outside Scribe, not yet adopted.
        // The draft changes only a box, so its Save writes nothing and the file stays H2: the preview, like the result
        // after Save, must not permit it, although the box change marks the library unsaved.
        var library = builtIn
            ? BuiltInLibrary("github", Edited("get hub", "GitHub", "get hub", "Nightjar Hub"))
            : CustomLibrary("team", Custom("get hub", "Nightjar Hub"));
        var id = library.Id;
        var before = change == "AI box"
            ? State(enabled: [id], ai: [(id, false)], accepted: [(id, H1)])
            : State(enabled: [], ai: [(id, true)], accepted: [(id, H1)]);
        var after = State(enabled: [id], ai: [(id, true)], accepted: [(id, H1)]);
        var committed = Catalog(before, Committed(library, H2));
        var draft = Draft(9, after, Draft(library) with { Unsaved = true });

        foreach (var preview in new[]
        {
            LibraryComposition.Preview(draft, committed, [], new GlossaryBudget(80)),
            LibraryComposition.Preview(draft, [], new GlossaryBudget(80)),
        })
        {
            Assert.Equal([id], preview.AiExcludedLibraryIds);
            Assert.Empty(preview.AiLibraryEntries);
            Assert.Equal(GlossaryInclusion.NotPermitted, preview.StatusOf(id, Key("get hub")).Glossary);
            Assert.Equal("Nightjar Hub", Winner(preview, "get hub"));
        }

        // The result after Save: the draft's state over the unchanged file.
        Assert.Equal([id], Compose(Catalog(after, Committed(library, H2))).AiExcludedLibraryIds);
    }

    [Fact]
    public void A_library_the_drafts_Save_writes_is_judged_by_the_drafts_choice_whatever_its_committed_file_holds()
    {
        // Each committed file here (H2) is not the accepted content (H1); the draft rewrites each one, so its Save records
        // the new hash and the choice applies. Libraries the committed catalog does not hold are written too.
        var team = CustomLibrary("team", Custom("kube", "K8s"));
        var renamed = CustomLibrary("renamed", Custom("helm", "Helm"));
        var github = BuiltInLibrary("github", Edited("get hub", "GitHub", "get hub", "Nightjar Hub"));
        string[] brought = ["custom-created", "custom-imported", "custom-duplicate", "custom-restored"];
        var state = State(
            enabled: ["team", "renamed", "github", .. brought],
            ai: [("team", true), ("renamed", true), ("github", true), .. brought.Select(b => (b, true))],
            accepted: [("team", H1), ("renamed", H1), ("github", H1)]);
        var committed = Catalog(state, Committed(team, H2), Committed(renamed, H2), Committed(github, H2));
        var draft = Draft(
            10, state,
            Draft(team with { Rows = [.. team.Rows, Custom("argo", "Argo")] }) with { Unsaved = true },
            Draft(renamed with { Name = "Renamed library" }) with { Unsaved = true },
            Draft(github with { Rows = [.. github.Rows, Added("octo cat", "Octocat")] }) with { Unsaved = true },
            Draft(CustomLibrary("custom-created", Custom("alpha", "Alpha")), LibraryOrigin.Created),
            Draft(CustomLibrary("custom-imported", Custom("beta", "Beta")), LibraryOrigin.Imported),
            Draft(CustomLibrary("custom-duplicate", Custom("gamma", "Gamma")), LibraryOrigin.Duplicated),
            Draft(CustomLibrary("custom-restored", Custom("delta", "Delta")), LibraryOrigin.Restored));

        var preview = LibraryComposition.Preview(draft, committed, [], new GlossaryBudget(80));

        Assert.Empty(preview.AiExcludedLibraryIds);
        Assert.Equal(
            ["Alpha", "Argo", "Beta", "Delta", "Gamma", "Helm", "K8s", "Nightjar Hub", "Octocat"],
            preview.AiLibraryEntries.Select(e => e.Replacement).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Without_its_committed_catalog_a_preview_permits_only_the_libraries_its_draft_brings_in()
    {
        // The overload without the committed catalog cannot see which files the Save leaves as they are, nor what they
        // hold, so it fails closed for every committed library; with the catalog, the accepted file is permitted.
        var team = CustomLibrary("team", Custom("kube", "K8s"));
        var github = BuiltInLibrary("github", Shipped("get hub", "GitHub"));
        var created = CustomLibrary("custom-created", Custom("helm", "Helm"));
        var state = State(enabled: ["team", "github", "custom-created"], ai: [("team", true), ("custom-created", true)], accepted: [("team", H1)]);
        var committed = Catalog(state, Committed(team, H1), Committed(github));
        var draft = Draft(11, state, Draft(team), Draft(github), Draft(created, LibraryOrigin.Created));

        var withCatalog = LibraryComposition.Preview(draft, committed, [], new GlossaryBudget(80));
        var without = LibraryComposition.Preview(draft, [], new GlossaryBudget(80));

        Assert.Empty(withCatalog.AiExcludedLibraryIds);
        Assert.Equal(["GitHub", "Helm", "K8s"], withCatalog.AiLibraryEntries.Select(e => e.Replacement).Order(StringComparer.Ordinal));
        Assert.Equal(["github", "team"], without.AiExcludedLibraryIds.Order(StringComparer.Ordinal));
        Assert.Equal(["Helm"], without.AiLibraryEntries.Select(e => e.Replacement));
    }

    [Fact]
    public void A_preview_refuses_a_committed_catalog_the_draft_was_not_built_from()
    {
        var team = CustomLibrary("team", Custom("kube", "K8s"));
        var state = State(enabled: ["team"], ai: [("team", true)], accepted: [("team", H1)]);
        var draft = Draft(12, state, Draft(team));

        Assert.Throws<ArgumentException>(() =>
            LibraryComposition.Preview(draft, Catalog(draft.BaseGeneration + 1, state, Committed(team, H1)), [], new GlossaryBudget(80)));
    }

    [Fact]
    public void An_unchanged_library_whose_committed_content_is_not_accepted_previews_with_the_markers_of_the_upgrade()
    {
        // A4 in a preview: the file the Save leaves in place composes with a discovered file's defaults, AI off and the
        // markers the upgrade would give it, exactly as the committed composition does after that Save.
        var github = BuiltInLibrary("github", Shipped("get hub", "GitHub"));
        var team = CustomLibrary("team", Custom("get hub", "TeamHub"));
        var before = State(enabled: ["github"], ai: [("team", true)], accepted: [("team", H1)]);
        var after = State(enabled: ["github", "team"], ai: [("team", true)], accepted: [("team", H1)]);
        var committed = Catalog(before, Committed(github), Committed(team, H2));

        var preview = LibraryComposition.Preview(
            Draft(13, after, Draft(github), Draft(team) with { Unsaved = true }), committed, [], new GlossaryBudget(80));

        Assert.Equal("GitHub", Winner(preview, "get hub"));
        Assert.True(preview.StatusOf("team", Key("get hub")).LegacyMarkerActive);
        Assert.Equal(["team"], preview.AiExcludedLibraryIds);
        var saved = Compose(Catalog(after, Committed(github), Committed(team, H2)));
        Assert.Equal(Winner(saved, "get hub"), Winner(preview, "get hub"));

        // Content the Save writes takes the draft's markers (none here), so the authored row comes first.
        var rewritten = LibraryComposition.Preview(
            Draft(14, after, Draft(github), Draft(team with { Name = "Team" }) with { Unsaved = true }), committed, [], new GlossaryBudget(80));
        Assert.Equal("TeamHub", Winner(rewritten, "get hub"));
    }
}
