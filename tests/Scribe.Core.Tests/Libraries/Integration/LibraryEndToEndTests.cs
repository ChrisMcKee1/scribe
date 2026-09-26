using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.Tests.Libraries.Storage;

namespace Scribe.Core.Tests.Libraries.Integration;

/// <summary>
/// Contract 9.1 step 3 and the cross-stream checks of 9.3 that need no seeded history: the real D, O, C, X and J parts
/// through one Save, and the refusals each part leaves to another.
/// </summary>
public sealed class LibraryEndToEndTests : IDisposable
{
    private readonly RealLibraries _libraries = new();

    public void Dispose() => _libraries.Dispose();

    private LibraryStorageFixture Fixture => _libraries.Fixture;

    [Fact]
    public void A_workspace_Save_through_the_journal_reloads_in_a_new_process_exactly_as_the_draft_showed_it()
    {
        // Catalog, workspace, change set, prepare, commit, complete and reload give the same content (9.1 step 3).
        Fixture.Write("team-terms.csv", "# name: Team terms\n# category: Custom\npattern,replacement\nkube,Kubernetes\nget hub,GitHub Enterprise\n");
        Fixture.SaveEnabled("team-terms", "github");
        var service = _libraries.Service();
        var workspace = RealLibraries.Workspace(service.LoadCatalog());

        Assert.True(workspace.EditTerm("team-terms", RealLibraries.Row(workspace, "team-terms", "kube").RowId, new TermValues("kube", "K8s")).Applied);
        Assert.True(workspace.AddTerm("team-terms", new TermValues("north star", "North Star")).Applied);
        Assert.True(workspace.Rename("team-terms", "Team words").Applied);
        Assert.True(workspace.SetDetails("team-terms", "Engineering", "What the team says").Applied);
        Assert.True(workspace.SetAiPermission("team-terms", false).Applied);
        var created = workspace.CreateLibrary();
        Assert.True(workspace.AddTerm(created, new TermValues("sprint review", "Sprint Review")).Applied);
        Assert.True(workspace.SetAiPermission(created, true).Applied);
        Assert.True(workspace.EditTerm("github", RealLibraries.Row(workspace, "github", "get hub").RowId, new TermValues("get hub", "GitHub Enterprise")).Applied);
        workspace.SetTermEnabled("github", RealLibraries.Row(workspace, "github", "copilot").RowId, enabled: false);
        var draft = workspace.Draft;

        var saved = _libraries.Save(service, workspace);

        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome.Status);
        Assert.False(workspace.HasUnsavedChanges);
        var reloaded = _libraries.Restart().LoadCatalog();
        foreach (var id in new[] { "team-terms", created, "github" })
        {
            var shown = draft.Find(id)!.Content;
            var stored = reloaded.Find(id)!.Content;
            Assert.Equal(shown.Name, stored.Name);
            Assert.Equal(shown.Category, stored.Category);
            Assert.Equal(shown.Description, stored.Description);
            Assert.Equal(shown.Rows.Select(row => row.Values), stored.Rows.Select(row => row.Values));
            Assert.Equal(shown.Rows.Select(row => (row.Key, row.Origin)), stored.Rows.Select(row => (row.Key, row.Origin)));
        }

        Assert.Equal(draft.LocalState.EnabledIds.Order(StringComparer.OrdinalIgnoreCase), reloaded.LocalState.EnabledIds.Order(StringComparer.OrdinalIgnoreCase));
        Assert.False(reloaded.LocalState.AiPermissions["team-terms"]);
        Assert.True(reloaded.LocalState.AiPermissions[created]);

        // The custom files are managed files of this version, which X's codec reads back exactly.
        Assert.Contains("# scribe-format: 2", Fixture.Read("team-terms.csv"), StringComparison.Ordinal);
        Assert.Equal(
            LibraryCsvCodec.Instance.WriteManaged(reloaded.Find("team-terms")!.Content),
            File.ReadAllBytes(Fixture.PathOf("team-terms.csv")));

        // A workspace over the reloaded catalog has nothing to save: the draft and the committed state agree.
        Assert.False(RealLibraries.Workspace(reloaded).HasUnsavedChanges);
    }

    [Fact]
    public void A_built_in_edit_round_trips_through_the_overlays_document_and_the_journal()
    {
        Fixture.SaveEnabled("github");
        var service = _libraries.Service();
        var workspace = RealLibraries.Workspace(service.LoadCatalog());
        workspace.EditTerm("github", RealLibraries.Row(workspace, "github", "get hub").RowId, new TermValues("get hub", "GitHub Enterprise"));
        workspace.SetTermEnabled("github", RealLibraries.Row(workspace, "github", "copilot").RowId, enabled: false);
        Assert.True(workspace.AddTerm("github", new TermValues("gh cli", "GitHub CLI")).Applied);

        Assert.Equal(LibrarySaveStatus.Applied, _libraries.Save(service, workspace).Outcome.Status);

        var reloaded = _libraries.Restart().LoadCatalog();
        var github = reloaded.Find("github")!;
        var bytes = File.ReadAllBytes(Fixture.PathOf("edits/github.json"));
        Assert.Equal(LibraryContentHashing.Of(bytes), github.ContentHash);
        Assert.Equal(github.ContentHash, reloaded.LocalState.AcceptedContent["github"]);
        var read = BuiltInLibraryOverlay.Instance.ReadEdits("github", bytes);
        Assert.Equal(LibraryFileState.Available, read.State);
        Assert.Equal(bytes, BuiltInLibraryOverlay.Instance.WriteEdits(read.Edits!));
        Assert.Equal(
            BuiltInLibraryOverlay.Instance.Apply(RealEdits.Shipped("github"), read.Edits).Select(row => row.Values),
            github.Content.Rows.Select(row => row.Values));
        Assert.Equal("GitHub Enterprise", github.Content.Rows.Single(row => row.Key == LibraryTermKey.From("get hub")).Values.Written);
        Assert.False(github.Content.Rows.Single(row => row.Key == LibraryTermKey.From("copilot")).Values.Enabled);
        Assert.Equal(TermOrigin.Added, github.Content.Rows.Single(row => row.Key == LibraryTermKey.From("gh cli")).Origin);

        // What dictation and AI cleanup take from it: the edit and the addition, and never the turned-off term.
        var restarted = _libraries.Restart();
        Assert.Contains(restarted.Current.Entries, entry => entry.Pattern == "get hub" && entry.Replacement == "GitHub Enterprise");
        Assert.Contains(restarted.Current.Entries, entry => entry.Pattern == "gh cli");
        Assert.DoesNotContain(restarted.Current.Entries, entry => entry.Pattern == "copilot");
    }

    [Fact]
    public void Export_then_import_through_the_codec_the_planner_the_workspace_and_the_journal_is_the_identity()
    {
        Fixture.Write("team-terms.csv", "# name: Team terms\npattern,replacement\nkube,Kubernetes\n");
        Fixture.SaveEnabled("team-terms");
        var service = _libraries.Service();
        var workspace = RealLibraries.Workspace(service.LoadCatalog());
        TermValues[] awkward =
        [
            new("sum formula", "=SUM(A1:A3)"),
            new("plus one", "+1"),
            new("mention", "@team"),
            new("quote comma", "say \"hi\", then go"),
            new("sign off", "Best,\nChris"),
            new("apostrophe", "'=already guarded"),
            new("in words", "InWords", WholeWord: false),
            new("off term", "Off", Enabled: false),
        ];
        foreach (var values in awkward)
        {
            Assert.True(workspace.AddTerm("team-terms", values).Applied, values.Spoken);
        }

        Assert.True(workspace.SetDetails("team-terms", "Custom, shared", "A description, with a comma").Applied);
        _libraries.Save(service, workspace);
        var original = service.LoadCatalog().Find("team-terms")!.Content;

        var export = LibraryCsvCodec.Instance.WriteExport(original);
        var document = LibraryCsvCodec.Instance.ReadImport(export);
        Assert.Empty(document.Errors);
        var importing = RealLibraries.Workspace(service.LoadCatalog());
        var plan = LibraryImportPlanner.Plan(document, new LibraryImportTarget.NewLibrary("team-terms.csv"), importing.Draft);
        Assert.True(importing.ApplyImport(plan, ImportConflictChoice.KeepMine).Applied);
        var imported = importing.Draft.Libraries.Single(library => library.Origin == LibraryOrigin.Imported).Content.Id;
        _libraries.Save(service, importing);

        var copy = _libraries.Restart().LoadCatalog().Find(imported)!.Content;
        Assert.Equal(original.Rows.Select(row => row.Values), copy.Rows.Select(row => row.Values));
        Assert.Equal(original.Category, copy.Category);
        Assert.Equal(original.Description, copy.Description);
        Assert.StartsWith(original.Name, copy.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_0_4_4s_parameterless_seam_is_the_committed_vocabulary_so_a_library_kept_from_AI_cleanup_still_applies()
    {
        // 9.1 step 1 and 3.1.1: the parameterless overload is Current.Entries, what the library state enables. The ids
        // overload, which dictation calls with the document's list until W-V replaces the seam, gets only the projection,
        // which leaves out a library kept from AI cleanup (9.1 step 7, the gap gate 1 of 9.5 closes).
        Fixture.Write("team-terms.csv", "# name: Team terms\npattern,replacement\nkube,Kubernetes\n");
        Fixture.SaveEnabled("team-terms");
        var service = _libraries.Service();
        var workspace = RealLibraries.Workspace(service.LoadCatalog());
        Assert.True(workspace.SetAiPermission("team-terms", false).Applied);
        _libraries.Save(service, workspace);

        var restarted = _libraries.Restart();
        Assert.Contains(restarted.GetEnabledLibraryEntries(), entry => entry.Replacement == "Kubernetes");
        Assert.Equal(restarted.Current.Entries, restarted.GetEnabledLibraryEntries());
        Assert.DoesNotContain(restarted.Current.AiEntries, entry => entry.Replacement == "Kubernetes");
        Assert.DoesNotContain("team-terms", Fixture.StoredEnabledList(), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(restarted.GetEnabledLibraryEntries(Fixture.StoredEnabledList()), entry => entry.Replacement == "Kubernetes");
    }

    [Fact]
    public void A_renamed_built_in_row_keeps_its_key_and_its_spoken_form_cannot_be_added_again()
    {
        // D keeps a built-in's row keys unique and never re-keys a renamed row (9.3, from Grok 4.7's review of O).
        var service = _libraries.Service();
        var workspace = RealLibraries.Workspace(service.LoadCatalog());
        var row = RealLibraries.Row(workspace, "github", "get hub");

        Assert.True(workspace.EditTerm("github", row.RowId, new TermValues("gett hub", "GitHub")).Applied);
        Assert.Equal(LibraryTermKey.From("get hub"), workspace.RowsOf("github").Single(r => r.RowId == row.RowId).Row.Key);
        var refused = workspace.AddTerm("github", new TermValues("get hub", "Anything"));
        Assert.False(refused.Applied);
        Assert.Equal(LibraryValidationKind.DuplicateSpoken, refused.Issue!.Kind);

        _libraries.Save(service, workspace);
        var stored = _libraries.Restart().LoadCatalog().Find("github")!.Content.Rows.Single(r => r.Key == LibraryTermKey.From("get hub"));
        Assert.Equal("gett hub", stored.Values.Spoken);
        Assert.Equal(TermOrigin.Edited, stored.Origin);
    }

    [Fact]
    public void A_turned_off_shipped_term_stays_off_through_a_damaged_or_newer_document_a_restart_and_an_unrelated_Save()
    {
        // O-4's R3 acceptance through the service (9.1 step 3), and J never calling Apply(shipped, null) for an edits
        // document that exists but cannot be used (9.3): the built-in pauses whole, so no turned-off term comes back.
        Fixture.Write("team-terms.csv", "# name: Team terms\npattern,replacement\nkube,Kubernetes\n");
        Fixture.SaveEnabled("team-terms", "github");
        var service = _libraries.Service();
        var workspace = RealLibraries.Workspace(service.LoadCatalog());
        workspace.SetTermEnabled("github", RealLibraries.Row(workspace, "github", "copilot").RowId, enabled: false);
        _libraries.Save(service, workspace);
        Assert.DoesNotContain(_libraries.Restart().Current.Entries, entry => entry.Pattern == "copilot");

        foreach (var (damage, state) in new (byte[] Bytes, LibraryFileState State)[]
                 {
                     (Encoding.UTF8.GetBytes("{ not an edits document"), LibraryFileState.Unreadable),
                     (Encoding.UTF8.GetBytes("{\"version\":2,\"library\":\"github\",\"terms\":[]}"), LibraryFileState.Newer),
                 })
        {
            Fixture.WriteBytes("edits/github.json", damage);
            var overlay = new RecordingOverlay(BuiltInLibraryOverlay.Instance);
            var restarted = _libraries.Restart(overlay: overlay);
            var catalog = restarted.LoadCatalog();
            var github = catalog.Find("github")!;
            Assert.Equal(state, github.State);
            Assert.Empty(github.Content.Rows);
            Assert.Equal(0, overlay.NullApplies("github"));
            Assert.DoesNotContain(restarted.Current.Entries, entry => entry.Pattern is "copilot" or "get hub");
            Assert.True(github.Content.Id == "github" && catalog.LocalState.EnabledIds.Contains("github"));

            // An unrelated Save leaves the document byte for byte, and the term still off.
            var unrelated = RealLibraries.Workspace(catalog);
            unrelated.EditTerm("team-terms", RealLibraries.Row(unrelated, "team-terms", "kube").RowId, new TermValues("kube", "K8s " + state));
            Assert.Equal(LibrarySaveStatus.Applied, _libraries.Save(restarted, unrelated).Outcome.Status);
            Assert.Equal(damage, File.ReadAllBytes(Fixture.PathOf("edits/github.json")));
            Assert.DoesNotContain(_libraries.Restart().Current.Entries, entry => entry.Pattern == "copilot");
        }
    }

    [Fact]
    public void A_locked_edits_document_at_a_fresh_start_supplies_nothing_rather_than_the_shipped_rows()
    {
        var service = _libraries.Service();
        var workspace = RealLibraries.Workspace(service.LoadCatalog());
        workspace.SetTermEnabled("github", RealLibraries.Row(workspace, "github", "copilot").RowId, enabled: false);
        _libraries.Save(service, workspace);

        var files = new FaultingFileSystem
        {
            ReadFault = path => path.EndsWith("github.json", StringComparison.OrdinalIgnoreCase) ? FaultingFileSystem.SharingViolation() : null,
        };
        var overlay = new RecordingOverlay(BuiltInLibraryOverlay.Instance);
        var fresh = _libraries.Restart(files, overlay);
        var github = fresh.LoadCatalog().Find("github")!;

        Assert.Equal(LibraryFileState.AwaitingRelease, github.State);
        Assert.Empty(github.Content.Rows);
        Assert.Equal(0, overlay.NullApplies("github"));
        Assert.DoesNotContain(fresh.Current.Entries, entry => entry.Pattern is "copilot" or "get hub");
    }

    [Fact]
    public void Every_origin_but_shipped_competes_as_authored_in_the_composition_the_service_publishes()
    {
        // C treats every TermOrigin but Shipped as authored (9.3), over rows O's overlay gave: edited, pinned, added and
        // no longer shipped rows supply authored rules, an off row supplies none, and a shipped row stays shipped.
        var entries = new List<BuiltInTermEdit>(RealEdits.Written("github", ("get hub", "GitHub Enterprise"), ("gh cli", "GitHub CLI")).Terms);
        var shippedCopilot = new TermValues("copilot", "Copilot");
        entries.Add(new BuiltInTermEdit(LibraryTermKey.From("copilot"), BuiltInTermIntent.Pinned, shippedCopilot, shippedCopilot));
        entries.Add(new BuiltInTermEdit(LibraryTermKey.From("github api"), BuiltInTermIntent.Off, new TermValues("github api", "GitHub API"), null));
        entries.Add(new BuiltInTermEdit(
            LibraryTermKey.From("hub classic"), BuiltInTermIntent.Edited, new TermValues("hub classic", "Hub"), new TermValues("hub classic", "Hub Classic")));
        Fixture.WriteBytes("edits/github.json", BuiltInLibraryOverlay.Instance.WriteEdits(new BuiltInLibraryEdits("github", entries)));
        Fixture.SaveEnabled("github");

        var service = _libraries.Service();
        var catalog = service.LoadCatalog();
        var rows = catalog.Find("github")!.Content.Rows;
        var composition = LibraryComposition.Committed(catalog, [], new GlossaryBudget(Cleanup.CleanupPrompt.MaxGlossaryTermsCloud));

        void Expect(string key, TermOrigin origin, RuleTier? tier)
        {
            Assert.Equal(origin, rows.Single(row => row.Key == LibraryTermKey.From(key)).Origin);
            var rule = composition.Rules.SingleOrDefault(r => r.LibraryId == "github" && r.Key == LibraryTermKey.From(key));
            Assert.Equal(tier, rule?.Tier);
        }

        Expect("get hub", TermOrigin.Edited, RuleTier.Authored);
        Expect("copilot", TermOrigin.Pinned, RuleTier.Authored);
        Expect("gh cli", TermOrigin.Added, RuleTier.Authored);
        Expect("hub classic", TermOrigin.NoLongerShipped, RuleTier.Authored);
        Expect("github api", TermOrigin.Off, null);
        Expect("git hub", TermOrigin.Shipped, RuleTier.Shipped);
        Assert.Equal(composition.LibraryEntries, service.Current.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scribes_own_removal_of_an_edits_document_drops_its_accepted_hash_in_the_same_commit(bool backUpAndReset)
    {
        // From C's round 2 (9.3): Restore all built-in values, and Back up and reset, remove the document and drop the
        // accepted hash in the Save's own commit, or the next load would read the missing document as one deleted outside
        // Scribe and turn the built-in's AI permission off.
        Fixture.SaveEnabled("github");
        var service = _libraries.Service();
        var workspace = RealLibraries.Workspace(service.LoadCatalog());
        workspace.EditTerm("github", RealLibraries.Row(workspace, "github", "get hub").RowId, new TermValues("get hub", "GitHub Enterprise"));
        _libraries.Save(service, workspace);
        Assert.True(Fixture.Exists("edits/github.json"));
        if (backUpAndReset)
        {
            Fixture.Write("edits/github.json", "{ damaged");
        }

        var catalog = _libraries.Restart().LoadCatalog();
        var resetting = RealLibraries.Workspace(catalog);
        if (backUpAndReset)
        {
            Assert.Equal(LibraryFileState.Unreadable, catalog.Find("github")!.State);
            resetting.RecoverBuiltIn("github", BuiltInEditsRecovery.BackUpAndReset);
        }
        else
        {
            resetting.RestoreAllBuiltInValues("github");
        }

        var reset = _libraries.Save(_libraries.Service(), resetting);

        Assert.Equal(LibrarySaveStatus.Applied, reset.Outcome.Status);
        Assert.False(Fixture.Exists("edits/github.json"));
        Assert.False(reset.Committed.LocalState.AcceptedContent.ContainsKey("github"));
        var next = _libraries.Restart();
        var after = next.LoadCatalog();
        Assert.False(after.LocalState.AcceptedContent.ContainsKey("github"));

        // Scribe's own removal is never read as a document that disappeared outside Scribe: nothing is left to adopt.
        Assert.Null(LibraryComposer.Instance.PlanAdoption(
            after, new LibraryStateContext(RunningOnDefaults: false, DatabaseRepaired: false, GenerationStored: true, CommitWitnessed: true)));
        Assert.Contains(next.Current.Entries, entry => entry.Pattern == "get hub" && entry.Replacement == "GitHub");
        if (!backUpAndReset)
        {
            Assert.True(AiVocabularyPolicy.IsPermitted(after.LocalState, "github", true, after.Find("github")!.ContentHash));
            Assert.Contains("github", next.Current.AiScope.PermittedLibraryIds);
            Assert.Contains(next.Current.AiEntries, entry => entry.Pattern == "get hub" && entry.Replacement == "GitHub");
        }
        else
        {
            // The damage was itself a change outside Scribe (A4): the load that met it took AI cleanup away from the
            // built-in, and resetting its edits does not give it back; the user turns it on again.
            Assert.False(after.LocalState.AiPermissions["github"]);
            Assert.DoesNotContain("github", next.Current.AiScope.PermittedLibraryIds);
        }
    }

    [Fact]
    public void Content_the_codec_refuses_to_encode_is_refused_before_any_file_is_opened()
    {
        // Encode before opening a destination (9.1 step 8): X's writer refuses a metadata line break, which only a bug
        // upstream can hand it, and the Save fails with nothing written, the library's file untouched.
        Fixture.Write("team-terms.csv", "# name: Team terms\npattern,replacement\nkube,Kubernetes\n");
        Fixture.SaveEnabled("team-terms");
        var service = _libraries.Service();
        var catalog = service.LoadCatalog();
        var before = File.ReadAllBytes(Fixture.PathOf("team-terms.csv"));
        var files = Fixture.AllFiles();
        var broken = LibraryStorageFixture.Content("team-terms", "Team\nterms", ("kube", "K8s"));

        var prepared = service.PrepareSave(Changes.Of(catalog, writes: [Changes.Edit(catalog, "team-terms", broken)]));

        Assert.Equal(LibraryPrepareStatus.Failed, prepared.Status);
        Assert.Null(prepared.Save);
        Assert.Equal(before, File.ReadAllBytes(Fixture.PathOf("team-terms.csv")));
        Assert.Equal(files, Fixture.AllFiles());
        Assert.Throws<ArgumentException>(() => LibraryCsvCodec.Instance.WriteManaged(broken));
    }

    [Fact]
    public void The_container_wires_one_library_service_behind_its_three_interfaces_over_the_real_parts()
    {
        // 9.1 step 2: one DictionaryLibraryService singleton behind IDictionaryLibraryService, ILibraryCatalogStore and
        // ILibraryVocabularySource, the three pure parts as singletons, and the janitor handed to storage maintenance.
        var root = Path.Combine(Path.GetTempPath(), "scribe-di-libraries-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddScribeCore();
        services.AddSingleton(new AppPaths(root));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        try
        {
            using var provider = services.BuildServiceProvider();
            var service = provider.GetRequiredService<DictionaryLibraryService>();
            Assert.Same(service, provider.GetRequiredService<IDictionaryLibraryService>());
            Assert.Same(service, provider.GetRequiredService<ILibraryCatalogStore>());
            Assert.Same(service, provider.GetRequiredService<ILibraryVocabularySource>());
            Assert.Same(LibraryCsvCodec.Instance, provider.GetRequiredService<ILibraryCsvCodec>());
            Assert.Same(BuiltInLibraryOverlay.Instance, provider.GetRequiredService<IBuiltInLibraryOverlay>());
            Assert.Same(LibraryComposer.Instance, provider.GetRequiredService<ILibraryComposer>());

            var maintenance = provider.GetRequiredService<StorageMaintenance>();
            var janitor = typeof(StorageMaintenance)
                .GetField("_libraryJanitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(maintenance);
            Assert.Same(service.Janitor, janitor);

            // The real parts, as the app runs them: the first start records the library state (the interim composer
            // wrote none), an edits document O writes applies, and an import is written by X's managed writer.
            var paths = provider.GetRequiredService<AppPaths>();
            Directory.CreateDirectory(paths.LibraryEditsDir);
            File.WriteAllBytes(Path.Combine(paths.LibraryEditsDir, "github.json"), RealEdits.Document("github", ("get hub", "GitHub Enterprise")));
            provider.GetRequiredService<ISettingsRepository>().Save(AppSettings.CreateDefault());
            var catalog = service.LoadCatalog();
            Assert.NotNull(provider.GetRequiredService<ISettingsRepository>().Get(LibrarySettingKeys.State));
            Assert.Equal("GitHub Enterprise", catalog.Find("github")!.Content.Rows.Single(row => row.Key == LibraryTermKey.From("get hub")).Values.Written);
            var imported = service.Import("pattern,replacement\nkube,Kubernetes\n", "Team");
            Assert.Contains("# scribe-format: 2", File.ReadAllText(Path.Combine(paths.LibrariesDir, imported.Id + ".csv")), StringComparison.Ordinal);
        }
        finally
        {
            DatabasePools.Release(new AppPaths(root));
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
}
