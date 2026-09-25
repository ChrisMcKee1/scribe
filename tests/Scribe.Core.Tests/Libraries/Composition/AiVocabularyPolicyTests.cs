using Scribe.Core.Libraries;
using Scribe.Core.Models;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// Which libraries AI cleanup may carry (W1b contracts 3.3.3): permission fails closed (C-5), the AI subset is a filter
/// of the winners (C-6), the scope while saving only narrows (C-16), and a request is bound to the content its
/// permission covered (C-16b).
/// </summary>
public sealed class AiVocabularyPolicyTests
{
    [Fact]
    public void Unreadable_newer_and_lost_states_permit_nothing_and_a_canary_library_is_applied_but_never_sent()
    {
        var canary = CustomLibrary("canary", Custom("nightjar", "Nightjar Canary"));
        var github = BuiltInLibrary("github", Shipped("get hub", "GitHub"));
        foreach (var health in new[] { LocalStateHealth.Unreadable, LocalStateHealth.Newer })
        {
            var state = State(enabled: ["canary", "github"], ai: [("canary", true), ("github", true)], accepted: [("canary", H1)], health: health);
            var vocabulary = LibraryComposer.Instance.ComposeVocabulary(Catalog(state, Committed(canary, H1), Committed(github)));

            Assert.Contains(vocabulary.Entries, e => e.Replacement == "Nightjar Canary");
            Assert.Empty(vocabulary.AiEntries);
            Assert.Empty(vocabulary.AiScope.PermittedLibraryIds);
            Assert.False(AiVocabularyPolicy.IsPermitted(state, "github", builtIn: true, content: null));
        }

        // A lost state denies everything nobody chose again, built-ins included; an explicit choice still counts.
        var lost = State(enabled: ["canary", "github"], ai: [("canary", true)], accepted: [("canary", H1)], lost: true);
        var afterLoss = LibraryComposer.Instance.ComposeVocabulary(Catalog(lost, Committed(canary, H1), Committed(github)));
        Assert.Equal(["Nightjar Canary"], afterLoss.AiEntries.Select(e => e.Replacement));
        Assert.False(AiVocabularyPolicy.IsPermitted(lost, "github", builtIn: true, content: null));
        Assert.Equal(["canary"], afterLoss.AiScope.PermittedLibraryIds);
    }

    [Fact]
    public void Permission_is_decided_in_order_health_content_explicit_choice_loss_and_then_the_kind_default()
    {
        var state = State(ai: [("team", false), ("github", false), ("chosen", true)], accepted: [("team", H1), ("chosen", H1), ("edited", H2)]);

        Assert.False(AiVocabularyPolicy.IsPermitted(state, "team", false, H1));       // explicit off
        Assert.True(AiVocabularyPolicy.IsPermitted(state, "chosen", false, H1));      // explicit on
        Assert.False(AiVocabularyPolicy.IsPermitted(state, "chosen", false, H2));     // other content (A4)
        Assert.False(AiVocabularyPolicy.IsPermitted(state, "chosen", false, null));   // bytes never read
        Assert.False(AiVocabularyPolicy.IsPermitted(state, "unrecorded", false, H3)); // no accepted entry
        Assert.False(AiVocabularyPolicy.IsPermitted(state, "github", true, null));    // explicit off beats the built-in default
        Assert.True(AiVocabularyPolicy.IsPermitted(state, "microsoft-365", true, null));   // built-in default, no document
        Assert.True(AiVocabularyPolicy.IsPermitted(state, "edited", true, H2));       // an accepted edits document
        Assert.False(AiVocabularyPolicy.IsPermitted(state, "edited", true, H3));      // a replaced edits document
        Assert.False(AiVocabularyPolicy.IsPermitted(state, "other-built-in", true, H3));   // a document never accepted

        // A custom library recorded without a choice meets the default for a discovered file: off.
        var recordedWithoutChoice = State(accepted: [("team", H1)]);
        Assert.False(AiVocabularyPolicy.IsPermitted(recordedWithoutChoice, "team", false, H1));
        Assert.True(AiVocabularyPolicy.IsPermitted(LibraryLocalState.Absent, "github", true, null));
        Assert.False(AiVocabularyPolicy.IsPermitted(LibraryLocalState.Absent, "team", false, H1));
    }

    [Fact]
    public void The_scope_pairs_every_permitted_library_in_use_with_the_content_its_permission_covers()
    {
        var catalog = Catalog(
            9,
            State(
                enabled: ["github", "team", "locked", "paused", "denied"],
                ai: [("team", true), ("locked", true), ("paused", true), ("denied", false), ("off", true)],
                accepted: [("team", H1), ("locked", H2), ("paused", H3), ("off", H4), ("denied", H1), ("microsoft-365", H2)]),
            Committed(BuiltInLibrary("github", Shipped("get hub", "GitHub"))),
            Committed(BuiltInLibrary("microsoft-365", Shipped("teams", "Teams"))),
            Committed(CustomLibrary("team", Custom("kube", "K8s")), H1),
            Committed(CustomLibrary("locked", Custom("helm", "Helm")), H2, state: LibraryFileState.AwaitingRelease),
            Committed(CustomLibrary("paused"), H3, state: LibraryFileState.Unreadable),
            Committed(CustomLibrary("off", Custom("gone", "Gone")), H4),
            Committed(CustomLibrary("denied", Custom("no", "No")), H1));

        var scope = AiVocabularyPolicy.ScopeOf(catalog);

        Assert.Equal(9, scope.Generation);
        Assert.Equal(["github", "locked", "team"], scope.PermittedLibraryIds.Order(StringComparer.Ordinal));
        Assert.Null(scope.PermittedContent["github"]);
        Assert.Equal(H1, scope.PermittedContent["team"]);
        Assert.Equal(H2, scope.PermittedContent["locked"]);
        Assert.Equal(scope.PermittedLibraryIds, LibraryComposer.Instance.ComposeVocabulary(catalog).AiScope.PermittedLibraryIds);
    }

    [Fact]
    public void Across_random_catalogs_the_AI_entries_are_a_filter_of_the_entries_in_the_same_order()
    {
        // C-6: the glossary can never teach the model a spelling local replacement would not write.
        var random = new Random(20260926);
        string[] forms = ["get hub", "kube", "helm", "north star", "copilot", "azure", "llm", "sprint"];
        for (var round = 0; round < 200; round++)
        {
            var libraries = new List<CatalogLibrary>();
            var enabled = new List<string>();
            var ai = new List<(string, bool)>();
            var accepted = new List<(string, LibraryContentHash)>();
            foreach (var id in new[] { "ai-terminology", "github", "alpha", "beta", "gamma" }.Where(_ => random.Next(4) != 0))
            {
                var builtIn = id is "ai-terminology" or "github";
                var rows = forms.Where(_ => random.Next(3) == 0)
                    .Select(form => builtIn
                        ? (random.Next(3) == 0 ? Edited(form, "Shipped " + form, form, "Edited " + form) : Shipped(form, "Shipped " + form))
                        : Custom(form, id + " " + form, enabled: random.Next(8) != 0))
                    .ToArray();
                var content = builtIn ? BuiltInLibrary(id, rows) : CustomLibrary(id, rows);
                LibraryContentHash? hash = builtIn ? (random.Next(3) == 0 ? H2 : null) : H1;
                libraries.Add(Committed(content, hash, state: random.Next(10) == 0 ? LibraryFileState.Unreadable : LibraryFileState.Available));
                if (random.Next(4) != 0)
                {
                    enabled.Add(id);
                }

                if (random.Next(3) != 0)
                {
                    ai.Add((id, random.Next(2) == 0));
                }

                if (hash is { } content2 && random.Next(5) != 0)
                {
                    accepted.Add((id, content2));
                }
            }

            var state = State(enabled: enabled, ai: ai, accepted: accepted, lost: random.Next(6) == 0,
                health: random.Next(10) == 0 ? LocalStateHealth.Unreadable : LocalStateHealth.Ok);
            var catalog = Catalog(state, [.. libraries]);
            var vocabulary = LibraryComposer.Instance.ComposeVocabulary(catalog);
            var composition = Compose(catalog);

            Assert.Equal(composition.LibraryEntries, vocabulary.Entries);
            var expected = composition.Rules
                .Where(rule => vocabulary.AiScope.PermittedLibraryIds.Contains(rule.LibraryId))
                .Select(rule => rule.Entry)
                .ToList();
            Assert.Equal(expected, vocabulary.AiEntries);
            Assert.True(IsSubsequence(vocabulary.AiEntries, vocabulary.Entries), $"round {round}: not a filter in order");
            foreach (var id in vocabulary.AiScope.PermittedLibraryIds)
            {
                var library = catalog.Find(id)!;
                Assert.True(AiVocabularyPolicy.IsPermitted(state, id, library.Content.BuiltIn, library.ContentHash), $"round {round}: {id}");
            }
        }
    }

    // --- The scope while saving (C-16, Astra N5) ---

    [Fact]
    public void While_saving_the_scope_only_narrows_whatever_the_change_set_grants()
    {
        var committed = Catalog(
            State(
                enabled: ["github", "team", "notes", "draft"],
                ai: [("team", true), ("notes", true), ("draft", false), ("private", true)],
                accepted: [("team", H1), ("notes", H2), ("draft", H3), ("private", H4)]),
            Committed(BuiltInLibrary("github", Shipped("get hub", "GitHub"))),
            Committed(CustomLibrary("team", Custom("kube", "K8s")), H1),
            Committed(CustomLibrary("notes", Custom("helm", "Helm")), H2),
            Committed(CustomLibrary("draft", Custom("sprint", "Sprint")), H3),
            Committed(CustomLibrary("private", Custom("codename", "Codename")), H4));
        Assert.Equal(["github", "notes", "team"], AiVocabularyPolicy.ScopeOf(committed).PermittedLibraryIds.Order(StringComparer.Ordinal));

        // The Save turns github off, deletes team, takes notes' permission away, grants draft and turns private on, and
        // writes new content for team (moot, it is deleted) and for a created library it permits.
        var next = State(
            enabled: ["notes", "draft", "private", "custom-new"],
            ai: [("notes", false), ("draft", true), ("private", true), ("custom-new", true)],
            accepted: [("notes", H2), ("draft", H3), ("private", H4)]);
        var changes = new LibraryChangeSet(
            5, 11,
            [new LibraryWrite("custom-new", false, LibraryOrigin.Created, null, CustomLibrary("custom-new", Custom("x", "X")))],
            [new LibraryDeletion("team", "team.csv", H1)],
            [],
            next,
            localStateChanged: true);

        var saving = LibraryComposer.Instance.ScopeWhileSaving(committed, changes);

        Assert.Empty(saving.PermittedLibraryIds);
        Assert.False(saving.Covers(AiVocabularyPolicy.ScopeOf(committed)));
    }

    [Fact]
    public void A_library_the_change_set_only_rewrites_keeps_its_committed_content_while_saving()
    {
        var committed = Catalog(
            State(enabled: ["team", "github"], ai: [("team", true)], accepted: [("team", H1)]),
            Committed(CustomLibrary("team", Custom("kube", "K8s")), H1),
            Committed(BuiltInLibrary("github", Shipped("get hub", "GitHub"))));
        var edited = new BuiltInLibraryEdits("github", [new BuiltInTermEdit(Key("get hub"), BuiltInTermIntent.Off, new TermValues("get hub", "GitHub"), null)]);
        var changes = new LibraryChangeSet(
            5, 12,
            [
                new LibraryWrite("team", false, LibraryOrigin.Existing, H1, CustomLibrary("team", Custom("kube", "Kubernetes"))),
                new LibraryWrite("github", true, LibraryOrigin.Existing, null, Edits: edited),
            ],
            [], [],
            committed.LocalState,
            localStateChanged: false);

        var saving = LibraryComposer.Instance.ScopeWhileSaving(committed, changes);

        Assert.Equal(H1, saving.PermittedContent["team"]);
        Assert.Null(saving.PermittedContent["github"]);
        Assert.True(saving.Covers(AiVocabularyPolicy.ScopeOf(committed)));
        Assert.Equal(5, saving.Generation);
    }

    [Fact]
    public void The_scope_while_saving_never_holds_a_library_the_committed_scope_lacks()
    {
        var random = new Random(20260927);
        string[] ids = ["github", "microsoft-365", "alpha", "beta", "gamma"];
        for (var round = 0; round < 300; round++)
        {
            LibraryLocalState RandomState() => State(
                enabled: ids.Where(_ => random.Next(3) != 0),
                ai: ids.Where(_ => random.Next(2) == 0).Select(id => (id, random.Next(2) == 0)),
                accepted: ids.Where(id => !id.Contains('-') && id != "github" && random.Next(4) != 0).Select(id => (id, H1)),
                lost: random.Next(8) == 0);
            var committed = Catalog(
                RandomState(),
                [.. ids.Select(id => id is "github" or "microsoft-365"
                    ? Committed(BuiltInLibrary(id, Shipped(id + " term", id)))
                    : Committed(CustomLibrary(id, Custom(id + " term", id)), H1))]);
            var deletions = ids.Where(id => id is not ("github" or "microsoft-365") && random.Next(5) == 0)
                .Select(id => new LibraryDeletion(id, id + ".csv", H1)).ToList();
            var next = RandomState();
            var changes = new LibraryChangeSet(5, 1, [], deletions, [], next, localStateChanged: true);

            var before = AiVocabularyPolicy.ScopeOf(committed);
            var saving = LibraryComposer.Instance.ScopeWhileSaving(committed, changes);

            Assert.True(before.Covers(saving), $"round {round}: the scope while saving widened");
            foreach (var id in saving.PermittedLibraryIds)
            {
                Assert.Contains(id, next.EnabledIds);
                Assert.DoesNotContain(deletions, deletion => deletion.LibraryId == id);
                Assert.True(AiVocabularyPolicy.IsPermittedByChoice(next, id, id is "github" or "microsoft-365"), $"round {round}: {id}");
                Assert.Equal(before.PermittedContent[id], saving.PermittedContent[id]);
            }
        }
    }

    // --- A request bound to content (C-16b, A12) ---

    [Fact]
    public void A_request_admitted_for_old_content_is_refused_after_a_replacement_even_once_the_new_content_is_permitted()
    {
        var github = BuiltInLibrary("github", Shipped("get hub", "GitHub"));
        var teamAtH1 = CustomLibrary("team", Custom("kube", "K8s"));
        var teamAtH2 = CustomLibrary("team", Custom("kube", "Kubernetes"));
        var atH1 = Catalog(1, State(enabled: ["github", "team"], ai: [("team", true)], accepted: [("team", H1)]), Committed(github), Committed(teamAtH1, H1));
        var source = new FakeVocabularySource(LibraryComposer.Instance.ComposeVocabulary(atH1));
        var admittedH1 = source.Current.AiScope;
        var sent = new List<string>();

        // The file is replaced outside Scribe by H2: the next load adopts it (ContentReplaced), which revokes it.
        var replaced = Catalog(2, atH1.LocalState, Committed(github), Committed(teamAtH2, H2));
        var adoption = LibraryComposer.Instance.PlanAdoption(replaced, new LibraryStateContext(false, false, true, true))!;
        Assert.Equal(LibraryAdoptionReasons.ContentReplaced, adoption.Reasons);
        source.Publish(LibraryComposer.Instance.ComposeVocabulary(Catalog(2, adoption.State, Committed(github), Committed(teamAtH2, H2))));

        Assert.True(LibraryComposer.Instance.HasNarrowed(admittedH1, source.Current.AiScope));
        Assert.False(source.TryHandOff(admittedH1, () => sent.Add("first attempt at H1")));

        // The user turns it back on and permits H2: a retry of the H1 request is still refused.
        var permittedH2 = LibraryLocalState.Create(
            adoption.State.EnabledIds.Append("team"), adoption.State.LegacyEnabledIds, [new("team", true)],
            adoption.State.LegacyMarkers, null, LocalStateHealth.Ok, adoption.State.AcceptedContent);
        source.Publish(LibraryComposer.Instance.ComposeVocabulary(Catalog(3, permittedH2, Committed(github), Committed(teamAtH2, H2))));
        Assert.True(source.Current.AiScope.PermittedLibraryIds.Contains("team"));
        Assert.True(LibraryComposer.Instance.HasNarrowed(admittedH1, source.Current.AiScope));
        Assert.False(source.TryHandOff(admittedH1, () => sent.Add("retry at H1")));

        // A request admitted at H2 goes.
        var admittedH2 = source.Current.AiScope;
        Assert.True(source.TryHandOff(admittedH2, () => sent.Add("at H2")));
        Assert.Equal(["at H2"], sent);
    }

    [Fact]
    public void A_usage_report_cached_for_old_content_is_refused_after_the_content_changes()
    {
        var team = CustomLibrary("team", Custom("kube", "Kubernetes"));
        var atH1 = Catalog(1, State(enabled: ["team"], ai: [("team", true)], accepted: [("team", H1)]), Committed(team, H1));
        var source = new FakeVocabularySource(LibraryComposer.Instance.ComposeVocabulary(atH1));
        var history = new[] { Entry(1, "Kubernetes rollout"), Entry(2, "Kubernetes again") };
        var report = Diagnostics.UsageReport.Build(_ => history, () => [], () => source.Current, periodDays: null, Now, CancellationToken.None);
        Assert.Contains(report.Snapshot.Terms, term => term is { Text: "Kubernetes", Shareable: true });
        Assert.Equal(H1, report.LibraryScope.PermittedContent["team"]);

        // The user's own Save changes the permitted library's content: once it completes, the old report is refused.
        var atH2 = Catalog(2, State(enabled: ["team"], ai: [("team", true)], accepted: [("team", H2)]), Committed(team with { }, H2));
        source.Publish(LibraryComposer.Instance.ComposeVocabulary(atH2));
        var sent = new List<string>();

        Assert.False(source.TryHandOff(report.LibraryScope, () => sent.Add("cached insight")));
        Assert.Empty(sent);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    // --- A built-in edits document deleted outside Scribe (round 2, Astra A1) ---

    [Fact]
    public void A_request_admitted_for_a_built_ins_edits_is_refused_once_the_edits_document_is_gone()
    {
        // github is on and permitted with its edits document H1, whose authored row writes a private term. A request is
        // admitted with those terms; then the document is deleted outside Scribe and the catalog reloaded.
        var edited = BuiltInLibrary("github", Edited("get hub", "GitHub", "get hub", "Nightjar Hub"), Shipped("octo cat", "Octocat"));
        var shippedOnly = BuiltInLibrary("github", Shipped("get hub", "GitHub"), Shipped("octo cat", "Octocat"));
        var state = State(enabled: ["github"], ai: [("github", true)], accepted: [("github", H1)]);
        var source = new FakeVocabularySource(LibraryComposer.Instance.ComposeVocabulary(Catalog(1, state, Committed(edited, H1))));
        var admitted = source.Current.AiScope;
        Assert.Equal(H1, admitted.PermittedContent["github"]);
        var sent = new List<string>();

        var deleted = Catalog(2, state, Committed(shippedOnly));
        source.Publish(LibraryComposer.Instance.ComposeVocabulary(deleted));

        // Before any adoption a built-in whose accepted document is gone is permitted for nothing (round 3, Astra A5):
        // the old request is refused, and not even the shipped rows are carried.
        Assert.False(source.Current.AiScope.PermittedContent.ContainsKey("github"));
        Assert.Empty(source.Current.AiEntries);
        Assert.True(LibraryComposer.Instance.HasNarrowed(admitted, source.Current.AiScope));
        Assert.False(source.TryHandOff(admitted, () => sent.Add("first attempt")));

        // The adoption reads the vanished document as replaced content: AI permission off, the stale hash gone, still on.
        var adoption = LibraryComposer.Instance.PlanAdoption(deleted, new LibraryStateContext(false, false, true, true))!;
        Assert.Equal(LibraryAdoptionReasons.ContentReplaced, adoption.Reasons);
        Assert.False(adoption.State.AiPermissions["github"]);
        Assert.False(adoption.State.AcceptedContent.ContainsKey("github"));
        Assert.Contains("github", adoption.State.EnabledIds);
        source.Publish(LibraryComposer.Instance.ComposeVocabulary(Catalog(3, adoption.State, Committed(shippedOnly))));
        Assert.False(source.TryHandOff(admitted, () => sent.Add("retry")));
        Assert.Empty(source.Current.AiEntries);
        Assert.Empty(sent);

        // Adopted, the state is settled: the next load has nothing more to record.
        Assert.Null(LibraryComposer.Instance.PlanAdoption(Catalog(3, adoption.State, Committed(shippedOnly)), new LibraryStateContext(false, false, true, true)));
    }

    [Fact]
    public void A_usage_report_cached_for_a_built_ins_edits_is_refused_once_the_edits_document_is_gone()
    {
        var edited = BuiltInLibrary("github", Edited("get hub", "GitHub", "get hub", "Nightjar Hub"));
        var state = State(enabled: ["github"], ai: [("github", true)], accepted: [("github", H1)]);
        var source = new FakeVocabularySource(LibraryComposer.Instance.ComposeVocabulary(Catalog(1, state, Committed(edited, H1))));
        var history = new[] { Entry(1, "the Nightjar Hub build"), Entry(2, "Nightjar Hub again") };
        var report = Diagnostics.UsageReport.Build(_ => history, () => [], () => source.Current, periodDays: null, Now, CancellationToken.None);
        Assert.Contains(report.Snapshot.Terms, term => term is { Text: "Nightjar Hub", Shareable: true });
        Assert.Equal(H1, report.LibraryScope.PermittedContent["github"]);

        source.Publish(LibraryComposer.Instance.ComposeVocabulary(
            Catalog(2, state, Committed(BuiltInLibrary("github", Shipped("get hub", "GitHub"))))));
        var sent = new List<string>();

        Assert.False(source.TryHandOff(report.LibraryScope, () => sent.Add("cached insight")));
        Assert.Empty(sent);
    }

    [Fact]
    public void A_built_in_whose_edits_document_cannot_be_read_is_not_taken_for_one_that_is_gone()
    {
        // Only an available built-in with no document has none; a paused or locked one may still have it. Until it can be
        // read, the content it holds is unknown and its accepted entry remains, so it is not permitted either (round 3).
        var state = State(enabled: ["github"], ai: [("github", true)], accepted: [("github", H1)]);
        var context = new LibraryStateContext(false, false, true, true);
        foreach (var fileState in new[] { LibraryFileState.Unreadable, LibraryFileState.AwaitingRelease, LibraryFileState.Newer })
        {
            var catalog = Catalog(2, state, Committed(BuiltInLibrary("github"), hash: null, state: fileState));
            Assert.Null(LibraryComposer.Instance.PlanAdoption(catalog, context));
        }

        Assert.False(AiVocabularyPolicy.IsPermitted(state, "github", builtIn: true, content: null));
    }

    [Fact]
    public void A_built_in_whose_edits_document_vanished_in_a_session_on_defaults_carries_nothing_to_AI_while_its_hash_remains()
    {
        // Round 3 (Astra A5): no adoption runs on defaults, so the healthy state still accepts H1 for github's edits
        // document, which turned the shipped "get hub" off, and it is gone. The shipped rows are back in the catalog; none
        // of them may reach AI cleanup until the user permits github again, while the unedited built-in and the accepted
        // custom library beside it stay permitted.
        var edited = BuiltInLibrary("github", TurnedOff("get hub", "GitHub"), Shipped("octo cat", "Octocat"));
        var shippedOnly = BuiltInLibrary("github", Shipped("get hub", "GitHub"), Shipped("octo cat", "Octocat"));
        var microsoft = Committed(BuiltInLibrary("microsoft-365", Shipped("teams", "Teams")));
        var team = Committed(CustomLibrary("team", Custom("kube", "K8s")), H2);
        var state = State(
            enabled: ["github", "microsoft-365", "team"], ai: [("github", true), ("team", true)], accepted: [("github", H1), ("team", H2)]);
        var admitted = LibraryComposer.Instance.ComposeVocabulary(Catalog(1, state, Committed(edited, H1), microsoft, team)).AiScope;
        var onDefaults = new LibraryStateContext(RunningOnDefaults: true, DatabaseRepaired: false, GenerationStored: true, CommitWitnessed: true);

        var vanished = Catalog(2, state, Committed(shippedOnly), microsoft, team);

        Assert.Null(LibraryComposer.Instance.PlanAdoption(vanished, onDefaults));
        Assert.False(AiVocabularyPolicy.IsPermitted(state, "github", builtIn: true, content: null));
        var vocabulary = LibraryComposer.Instance.ComposeVocabulary(vanished);
        Assert.Equal(["microsoft-365", "team"], vocabulary.AiScope.PermittedContent.Keys.Order(StringComparer.Ordinal));
        Assert.Null(vocabulary.AiScope.PermittedContent["microsoft-365"]);
        Assert.Equal(H2, vocabulary.AiScope.PermittedContent["team"]);
        Assert.Equal(["K8s", "Teams"], vocabulary.AiEntries.Select(e => e.Replacement).Order(StringComparer.Ordinal));
        Assert.Contains(vocabulary.Entries, entry => entry.Replacement == "GitHub");
        Assert.True(LibraryComposer.Instance.HasNarrowed(admitted, vocabulary.AiScope));
        var composition = LibraryComposition.Committed(vanished, [], new GlossaryBudget(80));
        Assert.Equal(GlossaryInclusion.NotPermitted, composition.StatusOf("github", Key("get hub")).Glossary);
    }

    [Fact]
    public void The_scope_pairs_each_permitted_library_with_the_hash_the_catalog_holds_not_a_stale_accepted_one()
    {
        // Round 2, part 2 (Grok G1), round 3 (Astra A5): github is on and permitted with its edits document H1 accepted;
        // the document is then gone (available, no content hash) while the state still accepts H1. A built-in with no
        // document passes the content check only when no accepted entry remains, so github is permitted for nothing, and
        // the admission at H1 is not covered. Every library the scope keeps is paired with the hash the catalog holds: H2
        // for the accepted custom file, none for the unedited built-in, and none for github once the user permits it again
        // after the entry is dropped.
        var state = State(
            enabled: ["github", "microsoft-365", "team"], ai: [("github", true), ("team", true)], accepted: [("github", H1), ("team", H2)]);
        var team = Committed(CustomLibrary("team", Custom("kube", "K8s")), H2);
        var microsoft = Committed(BuiltInLibrary("microsoft-365", Shipped("teams", "Teams")));
        var withDocument = Catalog(
            1, state, Committed(BuiltInLibrary("github", Edited("get hub", "GitHub", "get hub", "Nightjar Hub")), H1), microsoft, team);
        var withoutDocument = Catalog(2, state, Committed(BuiltInLibrary("github", Shipped("get hub", "GitHub"))), microsoft, team);

        var admitted = AiVocabularyPolicy.ScopeOf(withDocument);
        Assert.Equal(H1, admitted.PermittedContent["github"]);
        Assert.Null(admitted.PermittedContent["microsoft-365"]);
        Assert.Equal(H2, admitted.PermittedContent["team"]);

        Assert.False(AiVocabularyPolicy.IsPermitted(state, "github", builtIn: true, content: null));
        var current = AiVocabularyPolicy.ScopeOf(withoutDocument);
        Assert.Equal(["microsoft-365", "team"], current.PermittedContent.Keys.Order(StringComparer.Ordinal));
        Assert.Null(current.PermittedContent["microsoft-365"]);
        Assert.Equal(H2, current.PermittedContent["team"]);
        Assert.False(current.Covers(admitted));
        Assert.True(AiVocabularyPolicy.HasNarrowed(admitted, current));

        // What is admitted now carries what is there now; the custom library's pairing, its accepted hash, still holds.
        Assert.False(AiVocabularyPolicy.HasNarrowed(current, AiVocabularyPolicy.ScopeOf(withoutDocument)));
        Assert.False(AiVocabularyPolicy.HasNarrowed(new AiVocabularyScope(1, [new("team", H2)]), current));

        // With the stale entry dropped and github permitted again, it is paired with the hash it holds: none.
        var permittedAgain = State(enabled: ["github", "microsoft-365", "team"], ai: [("github", true), ("team", true)], accepted: [("team", H2)]);
        var again = AiVocabularyPolicy.ScopeOf(Catalog(3, permittedAgain, Committed(BuiltInLibrary("github", Shipped("get hub", "GitHub"))), microsoft, team));
        Assert.True(again.PermittedContent.ContainsKey("github"));
        Assert.Null(again.PermittedContent["github"]);
        Assert.False(again.Covers(admitted));
    }

    [Fact]
    public void An_edits_document_deleted_outside_Scribe_stops_covering_earlier_admissions_until_the_user_permits_again()
    {
        // Round 2, part 2 (Grok G1, the coordinator's end-to-end case), through J's load path (3.1.1): each load plans an
        // adoption and commits it, then composes and publishes. A request admitted with the edits is never handed over
        // again, and github carries nothing to AI cleanup until the user permits it again.
        var edited = BuiltInLibrary("github", Edited("get hub", "GitHub", "get hub", "Nightjar Hub"), Shipped("octo cat", "Octocat"));
        var shippedOnly = BuiltInLibrary("github", Shipped("get hub", "GitHub"), Shipped("octo cat", "Octocat"));
        var context = new LibraryStateContext(false, false, true, true);
        var state = State(enabled: ["github"], ai: [("github", true)], accepted: [("github", H1)]);
        var source = new FakeVocabularySource(LibraryComposer.Instance.ComposeVocabulary(Catalog(1, state, Committed(edited, H1))));
        var admitted = source.Current.AiScope;
        Assert.Contains(source.Current.AiEntries, entry => entry.Replacement == "Nightjar Hub");
        var sent = new List<string>();

        // Deleted outside Scribe: from that moment no scope of the catalog covers the admission (3.3.3), and since round 3
        // (Astra A5) no scope permits github at all while its accepted entry remains.
        var reloaded = Catalog(2, state, Committed(shippedOnly));
        Assert.True(AiVocabularyPolicy.HasNarrowed(admitted, AiVocabularyPolicy.ScopeOf(reloaded)));
        Assert.False(AiVocabularyPolicy.ScopeOf(reloaded).PermittedContent.ContainsKey("github"));

        // The load adopts the disappearance and publishes: github is still on for dictation, and permitted for nothing.
        var adoption = LibraryComposer.Instance.PlanAdoption(reloaded, context);
        Assert.NotNull(adoption);
        Assert.Equal(LibraryAdoptionReasons.ContentReplaced, adoption.Reasons);
        var adopted = Catalog(3, adoption.State, Committed(shippedOnly));
        source.Publish(LibraryComposer.Instance.ComposeVocabulary(adopted));
        Assert.False(source.Current.AiScope.PermittedContent.ContainsKey("github"));
        Assert.Empty(source.Current.AiEntries);
        Assert.Contains(source.Current.Entries, entry => entry.Replacement == "GitHub");
        Assert.False(source.TryHandOff(admitted, () => sent.Add("admitted before the deletion")));
        Assert.Null(LibraryComposer.Instance.PlanAdoption(adopted, context));

        // The user permits it again: a Save records the choice, with no accepted entry for a built-in with no document.
        source.Publish(LibraryComposer.Instance.ComposeVocabulary(
            Catalog(4, State(enabled: ["github"], ai: [("github", true)]), Committed(shippedOnly))));
        var readmitted = source.Current.AiScope;
        Assert.True(readmitted.PermittedContent.ContainsKey("github"));
        Assert.Null(readmitted.PermittedContent["github"]);
        Assert.False(source.TryHandOff(admitted, () => sent.Add("admitted before the deletion, retried")));
        Assert.True(source.TryHandOff(readmitted, () => sent.Add("admitted after permitting again")));
        Assert.Equal(["admitted after permitting again"], sent);
        Assert.Contains(source.Current.AiEntries, entry => entry.Replacement == "GitHub");
        Assert.DoesNotContain(source.Current.AiEntries, entry => entry.Replacement == "Nightjar Hub");
    }

    private static HistoryEntry Entry(long id, string text) =>
        new(id, Now.AddHours(-id), text, AudioMilliseconds: 1000, DecodeMilliseconds: 100, TargetApp: "notepad");

    private static bool IsSubsequence(IReadOnlyList<DictionaryEntry> subset, IReadOnlyList<DictionaryEntry> all)
    {
        var at = 0;
        foreach (var entry in subset)
        {
            while (at < all.Count && !ReferenceEquals(all[at], entry))
            {
                at++;
            }

            if (at++ >= all.Count)
            {
                return false;
            }
        }

        return true;
    }
}
