using System.Globalization;
using Microsoft.Extensions.Logging;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Pins the logging privacy contract for post-processing: a snippet trigger phrase, a dictionary
/// pattern or replacement, and a custom library's name (or the id and file name derived from it)
/// are the user's own words and must never reach the log, in the formatted message, the structured
/// state that structured sinks keep, or an attached exception. Every warning here reports shapes
/// instead: ids, lengths, counts and exception type names. The distinctive tokens below exist so a
/// leak cannot hide behind a common word.
/// </summary>
public sealed class PostProcessingLogPrivacyTests : IDisposable
{
    private const string Phrase = "zorblax quintessence protocol";
    private const string Template = "wibblefrotz template body";
    private const string LibraryName = "Zorblax Quintessence Protocol";
    private const string LibraryCsv =
        "# name: " + LibraryName + "\npattern,replacement\nwibble frotz,WibbleFrotz\n";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "scribe-log-privacy-" + Guid.NewGuid().ToString("N"));

    private readonly ScribeDatabase _db = ScribeDatabase.CreateInMemory();

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: a leftover temp dir is harmless.
        }
    }

    [Fact]
    public void A_snippet_the_matcher_rejects_is_logged_by_id_and_length_but_never_by_its_phrase()
    {
        var log = new CapturingLogger<TextPostProcessor>();

        // A regex error message quotes the pattern it rejected, so the exception must stay out too.
        var failure = new ArgumentException($"Invalid pattern '{Phrase}' at offset 3.");
        TextPostProcessor.LogSkippedSnippet(log, new Snippet(17, Phrase, Template), failure);

        var warning = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Null(warning.Exception);
        Assert.Contains(("Id", "17"), warning.State);
        Assert.Contains(("PhraseLength", Length(Phrase)), warning.State);
        Assert.Contains(("ExceptionType", nameof(ArgumentException)), warning.State);
        AssertNeverLogged(log, Phrase, "zorblax", "quintessence", Template, "wibblefrotz");
    }

    [Fact]
    public void A_dictionary_entry_the_matcher_rejects_is_logged_by_id_and_length_but_never_by_its_text()
    {
        const string pattern = "zorblax quintessence";
        var log = new CapturingLogger<TextPostProcessor>();

        // A null replacement cannot come from a saved row, but it makes the rule constructor throw,
        // which drives the real catch in the rule build end to end.
        var processor = new TextPostProcessor(
            new DictionaryStub([new DictionaryEntry(42, pattern, null!), DictionaryEntry.New("azure", "Azure")]),
            log);
        processor.Reload();

        var warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Null(warning.Exception);
        Assert.Contains(("Id", "42"), warning.State);
        Assert.Contains(("PatternLength", Length(pattern)), warning.State);
        Assert.Contains(warning.State, pair => pair.Key == "ExceptionType" && !string.IsNullOrEmpty(pair.Value));
        AssertNeverLogged(log, pattern, "zorblax", "quintessence");

        // The rejected entry is skipped; the rest of the dictionary still applies.
        Assert.Equal("Azure", processor.Process("azure"));
    }

    [Fact]
    public void Importing_a_custom_library_never_logs_its_name_the_id_derived_from_it_or_its_entries()
    {
        var (service, log, _) = CreateLibraryService();

        var library = service.Import(LibraryCsv, "fallback name");

        Assert.Equal("zorblax-quintessence-protocol", library.Id);
        var info = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Information);
        Assert.Contains(("Count", "1"), info.State);
        Assert.Contains(("NameLength", Length(LibraryName)), info.State);
        AssertNeverLogged(log, LibraryName, "zorblax", "quintessence", library.Id, "wibble", "frotz");
    }

    [Fact]
    public void Removing_a_custom_library_never_logs_its_id()
    {
        var (service, log, _) = CreateLibraryService();
        var library = service.Import(LibraryCsv, "fallback name");
        log.Entries.Clear();

        service.Remove(library.Id);

        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Information);
        AssertNeverLogged(log, library.Id, "zorblax", "quintessence");
    }

    [Fact]
    public void An_unreadable_custom_library_is_logged_without_its_path_or_the_name_in_it()
    {
        var (service, log, paths) = CreateLibraryService();
        var library = service.Import(LibraryCsv, "fallback name");
        log.Entries.Clear();

        // Holding the file open without sharing makes the read fail with a sharing violation,
        // whose message would repeat the full path, library slug included.
        using (new FileStream(
            Path.Combine(paths.LibrariesDir, library.Id + ".csv"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.DoesNotContain(service.GetLibraries(), candidate => candidate.Id == library.Id);
        }

        var warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Null(warning.Exception);
        Assert.Contains(("ExceptionType", nameof(IOException)), warning.State);
        AssertNeverLogged(log, library.Id, "zorblax", "quintessence", paths.LibrariesDir, _root);
    }

    private (DictionaryLibraryService Service, CapturingLogger<DictionaryLibraryService> Log, AppPaths Paths)
        CreateLibraryService()
    {
        var paths = new AppPaths(_root);
        var log = new CapturingLogger<DictionaryLibraryService>();
        return (new DictionaryLibraryService(paths, new SettingsRepository(_db), log), log, paths);
    }

    private static string Length(string value) => value.Length.ToString(CultureInfo.InvariantCulture);

    private static void AssertNeverLogged<T>(CapturingLogger<T> log, params string[] secrets)
    {
        Assert.NotEmpty(log.Entries);
        foreach (var entry in log.Entries)
        {
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, entry.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(secret, entry.Exception?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                foreach (var (_, value) in entry.State)
                {
                    Assert.DoesNotContain(secret, value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    private sealed record CapturedEntry(
        LogLevel Level,
        string Message,
        IReadOnlyList<(string Key, string? Value)> State,
        Exception? Exception);

    /// <summary>
    /// Keeps every entry at every level, Debug included: the formatted message, and the structured
    /// key/value state a structured sink would keep even when the template does not print a value.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<CapturedEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IReadOnlyList<KeyValuePair<string, object?>> pairs
                ? pairs.Select(pair => (pair.Key, Convert.ToString(pair.Value, CultureInfo.InvariantCulture))).ToList()
                : [];
            Entries.Add(new CapturedEntry(logLevel, formatter(state, exception), values, exception));
        }
    }

    private sealed class DictionaryStub(IReadOnlyList<DictionaryEntry> entries) : IDictionaryRepository
    {
        public IReadOnlyList<DictionaryEntry> GetAll() => entries;
        public IReadOnlyList<DictionaryEntry> GetEnabled() => entries;
        public DictionaryEntry Add(DictionaryEntry entry) => throw new NotSupportedException();
        public IReadOnlyList<DictionaryEntry> AddRange(IReadOnlyList<DictionaryEntry> added) =>
            throw new NotSupportedException();
        public void Update(DictionaryEntry entry) => throw new NotSupportedException();
        public void Delete(long id) => throw new NotSupportedException();
        public void SaveAll(IReadOnlyList<DictionaryEntry> updated) => throw new NotSupportedException();
        public int SeedIfEmpty(IEnumerable<DictionaryEntry> seed) => throw new NotSupportedException();
        public int DisableUnmodifiedEntries(IEnumerable<DictionaryEntry> retired) => throw new NotSupportedException();
    }
}
