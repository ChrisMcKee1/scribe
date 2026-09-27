using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.Tests.Libraries.Storage;

namespace Scribe.Core.Tests.Libraries.Integration;

/// <summary>
/// The upgrade and the downgrade through the real parts (contract 9.1 step 3): the first start over the W1a fixture keeps
/// every winner and every finished text 0.4.3 gave, and the remap regression of rounds 3 and 4 (review findings A15 to
/// A18) runs through the real <c>SaveBundle</c> and both readers, this build's and 0.4.3's.
/// </summary>
public sealed class LibraryUpgradeIntegrationTests
{
    private const string Canary = "Nightjar";

    [Fact]
    public void The_first_start_through_the_service_keeps_every_winner_and_finished_text_0_4_3_gave_the_W1a_fixture()
    {
        var sentences = LibraryFixture.Sentences.Concat(LibraryFixture.FixtureKeys).ToList();
        foreach (var (scenario, enabledIds) in LibraryFixture.Scenarios)
        {
            using var upgrade = new RealLibraries();
            foreach (var (fileName, csv) in LibraryFixture.CustomFiles)
            {
                upgrade.Fixture.Write(fileName, csv);
            }

            upgrade.Fixture.SaveEnabled(enabledIds);
            var dictionary = new DictionaryRepository(upgrade.Fixture.Database);
            dictionary.AddRange(LibraryFixture.Personal);
            var service = upgrade.Service();

            // The first start records the upgrade (one generation, a state row) and publishes the committed vocabulary.
            var vocabulary = service.Current;
            Assert.Equal(1, upgrade.Fixture.StoredGeneration);
            Assert.NotNull(upgrade.Fixture.Row(LibrarySettingKeys.State));
            var legacy = Legacy043LibrarySelection.EnabledEntries(enabledIds, upgrade.LibrariesDir);

            Assert.True(Winners(legacy).SequenceEqual(Winners(vocabulary.Entries)), $"{scenario}: a winner moved at the upgrade");
            var now = Processor(dictionary, vocabulary.Entries);
            var then = Processor(dictionary, legacy);
            foreach (var sentence in sentences)
            {
                Assert.True(then.Process(sentence) == now.Process(sentence), $"{scenario}: \"{sentence}\" is written differently after the upgrade");
            }

            // Release 0.4.4's seam, which dictation still calls until the vocabulary source replaces it, is 0.4.3's too.
            Assert.Equal(legacy, service.GetEnabledLibraryEntries(enabledIds));
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_hand_placed_twin_through_the_real_Save_and_both_readers(bool twinOn, bool twinAi)
    {
        // Rounds 3 and 4 (A15, A16, A17): epsilon.csv and a hand-placed github.csv both supply "project token"; the twin
        // also holds a private term. The first start takes 0.4.3's state (the github flag on turns both on), then the user
        // chooses the twin's switches here, through D's workspace and J's Save.
        using var libraries = new RealLibraries();
        var fixture = libraries.Fixture;
        fixture.Write("epsilon.csv", "# name: Epsilon\npattern,replacement\nproject token,Epsilon\n");
        fixture.Write("github.csv", "# name: Twin\npattern,replacement\nproject token,Twin\nprivate codename," + Canary + "\n");
        fixture.SaveEnabled("epsilon", "github");
        var service = libraries.Service();
        var start = service.LoadCatalog();
        Assert.Equal("custom-github", start.Libraries.Single(library => library.FileName == "github.csv").Content.Id);
        Assert.Contains("custom-github", start.LocalState.EnabledIds);

        var workspace = RealLibraries.Workspace(start);
        workspace.SetEnabled("custom-github", twinOn);
        Assert.True(workspace.SetAiPermission("custom-github", twinAi).Applied);

        // Every case is a real Save through SaveBundle, the twin's switches as chosen, beside an edit of another library.
        Assert.True(workspace.AddTerm("epsilon", new TermValues("extra term", "Extra")).Applied);
        var saved = libraries.Save(service, workspace);
        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome.Status);

        // This build writes 0.4.3's winner for the shared form (ranked by the physical file name, A16), and the twin's
        // private term locally while the twin is on, to AI cleanup only while it is also permitted.
        var restarted = libraries.Restart();
        Assert.Equal("Epsilon", Winner(restarted.Current.Entries, "project token"));
        Assert.Equal(twinOn, restarted.Current.Entries.Any(entry => entry.Replacement == Canary));
        Assert.Equal(twinOn && twinAi, restarted.Current.AiEntries.Any(entry => entry.Replacement == Canary));

        // An older build reads the stored list over the same folder: it applies the twin (and with it the built-in, one
        // flag for both) only while both are on and permitted here, and otherwise neither (A15).
        var older = Legacy043LibrarySelection.EnabledEntries(fixture.StoredEnabledList(), fixture.LibrariesDir);
        Assert.Equal(twinOn && twinAi, older.Any(entry => entry.Replacement == Canary));
        Assert.Equal(twinOn && twinAi, fixture.StoredEnabledList().Contains("github", StringComparer.OrdinalIgnoreCase));
        Assert.Equal("Epsilon", Winner(older, "project token") ?? "Epsilon");

        // After a restart each twin keeps its own choices (A17), and the published vocabulary is the composition's.
        var reloaded = restarted.LoadCatalog();
        Assert.Equal(twinOn, reloaded.LocalState.EnabledIds.Contains("custom-github"));
        Assert.Equal(twinAi, reloaded.LocalState.AiPermissions["custom-github"]);
        Assert.Contains("github", reloaded.LocalState.EnabledIds);
        var composition = LibraryComposition.Committed(reloaded, [], new GlossaryBudget(Cleanup.CleanupPrompt.MaxGlossaryTermsCloud));
        Assert.Equal(composition.LibraryEntries, restarted.Current.Entries);
        Assert.Equal(composition.AiLibraryEntries, restarted.Current.AiEntries);

        // The dictionary cleanup, adapted to the library state (3.3.6), judges the twin by its logical id and the
        // composition's winners: its "project token" row, which Epsilon's rule wins, meets a rule that stays in effect, so
        // the twin is kept on, whole and named, nothing is copied, and the built-in sharing its old id is not involved.
        if (twinOn)
        {
            var draft = RealLibraries.Workspace(reloaded).Draft;
            var preview = LibraryComposition.Preview(draft, reloaded, [], new GlossaryBudget(Cleanup.CleanupPrompt.MaxGlossaryTermsCloud));
            Assert.Equal("epsilon", preview.StatusOf("custom-github", LibraryTermKey.From("project token")).WinningLibraryId);
            var twin = preview.EnabledLibraries.Single(library => library.Id == "custom-github");

            // The libraries in use carry their physical file names, which the usage review, the badges and the cleanup
            // rank and name libraries by.
            Assert.Equal("github.csv", twin.FileName);
            Assert.Equal("epsilon.csv", preview.EnabledLibraries.Single(library => library.Id == "epsilon").FileName);
            Assert.Equal(["epsilon", "custom-github"], preview.EnabledLibraries.Where(library => !library.BuiltIn).Select(library => library.Id));
            Assert.Equal("github.csv", preview.Coverage()["private codename"].FileName);
            Assert.Equal("epsilon.csv", preview.Coverage()["project token"].FileName);
            var usage = new LibraryUsage(
                "custom-github", twin.Name, [.. twin.Entries.Where(entry => entry.Replacement == Canary)], UnusedCount: 1, BuiltIn: false);
            var plan = LibrarySwitchOffCopy.Plan([], preview, [usage]);
            Assert.Equal([("custom-github", false)], plan.KeptOn.Select(kept => (kept.Id, kept.BuiltIn)));
            Assert.Empty(plan.Copies);
        }
    }

    [Fact]
    public void An_older_builds_change_of_the_shared_flag_reaches_both_twins_off_and_on_with_AI_unchanged()
    {
        // Round 4 (A17): 0.4.3's Save writes only the document's list; turning github off there turns both twins off here,
        // and turning it back on turns both on with the twin's AI choice as this build stored it.
        using var libraries = new RealLibraries();
        var fixture = libraries.Fixture;
        fixture.Write("github.csv", "# name: Twin\npattern,replacement\nprivate codename," + Canary + "\n");
        fixture.SaveEnabled("github");
        libraries.Service().LoadCatalog();
        Assert.Contains("github", fixture.StoredEnabledList(), StringComparer.OrdinalIgnoreCase);

        OlderBuildSaves(fixture, list => [.. list.Where(id => !string.Equals(id, "github", StringComparison.OrdinalIgnoreCase))]);
        var service = libraries.Restart();
        var off = service.LoadCatalog();
        Assert.DoesNotContain("github", off.LocalState.EnabledIds);
        Assert.DoesNotContain("custom-github", off.LocalState.EnabledIds);

        // Here the twin is kept from AI cleanup, and that Save commits the older build's change too.
        var workspace = RealLibraries.Workspace(off);
        Assert.True(workspace.SetAiPermission("custom-github", false).Applied);
        libraries.Save(service, workspace);

        OlderBuildSaves(fixture, list => [.. list, "github"]);
        var restarted = libraries.Restart();
        var on = restarted.LoadCatalog();
        Assert.Contains("github", on.LocalState.EnabledIds);
        Assert.Contains("custom-github", on.LocalState.EnabledIds);
        Assert.False(on.LocalState.AiPermissions["custom-github"]);
        Assert.Contains(restarted.Current.Entries, entry => entry.Replacement == Canary);
        Assert.DoesNotContain(restarted.Current.AiEntries, entry => entry.Replacement == Canary);

        // The next commit writes the list again with the projection, which leaves github out while the twin is kept from AI.
        CommitStateOnly(libraries, restarted, on);
        Assert.DoesNotContain("github", fixture.StoredEnabledList(), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Legacy043LibrarySelection.EnabledEntries(fixture.StoredEnabledList(), fixture.LibrariesDir), entry => entry.Replacement == Canary);
    }

    [Fact]
    public void A_deferred_deletion_of_an_AI_excluded_twin_never_reaches_an_older_build()
    {
        // Round 4 (A18): the twin, kept from AI cleanup, is deleted while another app holds its file open, so its move to
        // Recently deleted waits. Right after the commit and for as long as the file is still there, the stored list keeps
        // github out, so an older build applies neither twin; the next commit after the deletion completes lets github
        // (the built-in, on and permitted) back into the list.
        using var libraries = new RealLibraries();
        var fixture = libraries.Fixture;
        fixture.Write("github.csv", "# name: Twin\npattern,replacement\nprivate codename," + Canary + "\n");
        fixture.SaveEnabled("github");
        var service = libraries.Service();
        var start = service.LoadCatalog();
        var workspace = RealLibraries.Workspace(start);
        Assert.True(workspace.SetAiPermission("custom-github", false).Applied);
        libraries.Save(service, workspace);
        Assert.DoesNotContain("github", fixture.StoredEnabledList(), StringComparer.OrdinalIgnoreCase);

        var held = true;
        var files = new FaultingFileSystem
        {
            MutationFault = (kind, source, _) =>
                held && kind.StartsWith("move", StringComparison.Ordinal) && source.EndsWith("github.csv", StringComparison.OrdinalIgnoreCase)
                    ? FaultingFileSystem.SharingViolation()
                    : null,
        };
        var holding = libraries.Restart(files);
        var deleting = RealLibraries.Workspace(holding.LoadCatalog());
        deleting.DeleteLibrary("custom-github");
        var deleted = libraries.Save(holding, deleting);

        Assert.Equal(LibrarySaveStatus.AppliedAwaitingRelease, deleted.Outcome.Status);
        Assert.True(fixture.Exists("github.csv"));
        Assert.DoesNotContain("github", fixture.StoredEnabledList(), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Legacy043LibrarySelection.EnabledEntries(fixture.StoredEnabledList(), fixture.LibrariesDir), entry => entry.Replacement == Canary);

        held = false;
        var released = libraries.Restart(files);
        var after = released.LoadCatalog();
        Assert.False(fixture.Exists("github.csv"));
        Assert.Null(after.Find("custom-github"));

        // The deletion completed without a settings write (decision 12); the next commit writes github back into the list.
        var next = RealLibraries.Workspace(after);
        Assert.True(next.SetAiPermission("github", true).Applied);
        libraries.Save(released, next);
        Assert.Contains("github", fixture.StoredEnabledList(), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Legacy043LibrarySelection.EnabledEntries(fixture.StoredEnabledList(), fixture.LibrariesDir), entry => entry.Replacement == Canary);
    }

    private static void CommitStateOnly(RealLibraries libraries, DictionaryLibraryService service, LibraryCatalog catalog)
    {
        var state = Changes.With(catalog.LocalState, ai: [("github", true)]);
        var saved = Changes.Save(service, libraries.Fixture.Settings, Changes.Of(catalog, state: state));
        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
    }

    // 0.4.3's Save: the whole document with its own list, no library payload and no notion of the state row.
    private static void OlderBuildSaves(LibraryStorageFixture fixture, Func<IReadOnlyList<string>, IReadOnlyList<string>> list)
    {
        var document = JsonNode.Parse(fixture.Settings.Get(SettingsRepository.SettingsKey)!)!.AsObject();
        var current = document["enabledDictionaryLibraryIds"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
        document["enabledDictionaryLibraryIds"] = new JsonArray([.. list(current).Select(id => (JsonNode?)id)]);
        fixture.Settings.Set(SettingsRepository.SettingsKey, document.ToJsonString());
    }

    private static TextPostProcessor Processor(DictionaryRepository dictionary, IReadOnlyList<DictionaryEntry> libraryEntries) =>
        new(dictionary, NullLogger<TextPostProcessor>.Instance, snippets: null, libraries: new FixedLibraries(libraryEntries));

    private static string? Winner(IEnumerable<DictionaryEntry> entries, string spoken) =>
        entries.FirstOrDefault(entry => LibraryTermKey.AreSame(entry.Pattern, spoken))?.Replacement;

    // What dictation writes for each spoken form: the winners, by key, whatever order a composition lists them in.
    private static IReadOnlyList<string> Winners(IEnumerable<DictionaryEntry> entries) =>
        [.. entries.Select(entry => $"{LibraryTermKey.From(entry.Pattern).Value.ToUpperInvariant()} => {entry.Replacement} ({entry.WholeWord})")
            .Order(StringComparer.Ordinal)];

    /// <summary>A library seam that hands the post-processor exactly the entries it is given.</summary>
    private sealed class FixedLibraries(IReadOnlyList<DictionaryEntry> entries) : IDictionaryLibraryService
    {
        public IReadOnlyList<DictionaryLibrary> GetLibraries() => [];

        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries() => entries;

        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries(IReadOnlyCollection<string> enabledIds) => entries;

        public DictionaryLibrary Import(string csv, string? suggestedName) => throw new NotSupportedException();

        public void Remove(string id) => throw new NotSupportedException();
    }
}
