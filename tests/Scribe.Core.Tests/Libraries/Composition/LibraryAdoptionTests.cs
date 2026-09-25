using Scribe.Core.Libraries;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// The state the service commits on its own (W1b contracts 3.3.4): the first start, files discovered later, content
/// changed outside Scribe (C-17, A4) and a lost state's durable denial (C-5b, A3).
/// </summary>
public sealed class LibraryAdoptionTests
{
    private static readonly LibraryStateContext FirstStart = new(false, false, false);
    private static readonly LibraryStateContext Later = new(false, false, GenerationStored: true, CommitWitnessed: true);

    private static readonly CatalogLibrary GitHub = Committed(BuiltInLibrary("github", Shipped("get hub", "GitHub"), Shipped("kube", "Kubernetes")));

    [Fact]
    public void The_first_start_records_every_custom_library_as_existing_with_its_content_markers_and_the_notice()
    {
        var team = Committed(CustomLibrary("team", Custom("get hub", "GitHub Enterprise"), Custom("kube", "Kubernetes"), Custom("helm", "Helm")), H1);
        var notes = Committed(CustomLibrary("Zulu Notes", Custom("north star", "North Star")), H2);
        var locked = Committed(CustomLibrary("locked"), hash: null, state: LibraryFileState.AwaitingRelease);
        var edited = Committed(BuiltInLibrary("microsoft-365", Shipped("teams", "Teams")), H3);
        var catalog = Catalog(0, ReadFirstStart(["github", "team"], GitHub, team, notes, locked, edited), GitHub, team, notes, locked, edited);

        var adoption = LibraryComposer.Instance.PlanAdoption(catalog, FirstStart)!;

        Assert.Equal(LibraryAdoptionReasons.FirstStart, adoption.Reasons);
        var state = adoption.State;
        Assert.Equal(LocalStateHealth.Ok, state.Health);
        Assert.Equal(["github", "team"], state.EnabledIds.Order(StringComparer.Ordinal));
        Assert.True(state.AiPermissions["team"]);
        Assert.True(state.AiPermissions["Zulu Notes"]);
        Assert.False(state.AiPermissions.ContainsKey("github"));
        Assert.False(state.AiPermissions.ContainsKey("locked"));
        Assert.Equal(H1, state.AcceptedContent["team"]);
        Assert.Equal(H2, state.AcceptedContent["Zulu Notes"]);
        Assert.Equal(H3, state.AcceptedContent["microsoft-365"]);
        Assert.Equal(["Zulu Notes", "team"], state.AiUpgradeNotice.Order(StringComparer.Ordinal));
        Assert.Equal([new LegacyMarker("team", Key("get hub"))], state.LegacyMarkers);   // "kube" matches the built-in
        Assert.Equal(1, adoption.MarkersAdded);
        Assert.Equal(3, adoption.LibrariesAdopted);
        Assert.False(state.AiPermissionsLost);

        // Planned again, as after a failed or deferred commit: the same plan.
        var again = LibraryComposer.Instance.PlanAdoption(catalog, FirstStart)!;
        Assert.Equal(state.EnabledIds.Order(), again.State.EnabledIds.Order());
        Assert.Equal(state.LegacyMarkers, again.State.LegacyMarkers);
        Assert.Equal(state.AcceptedContent.OrderBy(p => p.Key), again.State.AcceptedContent.OrderBy(p => p.Key));
    }

    [Fact]
    public void Nothing_is_adopted_in_a_session_on_defaults_or_over_a_newer_state()
    {
        var team = Committed(CustomLibrary("team", Custom("kube", "K8s")), H1);

        Assert.Null(LibraryComposer.Instance.PlanAdoption(Catalog(0, LibraryLocalState.Absent, GitHub, team), FirstStart with { RunningOnDefaults = true }));
        Assert.Null(LibraryComposer.Instance.PlanAdoption(Catalog(State(health: LocalStateHealth.Newer), GitHub, team), Later));
        Assert.Null(LibraryComposer.Instance.PlanAdoption(Catalog(State(health: LocalStateHealth.Unreadable), GitHub, team), Later with { RunningOnDefaults = true }));
        Assert.Null(LibraryComposer.Instance.PlanAdoption(Catalog(State(enabled: ["team"], ai: [("team", true)], accepted: [("team", H1)]), GitHub, team), Later));
    }

    [Fact]
    public void A_file_that_appears_later_is_discovered_with_AI_off_its_markers_and_its_content_accepted()
    {
        var team = Committed(CustomLibrary("team", Custom("kube", "K8s")), H1);
        var placed = Committed(CustomLibrary("placed", Custom("get hub", "Get Hub Placed"), Custom("helm", "Helm")), H2);
        var state = State(enabled: ["github", "team"], ai: [("team", true)], accepted: [("team", H1)], markers: [("team", "kube")]);

        var adoption = LibraryComposer.Instance.PlanAdoption(Catalog(state, GitHub, team, placed), Later)!;

        Assert.Equal(LibraryAdoptionReasons.Discovered, adoption.Reasons);
        Assert.False(adoption.State.AiPermissions["placed"]);
        Assert.True(adoption.State.AiPermissions["team"]);
        Assert.Equal(H2, adoption.State.AcceptedContent["placed"]);
        Assert.DoesNotContain("placed", adoption.State.EnabledIds);
        Assert.Contains(new LegacyMarker("placed", Key("get hub")), adoption.State.LegacyMarkers);
        Assert.Contains(new LegacyMarker("team", Key("kube")), adoption.State.LegacyMarkers);
        Assert.Equal(1, adoption.MarkersAdded);
        Assert.Equal(1, adoption.LibrariesAdopted);
    }

    [Fact]
    public void A_file_an_older_build_imported_and_turned_on_is_discovered_on_but_kept_from_AI_cleanup()
    {
        // Reading applies the older build's enabling (A17); the adoption keeps it from AI cleanup, so the projection the
        // adoption commit writes no longer lists it (A5).
        IReadOnlyList<LibraryIdentity> identities = [BuiltInIdentity("github"), CustomIdentity("imported")];
        var stored = LibraryComposer.Instance.EncodeLocalState(State(enabled: ["github"]), null, identities, identities);
        var read = LibraryComposer.Instance.ReadLocalState(["github", "imported"], stored.StateValue, identities, Later);
        var imported = Committed(CustomLibrary("imported", Custom("helm", "Helm")), H1);

        var adoption = LibraryComposer.Instance.PlanAdoption(Catalog(read, GitHub, imported), Later)!;

        Assert.Contains("imported", adoption.State.EnabledIds);
        Assert.False(AiVocabularyPolicy.IsPermitted(adoption.State, "imported", false, H1));
        Assert.Equal(["github"], LibraryComposer.Instance.EncodeLocalState(adoption.State, null, identities, identities).EnabledLibraryIds);
    }

    [Fact]
    public void Content_replaced_outside_Scribe_is_not_permitted_and_is_adopted_off_with_new_markers()
    {
        // C-17: the stored choices were for H1; the file now holds H2.
        var team = Committed(CustomLibrary("team", Custom("kube", "K8s"), Custom("helm", "Helm")), H2);
        var state = State(
            enabled: ["github", "team"], ai: [("team", true)], accepted: [("team", H1)],
            markers: [("team", "copilot")], notice: ["team"]);
        var catalog = Catalog(state, GitHub, team);

        Assert.False(AiVocabularyPolicy.IsPermitted(state, "team", false, H2));
        Assert.DoesNotContain(LibraryComposer.Instance.ComposeVocabulary(catalog).AiEntries, e => e.Replacement == "Helm");

        var adoption = LibraryComposer.Instance.PlanAdoption(catalog, Later)!;

        Assert.Equal(LibraryAdoptionReasons.ContentReplaced, adoption.Reasons);
        Assert.False(adoption.State.AiPermissions["team"]);
        Assert.DoesNotContain("team", adoption.State.EnabledIds);
        Assert.Contains("github", adoption.State.EnabledIds);
        Assert.Equal(H2, adoption.State.AcceptedContent["team"]);
        Assert.Equal([new LegacyMarker("team", Key("kube"))], adoption.State.LegacyMarkers);
        Assert.Empty(adoption.State.AiUpgradeNotice);

        // Turned off, it is out of the projection too.
        IReadOnlyList<LibraryIdentity> identities = [BuiltInIdentity("github"), CustomIdentity("team")];
        Assert.Equal(["github"], LibraryComposer.Instance.EncodeLocalState(adoption.State, null, identities, identities).EnabledLibraryIds);
    }

    [Fact]
    public void A_built_in_whose_edits_document_was_replaced_loses_AI_permission_and_keeps_its_enabled_state()
    {
        var edited = Committed(BuiltInLibrary("github", Edited("get hub", "GitHub", "get hub", "GitHub Cloud")), H2);
        var state = State(enabled: ["github"], ai: [("github", true)], accepted: [("github", H1)]);

        Assert.False(AiVocabularyPolicy.IsPermitted(state, "github", true, H2));

        var adoption = LibraryComposer.Instance.PlanAdoption(Catalog(state, edited), Later)!;

        Assert.Equal(LibraryAdoptionReasons.ContentReplaced, adoption.Reasons);
        Assert.False(adoption.State.AiPermissions["github"]);
        Assert.Contains("github", adoption.State.EnabledIds);
        Assert.Equal(H2, adoption.State.AcceptedContent["github"]);
    }

    [Fact]
    public void A_lost_state_is_adopted_as_a_denial_that_outlives_the_start_and_permits_nothing_unchosen()
    {
        // C-5b: each sign of a loss makes the absent row unreadable, and the adoption commits the denial.
        var team = Committed(CustomLibrary("team", Custom("get hub", "GitHub Enterprise")), H1);
        var twin = Committed(CustomLibrary("custom-github", Custom("nightjar", "Nightjar")), H2, fileName: "github.csv");
        IReadOnlyList<LibraryIdentity> identities = [IdentityOf(GitHub), IdentityOf(team), IdentityOf(twin)];
        LibraryStateContext[] signs =
        [
            new(false, DatabaseRepaired: true, GenerationStored: false),
            new(false, false, GenerationStored: true),
            new(false, false, false, CommitWitnessed: true),
        ];
        foreach (var context in signs)
        {
            var read = LibraryComposer.Instance.ReadLocalState(["github"], null, identities, context);
            Assert.Equal(LocalStateHealth.Unreadable, read.Health);

            var adoption = LibraryComposer.Instance.PlanAdoption(Catalog(read, GitHub, team, twin), context)!;

            Assert.Equal(LibraryAdoptionReasons.StateLost, adoption.Reasons);
            var denial = adoption.State;
            Assert.True(denial.AiPermissionsLost);
            Assert.Empty(denial.AiPermissions);
            Assert.Equal(["custom-github", "github"], denial.EnabledIds.Order(StringComparer.Ordinal));
            Assert.Equal(H1, denial.AcceptedContent["team"]);
            Assert.Equal([new LegacyMarker("team", Key("get hub"))], denial.LegacyMarkers);
            Assert.False(AiVocabularyPolicy.IsPermitted(denial, "github", true, null));
            Assert.False(AiVocabularyPolicy.IsPermitted(denial, "custom-github", false, H2));

            // Every later commit keeps the denial, and reading it back still permits nothing nobody chose again.
            var encoded = LibraryComposer.Instance.EncodeLocalState(denial, null, identities, identities);
            Assert.Empty(encoded.EnabledLibraryIds);
            var restarted = LibraryComposer.Instance.ReadLocalState(encoded.EnabledLibraryIds, encoded.StateValue, identities, Later);
            Assert.True(restarted.AiPermissionsLost);
            Assert.Empty(LibraryComposer.Instance.ComposeVocabulary(Catalog(restarted, GitHub, team, twin)).AiEntries);
            Assert.Null(LibraryComposer.Instance.PlanAdoption(Catalog(restarted, GitHub, team, twin), Later));
        }

        // A catalog handed over without the reading, with an absent state and a sign of loss, is still a denial.
        Assert.Equal(
            LibraryAdoptionReasons.StateLost,
            LibraryComposer.Instance.PlanAdoption(Catalog(LibraryLocalState.Absent, GitHub, team), Later)!.Reasons);
    }

    private static LibraryLocalState ReadFirstStart(IReadOnlyList<string> document, params CatalogLibrary[] libraries) =>
        LibraryComposer.Instance.ReadLocalState(document, null, [.. libraries.Select(IdentityOf)], FirstStart);
}
