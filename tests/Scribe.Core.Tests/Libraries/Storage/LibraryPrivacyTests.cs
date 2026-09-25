using System.IO.Compression;
using System.Text;
using Scribe.Core.Diagnostics;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// J-13 (plan 3.15): a library whose name, id, category, description, terms and file name are canaries goes through
/// import, edit, save, fault, recovery, quarantine and delete with every log line captured, and no canary reaches the log;
/// and the diagnostics bundle holds nothing from the libraries folder, whatever it contains.
/// </summary>
public sealed class LibraryPrivacyTests : IDisposable
{
    private const string Name = "Zorblax Quintessence Canary";
    private const string Category = "Wibblecategory Canary";
    private const string Description = "Frotzdescription canary text";
    private const string Spoken = "glimmerspoken canary";
    private const string Written = "GlimmerWritten Canary";
    private const string Slug = "zorblax-quintessence-canary";
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void No_canary_of_a_library_reaches_the_log_through_import_edit_save_fault_recovery_quarantine_and_delete()
    {
        var csv = $"# name: {Name}\n# category: {Category}\n# description: {Description}\npattern,replacement\n{Spoken},{Written}\n";

        // Import, through the journal.
        var service = _fixture.Service();
        var imported = service.Import(csv, "fallback");
        Assert.Equal(Slug, imported.Id);

        // Edit and save, with an outside version racing the install, then a crash inside the next completion.
        var catalog = service.LoadCatalog();
        var edited = new LibraryContent(Slug, false, Name, Category, Description, [LibraryRow.Custom(new TermValues(Spoken, Written + " edited"))]);
        Changes.Save(service, _fixture.Settings, Changes.Of(catalog, writes: [Changes.Edit(catalog, Slug, edited)], state: Changes.With(catalog.LocalState, enable: [Slug], ai: [(Slug, true)])),
            beforeCommit: () => _fixture.Write(Slug + ".csv", csv.Replace(Written, Written + " outside", StringComparison.Ordinal)));
        catalog = service.LoadCatalog();
        var crashing = new FaultingFileSystem { FailAt = 12, FailTiming = FaultingFileSystem.Timing.After };
        var twice = new LibraryContent(Slug, false, Name, Category, Description, [LibraryRow.Custom(new TermValues(Spoken, Written + " twice"))]);
        try
        {
            Changes.Save(_fixture.Service(crashing), _fixture.Settings, Changes.Of(catalog, writes: [Changes.Edit(catalog, Slug, twice)]));
        }
        catch (Exception)
        {
            // Died mid-completion.
        }

        // Recovery, a lost generation and the quarantine, its expiry, a lock, and the delete.
        _fixture.Restart();
        service = _fixture.Service();
        service.LoadCatalog();
        using (var connection = _fixture.Database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM settings WHERE key = 'libraries.generation';";
            command.ExecuteNonQuery();
        }

        var lost = service.LoadCatalog();
        var prepared = service.PrepareSave(Changes.Of(lost, writes: [Changes.Edit(lost, Slug, edited)]));
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);
        using (new FileStream(_fixture.PathOf(Slug + ".csv"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            service.CompleteSave(prepared.Save);
            service.LoadCatalog();
        }

        service.Recover();
        service.Remove(Slug);
        service.Janitor.Run(LibraryStorageFixture.Start.AddDays(60));

        var log = _fixture.Log.AllText;
        Assert.NotEmpty(_fixture.Log.Entries);
        foreach (var canary in (string[])[Name, Category, Description, Spoken, Written, Slug, "zorblax", "glimmer", "wibble", "frotz", _fixture.LibrariesDir, _fixture.Root])
        {
            Assert.DoesNotContain(canary, log, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(_fixture.Log.Entries, entry => entry.Message.StartsWith("Library file operation ", StringComparison.Ordinal));
        Assert.All(_fixture.Log.Entries, entry => Assert.Null(entry.Exception));
    }

    [Fact]
    public void The_diagnostics_bundle_holds_nothing_from_the_libraries_folder()
    {
        var today = new DateOnly(2026, 9, 24);
        Directory.CreateDirectory(_fixture.Paths.LogsDir);
        File.WriteAllText(Path.Combine(_fixture.Paths.LogsDir, "scribe-20260924.log"), "2026-09-24 a real log line\n");
        const string Canary = "LibraryFolderCanary";
        foreach (var relative in (string[])
                 ["team.csv", "scribe-20260924.log", "edits/github.json", "edits/scribe-20260924.log", "deleted/20260924T201000Z.team.csv",
                  "journal/state.witness", "journal/g1-0123456789abcdef0123456789abcdef.set-aside.20260924T201000Z.json",
                  "journal/g1-0123456789abcdef0123456789abcdef/0.redo", "journal/orphans/scribe-20260924.log", "journal/scribe-20260924.log",
                  "~g1-0123456789abcdef0123456789abcdef-0.scribe-backup"])
        {
            _fixture.Write(relative, Canary + " " + relative);
        }

        var zip = Path.Combine(_fixture.Root, "bundle.zip");
        DiagnosticsBundle.Create(_fixture.Paths.LogsDir, zip, "report", today);

        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(["logs/scribe-20260924.log", "report.txt"], archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            Assert.DoesNotContain(Canary, reader.ReadToEnd(), StringComparison.Ordinal);
        }
    }
}
