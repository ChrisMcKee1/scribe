using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// Adoption and the witness (contract 6.9, review finding A3): adoption once, as a generation of its own (J-10); a lost
/// state's durable denial across starts (J-10b); the witness written before every commit and at a start that detects a
/// loss (J-10c); and the invariant that no library row exists without a witness made durable first, from the writers'
/// side and through a real repair (J-10d).
/// </summary>
public sealed class LibraryWitnessTests : IDisposable
{
    private const string Witness = "journal/state.witness";
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private void SeedTeam()
    {
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.SaveEnabled("team");
    }

    private bool TeamPermitted(LibraryCatalog catalog) =>
        ContractComposer.IsPermitted(catalog.LocalState, "team", false, catalog.Find("team")!.ContentHash);

    private bool AnyLibraryRow() =>
        new[] { LibrarySettingKeys.Generation, LibrarySettingKeys.State, LibrarySettingKeys.FileIds }.Any(key => _fixture.Row(key) is not null);

    // --- J-10 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_first_start_is_adopted_once_as_a_generation_of_its_own()
    {
        SeedTeam();
        var service = _fixture.Service();

        var first = service.LoadCatalog();

        Assert.Equal(1, first.Generation);
        Assert.True(TeamPermitted(first));
        Assert.True(_fixture.Exists(Witness));
        Assert.Equal(1, service.LoadCatalog().Generation);
        _fixture.Restart();
        Assert.Equal(1, _fixture.Service().LoadCatalog().Generation);
    }

    [Fact]
    public void Nothing_is_adopted_or_witnessed_in_a_session_on_defaults()
    {
        SeedTeam();
        _fixture.Context = new LibraryStateContext(RunningOnDefaults: true, DatabaseRepaired: false, GenerationStored: false);

        var catalog = _fixture.Service().LoadCatalog();

        Assert.Equal(0, catalog.Generation);
        Assert.False(AnyLibraryRow());
        Assert.False(_fixture.Exists(Witness));
    }

    [Fact]
    public void A_failed_first_adoption_is_used_in_memory_without_widening_and_the_witness_makes_the_next_start_deny()
    {
        SeedTeam();
        var failing = new CommitFails(_fixture.Settings);

        var service = _fixture.Service(settings: failing);
        var catalog = service.LoadCatalog();

        Assert.Equal(1, failing.Attempts);
        Assert.False(AnyLibraryRow());
        Assert.True(_fixture.Exists(Witness));
        Assert.True(TeamPermitted(catalog));
        Assert.DoesNotContain("team", service.Current.AiScope.PermittedLibraryIds);

        // The accepted cost: the next start reads the missing row as lost and denies until the user confirms.
        _fixture.Restart();
        var next = _fixture.Service().LoadCatalog();
        Assert.True(next.LocalState.AiPermissionsLost);
        Assert.False(TeamPermitted(next));
        Assert.True(ContractComposer.ParseRow(_fixture.Row(LibrarySettingKeys.State)!)!.Lost);
    }

    [Fact]
    public void A_repaired_database_adopts_nothing_but_the_denial()
    {
        SeedTeam();
        _fixture.Context = new LibraryStateContext(RunningOnDefaults: false, DatabaseRepaired: true, GenerationStored: false);

        var catalog = _fixture.Service().LoadCatalog();

        Assert.Equal(1, catalog.Generation);
        Assert.True(catalog.LocalState.AiPermissionsLost);
        Assert.Empty(catalog.LocalState.AiPermissions);
        Assert.False(TeamPermitted(catalog));
        Assert.True(_fixture.Exists(Witness));
    }

    // --- J-10b ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_repair_that_loses_both_rows_denies_across_starts_and_unrelated_saves_until_the_user_confirms(bool firstCommitFails)
    {
        SeedTeam();
        Assert.True(TeamPermitted(_fixture.Service().LoadCatalog()));
        DeleteLibraryRows();

        // The start of the repair.
        _fixture.Context = new LibraryStateContext(RunningOnDefaults: false, DatabaseRepaired: true, GenerationStored: false);
        _fixture.Restart();
        var repairStart = _fixture.Service(settings: firstCommitFails ? new CommitFails(_fixture.Settings) : null).LoadCatalog();
        Assert.False(TeamPermitted(repairStart));
        Assert.Equal(firstCommitFails, !AnyLibraryRow());

        // The next start has no repair flag: the witness (or the committed denial) still denies.
        _fixture.Context = default;
        _fixture.Restart();
        var service = _fixture.Service();
        var second = service.LoadCatalog();
        Assert.True(second.LocalState.AiPermissionsLost);
        Assert.False(TeamPermitted(second));
        Assert.DoesNotContain("team", service.Current.AiScope.PermittedLibraryIds);

        // An unrelated Save keeps the denial.
        Changes.Save(service, _fixture.Settings, Changes.Of(second, writes: [Changes.Create(LibraryStorageFixture.Content("custom-other", "Other", ("o", "O")))]));
        var afterSave = service.LoadCatalog();
        Assert.True(afterSave.LocalState.AiPermissionsLost);
        Assert.False(TeamPermitted(afterSave));

        // The workspace's ConfirmAiPermissions records a choice for every library and clears the flag; its Save stands.
        var state = afterSave.LocalState;
        var confirmed = LibraryLocalState.Create(
            state.EnabledIds, state.LegacyEnabledIds, afterSave.Libraries.Select(library => new KeyValuePair<string, bool>(library.Content.Id, library.Content.Id == "team")),
            state.LegacyMarkers, state.AiUpgradeNotice, state.Health, state.AcceptedContent, aiPermissionsLost: false);
        Changes.Save(service, _fixture.Settings, Changes.Of(afterSave, state: confirmed));
        var cleared = service.LoadCatalog();
        Assert.False(cleared.LocalState.AiPermissionsLost);
        Assert.True(TeamPermitted(cleared));
        Assert.Contains("team", service.Current.AiScope.PermittedLibraryIds);
    }

    // --- J-10c ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_repair_start_writes_the_witness_before_its_denial_commit_so_a_failed_commit_still_denies_next_time()
    {
        SeedTeam();
        var witnessAtCommit = (bool?)null;
        var failing = new CommitFails(_fixture.Settings) { OnCommit = () => witnessAtCommit = _fixture.Exists(Witness) };
        _fixture.Context = new LibraryStateContext(RunningOnDefaults: false, DatabaseRepaired: true, GenerationStored: false);

        _fixture.Service(settings: failing).LoadCatalog();

        Assert.True(witnessAtCommit);
        Assert.False(AnyLibraryRow());

        _fixture.Context = default;
        _fixture.Restart();
        var next = _fixture.Service().LoadCatalog();
        Assert.Equal(LibraryAdoptionReasons.StateLost, LastAdoptionReasons());
        Assert.False(TeamPermitted(next));
    }

    [Fact]
    public void A_first_start_writes_the_witness_before_its_first_commit()
    {
        SeedTeam();
        var seen = new List<(string Step, bool Witness)>();
        _fixture.Settings.WriteStep = (step, _, _) => seen.Add((step, _fixture.Exists(Witness)));

        _fixture.Service().LoadCatalog();

        Assert.Contains(("library state committing", true), seen);
        Assert.DoesNotContain(seen, pair => !pair.Witness);
    }

    [Fact]
    public void A_crash_between_the_witness_and_the_first_commit_denies_next_time_and_one_after_it_reads_the_committed_state()
    {
        // Before the commit: the accepted cost.
        SeedTeam();
        _fixture.Service(settings: new CommitFails(_fixture.Settings)).LoadCatalog();
        _fixture.Restart();
        Assert.False(TeamPermitted(_fixture.Service().LoadCatalog()));

        // Immediately after the first database commit: the committed state stands.
        using var other = new LibraryStorageFixture();
        other.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        other.SaveEnabled("team");
        other.Settings.WriteStep = (step, _, _) =>
        {
            if (step == "library state committed")
            {
                throw FaultingFileSystem.Injected();
            }
        };
        other.Service().LoadCatalog();
        other.Restart();
        var next = other.Service().LoadCatalog();
        Assert.False(next.LocalState.AiPermissionsLost);
        Assert.True(ContractComposer.IsPermitted(next.LocalState, "team", false, next.Find("team")!.ContentHash));
    }

    // --- J-10d ----------------------------------------------------------------------------------------------------------

    public enum Writer
    {
        FirstAdoption,
        Discovered,
        ContentReplaced,
        StateLost,
        Save,
        SaveEndingDefaults,
        Import,
        Remove,
    }

    [Theory]
    [InlineData(Writer.FirstAdoption)]
    [InlineData(Writer.Discovered)]
    [InlineData(Writer.ContentReplaced)]
    [InlineData(Writer.StateLost)]
    [InlineData(Writer.Save)]
    [InlineData(Writer.SaveEndingDefaults)]
    [InlineData(Writer.Import)]
    [InlineData(Writer.Remove)]
    public void Every_writer_of_a_library_row_has_the_witness_on_disk_before_its_transaction_starts(Writer writer)
    {
        Arrange(writer);
        var steps = new List<(string Step, bool Witness)>();
        _fixture.Settings.WriteStep = (step, _, _) => steps.Add((step, _fixture.Exists(Witness)));

        Run(writer, _fixture.Service());

        Assert.Contains(steps, pair => pair.Step is "library state committing" or "save committing" or "library rows written");
        Assert.All(steps, pair => Assert.True(pair.Witness, $"{pair.Step} ran without the witness on disk"));
    }

    [Theory]
    [InlineData(Writer.FirstAdoption)]
    [InlineData(Writer.Discovered)]
    [InlineData(Writer.ContentReplaced)]
    [InlineData(Writer.StateLost)]
    [InlineData(Writer.Save)]
    [InlineData(Writer.SaveEndingDefaults)]
    [InlineData(Writer.Import)]
    [InlineData(Writer.Remove)]
    public void A_writer_whose_witness_cannot_be_written_writes_no_library_row(Writer writer)
    {
        Arrange(writer);
        var rows = (_fixture.Row(LibrarySettingKeys.Generation), _fixture.Row(LibrarySettingKeys.State), _fixture.Row(LibrarySettingKeys.FileIds));
        var files = new FaultingFileSystem
        {
            MutationFault = (kind, path, _) => kind == "write" && path.EndsWith("state.witness", StringComparison.Ordinal) ? FaultingFileSystem.AccessDenied() : null,
        };

        try
        {
            Run(writer, _fixture.Service(files));
        }
        catch (InvalidOperationException)
        {
            // A wrapper refuses; a Save reports Failed; an adoption stays in memory.
        }

        Assert.Equal(rows, (_fixture.Row(LibrarySettingKeys.Generation), _fixture.Row(LibrarySettingKeys.State), _fixture.Row(LibrarySettingKeys.FileIds)));
    }

    [Fact]
    public void A_repair_that_keeps_the_document_and_loses_the_library_rows_leads_the_next_start_to_the_denial_never_a_first_start()
    {
        // J-10d (2), Astra's sequence through the real repair.
        using var fixture = new LibraryStorageFixture(fileDatabase: true);
        fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        fixture.SaveEnabled("team");
        Assert.True(ContractComposer.IsPermitted(fixture.Service().LoadCatalog().LocalState, "team", false, fixture.HashOf("team.csv")));
        Assert.True(fixture.Exists(Witness));
        var history = new HistoryRepository(fixture.Database);
        for (var i = 0; i < 50; i++)
        {
            history.Add(new HistoryEntry(0, LibraryStorageFixture.Start.AddMinutes(i), new string('x', 4000), 1000, 50));
        }

        // The pages holding the library rows are lost; damage at the tail makes the next open repair the file.
        using (var connection = fixture.Database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM settings WHERE key LIKE 'libraries.%';";
            command.ExecuteNonQuery();
        }

        fixture.Database.Dispose();
        DatabasePools.Release(fixture.Paths);
        CorruptTail(fixture.Paths.DatabasePath, pages: 4);

        using (var repaired = new ScribeDatabase(fixture.Paths, NullLogger<ScribeDatabase>.Instance))
        {
            repaired.Initialize();
            Assert.True(repaired.RepairedAtStartup);
            Assert.False(repaired.SettingsLostInRepair);
            var settings = new SettingsRepository(repaired);
            Assert.NotNull(settings.Get(SettingsRepository.SettingsKey));
            Assert.Null(settings.Get(SettingsRepository.LostMarkerKey));
            Assert.Null(settings.Get(LibrarySettingKeys.State));

            // The process ends here, before the library service runs.
        }

        DatabasePools.Release(fixture.Paths);
        var next = new ScribeDatabase(fixture.Paths, NullLogger<ScribeDatabase>.Instance);
        next.Initialize();
        Assert.False(next.RepairedAtStartup);
        fixture.UseDatabase(next);

        var mark = fixture.Log.Entries.Count;
        var catalog = fixture.Service().LoadCatalog();
        var entries = fixture.Log.Entries.Skip(mark).ToList();

        Assert.Equal(LocalStateHealth.Ok, catalog.LocalState.Health);
        Assert.True(catalog.LocalState.AiPermissionsLost);
        Assert.False(ContractComposer.IsPermitted(catalog.LocalState, "team", false, catalog.Find("team")!.ContentHash));
        Assert.Contains(entries, entry => entry.Message.Contains("(StateLost;", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.Message.Contains("FirstStart", StringComparison.Ordinal));
    }

    [Fact]
    public void No_witness_and_no_row_is_a_first_start_and_a_row_without_a_witness_gets_one_at_once_on_defaults_too()
    {
        // J-10d (3): 0.4.3's upgrade reads as a first start; a witness deleted by hand comes back at the next start.
        SeedTeam();
        _fixture.Service().LoadCatalog();
        Assert.Equal(LibraryAdoptionReasons.FirstStart, LastAdoptionReasons());

        File.Delete(_fixture.PathOf(Witness));
        _fixture.Context = new LibraryStateContext(RunningOnDefaults: true, DatabaseRepaired: false, GenerationStored: false);
        _fixture.Restart();
        _fixture.Service().LoadCatalog();
        Assert.True(_fixture.Exists(Witness));
        Assert.Contains(_fixture.Log.Entries, entry => entry.Message.StartsWith("Library state witness restored beside 3", StringComparison.Ordinal) ||
                                                        entry.Message.StartsWith("Library state witness restored beside 2", StringComparison.Ordinal));

        // A later repair that loses the rows then reads as lost.
        DeleteLibraryRows();
        _fixture.Context = default;
        _fixture.Restart();
        var afterLoss = _fixture.Service().LoadCatalog();
        Assert.True(afterLoss.LocalState.AiPermissionsLost);
    }

    // --- helpers ----------------------------------------------------------------------------------------------------------

    private void Arrange(Writer writer)
    {
        SeedTeam();
        switch (writer)
        {
            case Writer.FirstAdoption or Writer.StateLost when writer == Writer.StateLost:
                _fixture.Context = new LibraryStateContext(RunningOnDefaults: false, DatabaseRepaired: true, GenerationStored: false);
                break;
            case Writer.FirstAdoption:
                break;
            case Writer.SaveEndingDefaults:
                _fixture.Context = new LibraryStateContext(RunningOnDefaults: true, DatabaseRepaired: false, GenerationStored: false);
                break;
            default:
                // Every other writer starts from a committed state; the witness is then removed by hand, so what each
                // writer finds on disk at its commit is what it wrote itself (or restored) first.
                _fixture.Service().LoadCatalog();
                if (writer == Writer.Discovered)
                {
                    _fixture.Write("found.csv", LibraryStorageFixture.Csv("Found", ("f", "F")));
                }
                else if (writer == Writer.ContentReplaced)
                {
                    _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Replaced outside Scribe")));
                }

                File.Delete(_fixture.PathOf(Witness));
                _fixture.Restart();
                break;
        }
    }

    private void Run(Writer writer, Scribe.Core.PostProcessing.DictionaryLibraryService service)
    {
        switch (writer)
        {
            case Writer.Save or Writer.SaveEndingDefaults:
            {
                var catalog = service.LoadCatalog();
                Changes.Save(service, _fixture.Settings, Changes.Of(catalog, writes: [Changes.Create(LibraryStorageFixture.Content("custom-new", "New", ("n", "N")))]));
                break;
            }

            case Writer.Import:
                service.Import(LibraryStorageFixture.Csv("Imported", ("i", "I")), "imported");
                break;
            case Writer.Remove:
                service.Remove("team");
                break;
            default:
                service.LoadCatalog();
                break;
        }
    }

    private LibraryAdoptionReasons LastAdoptionReasons()
    {
        var entry = _fixture.Log.Entries.Last(candidate => candidate.Message.StartsWith("Adopted ", StringComparison.Ordinal));
        return Enum.Parse<LibraryAdoptionReasons>(entry.State.Single(pair => pair.Key == "Reasons").Value!);
    }

    private void DeleteLibraryRows()
    {
        using var connection = _fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM settings WHERE key LIKE 'libraries.%';";
        command.ExecuteNonQuery();
    }

    // Overwrites the last pages with garbage, leaving the header, the schema and the early pages that hold the settings
    // rows (the shape SettingsLostInRepairTests damages a file in).
    private static void CorruptTail(string path, int pages)
    {
        const int PageSize = 4096;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var garbage = new byte[PageSize * pages];
        new Random(42).NextBytes(garbage);
        stream.Seek(-garbage.Length, SeekOrigin.End);
        stream.Write(garbage);
    }

    /// <summary>The real repository, except that its library state commit fails before writing anything.</summary>
    private sealed class CommitFails(SettingsRepository inner) : ISettingsRepository
    {
        public int Attempts { get; private set; }

        public Action? OnCommit { get; init; }

        public bool LastLoadFailed => inner.LastLoadFailed;

        public AppSettings Load() => inner.Load();

        public void Save(AppSettings settings) => inner.Save(settings);

        public AppSettings Update(Action<AppSettings> mutate) => inner.Update(mutate);

        public AppSettings Update(Action<AppSettings> mutate, long revision, out bool superseded) => inner.Update(mutate, revision, out superseded);

        public void SaveBundle(AppSettings settings, IReadOnlyList<DictionaryEntry>? dictionaryEntries, IReadOnlyList<Snippet>? snippets, long aiCleanupIntent = 0) =>
            inner.SaveBundle(settings, dictionaryEntries, snippets, aiCleanupIntent);

        public void SaveBundle(AppSettings settings, IReadOnlyList<DictionaryEntry>? dictionaryEntries, IReadOnlyList<Snippet>? snippets, ExternalIntents intents, LibrarySavePayload? libraries) =>
            inner.SaveBundle(settings, dictionaryEntries, snippets, intents, libraries);

        public void CommitLibraryState(LibrarySavePayload payload)
        {
            Attempts++;
            OnCommit?.Invoke();
            throw new IOException("the library state commit failed");
        }

        public string? Get(string key) => inner.Get(key);

        public void Set(string key, string value) => inner.Set(key, value);
    }
}
