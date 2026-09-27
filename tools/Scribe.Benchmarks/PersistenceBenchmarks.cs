using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Benchmarks;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = false, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class BenchmarkSettingsJsonContext : JsonSerializerContext;

public abstract class SettingsJsonBenchmarkBase
{
    protected static readonly JsonSerializerOptions ReflectionOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    protected AppSettings Settings = null!;
    protected string Json = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        Settings = AppSettings.CreateDefault();
        Settings.EnableAiCleanup = true;
        Settings.AiCleanupWritingStyle = "Use concise punctuation and keep technical terms intact.";
        Settings.Profiles =
        [
            new AppProfile
            {
                Name = "Notes",
                ProcessNames = ["notepad", "winword"],
                WritingStyle = "Plain notes",
                NewlineHandling = NewlineInjectionMode.SmartFlatten,
            },
        ];
        Json = JsonSerializer.Serialize(Settings, ReflectionOptions);
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("Persistence")]
public class SettingsJsonSerializeBenchmarks : SettingsJsonBenchmarkBase
{
    [Benchmark(Baseline = true)]
    public string ReflectionSerialize() => JsonSerializer.Serialize(Settings, ReflectionOptions);

    [Benchmark]
    public string SourceGeneratedSerialize() => JsonSerializer.Serialize(Settings, BenchmarkSettingsJsonContext.Default.AppSettings);
}

[MemoryDiagnoser]
[BenchmarkCategory("Persistence")]
public class SettingsJsonDeserializeBenchmarks : SettingsJsonBenchmarkBase
{
    [Benchmark(Baseline = true)]
    public AppSettings? ReflectionDeserialize() => JsonSerializer.Deserialize<AppSettings>(Json, ReflectionOptions);

    [Benchmark]
    public AppSettings? SourceGeneratedDeserialize() => JsonSerializer.Deserialize(Json, BenchmarkSettingsJsonContext.Default.AppSettings);
}

[MemoryDiagnoser]
[BenchmarkCategory("Persistence")]
public class HistoryReadBenchmarks
{
    private readonly List<string> _roots = [];
    private ScribeDatabase _database = null!;
    private HistoryRepository _repository = null!;
    private string _root = string.Empty;

    [Params(5_000, 50_000)]
    public int Entries { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "BenchmarkData", "Persistence", Guid.NewGuid().ToString("N"));
        _roots.Add(_root);
        Directory.CreateDirectory(_root);
        _database = new ScribeDatabase(new AppPaths(_root), NullLogger<ScribeDatabase>.Instance);
        _repository = new HistoryRepository(_database);
        SeedHistory(_database, Entries);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _database.Dispose();
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort cleanup. Benchmark artifacts are under the benchmark output tree.
            }
        }
    }

    [Benchmark]
    public IReadOnlyList<HistoryEntry> GetRecent50() => _repository.GetRecent(50);

    [Benchmark]
    public IReadOnlyList<HistoryEntry> SearchText50() => _repository.Search("needle", 50);

    [Benchmark]
    public IReadOnlyList<HistoryEntry> SearchKnownApp50() => _repository.Search("Word", 50);

    private static void SeedHistory(ScribeDatabase database, int count)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO history (timestamp_utc, text, audio_ms, decode_ms, target_app)
            VALUES ($ts, $text, $audio, $decode, $target);
            """;
        var ts = command.Parameters.Add("$ts", SqliteType.Text);
        var text = command.Parameters.Add("$text", SqliteType.Text);
        var audio = command.Parameters.Add("$audio", SqliteType.Integer);
        var decode = command.Parameters.Add("$decode", SqliteType.Integer);
        var target = command.Parameters.Add("$target", SqliteType.Text);

        var start = DateTimeOffset.Parse("2026-09-26T12:00:00Z", CultureInfo.InvariantCulture);
        for (var index = 0; index < count; index++)
        {
            ts.Value = start.AddSeconds(index).ToString("O", CultureInfo.InvariantCulture);
            text.Value = index % 100 == 0 ? "needle benchmark row" : "ordinary benchmark row";
            audio.Value = 1000 + index % 500;
            decode.Value = 40 + index % 20;
            target.Value = index % 10 == 0 ? "WINWORD.EXE" : "notepad";
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
