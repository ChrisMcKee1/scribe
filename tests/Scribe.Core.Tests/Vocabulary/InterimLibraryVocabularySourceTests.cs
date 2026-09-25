using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Vocabulary;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// Until the W1b integration the vocabulary source is stream W-V's stand-in over release 0.4.4's library-selection seam,
/// and the branch has to behave exactly as 0.4.4 end to end: the libraries the settings in use enable, every one of them
/// sent, nothing ever held back. These cases run it over the real library service and settings file, and compare what
/// dictation writes and sends through it with what 0.4.4's own path produced.
/// </summary>
public sealed class InterimLibraryVocabularySourceTests : IDisposable
{
    private const string DotnetDictated = "build asp net core with entity framework core and x unit on win ui 3";
    private const string DefaultAiDictated = "gpt five six terra";

    private readonly TempDatabaseFolder _folder = new();
    private readonly ScribeDatabase _database;
    private readonly SettingsRepository _settings;
    private readonly DictionaryRepository _dictionary;
    private readonly DictionaryLibraryService _libraries;

    public InterimLibraryVocabularySourceTests()
    {
        _database = _folder.Open();
        _settings = new SettingsRepository(_database);
        _dictionary = new DictionaryRepository(_database);
        _libraries = new DictionaryLibraryService(
            new AppPaths(_folder.Root), _settings, NullLogger<DictionaryLibraryService>.Instance);
    }

    public void Dispose()
    {
        _database.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public void Current_is_the_selection_of_the_settings_in_use_with_every_enabled_library_sent_and_no_content_bound()
    {
        IReadOnlyCollection<string>? inUse = ["dotnet-development"];
        var source = new InterimLibraryVocabularySource(_libraries, () => inUse);

        var vocabulary = source.Current;
        Assert.Equal(Describe(_libraries.GetEnabledLibraryEntries(["dotnet-development"])), Describe(vocabulary.Entries));
        Assert.Equal(Describe(vocabulary.Entries), Describe(vocabulary.AiEntries));
        Assert.Equal(["dotnet-development"], vocabulary.AiScope.PermittedLibraryIds);
        Assert.Null(vocabulary.AiScope.PermittedContent["dotnet-development"]);
        Assert.Equal(0, vocabulary.Generation);

        // The selection follows the settings in use, never the stored document, whatever state that document is in.
        _settings.Set(SettingsRepository.SettingsKey, "{ this is not a settings document");
        Assert.Equal(Describe(vocabulary.Entries), Describe(source.Current.Entries));
        inUse = ["ai-model-names"];
        Assert.Contains(source.Current.Entries, entry => entry.Replacement == "GPT-5.6-Terra");
        Assert.DoesNotContain(source.Current.Entries, entry => entry.Replacement == "ASP.NET Core");
        inUse = [];
        Assert.Same(LibraryVocabulary.Empty, source.Current);
        inUse = null;
        Assert.Same(LibraryVocabulary.Empty, source.Current);
    }

    [Fact]
    public void Nothing_is_ever_held_back_and_nothing_is_announced_before_W1b()
    {
        var source = new InterimLibraryVocabularySource(_libraries, () => ["dotnet-development"]);
        var unrelated = new AiVocabularyScope(9, [KeyValuePair.Create("somewhere-else", (LibraryContentHash?)new LibraryContentHash(new string('9', 64)))]);
        var handedOver = 0;

        Assert.True(source.TryHandOff(AiVocabularyScope.None, () => handedOver++));
        Assert.True(source.TryHandOff(unrelated, () => handedOver++));
        Assert.Equal(2, handedOver);

        // Subscribing is harmless and nothing is ever raised: nothing commits a library generation before W1b.
        source.Changed += _ => Assert.Fail("The interim source announced a generation.");
    }

    [Fact]
    public async Task Through_the_interim_source_dictation_writes_and_sends_what_0_4_4_wrote_and_sent()
    {
        _dictionary.AddRange([DictionaryEntry.New("quillmoor", "Quillmoor"), DictionaryEntry.New("x unit", "XUNIT-mine")]);
        var inUse = new List<string> { "dotnet-development", "ai-model-names" };
        var source = new InterimLibraryVocabularySource(_libraries, () => inUse);
        var processor = new TextPostProcessor(_dictionary, NullLogger<TextPostProcessor>.Instance, snippets: null, libraries: _libraries);
        using var publisher = new VocabularyPublisher(source, _dictionary, processor, NullLogger<VocabularyPublisher>.Instance, work => work());
        var generation = (await publisher.StartAsync().WaitAsync(TimeSpan.FromSeconds(30))).Generation;
        var dictation = new DictationPostProcessor(processor);
        dictation.Use(generation);

        // 0.4.4: the post-processor reloaded with the ids in use, and the glossary composed from the same selection.
        var legacy = new TextPostProcessor(_dictionary, NullLogger<TextPostProcessor>.Instance, snippets: null, libraries: _libraries);
        legacy.Reload(inUse);
        var legacyVocabulary = CleanupPrompt.ComposeVocabulary(_dictionary.GetEnabled(), _libraries.GetEnabledLibraryEntries(inUse));

        foreach (var sentence in new[] { DotnetDictated, DefaultAiDictated, "quillmoor ships on dot net", "nothing to replace here" })
        {
            Assert.Equal(legacy.ProcessDetailed(sentence, sentence).Text, dictation.ProcessDetailed(sentence, sentence).Text);
        }

        Assert.Equal("build ASP.NET Core with Entity Framework Core and XUNIT-mine on WinUI 3", dictation.ProcessDetailed(DotnetDictated).Text);
        foreach (var budget in new[] { CleanupPrompt.MaxGlossaryTermsLocal, CleanupPrompt.MaxGlossaryTermsCloud })
        {
            var expected = CleanupPrompt.BuildGlossary(legacyVocabulary, budget);
            Assert.Equal(expected, generation.Cleanup.GlossaryFor(budget));
        }
    }

    [Fact]
    public void The_stand_in_is_registered_after_the_core_services_so_it_overrides_the_library_service_until_the_integration()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);

        // The library service registers itself as the vocabulary source in Core, and the container resolves the last
        // registration, so the stand-in keeps 0.4.4's selection only while the app registers it after AddScribeCore.
        var core = File.ReadAllText(Path.Combine(root.FullName, "src", "Scribe.Core", "DependencyInjection", "CoreServiceCollectionExtensions.cs"));
        Assert.Contains("services.AddSingleton<ILibraryVocabularySource>(", core, StringComparison.Ordinal);
        var app = File.ReadAllText(Path.Combine(root.FullName, "src", "Scribe.App", "App.xaml.cs"));
        var coreServices = app.IndexOf("builder.Services.AddScribeCore();", StringComparison.Ordinal);
        var standIn = app.IndexOf("builder.Services.AddSingleton<ILibraryVocabularySource>(sp => new InterimLibraryVocabularySource(", StringComparison.Ordinal);
        Assert.True(coreServices >= 0, "The app no longer calls AddScribeCore where this test looks for it.");
        Assert.True(standIn > coreServices, "The stand-in must be registered after AddScribeCore, or the library service's own registration wins.");
    }

    private static List<string> Describe(IEnumerable<DictionaryEntry> entries) =>
        [.. entries.Select(entry => $"{entry.Pattern}|{entry.Replacement}|{entry.WholeWord}|{entry.Enabled}")];
}

/// <summary>
/// The vocabulary folder is new, and LogPrivacyGuardTests does not scan it yet (the request is in stream W-V's report):
/// the same scanner runs over it here, so nothing in it logs an exception object, its text or its data.
/// </summary>
public sealed class VocabularyLogPrivacyTests
{
    [Fact]
    public void No_log_call_in_the_vocabulary_folder_passes_an_exception_or_its_text()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var folder = Path.Combine(root.FullName, "src", "Scribe.Core", "Vocabulary");
        var calls = 0;
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            calls += LogCallScanner.Find(source).Count();
            offenders.AddRange(LogCallScanner.Check(source).Select(offence => $"{Path.GetFileName(file)}: {offence.Reason}: {offence.Call}"));
        }

        Assert.True(calls >= 6, $"The scanner found only {calls} log calls in the vocabulary folder, so it is not reading it.");
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }
}
