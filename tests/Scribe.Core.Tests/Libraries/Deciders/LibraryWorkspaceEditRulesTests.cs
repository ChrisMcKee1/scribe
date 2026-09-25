using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Deciders.DeciderFixture;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// Two integrator decisions after the review of the built-in overlay: an edit of a built-in row hands the overlay only
/// the fields the user changed, so the others keep taking later shipped values (per-field authorship, plan 3.3); and
/// ill-formed UTF-16 is refused in the editor before anything reaches the stores.
/// </summary>
public sealed class LibraryWorkspaceEditRulesTests
{
    private const string Half = "\uD83D";

    [Fact]
    public void Editing_a_built_in_row_hands_the_overlay_only_the_fields_the_user_changed()
    {
        // An edited row whose spoken form an older document left irregular: the user changes only its Written value.
        var edits = new BuiltInLibraryEdits(GitHubId,
        [
            new BuiltInTermEdit(LibraryTermKey.From("get hub"), BuiltInTermIntent.Edited,
                new TermValues("get hub", "GitHub"), new TermValues("get  hub", "GitHub")),
        ]);
        var recording = new RecordingOverlay(TestOverlay);
        var workspace = new LibraryWorkspace(Catalog([BuiltIn(GitHubId, edits)], [GitHubId]), recording, DefaultAi, ShippedLibrary);
        var row = workspace.RowsOf(GitHubId)[0];
        Assert.Equal("get  hub", row.Row.Values.Spoken);

        Assert.True(workspace.EditTerm(GitHubId, row.RowId, row.Row.Values with { Written = " GH " }).Applied);

        var (edited, given) = Assert.Single(recording.Edits);
        Assert.Same(row.Row, edited);
        Assert.Equal(new TermValues("get  hub", "GH", WholeWord: true, Enabled: true), given);
        Assert.Same(row.Row.Values.Spoken, given.Spoken);

        // A shipped row typed back to its own values is no edit at all, so nothing is claimed for the user.
        var copilot = workspace.RowsOf(GitHubId).Single(candidate => candidate.Row.Key == LibraryTermKey.From("copilot"));
        var revision = workspace.Revision;
        Assert.True(workspace.EditTerm(GitHubId, copilot.RowId, copilot.Row.Values with { Written = "  Copilot " }).Applied);
        Assert.Equal(revision, workspace.Revision);
        Assert.Single(recording.Edits);

        // Changing only Whole word hands over both text values exactly as shown.
        workspace.EditTerm(GitHubId, copilot.RowId, copilot.Row.Values with { WholeWord = false });
        Assert.Equal(copilot.Row.Values with { WholeWord = false }, recording.Edits[^1].Values);
    }

    [Fact]
    public void A_custom_row_is_still_committed_whole_so_an_edited_legacy_row_is_regular()
    {
        var catalog = Catalog([Custom("legacy", "Legacy", [new TermValues("get  hub", "Git Hub")])], ["legacy"], ai: [new("legacy", true)]);
        var workspace = Workspace(catalog);
        var row = workspace.RowsOf("legacy")[0];

        workspace.EditTerm("legacy", row.RowId, row.Row.Values with { Written = "GitHub" });

        Assert.Equal(new TermValues("get hub", "GitHub"), workspace.RowsOf("legacy")[0].Row.Values);
    }

    [Fact]
    public void Ill_formed_text_is_refused_before_it_reaches_the_draft_with_a_plain_message()
    {
        var workspace = Workspace(Standard());
        var kube = RowIdOf(workspace, "team-terms", "kube");

        var name = workspace.Rename("team-terms", "Team " + Half);
        Assert.Equal((false, LibraryValidationKind.IllFormedText, LibraryMetadataField.Name), (name.Applied, name.Issue!.Kind, name.Issue.Metadata));
        Assert.Equal(
            "Part of a character is missing here, so this can't be saved. Delete it and type it again.",
            LibraryEditor.Message(name.Issue));
        Assert.Equal(LibraryMetadataField.Category, workspace.SetDetails("team-terms", "Work" + Half, null).Issue!.Metadata);
        Assert.Equal(LibraryMetadataField.Description, workspace.SetDetails("team-terms", "Custom", "\uDE80 terms").Issue!.Metadata);

        var spoken = workspace.AddTerm("team-terms", new TermValues("kube" + Half, "K"));
        Assert.Equal((false, LibraryValidationKind.IllFormedText, TermFields.Spoken), (spoken.Applied, spoken.Issue!.Kind, spoken.Issue.Field));
        Assert.Equal(TermFields.Written, workspace.AddTerm("team-terms", new TermValues("x", "\uDE80")).Issue!.Field);
        var edited = workspace.EditTerm("team-terms", kube, new TermValues("kube", "K" + Half));
        Assert.Equal((false, LibraryValidationKind.IllFormedText, TermFields.Written, (long?)kube), (edited.Applied, edited.Issue!.Kind, edited.Issue.Field, edited.Issue.RowId));
        Assert.Equal(LibraryValidationKind.IllFormedText, workspace.AddTerm(GitHubId, new TermValues("gh" + Half, "GH")).Issue!.Kind);
        Assert.Equal(LibraryValidationKind.IllFormedText,
            workspace.EditTerm(GitHubId, RowIdOf(workspace, GitHubId, "copilot"), new TermValues("copilot", Half)).Issue!.Kind);
        Assert.False(workspace.HasUnsavedChanges);

        // A whole emoji is text like any other.
        Assert.True(workspace.AddTerm("team-terms", new TermValues("rocket", "\uD83D\uDE80")).Applied);
        Assert.True(workspace.Rename("team-terms", "Team \uD83D\uDE80").Applied);
        Assert.NotNull(workspace.CaptureChangeSet().ChangeSet);
    }

    [Fact]
    public void An_import_bringing_ill_formed_text_is_refused()
    {
        var workspace = Workspace(Standard());

        var badRow = LibraryImportPlanner.Plan(
            Document("Imported", new TermValues("ok", "OK"), new TermValues("bad", "B" + Half)),
            new LibraryImportTarget.ExistingLibrary("team-terms"),
            workspace.Draft);
        var refused = workspace.ApplyImport(badRow, ImportConflictChoice.KeepMine);
        Assert.Equal((false, LibraryValidationKind.IllFormedText, TermFields.Written, "team-terms"),
            (refused.Applied, refused.Issue!.Kind, refused.Issue.Field, refused.Issue.LibraryId));

        var named = LibraryImportPlanner.Plan(Document("Imported", new TermValues("ok", "OK")), new LibraryImportTarget.NewLibrary(null), workspace.Draft);
        var badName = workspace.ApplyImport(named with { SuggestedName = "Imported " + Half }, ImportConflictChoice.KeepMine);
        Assert.Equal((LibraryValidationKind.IllFormedText, LibraryMetadataField.Name), (badName.Issue!.Kind, badName.Issue.Metadata));
        Assert.False(workspace.HasUnsavedChanges);
        Assert.True(workspace.ApplyImport(named, ImportConflictChoice.KeepMine).Applied);
    }

    [Fact]
    public void Content_that_came_from_elsewhere_with_ill_formed_text_cannot_be_saved()
    {
        // No codec decodes bytes into ill-formed text; this is the last check before a write, for anything that did not
        // come through the editor's commands.
        var entry = Deleted("20260901T100000Z.odd.csv", "odd", "Odd", new TermValues("fine", "Fine"), new TermValues("odd", "O" + Half));
        var workspace = Workspace(Catalog([BuiltIn(GitHubId)], [GitHubId], recentlyDeleted: [entry.Entry]));
        var id = workspace.RestoreDeleted(entry);

        workspace.EditTerm(id, RowIdOf(workspace, id, "fine"), new TermValues("fine", "Finer"));
        var issue = Assert.Single(workspace.CaptureChangeSet().Issues);

        Assert.Equal((LibraryValidationKind.IllFormedText, TermFields.Written, (long?)RowIdOf(workspace, id, "odd")),
            (issue.Kind, issue.Field, issue.RowId));
    }

    // Records what each Edit hands the overlay, and otherwise is the test overlay.
    private sealed class RecordingOverlay(IBuiltInLibraryOverlay inner) : IBuiltInLibraryOverlay
    {
        public List<(LibraryRow Row, TermValues Values)> Edits { get; } = [];

        public BuiltInEditsReadResult ReadEdits(string libraryId, ReadOnlySpan<byte> bytes) => inner.ReadEdits(libraryId, bytes);

        public byte[] WriteEdits(BuiltInLibraryEdits edits) => inner.WriteEdits(edits);

        public IReadOnlyList<LibraryRow> Apply(DictionaryLibrary shipped, BuiltInLibraryEdits? edits) => inner.Apply(shipped, edits);

        public LibraryRow Edit(LibraryRow row, TermValues values)
        {
            Edits.Add((row, values));
            return inner.Edit(row, values);
        }

        public LibraryRow SetEnabled(LibraryRow row, bool enabled) => inner.SetEnabled(row, enabled);

        public LibraryRow? RestoreShipped(LibraryRow row) => inner.RestoreShipped(row);

        public LibraryRow Add(TermValues values) => inner.Add(values);

        public LibraryRow ResolveReview(LibraryRow row, TermReviewChoice choice) => inner.ResolveReview(row, choice);

        public BuiltInLibraryEdits? Collect(DictionaryLibrary shipped, BuiltInLibraryEdits? committed, IReadOnlyList<LibraryRow> rows) =>
            inner.Collect(shipped, committed, rows);

        public IReadOnlyList<TermValues> AuthoredTerms(BuiltInLibraryEdits edits) => inner.AuthoredTerms(edits);
    }
}
