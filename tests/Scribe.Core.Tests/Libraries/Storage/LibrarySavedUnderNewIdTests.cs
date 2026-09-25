using System.Text;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// Round 3, A14: when another app took a create's or a restore's target, Scribe's committed bytes go to the first free
/// candidate of the keepAs series (cases C3 and S5), and that move counts only once the candidate is observed holding S.
/// A write landing on the candidate as the move returns belongs to the other app: it stays where it landed, S goes to the
/// next free candidate, the kept version reported is the one that holds S, and a restore consumes its Recently deleted
/// entry only after that, so the redo image is never retired while the reported file holds someone else's bytes.
/// </summary>
public sealed class LibrarySavedUnderNewIdTests : IDisposable
{
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // Right after the journal moves an install copy to anywhere but the target (the first keepAs candidate), another app
    // writes over that candidate, once.
    private static FaultingFileSystem WritingOverTheFirstCandidate(string target, byte[] foreign, List<string> written) => new()
    {
        AfterMutation = (kind, source, destination) =>
        {
            if (written.Count == 0 && kind == "move" && destination is not null &&
                source.EndsWith(".scribe-staged", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(destination, target, StringComparison.OrdinalIgnoreCase))
            {
                written.Add(destination);
                File.WriteAllBytes(destination, foreign);
            }
        },
    };

    [Fact]
    public void A_create_whose_first_new_name_is_written_over_as_its_bytes_arrive_keeps_them_under_the_next_one()
    {
        var created = LibraryStorageFixture.Content("custom-release-notes", "Release notes", ("sprint", "Sprint"));
        var target = _fixture.PathOf("custom-release-notes.csv");
        var taken = Bytes(LibraryStorageFixture.Csv("Other app", ("other", "Other")));
        var landed = Bytes(LibraryStorageFixture.Csv("Other app again", ("again", "Again")));
        var written = new List<string>();
        var service = _fixture.Service(WritingOverTheFirstCandidate(target, landed, written));
        var start = service.LoadCatalog();

        var saved = Changes.Save(
            service, _fixture.Settings,
            Changes.Of(start, writes: [Changes.Create(created)], state: Changes.With(start.LocalState, enable: ["custom-release-notes"])),
            beforeCommit: () => File.WriteAllBytes(target, taken));

        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        var candidate = Assert.Single(written);
        Assert.Equal(taken, File.ReadAllBytes(target));
        Assert.Equal(landed, File.ReadAllBytes(candidate));
        var kept = Assert.Single(saved.Outcome.KeptVersions);
        Assert.Equal(LibraryKeptVersionKind.SavedUnderNewId, kept.Kind);
        Assert.NotEqual(Path.GetFileNameWithoutExtension(candidate), kept.KeptAsId, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(LibraryStorageFixture.Managed(created), File.ReadAllBytes(_fixture.PathOf(kept.KeptAsId + ".csv")));
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*" + LibraryJournalNames.ManifestSuffix));
    }

    [Fact]
    public void A_restore_whose_first_new_name_is_written_over_as_its_bytes_arrive_keeps_them_and_only_then_consumes_the_entry()
    {
        const string Entry = "20260901T101010Z.scratch.csv";
        _fixture.Write("deleted/" + Entry, LibraryStorageFixture.Csv("Scratch", ("scratch", "Scratch")));
        var target = _fixture.PathOf("scratch.csv");
        var taken = Bytes(LibraryStorageFixture.Csv("Other app", ("other", "Other")));
        var landed = Bytes(LibraryStorageFixture.Csv("Other app again", ("again", "Again")));
        var written = new List<string>();
        var service = _fixture.Service(WritingOverTheFirstCandidate(target, landed, written));
        var catalog = service.LoadCatalog();
        var entry = Assert.Single(catalog.RecentlyDeleted);

        var saved = Changes.Save(
            service, _fixture.Settings,
            Changes.Of(catalog, recentlyDeleted: [new RecentlyDeletedAction(RecentlyDeletedActionKind.Restore, entry.EntryName, entry.ContentHash, "scratch")]),
            beforeCommit: () => File.WriteAllBytes(target, taken));

        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        var candidate = Assert.Single(written);
        Assert.Equal(taken, File.ReadAllBytes(target));
        Assert.Equal(landed, File.ReadAllBytes(candidate));
        var kept = Assert.Single(saved.Outcome.KeptVersions);
        Assert.Equal(LibraryKeptVersionKind.SavedUnderNewId, kept.Kind);
        Assert.NotEqual(Path.GetFileNameWithoutExtension(candidate), kept.KeptAsId, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(entry.ContentHash, _fixture.HashOf(kept.KeptAsId + ".csv"));
        Assert.False(File.Exists(_fixture.PathOf("deleted/" + Entry)));
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*" + LibraryJournalNames.ManifestSuffix));
    }
}
