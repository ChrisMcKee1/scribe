using System.Text.RegularExpressions;
using Scribe.Core.Libraries;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Deciders.DeciderFixture;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// The maintainer's product decision: people know libraries as word packs ("Word packs" as a title, "word pack" in a
/// sentence). Every text the deciders hand the page says so, in sentence case: the messages beside a field, the Undo
/// labels, the names the editor suggests and the close prompt. Identifiers, types, ids, file names and log text keep
/// "library", and so does the id rule's fallback slug, which the storage stream's copy of the rule shares (G9). An id is
/// still derived from the name a library is created with, so a new word pack's id follows its new default name.
/// </summary>
public sealed class WordPackWordingTests
{
    [Fact]
    public void Every_message_beside_a_field_calls_a_library_a_word_pack()
    {
        LibraryValidationIssue Issue(LibraryValidationKind kind, LibraryMetadataField field = LibraryMetadataField.None) =>
            new("team-terms", 1, kind, TermFields.None, field);

        // Every message the editor can give: every kind, for every metadata field and file state, with no term, a term,
        // and a renamed built-in row's other form.
        var messages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in Enum.GetValues<LibraryValidationKind>())
        {
            foreach (var field in Enum.GetValues<LibraryMetadataField>())
            {
                foreach (var state in Enum.GetValues<LibraryFileState>())
                {
                    foreach (var (spoken, other) in new (string?, string?)[] { (null, null), ("get hub", null), ("get hub", "git hub") })
                    {
                        messages.Add(LibraryEditor.Message(Issue(kind, field), spoken, state, other));
                    }
                }
            }
        }

        Assert.All(messages, AssertCallsItAWordPack);
        Assert.Equal("\"get hub\" is already in this word pack.", LibraryEditor.Message(Issue(LibraryValidationKind.DuplicateSpoken), "get hub"));
        Assert.Equal("This term is already in this word pack.", LibraryEditor.Message(Issue(LibraryValidationKind.DuplicateSpoken)));
        Assert.Equal(
            "\"get hub\" is already in this word pack as the term you changed to \"git hub\".",
            LibraryEditor.Message(Issue(LibraryValidationKind.DuplicateSpoken), "get hub", otherSpoken: "git hub"));
        Assert.Equal("A word pack can hold up to 50,000 terms.", LibraryEditor.Message(Issue(LibraryValidationKind.TooManyTerms)));
        Assert.Equal("Type a name for this word pack.", LibraryEditor.Message(Issue(LibraryValidationKind.EmptyName, LibraryMetadataField.Name)));
        Assert.Equal("Another word pack already has this name.", LibraryEditor.Message(Issue(LibraryValidationKind.DuplicateName, LibraryMetadataField.Name)));
        Assert.Equal(
            "Some rows of this word pack couldn't be read, so it can't be edited here. Import the file again to see them.",
            LibraryEditor.Message(Issue(LibraryValidationKind.ContentNotSaveable), state: LibraryFileState.PartlyReadable));
        Assert.Equal(
            "This word pack is open in another app. Close it there to make changes.",
            LibraryEditor.Message(Issue(LibraryValidationKind.ContentNotSaveable), state: LibraryFileState.AwaitingRelease));
        Assert.Equal(
            "This word pack couldn't be read, so it can't be edited here.",
            LibraryEditor.Message(Issue(LibraryValidationKind.ContentNotSaveable), state: LibraryFileState.Unreadable));
    }

    [Fact]
    public void Every_undo_label_calls_a_library_a_word_pack()
    {
        var edits = new BuiltInLibraryEdits(GitHubId,
        [
            new BuiltInTermEdit(LibraryTermKey.From("copilot"), BuiltInTermIntent.Edited,
                new TermValues("copilot", "Copilot"), new TermValues("copilot", "GitHub Copilot")),
        ]);
        var catalog = Catalog(
            [
                BuiltIn(GitHubId, edits),
                BuiltIn(AzureId),
                Custom("team-terms", "Team terms", [new TermValues("kube", "Kubernetes"), new TermValues("copilot", "Team Copilot")]),
                Custom("team-terms-copy", "Team terms - Copy", [new TermValues("kube", "K8s")], basedOn: "team-terms"),
            ],
            [GitHubId, AzureId, "team-terms"],
            ai: [new("team-terms", true)]);
        LibraryUsage Usage(string id, bool builtIn) => new(id, id, [], UnusedCount: 1, builtIn);

        // Every structural operation, each on a fresh workspace, and the label Undo would show for it.
        var operations = new (string Label, Action<LibraryWorkspace> Run)[]
        {
            ("Turn off word pack", workspace => workspace.SetEnabled(GitHubId, false)),
            ("Turn on word pack", workspace => workspace.SetEnabled("team-terms-copy", true)),
            ("Delete word pack", workspace => workspace.DeleteLibrary("team-terms-copy")),
            ("Turn off unused word packs", workspace => workspace.ApplyDictionaryCleanup(
                [Usage(AzureId, true)], new LibrarySwitchOffCopy.Result([], 0, []))),
            ("Turn off in other word packs", workspace =>
                workspace.TurnOffInOtherLibraries("team-terms", RowIdOf(workspace, "team-terms", "copilot"), [GitHubId, AzureId])),
            ("Use this copy instead", workspace => workspace.UseCopyInstead("team-terms-copy")),
            ("Delete term", workspace => workspace.DeleteTerm("team-terms", RowIdOf(workspace, "team-terms", "kube"))),
            ("Turn off term", workspace => workspace.SetTermEnabled("team-terms", RowIdOf(workspace, "team-terms", "kube"), false)),
            ("Restore built-in values", workspace => workspace.RestoreBuiltInValues(GitHubId, RowIdOf(workspace, GitHubId, "copilot"))),
            ("Restore all built-in values", workspace => workspace.RestoreAllBuiltInValues(GitHubId)),
            ("Import terms", workspace => workspace.ApplyImport(
                LibraryImportPlanner.Plan(Document(null, new TermValues("aks", "AKS")), new LibraryImportTarget.ExistingLibrary("team-terms"), workspace.Draft),
                ImportConflictChoice.KeepMine)),
            ("Use as a removal rule", workspace =>
            {
                workspace.AddTerm("team-terms", new TermValues("um", ""));
                workspace.EditTerm("team-terms", RowIdOf(workspace, "team-terms", "um"), new TermValues("um", ""), removalIntent: true);
            }),
        };

        foreach (var (label, run) in operations)
        {
            var workspace = Workspace(catalog);
            run(workspace);
            Assert.Equal(label, workspace.UndoLabel);
            AssertCallsItAWordPack(label);
            Assert.All(label.Split(' ').Skip(1), word => Assert.False(char.IsUpper(word[0]), $"\"{label}\" is not in sentence case"));
        }

        // A turned-off term turned back on has a label of its own.
        var turnedOn = Workspace(catalog);
        turnedOn.SetTermEnabled("team-terms", RowIdOf(turnedOn, "team-terms", "kube"), false);
        turnedOn.SetTermEnabled("team-terms", RowIdOf(turnedOn, "team-terms", "kube"), true);
        Assert.Equal("Turn on term", turnedOn.UndoLabel);
    }

    [Fact]
    public void The_names_the_editor_suggests_call_a_library_a_word_pack_and_a_new_id_follows_them()
    {
        Assert.Equal("New word pack", LibraryNaming.NewLibraryBaseName);
        Assert.Equal("Imported word pack", LibraryNaming.ImportedLibraryBaseName);
        Assert.Equal("New word pack", LibraryNaming.NewLibraryName([]));
        Assert.Equal("New word pack 2", LibraryNaming.NewLibraryName(["new WORD PACK"]));
        Assert.Equal("Word pack - Copy", LibraryNaming.CopyName("  ", []));
        Assert.Equal("Word pack - Copy 2", LibraryNaming.CopyName(null, ["word pack - copy"]));
        Assert.Equal("Word pack", LibraryNaming.UniqueName("  ", []));
        Assert.Equal("Word pack 2", LibraryNaming.UniqueName(null, ["WORD PACK"]));
        foreach (var name in new[] { LibraryNaming.NewLibraryName([]), LibraryNaming.ImportedLibraryBaseName, LibraryNaming.CopyName(null, []), LibraryNaming.UniqueName(null, []) })
        {
            AssertCallsItAWordPack(name);
        }

        // The id rule is unchanged: a new library's id comes from the name it is created with, so a new word pack's id and
        // file name follow the new default names, while an id already given never changes.
        var workspace = Workspace(Standard());
        var created = workspace.CreateLibrary();
        Assert.Equal("custom-new-word-pack", created);
        Assert.Equal(("New word pack", "custom-new-word-pack.csv"), (workspace.Draft.Find(created)!.Content.Name, workspace.Draft.Find(created)!.FileName));
        var plan = LibraryImportPlanner.Plan(Document(null, new TermValues("aks", "AKS")), new LibraryImportTarget.NewLibrary(null), workspace.Draft);
        Assert.Equal("Imported word pack", plan.SuggestedName);
        Assert.True(workspace.ApplyImport(plan, ImportConflictChoice.KeepMine).Applied);
        var imported = workspace.Draft.Libraries.Single(library => library.Origin == LibraryOrigin.Imported);
        Assert.Equal(("custom-imported-word-pack", "Imported word pack"), (imported.Content.Id, imported.Content.Name));

        // The fallback slug is part of the id rule, not a name: it stays "library", as tests/fixtures/libraries/slugs.json
        // pins it for the storage stream's copy of the rule.
        Assert.Equal("library", LibraryNaming.Slug("---"));
        Assert.Equal("custom-library", LibraryNaming.NewCustomId("---", []));
    }

    [Fact]
    public void The_close_prompt_names_the_word_packs_page()
    {
        Assert.Equal("You have unsaved changes to Word packs.", SettingsCloseGuard.Prompt(UnsavedSections.Libraries));
        Assert.Equal("You have unsaved changes to Word packs and Voice snippets.", SettingsCloseGuard.Prompt(UnsavedSections.Libraries | UnsavedSections.Snippets));
        for (var flags = 1; flags < 8; flags++)
        {
            var sections = (UnsavedSections)flags;
            var prompt = SettingsCloseGuard.Prompt(sections);
            Assert.DoesNotContain("librar", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(sections.HasFlag(UnsavedSections.Libraries), prompt.Contains("Word packs", StringComparison.Ordinal));
            Assert.Equal(prompt, SettingsCloseGuard.Decide(sections, CloseTrigger.CloseButton).Prompt);
        }
    }

    // A text never says library, and says word pack in sentence case: capitalized only where the text starts with it.
    private static void AssertCallsItAWordPack(string text)
    {
        Assert.DoesNotContain("librar", text, StringComparison.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, "word packs?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var expected = match.Index == 0 ? "W" + match.Value[1..].ToLowerInvariant() : match.Value.ToLowerInvariant();
            Assert.True(match.Value == expected, $"\"{text}\" does not say \"{expected}\"");
        }
    }
}
