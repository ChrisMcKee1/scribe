using System.Text;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// Round 2, A9: JSON that System.Text.Json parses but will not hand over (an escaped unpaired surrogate, which
/// <c>GetString</c> refuses with <see cref="InvalidOperationException"/>; a property name twice, which
/// <c>JsonObject</c> refuses with <see cref="ArgumentException"/>) is malformed like any other: a manifest holding it is
/// unreadable and set aside, and a file-id map holding it is unreadable and recomputed whole, never used half parsed.
/// Neither ever blocks a load.
/// </summary>
public sealed class LibraryMalformedJsonTests : IDisposable
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    public static TheoryData<string, string> ManifestDefects => new()
    {
        { "\"target\": \"team.csv\"", "\"target\": \"\\uD800team.csv\"" },
        { "\"library\": \"team\"", "\"library\": \"te\\uDC00am\"" },
        { "\"keepAs\": \"custom-kept.csv\"", "\"keepAs\": \"custom-kept\\uD800.csv\"" },
        { "\"version\": 1,", "\"version\": 1, \"version\": 1," },
    };

    [Theory]
    [MemberData(nameof(ManifestDefects))]
    public void A_manifest_json_will_not_hand_over_is_unreadable_and_set_aside_and_the_catalog_loads(string find, string replace)
    {
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.SaveEnabled("team");
        Assert.Equal(1, _fixture.Service().LoadCatalog().Generation);
        var hash = new LibraryContentHash(new string('a', 64));
        var json = Encoding.UTF8.GetString(new LibraryManifest(Id, 1, 0, DateTimeOffset.UnixEpoch,
            [new LibraryManifestOperation(0, LibraryOperationKind.Write, "team.csv", "team", hash, hash, KeepAs: "custom-kept.csv")]).ToJson());
        Assert.Contains(find, json, StringComparison.Ordinal);
        _fixture.Write("journal/" + LibraryJournalNames.Manifest(1, Id), json.Replace(find, replace, StringComparison.Ordinal));

        var catalog = _fixture.Service().LoadCatalog();

        Assert.Equal("Kubernetes", catalog.Find("team")!.Content.Rows[0].Values.Written);
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.manifest.json"));
        Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.set-aside.*.json"));
    }

    public static TheoryData<string> FileIdDefects =>
    [
        "{\"version\":1,\"files\":{\"github.csv\":\"custom-kept\",\"data-and-ai.csv\":\"a\",\"data-and-ai.csv\":\"b\"}}",
        "{\"version\":1,\"version\":1,\"files\":{\"github.csv\":\"custom-kept\"}}",
        "{\"version\":1,\"files\":{\"github.csv\":\"custom-kept\",\"data-and-ai.csv\":\"custom-\\uD800\"}}",
        "{\"version\":1,\"files\":{\"github.csv\":\"custom-kept\",\"\\uDC00.csv\":\"custom-x\"}}",
    ];

    [Theory]
    [MemberData(nameof(FileIdDefects))]
    public void A_file_id_map_json_will_not_hand_over_is_recomputed_whole_and_never_used_half_parsed(string row)
    {
        _fixture.Write("github.csv", LibraryStorageFixture.Csv("Twin", ("private codename", "Twin")));
        _fixture.Write("data-and-ai.csv", LibraryStorageFixture.Csv("Data", ("lake", "Lake")));
        _fixture.SaveEnabled("github");
        _fixture.Settings.Set(LibrarySettingKeys.FileIds, row);

        var catalog = _fixture.Service().LoadCatalog();

        Assert.Equal("github.csv", catalog.Find("custom-github")!.FileName);
        Assert.Equal("data-and-ai.csv", catalog.Find("custom-data-and-ai")!.FileName);
        Assert.Null(catalog.Find("custom-kept"));
    }

    [Fact]
    public void The_file_id_map_parser_answers_unreadable_and_empty_for_json_it_cannot_hand_over()
    {
        foreach (var row in FileIdDefects)
        {
            var parsed = LibraryFileIds.Parse(row);
            Assert.Equal(LibraryFileIdsHealth.Unreadable, parsed.Health);
            Assert.Empty(parsed.Files);
        }
    }

    [Fact]
    public void A_settings_document_json_will_not_hand_over_reads_as_unreadable_and_the_catalog_still_loads()
    {
        // The same boundary for the settings document the library read uses: an escaped unpaired surrogate in one of its
        // strings is an unreadable document (the session on defaults), never a load that throws.
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.SaveEnabled("team");
        var stored = _fixture.Row("app_settings")!;
        Assert.Contains("\"enabledDictionaryLibraryIds\"", stored, StringComparison.Ordinal);
        _fixture.Settings.Set("app_settings", stored.Replace("\"team\"", "\"te\\uD800am\"", StringComparison.Ordinal));
        _fixture.Restart();

        _fixture.Settings.Load();
        var catalog = _fixture.Service().LoadCatalog();

        Assert.True(_fixture.Settings.LastLoadFailed);
        Assert.NotNull(catalog.Find("team"));
    }

    [Fact]
    public void The_manifest_parser_answers_unreadable_for_json_it_cannot_hand_over()
    {
        var hash = new LibraryContentHash(new string('a', 64));
        var json = Encoding.UTF8.GetString(new LibraryManifest(Id, 1, 0, DateTimeOffset.UnixEpoch,
            [new LibraryManifestOperation(0, LibraryOperationKind.Write, "team.csv", "team", hash, hash, KeepAs: "custom-kept.csv")]).ToJson());
        foreach (var defect in ManifestDefects)
        {
            var read = LibraryManifest.Parse(Encoding.UTF8.GetBytes(json.Replace((string)defect[0], (string)defect[1], StringComparison.Ordinal)));
            Assert.Equal(LibraryManifestReadStatus.Unreadable, read.Status);
        }
    }
}
