using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class DictionaryLibraryServiceTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly ScribeDatabase _db;
    private readonly SettingsRepository _settings;
    private readonly DictionaryLibraryService _service;

    public DictionaryLibraryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "scribe-lib-tests-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _db = ScribeDatabase.CreateInMemory();
        _settings = new SettingsRepository(_db);
        _service = new DictionaryLibraryService(_paths, _settings, NullLogger<DictionaryLibraryService>.Instance);
    }

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
        catch
        {
            // Best effort: a leftover temp dir is harmless.
        }
    }

    [Fact]
    public void GetLibraries_includes_the_built_in_set()
    {
        var libraries = _service.GetLibraries();

        Assert.Contains(libraries, l => l.Id == "microsoft-azure" && l.BuiltIn);
        Assert.Contains(libraries, l => l.Id == "dotnet-development" && l.BuiltIn);
        Assert.Contains(libraries, l => l.Id == "data-engineering" && l.BuiltIn);
        Assert.Contains(libraries, l => l.Id == "data-science-machine-learning" && l.BuiltIn);
        Assert.True(libraries.Count >= 9);
    }

    [Fact]
    public void Import_writes_a_custom_library_that_lists_and_reads_back()
    {
        var csv = "# name: My Terms\n# category: Custom\npattern,replacement\nfoo bar,FooBar\n";

        var imported = _service.Import(csv, "fallback-name");

        Assert.False(imported.BuiltIn);
        Assert.Equal("My Terms", imported.Name);
        Assert.Single(imported.Entries);

        var listed = _service.GetLibraries().Single(l => l.Id == imported.Id);
        Assert.False(listed.BuiltIn);
        Assert.Equal("My Terms", listed.Name);
        Assert.True(File.Exists(Path.Combine(_paths.LibrariesDir, imported.Id + ".csv")));
    }

    [Fact]
    public void Import_uses_the_suggested_name_when_the_csv_has_no_header()
    {
        var imported = _service.Import("foo,Foo\n", "My File");
        Assert.Equal("My File", imported.Name);
    }

    [Fact]
    public void Import_rejects_a_csv_with_no_entries()
    {
        Assert.Throws<InvalidOperationException>(() => _service.Import("# name: Empty\n", "empty"));
    }

    [Fact]
    public void Import_generates_an_id_that_never_collides_with_a_built_in()
    {
        var imported = _service.Import("# name: Microsoft Azure\npattern,replacement\nx,Y\n", null);
        Assert.NotEqual("microsoft-azure", imported.Id);
    }

    [Fact]
    public void Remove_deletes_a_custom_library_but_refuses_a_built_in()
    {
        var imported = _service.Import("# name: Temp\npattern,replacement\na,B\n", null);

        _service.Remove(imported.Id);
        Assert.DoesNotContain(_service.GetLibraries(), l => l.Id == imported.Id);

        Assert.Throws<InvalidOperationException>(() => _service.Remove("microsoft-azure"));
    }

    [Fact]
    public void GetEnabledLibraryEntries_composes_only_enabled_libraries()
    {
        Assert.Empty(_service.GetEnabledLibraryEntries()); // nothing enabled by default

        var settings = AppSettings.CreateDefault();
        settings.EnabledDictionaryLibraryIds.Add("microsoft-azure");
        _settings.Save(settings);

        var entries = _service.GetEnabledLibraryEntries();
        Assert.Contains(entries, e => e.Replacement == "APIM");
    }

    [Fact]
    public void Enabled_builtin_library_substitutes_through_the_real_post_processor()
    {
        // Enable the shipped Microsoft Azure library.
        var settings = AppSettings.CreateDefault();
        settings.EnabledDictionaryLibraryIds.Add("microsoft-azure");
        _settings.Save(settings);

        // A real post-processor wired to the real library service over an empty base dictionary, so
        // this exercises the actual shipped library data end to end (compose + compiled regex rules),
        // not a stub. Confirms an enabled library canonicalizes terms in transcriber-style output.
        var dictionary = new DictionaryRepository(_db);
        var processor = new TextPostProcessor(
            dictionary, NullLogger<TextPostProcessor>.Instance, snippets: null, libraries: _service);

        var result = processor.Process("we deployed a p i m and cosmos db to azure");

        Assert.Equal("we deployed APIM and Cosmos DB to Azure", result);
    }

    [Fact]
    public void Enabled_github_library_fixes_common_mishears_through_the_post_processor()
    {
        var settings = AppSettings.CreateDefault();
        settings.EnabledDictionaryLibraryIds.Add("github");
        _settings.Save(settings);

        var dictionary = new DictionaryRepository(_db);
        var processor = new TextPostProcessor(
            dictionary, NullLogger<TextPostProcessor>.Instance, snippets: null, libraries: _service);

        // "get hub" is how a transcriber often renders spoken "GitHub"; the library canonicalizes it,
        // and "github copilot" becomes the correctly cased product name.
        var result = processor.Process("i pushed to get hub and used github copilot");

        Assert.Equal("i pushed to GitHub and used GitHub Copilot", result);
    }

    [Fact]
    public void Enabled_modern_developer_stack_canonicalizes_products_without_rewriting_ordinary_prose()
    {
        var settings = AppSettings.CreateDefault();
        settings.EnabledDictionaryLibraryIds.Add("modern-developer-stack");
        _settings.Save(settings);

        var dictionary = new DictionaryRepository(_db);
        var processor = new TextPostProcessor(
            dictionary, NullLogger<TextPostProcessor>.Instance, snippets: null, libraries: _service);

        var technical = processor.Process(
            "deploy supa base behind cloud flare on vercel with next js and tailwind css using cursor ide and warp terminal");
        var ordinary = processor.Process(
            "react quickly and go home because rust covered the swift car near a bun; the postman made it prettier " +
            "while the playwright moved the cursor before the warp drive");

        Assert.Equal(
            "deploy Supabase behind Cloudflare on Vercel with Next.js and Tailwind CSS using Cursor IDE and Warp terminal",
            technical);
        Assert.Equal(
            "react quickly and go home because rust covered the swift car near a bun; the postman made it prettier " +
            "while the playwright moved the cursor before the warp drive",
            ordinary);
    }

    [Theory]
    [InlineData(
        "dotnet-development",
        "build asp net core with entity framework core and x unit on win ui 3",
        "build ASP.NET Core with Entity Framework Core and xUnit on WinUI 3")]
    [InlineData(
        "data-engineering",
        "run dbt core in fabric data factory and write apache iceberg from py spark",
        "run dbt Core in Fabric Data Factory and write Apache Iceberg from PySpark")]
    [InlineData(
        "data-science-machine-learning",
        "train xg boost with scikit learn and track it in weights and biases using f one score",
        "train XGBoost with scikit-learn and track it in Weights & Biases using F1 score")]
    public void Enabled_specialized_library_canonicalizes_representative_terms(
        string libraryId, string dictated, string expected)
    {
        var settings = AppSettings.CreateDefault();
        settings.EnabledDictionaryLibraryIds.Add(libraryId);
        _settings.Save(settings);

        var dictionary = new DictionaryRepository(_db);
        var processor = new TextPostProcessor(
            dictionary, NullLogger<TextPostProcessor>.Instance, snippets: null, libraries: _service);

        Assert.Equal(expected, processor.Process(dictated));
    }
}
