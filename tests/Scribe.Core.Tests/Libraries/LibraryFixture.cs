using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// Custom libraries, a personal dictionary and enabled sets that exercise every way libraries compete for a spoken
/// form: a custom library against a shipped one, two custom libraries against each other (including the "-2" file a
/// second import of the same library gets), a row turned off in the library that would otherwise win, and names whose
/// alphabetical order is the opposite of their precedence. Real services over a temporary folder: the built-ins are
/// the shipped ones, the custom libraries are files the loader reads, and the dictionary is a real repository.
/// </summary>
internal sealed class LibraryFixture : IDisposable
{
    /// <summary>
    /// Custom library files. Their file names order them for precedence (the loader has always read them in file-name
    /// order, where "team-terms-2.csv" comes before "team-terms.csv" because '-' sorts before '.'), and their display
    /// names deliberately disagree with that order: "alpha.csv" is named "Zeta words", "Zulu Notes.csv" is named
    /// "alpha notes", and "release-10.csv" precedes "release-9.csv" by file name while "Release 9 terms" sorts before
    /// "Release 10 terms" on screen. The row turned off in "team-terms-2.csv" sits in the library that would otherwise
    /// supply "pipeline".
    /// </summary>
    public static readonly IReadOnlyList<(string FileName, string Csv)> CustomFiles =
    [
        ("team-terms.csv",
            "# name: Team terms\n# category: Custom\npattern,replacement,whole_word,enabled\n" +
            "get hub,GitHub Enterprise,true,true\nkube,Kubernetes,true,true\nnorth star,North Star,true,true\n" +
            "contoso,Contoso Ltd,true,true\npipeline,Pipelines,true,true\n"),
        ("team-terms-2.csv",
            "# name: Team terms v2\n# category: Custom\npattern,replacement,whole_word,enabled\n" +
            "kube,K8s,true,true\nnorth star,NorthStar,true,true\npipeline,Pipeline,true,false\n"),
        ("alpha.csv",
            "# name: Zeta words\npattern,replacement\ncontoso,CONTOSO\nfabrikam,Fabrikam\n"),
        ("Zulu Notes.csv",
            "# name: alpha notes\npattern,replacement\nfabrikam,FabriKam\ntailspin,Tailspin Toys\n"),
        ("release-10.csv",
            "# name: Release 10 terms\npattern,replacement\nsprint,Sprint 10\nretro,Retro\n"),
        ("release-9.csv",
            "# name: Release 9 terms\npattern,replacement\nsprint,Sprint 9\nstandup,Stand-up\ngpt five six terra,GPT 5.6 Terra\n"),
    ];

    /// <summary>
    /// The personal dictionary: entries a library already writes the same way, entries it writes differently, one the
    /// user turned off, one no library covers, and one a turned-off library row shares. The spoken forms it leaves out
    /// ("get hub", "kube", "north star", "tailspin", "sprint", "retro", "gpt five six terra") are decided by the
    /// libraries alone.
    /// </summary>
    public static readonly IReadOnlyList<DictionaryEntry> Personal =
    [
        DictionaryEntry.New("fabrikam", "Fabrikam"),
        DictionaryEntry.New("pipeline", "Pipelines"),
        DictionaryEntry.New("contoso", "Contoso"),
        DictionaryEntry.New("azure", "Azure") with { Enabled = false },
        DictionaryEntry.New("llm", "LLM"),
        DictionaryEntry.New("standup", "standup"),
        DictionaryEntry.New("scribe", "Scribe"),
    ];

    public static readonly IReadOnlyList<string> Sentences =
    [
        "i pushed the kube fix to get hub before the sprint retro",
        "the standup covered contoso and fabrikam near north star",
        "tailspin wants the pipeline on gpt five six terra with an llm",
        "scribe typed azure for me",
    ];

    /// <summary>
    /// The enabled sets, each listed in a scrambled order: the stored order of enabled ids has never mattered, and the
    /// golden outputs prove it still does not.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string[] EnabledIds)> Scenarios =
    [
        ("shipped and custom",
            ["release-9", "Zulu Notes", "github", "team-terms-2", "ai-terminology", "alpha", "release-10", "team-terms", "ai-model-names"]),
        ("custom only",
            ["release-9", "Zulu Notes", "team-terms-2", "alpha", "release-10", "team-terms"]),
        ("default install",
            [.. AppSettings.DefaultLibraryIds.Reverse()]),
        ("everything",
            [.. CustomFiles.Select(f => Path.GetFileNameWithoutExtension(f.FileName)).Reverse(),
             .. BuiltInDictionaryLibraries.All.Select(l => l.Id).Reverse()]),
    ];

    /// <summary>Every spoken form the fixture defines, so the golden reports those winners and nothing else.</summary>
    public static readonly IReadOnlySet<string> FixtureKeys = CustomFiles
        .SelectMany(f => DictionaryLibraryCsv.Parse(f.Csv).Entries.Select(e => e.Pattern.Trim()))
        .Concat(Personal.Select(e => e.Pattern.Trim()))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly string _root;

    public LibraryFixture()
    {
        _root = Path.Combine(Path.GetTempPath(), "scribe-lib-fixture-" + Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(_root);
        Directory.CreateDirectory(Paths.LibrariesDir);
        foreach (var (fileName, csv) in CustomFiles)
        {
            File.WriteAllText(Path.Combine(Paths.LibrariesDir, fileName), csv);
        }

        Database = ScribeDatabase.CreateInMemory();
        Settings = new SettingsRepository(Database);
        Dictionary = new DictionaryRepository(Database);
        Dictionary.AddRange(Personal);
        Service = new DictionaryLibraryService(Paths, Settings, NullLogger<DictionaryLibraryService>.Instance);
    }

    public AppPaths Paths { get; }

    public ScribeDatabase Database { get; }

    public SettingsRepository Settings { get; }

    public DictionaryRepository Dictionary { get; }

    public DictionaryLibraryService Service { get; }

    /// <summary>Saves an enabled set the way Settings does, as the stored list the service reads.</summary>
    public void Enable(IEnumerable<string> ids)
    {
        var settings = AppSettings.CreateDefault();
        settings.EnabledDictionaryLibraryIds.Clear();
        settings.EnabledDictionaryLibraryIds.AddRange(ids);
        Settings.Save(settings);
    }

    public void Dispose()
    {
        Database.Dispose();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: a leftover temp folder is harmless.
        }
    }
}
