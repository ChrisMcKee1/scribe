using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.Vocabulary;

namespace Scribe.Core.Tests;

/// <summary>
/// Dictation's library vocabulary is the committed one the library vocabulary source publishes, never a fresh read of the
/// stored settings document. Release 0.4.4 passed the enabled ids of the settings in use to the library service
/// (<c>GetEnabledLibraryEntries(ids)</c>, <c>ITextPostProcessor.Reload(ids)</c>) for the same reason: a document that turns
/// unreadable mid-session reads as the defaults, which switched the user's libraries off and the default AI libraries on
/// and sent their terms to a remote provider. Stream W-V replaced that seam with the vocabulary source: every dictation
/// is admitted with a vocabulary generation built from one <c>Current</c> snapshot, and its post-processing and its
/// cleanup glossary both come from it. The seam's two guarantees now hold by construction, and these cases pin both: no
/// consumer re-reads the stored document per request, and a dictionary-only reload keeps the library vocabulary. Until the
/// W1b integration the source is <see cref="InterimLibraryVocabularySource"/>, over the real library service and settings
/// file here, and the cleanup service runs over a recording fake provider.
/// </summary>
public sealed class LibrarySelectionInUseTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // dotnet-development, the user's choice here, and ai-model-names, one of the defaults, which the user turned off. The
    // guardrail prompt names GPT-5.6-Terra in an example of its own, so the glossary is probed by its line.
    private const string DotnetDictated = "build asp net core with entity framework core and x unit on win ui 3";
    private const string DotnetWritten = "build ASP.NET Core with Entity Framework Core and xUnit on WinUI 3";
    private const string DotnetGlossaryLine = "- ASP.NET Core (transcribed as \"asp net core\")";
    private const string DefaultAiDictated = "gpt five six terra";
    private const string DefaultAiWritten = "GPT-5.6-Terra";
    private const string DefaultAiGlossaryLine = "- GPT-5.6-Terra (transcribed as \"gpt five six terra\")";
    private const string Dictated = "we moved the service to asp net core last week";

    private readonly TempDatabaseFolder _folder = new();
    private readonly ScribeDatabase _database;
    private readonly SettingsRepository _settings;
    private readonly DictionaryRepository _dictionary;
    private readonly DictionaryLibraryService _libraries;
    private readonly TextPostProcessor _processor;

    public LibrarySelectionInUseTests()
    {
        _database = _folder.Open();
        _settings = new SettingsRepository(_database);
        _dictionary = new DictionaryRepository(_database);
        _libraries = new DictionaryLibraryService(
            new AppPaths(_folder.Root), _settings, NullLogger<DictionaryLibraryService>.Instance);
        _processor = new TextPostProcessor(
            _dictionary, NullLogger<TextPostProcessor>.Instance, snippets: null, libraries: _libraries);
    }

    public void Dispose()
    {
        _database.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task A_non_default_selection_survives_an_unreadable_document_through_the_usage_add()
    {
        // Saved: Microsoft Foundry cleanup, with only the .NET library on and both default AI libraries off.
        var saved = AppSettings.CreateDefault();
        saved.EnableAiCleanup = true;
        saved.AiCleanupProvider = CleanupProvider.AzureFoundry;
        saved.AiCleanupAzureEndpoint = "https://in-use-canary.example.invalid/";
        saved.AiCleanupAzureDeployment = "in-use-deployment";
        saved.EnabledDictionaryLibraryIds = ["dotnet-development"];
        _settings.Save(saved);

        // Dictation starts on the stored settings, the way DictationController.Start and the app's composition root do:
        // the vocabulary source reads the library selection of the settings in use, and the first generation is built.
        var inUse = _settings.Load();
        var source = new InterimLibraryVocabularySource(_libraries, () => inUse.EnabledDictionaryLibraryIds);
        using var publisher = new VocabularyPublisher(source, _dictionary, _processor, NullLogger<VocabularyPublisher>.Instance, work => work());
        var generation = publisher.Start();
        await using var harness = new CleanupHarness();
        var provider = new RecordingProvider();
        harness.Service.ProviderFactoryForTesting = provider.Connect;
        harness.Service.Configure(OptionsFor(inUse));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var dictation = new DictationPostProcessor(_processor);
        dictation.Use(generation);
        Assert.Equal(DotnetWritten, dictation.ProcessDetailed(DotnetDictated).Text);
        Assert.Equal(DefaultAiDictated, dictation.ProcessDetailed(DefaultAiDictated).Text);
        await harness.Service.Admit(generation.Cleanup).CleanAsync(Dictated).WaitAsync(Bound);
        var before = provider.Client.Instructions[^1];
        Assert.Contains(DotnetGlossaryLine, before, StringComparison.Ordinal);
        Assert.DoesNotContain(DefaultAiGlossaryLine, before, StringComparison.Ordinal);

        // The document turns unreadable mid-session.
        _settings.Set(SettingsRepository.SettingsKey, "{ this is not a settings document");

        // The Add stores an entry, and with no usable stored settings only the vocabulary reloads, on the settings in use.
        _dictionary.AddRange([DictionaryEntry.New("quillmoor", "Quillmoor")]);
        Task<VocabularyGeneration>? reloaded = null;
        var outcome = StoredSettingsReapply.Reapply(
            _settings,
            _ => Assert.Fail("Stored settings that cannot be read are never applied."),
            () => reloaded = publisher.RefreshAsync());
        Assert.Equal(StoredSettingsReapplied.VocabularyOnly, outcome);
        var next = await reloaded!.WaitAsync(Bound);

        // Locally the selection holds: the .NET library still applies, the default AI library stays off, the entry applies.
        dictation.Use(next);
        Assert.Equal(DotnetWritten, dictation.ProcessDetailed(DotnetDictated).Text);
        Assert.Equal(DefaultAiDictated, dictation.ProcessDetailed(DefaultAiDictated).Text);
        Assert.Equal("Quillmoor", dictation.ProcessDetailed("quillmoor").Text);

        // The next cleanup prompt is the one before with the new entry's line added, and nothing else changed.
        Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
        await harness.Service.Admit(next.Cleanup).CleanAsync(Dictated).WaitAsync(Bound);
        var after = provider.Client.Instructions[^1];
        Assert.DoesNotContain(DefaultAiGlossaryLine, after, StringComparison.Ordinal);
        Assert.Contains(DotnetGlossaryLine, after, StringComparison.Ordinal);
        var lines = after.Split('\n').ToList();
        Assert.True(lines.Remove("- Quillmoor"), "The new entry never reached the cleanup prompt.");
        Assert.Equal(before, string.Join('\n', lines));
    }

    [Fact]
    public void The_library_service_selects_by_the_ids_it_is_given_whatever_the_stored_document_says()
    {
        // Release 0.4.4's seam, which the interim vocabulary source adapts and J keeps as it is until the integration.
        _settings.Set(SettingsRepository.SettingsKey, "{ this is not a settings document");

        var entries = _libraries.GetEnabledLibraryEntries(["dotnet-development"]);

        Assert.Contains(entries, entry => entry.Replacement == "ASP.NET Core");
        Assert.DoesNotContain(entries, entry => entry.Replacement == DefaultAiWritten);
        Assert.Empty(_libraries.GetEnabledLibraryEntries([]));
    }

    [Theory]
    [InlineData("unreadable")]
    [InlineData("lost in a repair")]
    public void Without_usable_stored_settings_the_stored_selection_is_none_rather_than_the_defaults(string state)
    {
        if (state == "unreadable")
        {
            _settings.Set(SettingsRepository.SettingsKey, "{ this is not a settings document");
        }
        else
        {
            // No document, and the marker a repair leaves when it lost the one the user had.
            _settings.Set(SettingsRepository.LostMarkerKey, "lost");
        }

        Assert.Empty(_libraries.GetEnabledLibraryEntries());
        Assert.True(_settings.LastLoadFailed);

        // A post-processor no owner has given a selection falls back to that stored selection: nothing, not the defaults.
        Assert.Equal(DefaultAiDictated, _processor.Process(DefaultAiDictated));
    }

    [Fact]
    public void A_dictionary_only_reload_keeps_the_library_vocabulary()
    {
        var saved = AppSettings.CreateDefault();
        saved.EnabledDictionaryLibraryIds = ["dotnet-development"];
        _settings.Save(saved);
        var inUse = _settings.Load();
        var source = new InterimLibraryVocabularySource(_libraries, () => inUse.EnabledDictionaryLibraryIds);
        using var publisher = new VocabularyPublisher(source, _dictionary, _processor, NullLogger<VocabularyPublisher>.Instance, work => work());
        var before = publisher.Start();
        _settings.Set(SettingsRepository.SettingsKey, "{ this is not a settings document");
        _dictionary.AddRange([DictionaryEntry.New("quillmoor", "Quillmoor")]);

        // What quick add and learning from history call once they have stored an entry; the publisher answers it.
        _processor.Reload();
        var after = publisher.Current;

        Assert.NotSame(before, after);
        Assert.Equal(before.Libraries.Entries.Select(Describe), after.Libraries.Entries.Select(Describe));
        var dictation = new DictationPostProcessor(_processor);
        dictation.Use(after);
        Assert.Equal(DotnetWritten, dictation.ProcessDetailed(DotnetDictated).Text);
        Assert.Equal(DefaultAiDictated, dictation.ProcessDetailed(DefaultAiDictated).Text);
        Assert.Equal("Quillmoor", dictation.ProcessDetailed("quillmoor").Text);

        // Release 0.4.4's own overloads keep compiling as J leaves them, and keep what they promised.
        _processor.Reload(saved.EnabledDictionaryLibraryIds);
        _processor.Reload();
        Assert.Equal(DotnetWritten, _processor.Process(DotnetDictated));
        Assert.Equal(DefaultAiDictated, _processor.Process(DefaultAiDictated));
    }

    [Fact]
    public void Production_code_takes_every_library_vocabulary_from_the_vocabulary_source()
    {
        var root = RepositoryRoot();

        // The stored selection is asked for only where it is defined and by the post-processor before any owner has named
        // a selection; release 0.4.4's ids overload only where it is defined, where the post-processor's legacy reload
        // reads it, in the interim vocabulary source that adapts it, and in the usage report's path for a library service
        // that is not a vocabulary source.
        var parameterless = new Regex(@"\bGetEnabledLibraryEntries\b(?!\s*\(\s*[^\s)])");
        var withIds = new Regex(@"\bGetEnabledLibraryEntries\s*\(\s*[^\s)]");
        var lines = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadAllLines(file)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Select(line => (File: Path.GetFileName(file), Line: line.Trim())))
            .ToList();
        Assert.Equal(
            [
                "DictionaryLibraryService.cs: public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries()",
                "IDictionaryLibraryService.cs: IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries();",
                "TextPostProcessor.cs: : _libraries.GetEnabledLibraryEntries();",
            ],
            lines.Where(l => parameterless.IsMatch(l.Line)).Select(l => $"{l.File}: {l.Line}").Order(StringComparer.Ordinal).ToList());
        Assert.Equal(
            ["DictionaryLibraryService.cs", "IDictionaryLibraryService.cs", "InterimLibraryVocabularySource.cs", "TextPostProcessor.cs", "UsageReport.cs"],
            lines.Where(l => withIds.IsMatch(l.Line) && !l.Line.StartsWith("///", StringComparison.Ordinal))
                .Select(l => l.File).Distinct().Order(StringComparer.Ordinal).ToList());
        Assert.DoesNotContain(lines, l => l.Line.Contains(".Reload(settings.EnabledDictionaryLibraryIds)", StringComparison.Ordinal));

        // Dictation: the generation is taken with the recording's settings at its admission, and that one generation feeds
        // both the cleanup (its glossary and scope) and the dictionary pass. The options carry no glossary of their own.
        var controller = File.ReadAllText(Path.Combine(root, "src", "Scribe.App", "Dictation", "DictationController.cs"));
        Assert.DoesNotMatch(@"_postProcessor\.Reload\(", controller);
        Assert.DoesNotContain("GetEnabledLibraryEntries", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("IDictionaryLibraryService", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildGlossary", controller, StringComparison.Ordinal);
        Assert.Contains("Glossary: null,", controller, StringComparison.Ordinal);
        var factory = controller.IndexOf("return new CaptureContext(", StringComparison.Ordinal);
        Assert.True(factory > 0, "The capture context is not created where the recording is admitted.");
        Assert.Contains("_vocabulary.Current);", controller[factory..controller.IndexOf("},", factory, StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Contains("capture.Vocabulary);", controller, StringComparison.Ordinal);
        var cleanup = controller.IndexOf("cleanup = await _cleanup.Admit(session.Vocabulary.Cleanup)", StringComparison.Ordinal);
        Assert.True(cleanup > 0, "The cleanup is not admitted with the dictation's vocabulary.");
        Assert.StartsWith(
            ".CleanAsync(recognized, cancellationToken, cleanupWritingStyle)",
            controller[(controller.IndexOf('\n', cleanup) + 1)..].TrimStart(),
            StringComparison.Ordinal);
        Assert.Single(Regex.Matches(controller, Regex.Escape(".CleanAsync(")));
        var use = controller.IndexOf("_postProcessor.Use(session.Vocabulary);", StringComparison.Ordinal);
        var process = controller.IndexOf("_postProcessor.ProcessDetailed(recognized, result.Text)", StringComparison.Ordinal);
        Assert.InRange(use, cleanup, process);
        Assert.Single(Regex.Matches(controller, Regex.Escape("_postProcessor.ProcessDetailed(")));
        Assert.Single(Regex.Matches(controller, @"_vocabulary\.Current\b"));

        // Settings and the stored-settings reapply ask for a new generation; the first one is built at Start.
        foreach (var method in new[] { "public void ApplySettings(AppSettings settings)", "public void ReloadVocabulary()" })
        {
            var start = controller.IndexOf(method, StringComparison.Ordinal);
            var body = controller[start..controller.IndexOf("\n    }", start, StringComparison.Ordinal)];
            Assert.Contains("_postProcessor.ReloadSnippets();", body, StringComparison.Ordinal);
            Assert.Contains("_ = _vocabulary.RefreshAsync();", body, StringComparison.Ordinal);
        }

        var startMethod = controller.IndexOf("public void Start()", StringComparison.Ordinal);
        Assert.Contains("_vocabulary.Start();", controller[startMethod..controller.IndexOf("\n    }", startMethod, StringComparison.Ordinal)], StringComparison.Ordinal);

        // The composition root: the vocabulary source, which AI cleanup and the publisher take, and until the W1b
        // integration it is the interim one over the settings in use. Quick add's conflict check reads its committed
        // vocabulary, and the usage report's selection comes from it, never from the ids of the settings.
        var app = File.ReadAllText(Path.Combine(root, "src", "Scribe.App", "App.xaml.cs"));
        Assert.Contains("AddSingleton<ILibraryVocabularySource>(sp => new InterimLibraryVocabularySource(", app, StringComparison.Ordinal);
        Assert.Contains("() => _controller?.CurrentSettings.EnabledDictionaryLibraryIds));", app, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddSingleton<VocabularyPublisher>();", app, StringComparison.Ordinal);
        Assert.Contains("services.GetRequiredService<VocabularyPublisher>(),", app, StringComparison.Ordinal);
        Assert.Contains("baseEntries, services.GetRequiredService<ILibraryVocabularySource>().Current.Entries);", app, StringComparison.Ordinal);
        Assert.DoesNotContain("GetEnabledLibraryEntries", app, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(app, Regex.Escape("EnabledDictionaryLibraryIds")));
        var window = File.ReadAllText(Path.Combine(root, "src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs"));
        Assert.Contains("var vocabulary = _libraryVocabulary.Current;", window, StringComparison.Ordinal);
        Assert.Contains("[.. vocabulary.AiScope.PermittedLibraryIds],", window, StringComparison.Ordinal);
        Assert.DoesNotContain("_enabledLibrariesInUse", window, StringComparison.Ordinal);
        Assert.DoesNotContain("GetEnabledLibraryEntries", window, StringComparison.Ordinal);

        // The usage insight goes under the scope of the report its snapshot came from (contract 3.3.6): the scope is set
        // wherever the snapshot is, cleared wherever it is, taken with it when the button is pressed and handed to the
        // scoped CompleteAsync. The window's only other completion is the dictionary suggester's, which sends history and
        // no library vocabulary, so it goes under no library scope.
        Assert.Equal(
            Regex.Matches(window, @"_usageSnapshot = result\.Snapshot;").Count,
            Regex.Matches(window, @"_usageSnapshot = result\.Snapshot;\r?\n\s*_usageLibraryScope = result\.LibraryScope;").Count);
        Assert.Equal(
            Regex.Matches(window, @"_usageSnapshot = null;").Count,
            Regex.Matches(window, @"_usageSnapshot = null;\r?\n\s*_usageLibraryScope = AiVocabularyScope\.None;").Count);
        Assert.Single(Regex.Matches(window, @"_usageSnapshot = result\.Snapshot;"));
        var click = window.IndexOf("private async void UsageInsightButton_Click(", StringComparison.Ordinal);
        Assert.True(click > 0, "The usage insight's handler was not found.");
        var handler = window[click..window.IndexOf("\n    }", click, StringComparison.Ordinal)];
        Assert.Contains("var libraryScope = _usageLibraryScope;", handler, StringComparison.Ordinal);
        Assert.Matches(@"CompleteAsync\(\s*UsageInsight\.SystemPrompt,\s*UsageInsight\.BuildSummary\(snapshot\),\s*recipient,\s*libraryScope\);", handler);
        Assert.Equal(
            ["_cleanup.CompleteAsync(", "_cleanup.CompleteAsync(AiDictionarySuggester.SystemPrompt, sample, recipient);"],
            Regex.Matches(window, @"_cleanup\.CompleteAsync\([^\r\n]*").Select(match => match.Value.Trim()).Order(StringComparer.Ordinal).ToList());
    }

    // The fields that decide where a request goes, mapped the way DictationController.BuildCleanupOptions maps them: no
    // glossary, which each dictation's admission carries.
    private static CleanupOptions OptionsFor(AppSettings settings) => new(
        settings.EnableAiCleanup,
        settings.AiCleanupProvider,
        settings.AiCleanupModel,
        settings.AiCleanupAzureEndpoint,
        settings.AiCleanupAzureDeployment,
        WritingStyle: settings.AiCleanupWritingStyle,
        Glossary: null,
        PromptStyle: settings.AiCleanupPromptStyle);

    private static string Describe(DictionaryEntry entry) => $"{entry.Pattern}|{entry.Replacement}|{entry.WholeWord}|{entry.Enabled}";

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    /// <summary>Stands in for the remote provider's connection: every agent runs over one recording client.</summary>
    private sealed class RecordingProvider
    {
        public RecordingChatClient Client { get; } = new();

        public Task<Func<string, AIAgent>> Connect(CleanupOptions options, CancellationToken cancellationToken) =>
            Task.FromResult<Func<string, AIAgent>>(
                instructions => new ChatClientAgent(Client, instructions: instructions, name: "ScribeCleanup"));
    }

    /// <summary>Answers every call and records the instructions each call carried.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private readonly ConcurrentQueue<string> _instructions = new();

        public IReadOnlyList<string> Instructions => _instructions.ToArray();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var system = messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text);
            _instructions.Enqueue(string.Join("\n", system.Prepend(options?.Instructions ?? string.Empty)));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "We moved the service to ASP.NET Core last week.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
