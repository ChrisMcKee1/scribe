using System.Text;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// J-24 (amended after O's review): well-formed UTF-16. A change set holding an unpaired surrogate anywhere is refused
/// with <see cref="ArgumentException"/> before any file is touched, the witness included; and a <c>*.csv</c> whose file
/// name is not well-formed (NTFS allows one) is never a library, so its name never becomes an id, a manifest path or a
/// state entry, and the load line counts it.
/// </summary>
public sealed class LibraryWellFormedTextTests : IDisposable
{
    private const string LoneHigh = "\uD800";
    private const string LoneLow = "\uDC00";
    private const string Emoji = "\uD83D\uDE00";
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    public static TheoryData<string> Places => ["spoken", "written", "name", "category", "description", "based-on", "edits-key", "edits-value", "state-id", "marker"];

    [Theory]
    [MemberData(nameof(Places))]
    public void A_change_set_holding_an_unpaired_surrogate_is_refused_before_anything_is_written(string place)
    {
        var service = _fixture.Service();

        Assert.Throws<ArgumentException>(() => service.PrepareSave(ChangeSet(place, LoneHigh)));
        Assert.Throws<ArgumentException>(() => service.PrepareSave(ChangeSet(place, LoneLow)));

        Assert.Empty(_fixture.AllFiles());
        Assert.False(_fixture.Exists("journal/state.witness"));
        Assert.Null(_fixture.Row(Scribe.Core.Libraries.LibrarySettingKeys.Generation));
    }

    [Theory]
    [MemberData(nameof(Places))]
    public void A_surrogate_pair_is_ordinary_text(string place)
    {
        // The control: the same change sets with a character outside the Basic Multilingual Plane prepare normally.
        var service = _fixture.Service();

        var prepared = service.PrepareSave(ChangeSet(place, Emoji));

        Assert.Equal(LibraryPrepareStatus.Prepared, prepared.Status);
        Assert.True(_fixture.Exists("journal/state.witness"));
        service.CompleteSave(prepared.Save!);
    }

    [Fact]
    public void A_csv_whose_name_is_not_well_formed_is_never_a_library_an_id_a_manifest_path_or_a_state_entry()
    {
        var malformed = "team" + LoneHigh + ".csv";
        var bytes = Encoding.UTF8.GetBytes(LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        File.WriteAllBytes(Path.Combine(_fixture.LibrariesDir, malformed), bytes);
        _fixture.Write("other.csv", LibraryStorageFixture.Csv("Other", ("o", "O")));
        _fixture.SaveEnabled("other");
        var service = _fixture.Service();

        var catalog = service.LoadCatalog();

        Assert.DoesNotContain(catalog.Libraries, library => !LibraryText.IsWellFormed(library.FileName) || !LibraryText.IsWellFormed(library.Content.Id));
        Assert.Equal(["other"], catalog.Libraries.Where(library => !library.Content.BuiltIn).Select(library => library.Content.Id));
        Assert.DoesNotContain(service.GetLibraries(), library => library.Name == "Team" || !LibraryText.IsWellFormed(library.Id));
        Assert.DoesNotContain(service.Current.Entries, entry => entry.Replacement == "Kubernetes");
        Assert.Contains(_fixture.Log.Entries, entry =>
            entry.Message.StartsWith("Loaded ", StringComparison.Ordinal) && entry.State.Contains(("Skipped", "1")));

        // A Save's manifest names no path of it, and neither committed row holds it.
        var prepared = service.PrepareSave(Changes.Of(
            catalog,
            writes: [Changes.Edit(catalog, "other", LibraryStorageFixture.Content("other", "Other", ("o", "O2")))],
            state: Changes.With(catalog.LocalState, enable: ["other"], ai: [("other", true)])));
        Assert.Equal(LibraryPrepareStatus.Prepared, prepared.Status);
        var manifests = Directory.EnumerateFiles(Path.Combine(_fixture.LibrariesDir, "journal"), "*.manifest.json").Select(File.ReadAllText).ToList();
        Assert.Single(manifests);
        Assert.DoesNotContain("team", manifests[0], StringComparison.OrdinalIgnoreCase);
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);
        Assert.Equal(LibrarySaveStatus.Applied, service.CompleteSave(prepared.Save).Status);

        foreach (var key in (string[])[LibrarySettingKeys.State, LibrarySettingKeys.FileIds])
        {
            Assert.DoesNotContain("team", _fixture.Row(key) ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        Assert.All(service.LoadCatalog().LocalState.EnabledIds, id => Assert.True(LibraryText.IsWellFormed(id)));

        // The file itself is nobody's: never read into a library, renamed, rewritten or deleted.
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_fixture.LibrariesDir, malformed)));
    }

    [Fact]
    public void A_Recently_deleted_entry_whose_name_is_not_well_formed_is_not_listed_and_never_expired()
    {
        var malformed = "20260801T000000Z.team" + LoneLow + ".csv";
        var path = Path.Combine(_fixture.LibrariesDir, "deleted", malformed);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        File.WriteAllBytes(path, bytes);
        _fixture.SaveEnabled();
        var service = _fixture.Service();

        Assert.Empty(service.LoadCatalog().RecentlyDeleted);
        Assert.Contains(_fixture.Log.Entries, entry =>
            entry.Message.StartsWith("Loaded ", StringComparison.Ordinal) && entry.State.Contains(("Skipped", "1")));

        var result = service.Janitor.Run(LibraryStorageFixture.Start.AddDays(90));

        Assert.Equal(0, result.RecentlyDeletedRemoved);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Import_does_not_use_a_suggested_name_that_is_not_well_formed()
    {
        _fixture.SaveEnabled();
        var service = _fixture.Service();

        var imported = service.Import("pattern,replacement\nkube,Kubernetes\n", "team" + LoneHigh);

        Assert.Equal("Imported library", imported.Name);
        Assert.All(_fixture.AllFiles(), relative => Assert.True(LibraryText.IsWellFormed(relative)));
        Assert.Contains(service.LoadCatalog().Libraries, library => library.Content.Name == "Imported library");
    }

    [Fact]
    public void Import_of_text_no_decoder_produces_is_refused_without_writing_a_library()
    {
        _fixture.SaveEnabled();
        var service = _fixture.Service();
        service.LoadCatalog();
        var before = _fixture.AllFiles();

        Assert.Throws<ArgumentException>(() => service.Import("# name: Team" + LoneHigh + "\npattern,replacement\nkube,Kubernetes\n", null));
        Assert.Throws<ArgumentException>(() => service.Import("pattern,replacement\nkube" + LoneLow + ",Kubernetes\n", null));

        Assert.Equal(before, _fixture.AllFiles());
    }

    [Fact]
    public void Well_formed_means_every_surrogate_is_paired()
    {
        // Built in code: an attribute argument is stored as UTF-8, which would turn a lone surrogate into U+FFFD.
        (string Value, bool WellFormed)[] cases =
        [
            ("", true), ("plain", true), (Emoji, true), ("a" + Emoji + "b" + Emoji, true), ("\uFFFD", true),
            (LoneHigh, false), (LoneLow, false), ("a" + LoneHigh + "b", false), ("a" + LoneLow + "\uD83D", false),
            ("\uDE00\uD83D", false), (Emoji + LoneHigh, false), (LoneHigh + LoneHigh + LoneLow, false),
        ];

        foreach (var (value, wellFormed) in cases)
        {
            Assert.True(wellFormed == LibraryText.IsWellFormed(value), string.Join(" ", value.Select(c => ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture))));
        }
    }

    // A change set of one Save with the text in one place: a new custom library, a built-in's edits document, or the state.
    private static LibraryChangeSet ChangeSet(string place, string text)
    {
        string At(string where, string plain) => where == place ? plain + text : plain;

        var state = LibraryLocalState.Create(
            [At("state-id", "team"), "github"], null, [new KeyValuePair<string, bool>("team", true)],
            place == "marker" ? [new LegacyMarker("team", LibraryTermKey.From("kube" + text))] : [], null, LocalStateHealth.Ok);
        var writes = new List<LibraryWrite>();
        if (place is "edits-key" or "edits-value")
        {
            var key = At("edits-key", "git hub");
            writes.Add(new LibraryWrite(
                "github", true, LibraryOrigin.Existing, null,
                Edits: new BuiltInLibraryEdits("github", [new BuiltInTermEdit(
                    LibraryTermKey.From(key), BuiltInTermIntent.Edited, new TermValues(key, "GitHub"), new TermValues(key, At("edits-value", "GitHub")))])));
        }
        else
        {
            var content = new LibraryContent(
                "team", false, At("name", "Team"), At("category", "Custom"), At("description", "About"),
                [LibraryRow.Custom(new TermValues(At("spoken", "kube"), At("written", "Kubernetes")))],
                BasedOn: place == "based-on" ? "github" + text : null);
            writes.Add(new LibraryWrite("team", false, LibraryOrigin.Created, null, content));
        }

        return new LibraryChangeSet(0, 1, writes, [], [], state, localStateChanged: true);
    }
}
