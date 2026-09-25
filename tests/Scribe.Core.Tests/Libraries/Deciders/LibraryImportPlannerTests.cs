using Scribe.Core.Libraries;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Deciders.DeciderFixture;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>D-7 and D-7b: the import preview's counts and choices, and a plan that applies exactly its own rows.</summary>
public sealed class LibraryImportPlannerTests
{
    [Fact]
    public void D7_the_plan_counts_adds_written_differently_already_here_removal_rules_and_skipped_rows()
    {
        var workspace = Workspace(Standard());
        var document = new LibraryCsvDocument(
            "Team terms", "Work", "Shared terms", null,
            [
                new TermValues("kube", "Kubernetes"),
                new TermValues("get hub", "GitHub"),
                new TermValues("Get Hub ", "GitHub Enterprise", WholeWord: false),
                new TermValues("vm", "VM"),
                new TermValues("um", ""),
                new TermValues("vm", "virtual machine"),
                new TermValues("vm", "VM"),
            ],
            [new LibraryCsvRowError(9, LibraryCsvRowErrorKind.InvalidWholeWord, "maybe")],
            new LibraryTextEncoding(1252, false, true, false));

        var plan = LibraryImportPlanner.Plan(document, new LibraryImportTarget.ExistingLibrary("team-terms"), workspace.Draft);

        Assert.Equal(
            [
                LibraryImportOperationKind.AlreadyHere,
                LibraryImportOperationKind.WrittenDifferently,
                LibraryImportOperationKind.WrittenDifferently,
                LibraryImportOperationKind.Add,
                LibraryImportOperationKind.Add,
                LibraryImportOperationKind.WrittenDifferently,
                LibraryImportOperationKind.AlreadyHere,
            ],
            plan.Operations.Select(operation => operation.Kind));
        Assert.Equal((2, 3, 2, 1, 1), (plan.Adds, plan.WrittenDifferently, plan.AlreadyHere, plan.RemovalRules, plan.Skipped));
        Assert.Equal(RowIdOf(workspace, "team-terms", "get hub"), plan.Operations[1].ExistingRowId);
        Assert.Equal(new TermValues("get hub", "GitHub Enterprise"), plan.Operations[1].ExistingValues);
        Assert.Null(plan.Operations[5].ExistingRowId);
        Assert.Equal(new TermValues("vm", "VM"), plan.Operations[5].ExistingValues);
        Assert.Equal(workspace.Revision, plan.DraftRevision);
        Assert.True(plan.Encoding.AnsiFallback);
        Assert.Empty(plan.NonAsciiRows);

        // Keep mine is the default and leaves every row written differently alone.
        Assert.True(workspace.ApplyImport(plan, ImportConflictChoice.KeepMine).Applied);
        Assert.Equal(
            [new TermValues("kube", "Kubernetes"), new TermValues("get hub", "GitHub Enterprise"), new TermValues("vm", "VM"), new TermValues("um", "")],
            ValuesOf(workspace, "team-terms"));
        Assert.Equal(ImportConflictChoice.KeepMine, default(ImportConflictChoice));
    }

    [Fact]
    public void D7_use_the_files_version_takes_its_values_and_keeps_the_existing_spoken_form()
    {
        var workspace = Workspace(Standard());
        var plan = LibraryImportPlanner.Plan(
            Document(null, new TermValues("GET HUB", "GH", WholeWord: false), new TermValues("vm", "VM"), new TermValues("vm", "virtual machine")),
            new LibraryImportTarget.ExistingLibrary("team-terms"),
            workspace.Draft);

        Assert.True(workspace.ApplyImport(plan, ImportConflictChoice.UseFilesVersion).Applied);

        Assert.Equal(
            [new TermValues("kube", "Kubernetes"), new TermValues("get hub", "GH", WholeWord: false), new TermValues("vm", "virtual machine")],
            ValuesOf(workspace, "team-terms"));
    }

    [Fact]
    public void D7_the_name_comes_from_the_header_then_the_file_and_is_made_unique_and_based_on_suggests_a_target()
    {
        var workspace = Workspace(Standard());

        var fromHeader = LibraryImportPlanner.Plan(Document("Team terms"), new LibraryImportTarget.NewLibrary("whatever.csv"), workspace.Draft);
        var fromFile = LibraryImportPlanner.Plan(Document(null), new LibraryImportTarget.NewLibrary(@"C:\Downloads\Release notes.csv"), workspace.Draft);
        var fallback = LibraryImportPlanner.Plan(Document("  "), new LibraryImportTarget.NewLibrary(null), workspace.Draft);
        var basedOn = LibraryImportPlanner.Plan(
            new LibraryCsvDocument("GitHub - Copy", null, null, GitHubId, [], [], new LibraryTextEncoding(65001, true, false, false)),
            new LibraryImportTarget.NewLibrary(null),
            workspace.Draft);

        Assert.Equal("Team terms 2", fromHeader.SuggestedName);
        Assert.Equal("Release notes", fromFile.SuggestedName);
        Assert.Equal("Imported library", fallback.SuggestedName);
        Assert.Equal(GitHubId, basedOn.BasedOnTarget);
        Assert.IsType<LibraryImportTarget.NewLibrary>(basedOn.Target);

        // Applied as a new library: named by the plan, off, and every validated row in file order.
        var rows = LibraryImportPlanner.Plan(
            Document("Ops", new TermValues("aks", "AKS"), new TermValues("kube", "K8s")), new LibraryImportTarget.NewLibrary(null), workspace.Draft);
        Assert.True(workspace.ApplyImport(rows, ImportConflictChoice.KeepMine).Applied);
        var imported = workspace.Draft.Libraries.Single(library => library.Origin == LibraryOrigin.Imported);
        Assert.Equal(("Ops", "custom-ops"), (imported.Content.Name, imported.Content.Id));
        Assert.DoesNotContain(imported.Content.Id, workspace.Draft.LocalState.EnabledIds);
        Assert.Equal([new TermValues("aks", "AKS"), new TermValues("kube", "K8s")], ValuesOf(workspace, imported.Content.Id));
    }

    [Fact]
    public void D7_an_imported_name_with_a_double_quote_must_be_edited_before_the_import_applies()
    {
        var workspace = Workspace(Standard());
        var plan = LibraryImportPlanner.Plan(Document("Bob\"s terms", new TermValues("a", "A")), new LibraryImportTarget.NewLibrary(null), workspace.Draft);
        Assert.Equal("Bob\"s terms", plan.SuggestedName);

        var refused = workspace.ApplyImport(plan, ImportConflictChoice.KeepMine);
        Assert.False(refused.Applied);
        Assert.Equal((LibraryValidationKind.MetadataDoubleQuote, LibraryMetadataField.Name), (refused.Issue!.Kind, refused.Issue.Metadata));
        Assert.False(workspace.HasUnsavedChanges);

        Assert.True(workspace.ApplyImport(plan with { SuggestedName = "Bobs terms" }, ImportConflictChoice.KeepMine).Applied);
        var taken = workspace.ApplyImport(
            LibraryImportPlanner.Plan(Document("x"), new LibraryImportTarget.NewLibrary(null), workspace.Draft) with { SuggestedName = "bobs TERMS" },
            ImportConflictChoice.KeepMine);
        Assert.Equal(LibraryValidationKind.DuplicateName, taken.Issue!.Kind);
    }

    [Fact]
    public void D7_importing_into_a_built_in_makes_edits_and_additions()
    {
        var workspace = Workspace(Standard());
        var plan = LibraryImportPlanner.Plan(
            Document(null, new TermValues("copilot", "GitHub Copilot"), new TermValues("gh cli", "GitHub CLI")),
            new LibraryImportTarget.ExistingLibrary(GitHubId),
            workspace.Draft);

        workspace.ApplyImport(plan, ImportConflictChoice.UseFilesVersion);

        var rows = workspace.Draft.Find(GitHubId)!.Content.Rows;
        Assert.Equal(TermOrigin.Edited, rows.Single(row => row.Key == LibraryTermKey.From("copilot")).Origin);
        Assert.Equal(TermOrigin.Added, rows.Single(row => row.Key == LibraryTermKey.From("gh cli")).Origin);
        var edits = Assert.Single(Capture(workspace).Writes).Edits!;
        Assert.Equal(2, edits.Terms.Count);
    }

    [Fact]
    public void D7_a_file_row_saying_a_renamed_built_in_rows_original_form_meets_that_row_and_never_adds_a_second_with_its_key()
    {
        LibraryWorkspace Renamed(out long getHub)
        {
            var workspace = Workspace(Standard());
            getHub = RowIdOf(workspace, GitHubId, "get hub");
            workspace.EditTerm(GitHubId, getHub, new TermValues("git hub", "GitHub"));
            return workspace;
        }

        var document = Document(null, new TermValues("get hub", "GitHub!"), new TermValues("git hub", "GitHub"), new TermValues("gh cli", "GitHub CLI"));
        var keep = Renamed(out var renamedRow);
        var plan = LibraryImportPlanner.Plan(document, new LibraryImportTarget.ExistingLibrary(GitHubId), keep.Draft);

        Assert.Equal(
            [LibraryImportOperationKind.WrittenDifferently, LibraryImportOperationKind.AlreadyHere, LibraryImportOperationKind.Add],
            plan.Operations.Select(operation => operation.Kind));
        Assert.Equal(renamedRow, plan.Operations[0].ExistingRowId);
        Assert.Equal(new TermValues("git hub", "GitHub"), plan.Operations[0].ExistingValues);

        Assert.True(keep.ApplyImport(plan, ImportConflictChoice.KeepMine).Applied);
        Assert.Equal("git hub", keep.RowsOf(GitHubId).Single(row => row.RowId == renamedRow).Row.Values.Spoken);
        Assert.NotNull(keep.CaptureChangeSet().ChangeSet);

        // The file's version of that term is the term as the file says it: its original form comes back.
        var take = Renamed(out renamedRow);
        Assert.True(take.ApplyImport(LibraryImportPlanner.Plan(document, new LibraryImportTarget.ExistingLibrary(GitHubId), take.Draft),
            ImportConflictChoice.UseFilesVersion).Applied);
        Assert.Equal(new TermValues("get hub", "GitHub!"), take.RowsOf(GitHubId).Single(row => row.RowId == renamedRow).Row.Values);
        Assert.Equal(2, Assert.Single(Capture(take).Writes).Edits!.Terms.Count);
    }

    [Fact]
    public void D7b_two_files_that_differ_in_one_added_row_give_different_plans_that_each_add_exactly_their_own_row()
    {
        var catalog = Standard();
        var first = Document("Team terms", new TermValues("kube", "Kubernetes"), new TermValues("aks", "AKS"));
        var second = Document("Team terms", new TermValues("kube", "Kubernetes"), new TermValues("vm", "VM"));
        var target = new LibraryImportTarget.ExistingLibrary("team-terms");

        var one = Workspace(catalog);
        var two = Workspace(catalog);
        var planOne = LibraryImportPlanner.Plan(first, target, one.Draft);
        var planTwo = LibraryImportPlanner.Plan(second, target, two.Draft);

        Assert.Equal((planOne.Adds, planOne.AlreadyHere), (planTwo.Adds, planTwo.AlreadyHere));
        Assert.NotEqual(planOne.Operations.Select(operation => operation.FileRow), planTwo.Operations.Select(operation => operation.FileRow));

        Assert.True(one.ApplyImport(planOne, ImportConflictChoice.KeepMine).Applied);
        Assert.True(two.ApplyImport(planTwo, ImportConflictChoice.KeepMine).Applied);
        var original = catalog.Find("team-terms")!.Content.Rows.Select(row => row.Values).ToList();
        Assert.Equal(original.Append(new TermValues("aks", "AKS")), ValuesOf(one, "team-terms"));
        Assert.Equal(original.Append(new TermValues("vm", "VM")), ValuesOf(two, "team-terms"));

        // A plan for an older revision is refused, and the shell plans again.
        var stale = LibraryImportPlanner.Plan(second, target, one.Draft);
        one.SetEnabled(AzureId, true);
        var refused = one.ApplyImport(stale, ImportConflictChoice.KeepMine);
        Assert.False(refused.Applied);
        Assert.Null(refused.Issue);
        Assert.True(one.ApplyImport(LibraryImportPlanner.Plan(second, target, one.Draft), ImportConflictChoice.KeepMine).Applied);
    }

    [Fact]
    public void A_file_read_as_ansi_lists_its_rows_with_letters_outside_ascii()
    {
        var workspace = Workspace(Standard());
        var plan = LibraryImportPlanner.Plan(
            Document(null, new TermValues("creme brulee", "crème brûlée"), new TermValues("kube", "Kubernetes"), new TermValues("€", "euro")),
            new LibraryImportTarget.NewLibrary("dessert.csv"),
            workspace.Draft);

        Assert.Equal([new TermValues("creme brulee", "crème brûlée")], plan.NonAsciiRows);
    }

    [Fact]
    public void An_import_into_a_library_that_cannot_be_edited_or_a_deleted_one_is_refused()
    {
        var catalog = Catalog([Custom("partial", "Partial", [new TermValues("a", "A")], state: LibraryFileState.PartlyReadable)], ["partial"]);
        var workspace = Workspace(catalog);
        var plan = LibraryImportPlanner.Plan(Document(null, new TermValues("b", "B")), new LibraryImportTarget.ExistingLibrary("partial"), workspace.Draft);

        Assert.Equal(LibraryValidationKind.ContentNotSaveable, workspace.ApplyImport(plan, ImportConflictChoice.KeepMine).Issue!.Kind);
        workspace.DeleteLibrary("partial");
        Assert.Throws<ArgumentException>(() =>
            LibraryImportPlanner.Plan(Document(null), new LibraryImportTarget.ExistingLibrary("partial"), workspace.Draft));
    }
}
