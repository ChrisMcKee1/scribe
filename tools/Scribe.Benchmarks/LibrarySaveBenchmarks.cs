using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;

namespace Scribe.Benchmarks;

/// <summary>
/// A library Save phase by phase (plan 3.14): one custom library of <see cref="Terms"/> terms, all enabled, with every
/// built-in at its default, saved through the real journal over a real temp folder and a settings database on disk, with
/// the service's default parts (the interim adapters in J's branch; the codec, overlay and composer at integration).
/// 1,549 is the number of rows the built-in libraries ship, and 10,000 is the large-vocabulary notice.
/// <list type="bullet">
/// <item><see cref="Stage"/>: <see cref="ILibraryCatalogStore.PrepareSave"/>, which reads the folder, checks every
/// pre-image and writes the redo image and the manifest, both flushed.</item>
/// <item><see cref="Commit"/>: the settings transaction that commits the generation.</item>
/// <item><see cref="Install"/>: <see cref="ILibraryCatalogStore.CompleteSave"/>, which installs the file, retires the
/// manifest and publishes the vocabulary, so it ends when the next dictation can use the Save.</item>
/// <item><see cref="Compose"/>: the committed catalog composed into the vocabulary.</item>
/// <item><see cref="Compile"/>: the post-processor's rules rebuilt from that vocabulary, which the next dictation runs.</item>
/// <item><see cref="Publish"/>: the first read of a new service over the same data, from the files to a published
/// vocabulary: what every start pays before <see cref="ILibraryVocabularySource.Current"/> holds the libraries.</item>
/// <item><see cref="FullSave"/>: stage, commit and install in one invocation.</item>
/// </list>
/// Each Save changes one term, so every file write is real. The iteration setups force one invocation per iteration,
/// which is why each phase runs fifteen iterations; these are costs on this machine and disk, not user-visible latency
/// on their own.
/// </summary>
[MemoryDiagnoser]
[IterationCount(15)]
public class LibrarySaveBenchmarks
{
    private const string LibraryId = "bench-terms";

    private string _root = string.Empty;
    private AppPaths _paths = null!;
    private ScribeDatabase _database = null!;
    private SettingsRepository _settings = null!;
    private DictionaryLibraryService _service = null!;
    private LibraryCatalog _catalog = null!;
    private LibraryChangeSet _changes = null!;
    private PreparedLibrarySave? _prepared;
    private int _revision;

    [Params(1_549, 10_000, 100_000)]
    public int Terms { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "scribe-librarybench-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        _database = new ScribeDatabase(_paths, NullLogger<ScribeDatabase>.Instance);
        _database.Initialize();
        _settings = new SettingsRepository(_database);

        var settings = AppSettings.CreateDefault();
        settings.EnabledDictionaryLibraryIds = [.. settings.EnabledDictionaryLibraryIds, LibraryId];
        _settings.Save(settings);
        File.WriteAllBytes(
            Path.Combine(_paths.LibrariesDir, LibraryId + ".csv"),
            LibraryServiceParts.Default.Codec.WriteManaged(Content(written: 0)));

        _service = NewService();
        _catalog = _service.LoadCatalog();
        if (_catalog.Find(LibraryId) is not { State: LibraryFileState.Available })
        {
            throw new InvalidOperationException("The benchmark library did not load.");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _database.Dispose();

        // Only this file's pool, so the folder can go.
        using (var key = new SqliteConnection(ScribeDatabase.BuildFileConnectionString(_paths.DatabasePath)))
        {
            SqliteConnection.ClearPool(key);
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // Temp folder; best effort.
        }
    }

    [IterationSetup(Targets = [nameof(Stage), nameof(FullSave)])]
    public void NextChangeSet()
    {
        _catalog = _service.LoadCatalog();
        _changes = ChangeSet(_catalog);
    }

    [Benchmark]
    public LibraryPrepareStatus Stage()
    {
        var prepared = _service.PrepareSave(_changes);
        _prepared = prepared.Save;
        return prepared.Status;
    }

    // The staged Save is discarded, uncommitted, so the next iteration stages against the same generation.
    [IterationCleanup(Target = nameof(Stage))]
    public void DiscardStaged() => Complete(expected: LibrarySaveStatus.NotCommitted);

    [IterationSetup(Target = nameof(Commit))]
    public void StageForCommit()
    {
        NextChangeSet();
        Stage();
    }

    [Benchmark]
    public void Commit() => _settings.SaveBundle(_settings.Load(), null, null, default, _prepared!.Payload);

    [IterationCleanup(Target = nameof(Commit))]
    public void InstallCommitted() => Complete(expected: LibrarySaveStatus.Applied);

    [IterationSetup(Target = nameof(Install))]
    public void CommitForInstall()
    {
        StageForCommit();
        Commit();
    }

    [Benchmark]
    public LibrarySaveStatus Install()
    {
        var outcome = _service.CompleteSave(_prepared!);
        _prepared = null;
        return outcome.Status;
    }

    [IterationSetup(Target = nameof(Compose))]
    public void LoadForCompose() => _catalog = _service.LoadCatalog();

    [Benchmark]
    public int Compose() => LibraryServiceParts.Default.Composer.ComposeVocabulary(_catalog).Entries.Count;

    [Benchmark]
    public string Compile()
    {
        var processor = new TextPostProcessor(
            new RepresentativeWorkload.DictionaryStub([]),
            NullLogger<TextPostProcessor>.Instance,
            libraries: new VocabularyEntries(_service.Current.Entries));
        processor.Reload();
        return processor.Process("term 000001");
    }

    [Benchmark]
    public int Publish()
    {
        var service = NewService();
        service.LoadCatalog();
        return service.Current.Entries.Count;
    }

    [Benchmark]
    public LibrarySaveStatus FullSave()
    {
        var prepared = _service.PrepareSave(_changes);
        var save = prepared.Save ?? throw new InvalidOperationException($"The Save was not prepared ({prepared.Status}).");
        try
        {
            _settings.SaveBundle(_settings.Load(), null, null, default, save.Payload);
        }
        finally
        {
            _prepared = save;
        }

        return Install();
    }

    private DictionaryLibraryService NewService() => new(_paths, _settings, NullLogger<DictionaryLibraryService>.Instance);

    private void Complete(LibrarySaveStatus expected)
    {
        if (_prepared is not { } prepared)
        {
            throw new InvalidOperationException("Nothing was staged.");
        }

        _prepared = null;
        var status = _service.CompleteSave(prepared).Status;
        if (status != expected)
        {
            throw new InvalidOperationException($"The Save completed as {status}, not {expected}.");
        }
    }

    // One term's written form changes each Save, so each one writes a new file.
    private LibraryChangeSet ChangeSet(LibraryCatalog catalog)
    {
        var library = catalog.Find(LibraryId) ?? throw new InvalidOperationException("The benchmark library is gone.");
        var write = new LibraryWrite(LibraryId, false, LibraryOrigin.Existing, library.ContentHash, Content(++_revision));
        return new LibraryChangeSet(catalog.Generation, _revision, [write], [], [], catalog.LocalState, localStateChanged: false);
    }

    private LibraryContent Content(int written)
    {
        var rows = new List<LibraryRow>(Terms);
        for (var index = 0; index < Terms; index++)
        {
            var spoken = FormattableString.Invariant($"term {index:D6}");
            var value = index == 0 ? FormattableString.Invariant($"Term{written}") : FormattableString.Invariant($"Term{index:D6}");
            rows.Add(LibraryRow.Custom(new TermValues(spoken, value)));
        }

        return new LibraryContent(LibraryId, false, "Benchmark terms", "Custom", null, rows);
    }

    private sealed class VocabularyEntries(IReadOnlyList<DictionaryEntry> entries) : IDictionaryLibraryService
    {
        public IReadOnlyList<DictionaryLibrary> GetLibraries() => [];

        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries() => entries;

        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries(IReadOnlyCollection<string> enabledIds) => entries;

        public DictionaryLibrary Import(string csv, string? suggestedName) => throw new NotSupportedException();

        public void Remove(string id) => throw new NotSupportedException();
    }
}
