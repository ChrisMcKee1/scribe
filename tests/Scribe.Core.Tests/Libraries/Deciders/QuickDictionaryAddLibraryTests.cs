using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Deciders.DeciderFixture;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// D-6: quick add reads committed libraries only (review finding R12). An unsaved library correction never makes it
/// say a rule "already becomes" something, and <see cref="QuickDictionaryAdd.UnsavedLibraryMatches"/> names the unsaved
/// library for "Also in an unsaved library".
/// </summary>
public sealed class QuickDictionaryAddLibraryTests
{
    [Fact]
    public void D6_an_unsaved_library_correction_never_makes_quick_add_answer_already_becomes()
    {
        var catalog = Standard();
        var committed = new LibraryVocabulary(
            catalog.Generation,
            [DictionaryEntry.New("get hub", "GitHub Enterprise")],
            [],
            AiVocabularyScope.None);
        var workspace = Workspace(catalog);
        workspace.AddTerm("team-terms", new TermValues("vm", "virtual machine"));
        workspace.EditTerm("team-terms", RowIdOf(workspace, "team-terms", "get hub"), new TermValues("get hub", "GH"));

        var unsaved = QuickDictionaryAdd.Build("vm", "virtual machine", wholeWord: true, [], committed);
        Assert.Equal(QuickDictionaryAdd.PlanKind.Create, unsaved.Kind);
        Assert.DoesNotContain("already", unsaved.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["team-terms"], QuickDictionaryAdd.UnsavedLibraryMatches(workspace.Draft, committed, "VM "));

        // The committed rule is what quick add knows, however the draft changed it.
        var saved = QuickDictionaryAdd.Build("get hub", "GitHub Enterprise", wholeWord: true, [], committed);
        Assert.Equal(QuickDictionaryAdd.PlanKind.NoChange, saved.Kind);
        Assert.Equal(["team-terms"], QuickDictionaryAdd.UnsavedLibraryMatches(workspace.Draft, committed, "get hub"));
    }

    [Fact]
    public void D6_a_library_whose_unsaved_rule_is_the_committed_one_or_is_off_is_not_named()
    {
        var catalog = Standard();
        var committed = new LibraryVocabulary(catalog.Generation, [DictionaryEntry.New("kube", "Kubernetes")], [], AiVocabularyScope.None);
        var workspace = Workspace(catalog);

        // Unsaved, but its rule for "kube" is the committed one.
        workspace.AddTerm("team-terms", new TermValues("aks", "AKS"));
        Assert.Empty(QuickDictionaryAdd.UnsavedLibraryMatches(workspace.Draft, committed, "kube"));

        // A library off in the draft applies nothing after Save.
        var copy = workspace.Duplicate("team-terms");
        workspace.EditTerm(copy, RowIdOf(workspace, copy, "kube"), new TermValues("kube", "K8s"));
        Assert.Empty(QuickDictionaryAdd.UnsavedLibraryMatches(workspace.Draft, committed, "kube"));
        workspace.SetEnabled(copy, true);
        Assert.Equal([copy], QuickDictionaryAdd.UnsavedLibraryMatches(workspace.Draft, committed, "kube"));
        Assert.Empty(QuickDictionaryAdd.UnsavedLibraryMatches(workspace.Draft, committed, "  "));
    }

    [Fact]
    public void D6_the_dictionary_wins_over_the_committed_libraries_as_dictation_merges_them()
    {
        var committed = new LibraryVocabulary(1, [DictionaryEntry.New("kube", "Kubernetes")], [], AiVocabularyScope.None);

        var plan = QuickDictionaryAdd.Build("kube", "K8s", wholeWord: true, [DictionaryEntry.New("kube", "K8s")], committed);
        Assert.Equal(QuickDictionaryAdd.PlanKind.NoChange, plan.Kind);

        var library = QuickDictionaryAdd.Build("kube", "Kubernetes", wholeWord: true, [], committed);
        Assert.Equal(QuickDictionaryAdd.PlanKind.NoChange, library.Kind);
        Assert.Equal(QuickDictionaryAdd.PlanKind.Create, QuickDictionaryAdd.Build("kube", "Kubernetes", wholeWord: true, [], LibraryVocabulary.Empty).Kind);
    }
}
