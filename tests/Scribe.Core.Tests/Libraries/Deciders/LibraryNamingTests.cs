using System.Reflection;
using System.Text.Json;
using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Deciders.DeciderFixture;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// D-3: names and ids (review findings R11 and G9). The slug is exactly the rule the library service has always used,
/// pinned by the shared vectors in <c>tests/fixtures/libraries/slugs.json</c>; new ids are <c>custom-&lt;slug&gt;</c> fixed at
/// creation and never collide with a built-in id, a file in the folder or a Recently deleted entry; names are suggested
/// unique, and untouched legacy duplicates are left alone.
/// </summary>
public sealed class LibraryNamingTests
{
    [Fact]
    public void Every_slug_vector_gives_its_answer_and_the_answer_follows_the_pasted_rule()
    {
        var vectors = Fixture().GetProperty("slugs").EnumerateArray().ToList();
        Assert.NotEmpty(vectors);
        foreach (var vector in vectors)
        {
            var name = vector.GetProperty("name").GetString()!;
            var expected = vector.GetProperty("slug").GetString()!;
            Assert.Equal(expected, LibraryNaming.Slug(name));
            Assert.Equal(expected, ReferenceSlug(name));
        }
    }

    [Fact]
    public void The_slug_is_the_one_the_library_service_derives_ids_from_today()
    {
        // The storage stream points its copy of the rule at LibraryNaming at integration; while the service still has
        // its own, the two must agree on every vector (review finding G9).
        var slugify = typeof(DictionaryLibraryService).GetMethod("Slugify", BindingFlags.NonPublic | BindingFlags.Static);
        if (slugify is null)
        {
            return;
        }

        foreach (var vector in Fixture().GetProperty("slugs").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            Assert.Equal((string)slugify.Invoke(null, [name])!, LibraryNaming.Slug(name));
        }
    }

    [Fact]
    public void New_and_remapped_ids_take_the_next_free_suffix()
    {
        foreach (var vector in Fixture().GetProperty("newCustomIds").EnumerateArray())
        {
            var taken = vector.GetProperty("taken").EnumerateArray().Select(item => item.GetString()).ToList();
            Assert.Equal(vector.GetProperty("id").GetString(), LibraryNaming.NewCustomId(vector.GetProperty("name").GetString(), taken));
        }

        foreach (var vector in Fixture().GetProperty("remapIds").EnumerateArray())
        {
            var taken = vector.GetProperty("taken").EnumerateArray().Select(item => item.GetString()).ToList();
            Assert.Equal(vector.GetProperty("id").GetString(), LibraryNaming.RemapId(vector.GetProperty("stem").GetString()!, taken));
        }
    }

    [Fact]
    public void Suggested_names_are_unique_without_case_and_use_ascii_hyphens()
    {
        Assert.Equal("New word pack", LibraryNaming.NewLibraryName(["Team terms"]));
        Assert.Equal("New word pack 2", LibraryNaming.NewLibraryName(["new WORD PACK"]));
        Assert.Equal("New word pack 3", LibraryNaming.NewLibraryName(["New word pack", "New word pack 2"]));
        Assert.Equal("GitHub - Copy", LibraryNaming.CopyName("GitHub", ["GitHub"]));
        Assert.Equal("GitHub - Copy 2", LibraryNaming.CopyName("GitHub", ["GitHub", "github - copy"]));
        Assert.Equal("Team terms (changed outside Scribe)", LibraryNaming.ChangedOutsideName("Team terms"));
        Assert.DoesNotContain('\u2013', LibraryNaming.CopyName("GitHub", []));
        Assert.DoesNotContain('\u2014', LibraryNaming.CopyName("GitHub", []));
        Assert.True(LibraryNaming.IsNameTaken(" team TERMS ", ["Team terms"]));
        Assert.False(LibraryNaming.IsNameTaken("Team terms 2", ["Team terms"]));
        Assert.False(LibraryNaming.IsNameTaken("  ", ["  "]));
    }

    [Fact]
    public void A_new_library_gets_a_custom_id_that_no_built_in_file_or_deleted_entry_holds()
    {
        var catalog = Catalog(
            [
                BuiltIn(GitHubId),
                Custom("custom-new-word-pack", "Old thing", [new TermValues("a", "A")]),
                Custom("custom-github", "Hand placed", [new TermValues("b", "B")], fileName: "github.csv"),
            ],
            recentlyDeleted: [Deleted("20260901T100000Z.custom-new-word-pack-2.csv", "custom-new-word-pack-2", "Gone").Entry]);
        var workspace = Workspace(catalog);

        var first = workspace.CreateLibrary();
        var second = workspace.CreateLibrary();

        Assert.Equal("custom-new-word-pack-3", first);
        Assert.Equal("custom-new-word-pack-2-2", second);
        Assert.Equal("New word pack", workspace.Draft.Find(first)!.Content.Name);
        Assert.Equal("New word pack 2", workspace.Draft.Find(second)!.Content.Name);

        // The id is fixed at creation: a rename changes neither the id nor the file name.
        workspace.Rename(first, "Release notes");
        Assert.Equal("custom-new-word-pack-3.csv", workspace.Draft.Find(first)!.FileName);
        Assert.Null(workspace.Draft.Find("custom-release-notes"));
    }

    [Fact]
    public void Untouched_legacy_duplicate_names_are_allowed_and_a_typed_duplicate_is_refused()
    {
        var catalog = Catalog(
            [
                Custom("notes", "Notes", [new TermValues("a", "A")]),
                Custom("notes-2", "Notes", [new TermValues("b", "B")]),
            ],
            ["notes", "notes-2"],
            ai: [new("notes", true), new("notes-2", true)]);
        var workspace = Workspace(catalog);

        // Editing a row of one of two libraries that already share a name saves: the name was not typed.
        Assert.True(workspace.EditTerm("notes", RowIdOf(workspace, "notes", "a"), new TermValues("a", "Alpha")).Applied);
        Assert.NotNull(workspace.CaptureChangeSet().ChangeSet);

        var refused = workspace.Rename("notes-2", " NOTES ");
        Assert.False(refused.Applied);
        Assert.Equal(LibraryValidationKind.DuplicateName, refused.Issue!.Kind);
        Assert.Equal(LibraryMetadataField.Name, refused.Issue.Metadata);

        // Once the other library has a name of its own, a change of case to a library's own name is no duplicate.
        Assert.True(workspace.Rename("notes-2", "Notes 2").Applied);
        Assert.True(workspace.Rename("notes", "NOTES").Applied);
        Assert.Equal("NOTES", workspace.Draft.Find("notes")!.Content.Name);

        var blank = workspace.Rename("notes", "   ");
        Assert.False(blank.Applied);
        Assert.Equal(LibraryValidationKind.EmptyName, blank.Issue!.Kind);
    }

    [Fact]
    public void A_restore_keeps_its_old_id_unless_something_took_it()
    {
        var free = Deleted("20260901T100000Z.team-notes.csv", "team-notes", "Team notes", new TermValues("x", "X"));
        var takenAway = Deleted("20260901T100001Z.team-terms.csv", "team-terms", "Team terms", new TermValues("y", "Y"));
        var catalog = Catalog(
            [Custom("team-terms", "Team terms", [new TermValues("z", "Z")])],
            recentlyDeleted: [free.Entry, takenAway.Entry]);
        var workspace = Workspace(catalog);

        Assert.Equal("team-notes", workspace.RestoreDeleted(free));
        Assert.Equal("custom-team-terms", workspace.RestoreDeleted(takenAway));
    }

    // The rule pasted in contracts 3.5.4, written out again so a change to either copy fails here.
    private static string ReferenceSlug(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        var pendingDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingDash && sb.Length > 0)
                {
                    sb.Append('-');
                }

                sb.Append(ch);
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }

        var slug = sb.ToString();
        return slug.Length == 0 ? "library" : slug;
    }

    private static JsonElement Fixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "tests", "fixtures", "libraries", "slugs.json")));
        return document.RootElement.Clone();
    }
}
