using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Tests.StorageTime;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// A test double of the built-in overlay (the O sub-stream's): an edits document is compact JSON of its entries, read back
/// exactly, a version above 1 is newer and anything else it cannot read is unreadable; applying one sets an edited or
/// pinned row's values verbatim, turns an off row off and appends added rows. Enough to store, read and pause documents.
/// </summary>
internal sealed class JsonEditsOverlay : IBuiltInLibraryOverlay
{
    public static JsonEditsOverlay Instance { get; } = new();

    public static byte[] Document(string libraryId, params (string Key, string Written)[] edits) =>
        Instance.WriteEdits(Edits(libraryId, edits));

    public static BuiltInLibraryEdits Edits(string libraryId, params (string Key, string Written)[] edits) =>
        new(libraryId, [.. edits.Select(edit => new BuiltInTermEdit(
            LibraryTermKey.From(edit.Key), BuiltInTermIntent.Edited, new TermValues(edit.Key, edit.Key), new TermValues(edit.Key, edit.Written)))]);

    public BuiltInEditsReadResult ReadEdits(string libraryId, ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (JsonNode.Parse(Encoding.UTF8.GetString(bytes)) is not JsonObject root || root["version"] is not JsonValue version)
            {
                return new(LibraryFileState.Unreadable, null, null);
            }

            var number = version.GetValue<int>();
            if (number > 1)
            {
                return new(LibraryFileState.Newer, null, number);
            }

            if (!string.Equals(root["library"]?.GetValue<string>(), libraryId, StringComparison.OrdinalIgnoreCase) ||
                root["terms"] is not JsonArray terms)
            {
                return new(LibraryFileState.Unreadable, null, number);
            }

            var entries = terms.OfType<JsonObject>().Select(term => new BuiltInTermEdit(
                LibraryTermKey.From(term["key"]!.GetValue<string>()),
                Enum.Parse<BuiltInTermIntent>(term["intent"]!.GetValue<string>(), ignoreCase: true),
                term["base"] is JsonObject @base ? Values(@base) : null,
                term["value"] is JsonObject value ? Values(value) : null)).ToList();
            return new(LibraryFileState.Available, new BuiltInLibraryEdits(libraryId, entries), number);
        }
        catch (Exception)
        {
            return new(LibraryFileState.Unreadable, null, null);
        }
    }

    public byte[] WriteEdits(BuiltInLibraryEdits edits)
    {
        var terms = new JsonArray();
        foreach (var term in edits.Terms)
        {
            var node = new JsonObject { ["key"] = term.Key.Value, ["intent"] = term.Intent.ToString().ToLowerInvariant() };
            if (term.Base is { } @base)
            {
                node["base"] = Node(@base);
            }

            if (term.Value is { } value)
            {
                node["value"] = Node(value);
            }

            terms.Add(node);
        }

        return Encoding.UTF8.GetBytes(new JsonObject { ["version"] = 1, ["library"] = edits.LibraryId, ["terms"] = terms }.ToJsonString());
    }

    public IReadOnlyList<LibraryRow> Apply(DictionaryLibrary shipped, BuiltInLibraryEdits? edits)
    {
        var rows = new List<LibraryRow>();
        foreach (var entry in shipped.Entries)
        {
            var values = TermValues.FromEntry(entry);
            var key = LibraryTermKey.From(values.Spoken);
            var edit = edits?.Terms.FirstOrDefault(term => term.Key == key);
            rows.Add(edit switch
            {
                { Intent: BuiltInTermIntent.Edited or BuiltInTermIntent.Pinned, Value: { } value } =>
                    new LibraryRow(key, value, TermOrigin.Edited, values, edit),
                { Intent: BuiltInTermIntent.Off } => new LibraryRow(key, values with { Enabled = false }, TermOrigin.Off, values, edit),
                _ => new LibraryRow(key, values, TermOrigin.Shipped, values),
            });
        }

        foreach (var added in edits?.Terms.Where(term => term.Intent == BuiltInTermIntent.Added && term.Value is not null) ?? [])
        {
            rows.Add(new LibraryRow(added.Key, added.Value!, TermOrigin.Added, Edit: added));
        }

        return rows;
    }

    public LibraryRow Edit(LibraryRow row, TermValues values) => throw new NotSupportedException();

    public LibraryRow SetEnabled(LibraryRow row, bool enabled) => throw new NotSupportedException();

    public LibraryRow? RestoreShipped(LibraryRow row) => throw new NotSupportedException();

    public LibraryRow Add(TermValues values) => throw new NotSupportedException();

    public LibraryRow ResolveReview(LibraryRow row, TermReviewChoice choice) => throw new NotSupportedException();

    public BuiltInLibraryEdits? Collect(DictionaryLibrary shipped, BuiltInLibraryEdits? committed, IReadOnlyList<LibraryRow> rows) => committed;

    public IReadOnlyList<TermValues> AuthoredTerms(BuiltInLibraryEdits edits) =>
        [.. edits.Terms.Where(term => term.Value is not null && term.Intent != BuiltInTermIntent.Off).Select(term => term.Value!)];

    private static JsonObject Node(TermValues values) => new()
    {
        ["spoken"] = values.Spoken, ["written"] = values.Written, ["wholeWord"] = values.WholeWord, ["enabled"] = values.Enabled,
    };

    private static TermValues Values(JsonObject node) => new(
        node["spoken"]!.GetValue<string>(), node["written"]!.GetValue<string>(),
        node["wholeWord"]?.GetValue<bool>() ?? true, node["enabled"]?.GetValue<bool>() ?? true);
}

/// <summary>Records every log call at every level, with its structured values, thread-safely.</summary>
internal sealed class StorageLog<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message, IReadOnlyList<(string Key, string? Value)> State, Exception? Exception)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message, IReadOnlyList<(string Key, string? Value)> State, Exception? Exception)> Entries =>
        [.. _entries];

    /// <summary>Everything a sink could keep: messages, structured values and attached exceptions.</summary>
    public string AllText => string.Join(
        "\n",
        Entries.Select(entry => entry.Message + "\n" + string.Join("\n", entry.State.Select(pair => pair.Key + "=" + pair.Value)) +
                                "\n" + entry.Exception));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var values = state is IReadOnlyList<KeyValuePair<string, object?>> pairs
            ? pairs.Select(pair => (pair.Key, Convert.ToString(pair.Value, CultureInfo.InvariantCulture))).ToList()
            : [];
        _entries.Enqueue((logLevel, formatter(state, exception), values, exception));
    }
}

/// <summary>
/// A data folder with a libraries folder and a settings database for the storage tests, and the services built over it.
/// Every service a test makes shares the folder and the database, as successive processes over one data folder do; the
/// file system, the composer, the overlay and the session context are the test's to choose.
/// </summary>
internal sealed class LibraryStorageFixture : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 9, 24, 20, 10, 0, TimeSpan.Zero);

    private int _manifestIds;

    public LibraryStorageFixture(bool fileDatabase = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "scribe-lib-storage-" + Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Root);
        Directory.CreateDirectory(Paths.LibrariesDir);
        Database = fileDatabase ? new ScribeDatabase(Paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<ScribeDatabase>.Instance) : ScribeDatabase.CreateInMemory();
        Settings = new SettingsRepository(Database);
        Time = new ManualTimeProvider(Start);
    }

    public string Root { get; }

    public AppPaths Paths { get; }

    public ScribeDatabase Database { get; private set; }

    public SettingsRepository Settings { get; private set; }

    public ManualTimeProvider Time { get; }

    public LibraryStateContext Context { get; set; }

    public StorageLog<DictionaryLibraryService> Log { get; } = new();

    public string LibrariesDir => Paths.LibrariesDir;

    /// <summary>A service over the fixture, as a process would build it.</summary>
    public DictionaryLibraryService Service(
        ILibraryFileSystem? files = null, ILibraryComposer? composer = null, IBuiltInLibraryOverlay? overlay = null,
        ILibraryCsvCodec? codec = null, ISettingsRepository? settings = null) =>
        new(Paths, settings ?? Settings, Log, Parts(files, composer, overlay, codec));

    public LibraryServiceParts Parts(
        ILibraryFileSystem? files = null, ILibraryComposer? composer = null, IBuiltInLibraryOverlay? overlay = null, ILibraryCsvCodec? codec = null) =>
        new(codec ?? InterimCsvCodec.Instance, overlay ?? JsonEditsOverlay.Instance, composer ?? ContractComposer.Instance,
            files ?? PhysicalLibraryFileSystem.Instance, Time, () => Context, NextManifestId);

    /// <summary>A scripted, deterministic sequence of manifest ids.</summary>
    public Guid NextManifestId() =>
        new(string.Create(CultureInfo.InvariantCulture, $"00000000-0000-0000-0000-{Interlocked.Increment(ref _manifestIds):D12}"));

    /// <summary>A fresh SettingsRepository over the same database, as the next process would have.</summary>
    public void Restart() => Settings = new SettingsRepository(Database);

    /// <summary>Swaps in another database over the same folder (a repaired or reopened file).</summary>
    public void UseDatabase(ScribeDatabase database)
    {
        Database = database;
        Settings = new SettingsRepository(database);
    }

    public string PathOf(string relative) => LibraryManifest.ToAbsolute(LibrariesDir, relative);

    public void Write(string relative, string text) => WriteBytes(relative, Encoding.UTF8.GetBytes(text));

    public void WriteBytes(string relative, byte[] bytes)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public string Read(string relative) => File.ReadAllText(PathOf(relative));

    public bool Exists(string relative) => File.Exists(PathOf(relative));

    public static LibraryContentHash Hash(string text) => LibraryContentHashing.Of(Encoding.UTF8.GetBytes(text));

    public LibraryContentHash HashOf(string relative) => LibraryContentHashing.Of(File.ReadAllBytes(PathOf(relative)));

    /// <summary>A managed library CSV in today's form.</summary>
    public static string Csv(string name, params (string Spoken, string Written)[] rows)
    {
        var text = new StringBuilder("# name: ").Append(name).Append("\n# category: Custom\npattern,replacement,whole_word,enabled\n");
        foreach (var (spoken, written) in rows)
        {
            text.Append(spoken).Append(',').Append(written).Append(",true,true\n");
        }

        return text.ToString();
    }

    public static LibraryContent Content(string id, string name, params (string Spoken, string Written)[] rows) =>
        new(id, false, name, "Custom", null, [.. rows.Select(row => LibraryRow.Custom(new TermValues(row.Spoken, row.Written)))]);

    /// <summary>The bytes the interim codec writes for content, which is what the journal stages.</summary>
    public static byte[] Managed(LibraryContent content) => InterimCsvCodec.Instance.WriteManaged(content);

    /// <summary>Every file under the libraries folder, relative, for "nothing else changed" assertions.</summary>
    public IReadOnlyList<string> AllFiles() =>
        [.. Directory.EnumerateFiles(LibrariesDir, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(LibrariesDir, path).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>A settings document with an enabled list, saved the way Settings saves it.</summary>
    public void SaveEnabled(params string[] ids)
    {
        var settings = AppSettings.CreateDefault();
        settings.EnabledDictionaryLibraryIds = [.. ids];
        Settings.Save(settings);
    }

    public IReadOnlyList<string> StoredEnabledList() => Settings.Load().EnabledDictionaryLibraryIds;

    public string? Row(string key) => Settings.Get(key);

    public long StoredGeneration => SettingsRepository.ParseLibraryGeneration(Settings.Get(LibrarySettingKeys.Generation));

    public void Dispose()
    {
        Database.Dispose();
        DatabasePools.Release(Paths);
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp folder is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Change sets and catalogs built directly, as the workspace and the loader build them.</summary>
internal static class Changes
{
    public static LibraryChangeSet Of(
        LibraryCatalog catalog,
        IEnumerable<LibraryWrite>? writes = null,
        IEnumerable<LibraryDeletion>? deletions = null,
        IEnumerable<RecentlyDeletedAction>? recentlyDeleted = null,
        LibraryLocalState? state = null,
        long revision = 1) =>
        new(catalog.Generation, revision, [.. writes ?? []], [.. deletions ?? []], [.. recentlyDeleted ?? []],
            state ?? catalog.LocalState, state is not null);

    /// <summary>The catalog's state with some libraries turned on or off, and some AI choices made.</summary>
    public static LibraryLocalState With(
        LibraryLocalState state, IEnumerable<string>? enable = null, IEnumerable<string>? disable = null,
        IEnumerable<(string Id, bool Permitted)>? ai = null)
    {
        var enabled = new HashSet<string>(state.EnabledIds, StringComparer.OrdinalIgnoreCase);
        enabled.UnionWith(enable ?? []);
        enabled.ExceptWith(disable ?? []);
        var permissions = state.AiPermissions.Concat((ai ?? []).Select(pair => new KeyValuePair<string, bool>(pair.Id, pair.Permitted)));
        return LibraryLocalState.Create(
            enabled, state.LegacyEnabledIds, permissions, state.LegacyMarkers, state.AiUpgradeNotice, state.Health,
            state.AcceptedContent, state.AiPermissionsLost);
    }

    public static LibraryWrite Edit(LibraryCatalog catalog, string id, LibraryContent content) =>
        new(id, false, LibraryOrigin.Existing, catalog.Find(id)!.ContentHash, content);

    public static LibraryWrite Create(LibraryContent content) => new(content.Id, false, LibraryOrigin.Created, null, content);

    public static LibraryDeletion Delete(LibraryCatalog catalog, string id) =>
        new(id, catalog.Find(id)!.FileName!, catalog.Find(id)!.ContentHash!.Value);

    /// <summary>The shell's Save sequence: prepare, commit through SaveBundle, and complete from a finally.</summary>
    public static (LibraryPrepareResult Prepared, LibrarySaveOutcome? Outcome, Exception? CommitFailure) Save(
        DictionaryLibraryService service, ISettingsRepository settings, LibraryChangeSet changes, Action? beforeCommit = null)
    {
        var prepared = service.PrepareSave(changes);
        if (prepared.Save is not { } save)
        {
            return (prepared, null, null);
        }

        Exception? failure = null;
        LibrarySaveOutcome? outcome = null;
        try
        {
            beforeCommit?.Invoke();
            settings.SaveBundle(settings.Load(), null, null, default, save.Payload);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            outcome = service.CompleteSave(save);
        }

        return (prepared, outcome, failure);
    }
}
