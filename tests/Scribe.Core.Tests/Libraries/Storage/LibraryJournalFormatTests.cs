using System.Text;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// The journal's file formats: every journal name has one grammar and is recognized only by parsing it whole (contract
/// 6.6.1, review finding A19), and a manifest is read strictly, so a tampered one can move nothing outside the folder.
/// </summary>
public sealed class LibraryJournalFormatTests
{
    private const string Id = "3f9c2a7d41b04e6c9d1a5e8b7c6d0f12";

    [Theory]
    [InlineData("g43-" + Id + ".manifest.json", "Manifest", 43, 0, 0)]
    [InlineData("G43-" + Id + ".MANIFEST.JSON", "Manifest", 43, 0, 0)]
    [InlineData("g43-" + Id + ".manifest.json.tmp", "ManifestTemp", 43, 0, 0)]
    [InlineData("g43-" + Id + ".set-aside.20260924T201000Z.json", "SetAsideManifest", 43, 0, 0)]
    [InlineData("g43-" + Id, "RedoFolder", 43, 0, 0)]
    [InlineData("~g43-" + Id + "-0.scribe-staged", "InstallCopy", 43, 0, 0)]
    [InlineData("~g43-" + Id + "-10.scribe-backup", "Backup", 43, 10, 1)]
    [InlineData("~g43-" + Id + "-1-2.scribe-backup", "Backup", 43, 1, 2)]
    [InlineData("~g1-" + Id + "-100-37.scribe-backup", "Backup", 1, 100, 37)]
    public void Every_journal_name_parses_whole_into_its_parts(string name, string kind, long generation, int operation, int index)
    {
        Assert.True(LibraryJournalNames.TryParse(name, out var parsed));
        Assert.Equal(Enum.Parse<LibraryJournalNameKind>(kind), parsed.Kind);
        Assert.Equal(generation, parsed.Generation);
        Assert.Equal(Id, parsed.ManifestId, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(operation, parsed.Operation);
        Assert.Equal(index, parsed.BackupIndex);
    }

    [Theory]
    [InlineData("~g43-" + Id + "-01.scribe-backup")]
    [InlineData("~g043-" + Id + "-1.scribe-backup")]
    [InlineData("~g0-" + Id + "-1.scribe-backup")]
    [InlineData("~g43-3f9c2a7d41b04e6c9d1a5e8b7c6d0f1-1.scribe-backup")]
    [InlineData("~g43-" + Id + "0-1.scribe-backup")]
    [InlineData("~g43-" + Id + "-1-1.scribe-backup")]
    [InlineData("~g43-" + Id + "-1-0.scribe-backup")]
    [InlineData("~g43-" + Id + "-1-02.scribe-backup")]
    [InlineData("~g43-" + Id + "-1.scribe-backup.old")]
    [InlineData("~g43-" + Id + "-1.scribe-backup\n")]
    [InlineData("~g43-" + Id + "-1.scribe-bac\u212Aup")]
    [InlineData("g43-" + Id + ".manifest.json.bak")]
    [InlineData("g43-" + Id + ".set-aside.2026-09-24.json")]
    [InlineData("g43-" + Id + "x")]
    [InlineData("state.witness")]
    [InlineData("orphans")]
    [InlineData("")]
    public void A_name_that_does_not_parse_whole_is_not_a_journal_file(string name)
    {
        Assert.False(LibraryJournalNames.TryParse(name, out _));
    }

    [Fact]
    public void Ownership_compares_the_parsed_operation_as_an_integer_never_as_a_prefix()
    {
        // A19: a glob ~g43-<id>-1*.scribe-backup also matches operations 10, 11 and 100.
        foreach (var other in (int[])[10, 11, 100, 19])
        {
            Assert.True(LibraryJournalNames.TryParse(LibraryJournalNames.Backup(43, Id, other, 1), out var name));
            Assert.False(name.BelongsTo(43, Id, 1));
            Assert.True(name.BelongsTo(43, Id.ToUpperInvariant(), other));
        }

        Assert.False(LibraryJournalNames.TryParse(LibraryJournalNames.Backup(43, Id, 1, 1), out var mine) && mine.BelongsTo(44, Id, 1));
        Assert.Equal("~g43-" + Id + "-1.scribe-backup", LibraryJournalNames.Backup(43, Id, 1, 1));
        Assert.Equal("~g43-" + Id + "-1-2.scribe-backup", LibraryJournalNames.Backup(43, Id, 1, 2));
    }

    [Fact]
    public void Stamps_and_redo_images_parse_exactly()
    {
        Assert.True(LibraryJournalNames.TryParseStamp("20260924T201000Z", out var stamp));
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 20, 10, 0, TimeSpan.Zero), stamp);
        Assert.False(LibraryJournalNames.TryParseStamp("20261324T201000Z", out _));
        Assert.False(LibraryJournalNames.TryParseStamp("20260924T201000", out _));
        Assert.True(LibraryJournalNames.TryParseRedoImage("0.redo", out var zero));
        Assert.Equal(0, zero);
        Assert.True(LibraryJournalNames.TryParseRedoImage("12.redo", out var twelve));
        Assert.Equal(12, twelve);
        Assert.False(LibraryJournalNames.TryParseRedoImage("012.redo", out _));
    }

    [Fact]
    public void A_manifest_round_trips_exactly()
    {
        var hash = new LibraryContentHash(new string('a', 64));
        var manifest = new LibraryManifest(Id, 43, 42, new DateTimeOffset(2026, 9, 24, 20, 10, 0, TimeSpan.Zero),
        [
            new LibraryManifestOperation(0, LibraryOperationKind.Write, "team-terms.csv", "team-terms", hash, hash, KeepAs: "custom-team-terms-changed-outside-scribe.csv"),
            new LibraryManifestOperation(1, LibraryOperationKind.Create, "custom-release-notes.csv", "custom-release-notes", Staged: hash, KeepAs: "custom-release-notes-2.csv"),
            new LibraryManifestOperation(2, LibraryOperationKind.Write, "edits/github.json", "github", hash, hash, Replaced: LibraryEditsReplacement.Previous, SetAsideStem: "edits/github.20260924T201000Z"),
            new LibraryManifestOperation(3, LibraryOperationKind.Remove, "edits/microsoft-azure.json", "microsoft-azure", hash, Replaced: LibraryEditsReplacement.SetAside, SetAsideStem: "edits/microsoft-azure.20260924T201000Z"),
            new LibraryManifestOperation(4, LibraryOperationKind.Restore, "team-notes.csv", "team-notes", Staged: hash, KeepAs: "custom-team-notes-2.csv", From: "deleted/20260901T101010Z.team-notes.csv", FromHash: hash),
            new LibraryManifestOperation(5, LibraryOperationKind.Delete, "old-library.csv", "old-library", hash, To: "deleted/20260924T201000Z.old-library.csv"),
            new LibraryManifestOperation(6, LibraryOperationKind.Purge, "deleted/20260801T090000Z.scratch.csv"),
        ]);

        var read = LibraryManifest.Parse(manifest.ToJson());

        Assert.Equal(LibraryManifestReadStatus.Read, read.Status);
        Assert.Equal(manifest.Operations, read.Manifest!.Operations);
        Assert.Equal(Encoding.UTF8.GetString(manifest.ToJson()), Encoding.UTF8.GetString(read.Manifest.ToJson()));
    }

    [Theory]
    [InlineData("\"target\": \"team-terms.csv\"", "\"target\": \"../team-terms.csv\"")]
    [InlineData("\"target\": \"team-terms.csv\"", "\"target\": \"sub/team-terms.csv\"")]
    [InlineData("\"target\": \"team-terms.csv\"", "\"target\": \"C:\\\\team-terms.csv\"")]
    [InlineData("\"target\": \"team-terms.csv\"", "\"target\": \"\\\\team-terms.csv\"")]
    [InlineData("\"target\": \"team-terms.csv\"", "\"target\": \"team-terms.txt\"")]
    [InlineData("\"target\": \"team-terms.csv\"", "\"target\": \"team-terms.csv.\"")]
    [InlineData("\"target\": \"team-terms.csv\"", "\"target\": \"edits/team-terms.csv\"")]
    [InlineData("\"keepAs\": \"custom-kept.csv\"", "\"keepAs\": \"deleted/custom-kept.csv\"")]
    [InlineData("\"n\": 0", "\"n\": 1")]
    [InlineData("\"op\": \"write\"", "\"op\": \"rewrite\"")]
    [InlineData("\"baseGeneration\": 42", "\"baseGeneration\": 41")]
    public void A_manifest_breaking_the_rules_is_unreadable(string find, string replace)
    {
        var hash = new LibraryContentHash(new string('a', 64));
        var json = Encoding.UTF8.GetString(new LibraryManifest(Id, 43, 42, DateTimeOffset.UnixEpoch,
            [new LibraryManifestOperation(0, LibraryOperationKind.Write, "team-terms.csv", "team-terms", hash, hash, KeepAs: "custom-kept.csv")]).ToJson());
        Assert.Contains(find, json, StringComparison.Ordinal);

        var read = LibraryManifest.Parse(Encoding.UTF8.GetBytes(json.Replace(find, replace, StringComparison.Ordinal)));

        Assert.Equal(LibraryManifestReadStatus.Unreadable, read.Status);
    }

    [Fact]
    public void Two_operations_on_one_file_and_a_later_version_are_refused()
    {
        var hash = new LibraryContentHash(new string('a', 64));
        var twice = new LibraryManifest(Id, 43, 42, DateTimeOffset.UnixEpoch,
        [
            new LibraryManifestOperation(0, LibraryOperationKind.Write, "team-terms.csv", "team-terms", hash, hash, KeepAs: "a.csv"),
            new LibraryManifestOperation(1, LibraryOperationKind.Delete, "Team-Terms.csv", "team-terms", hash, To: "deleted/x.csv"),
        ]);
        Assert.Equal(LibraryManifestReadStatus.Unreadable, LibraryManifest.Parse(twice.ToJson()).Status);

        var newer = Encoding.UTF8.GetString(new LibraryManifest(Id, 43, 42, DateTimeOffset.UnixEpoch, []).ToJson()).Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal);
        Assert.Equal(LibraryManifestReadStatus.Newer, LibraryManifest.Parse(Encoding.UTF8.GetBytes(newer)).Status);
        Assert.Equal(LibraryManifestReadStatus.Unreadable, LibraryManifest.Parse("{ nope"u8).Status);
    }
}
