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

namespace Scribe.Core.Tests;

/// <summary>
/// Dictation's library selection is the one in the settings it runs on. Both vocabulary consumers, the post-processor
/// and the AI cleanup glossary, used to read it again from the stored document on every rebuild, and a document that
/// turns unreadable mid-session reads as the defaults standing in for it. So the vocabulary-only reload after the Usage
/// page's Add (<see cref="StoredSettingsReapply"/>) switched off the libraries the user had chosen, switched on the default
/// AI libraries, and sent their terms to a remote provider with every later dictation. These cases run the real library
/// service and post-processor over a real settings file, and a real cleanup service over a recording fake provider.
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

        // Dictation starts on the stored settings, the way DictationController.Start applies them.
        var inUse = _settings.Load();
        await using var harness = new CleanupHarness();
        var provider = new RecordingProvider();
        harness.Service.ProviderFactoryForTesting = provider.Connect;
        ApplyVocabulary(inUse, harness.Service);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(DotnetWritten, _processor.Process(DotnetDictated));
        Assert.Equal(DefaultAiDictated, _processor.Process(DefaultAiDictated));
        await harness.Service.CleanAsync(Dictated).WaitAsync(Bound);
        var before = provider.Client.Instructions[^1];
        Assert.Contains(DotnetGlossaryLine, before, StringComparison.Ordinal);
        Assert.DoesNotContain(DefaultAiGlossaryLine, before, StringComparison.Ordinal);

        // The document turns unreadable mid-session.
        _settings.Set(SettingsRepository.SettingsKey, "{ this is not a settings document");

        // The Add stores an entry, and with no usable stored settings only the vocabulary reloads, on the settings in use.
        _dictionary.AddRange([DictionaryEntry.New("quillmoor", "Quillmoor")]);
        var outcome = StoredSettingsReapply.Reapply(
            _settings,
            _ => Assert.Fail("Stored settings that cannot be read are never applied."),
            () => ApplyVocabulary(inUse, harness.Service));
        Assert.Equal(StoredSettingsReapplied.VocabularyOnly, outcome);

        // Locally the selection holds: the .NET library still applies, the default AI library stays off, the entry applies.
        Assert.Equal(DotnetWritten, _processor.Process(DotnetDictated));
        Assert.Equal(DefaultAiDictated, _processor.Process(DefaultAiDictated));
        Assert.Equal("Quillmoor", _processor.Process("quillmoor"));

        // The next cleanup prompt is the one before with the new entry's line added, and nothing else changed.
        Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
        await harness.Service.CleanAsync(Dictated).WaitAsync(Bound);
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
    public void A_dictionary_only_reload_keeps_the_selection_dictation_named()
    {
        var saved = AppSettings.CreateDefault();
        saved.EnabledDictionaryLibraryIds = ["dotnet-development"];
        _settings.Save(saved);
        _processor.Reload(saved.EnabledDictionaryLibraryIds);
        _settings.Set(SettingsRepository.SettingsKey, "{ this is not a settings document");
        _dictionary.AddRange([DictionaryEntry.New("quillmoor", "Quillmoor")]);

        // What quick add and learning from history call once they have stored an entry.
        _processor.Reload();

        Assert.Equal(DotnetWritten, _processor.Process(DotnetDictated));
        Assert.Equal(DefaultAiDictated, _processor.Process(DefaultAiDictated));
        Assert.Equal("Quillmoor", _processor.Process("quillmoor"));
    }

    [Fact]
    public void Production_code_selects_libraries_only_from_the_settings_dictation_runs_on()
    {
        var root = RepositoryRoot();

        // The stored selection is asked for only where it is defined and by the post-processor before any owner has
        // named a selection. Every other caller passes the ids of the settings in use.
        var parameterless = new Regex(@"\bGetEnabledLibraryEntries\b(?!\s*\(\s*[^\s)])");
        var uses = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadAllLines(file)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && parameterless.IsMatch(line))
                .Select(line => $"{Path.GetFileName(file)}: {line.Trim()}"))
            .ToList();
        Assert.Equal(
            [
                "DictionaryLibraryService.cs: public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries()",
                "IDictionaryLibraryService.cs: IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries();",
                "TextPostProcessor.cs: : _libraries.GetEnabledLibraryEntries();",
            ],
            uses.Order(StringComparer.Ordinal).ToList());

        // Both vocabulary consumers take the same selection: the settings being applied, or those in use.
        var controller = File.ReadAllText(Path.Combine(root, "src", "Scribe.App", "Dictation", "DictationController.cs"));
        Assert.DoesNotMatch(@"_postProcessor\.Reload\(\s*\)", controller);
        Assert.Equal(3, Regex.Matches(controller, Regex.Escape("_postProcessor.Reload(settings.EnabledDictionaryLibraryIds);")).Count);
        Assert.Contains("_libraries.GetEnabledLibraryEntries(settings.EnabledDictionaryLibraryIds)", controller, StringComparison.Ordinal);
        var reload = controller.IndexOf("public void ReloadVocabulary()", StringComparison.Ordinal);
        var body = controller[reload..controller.IndexOf("\n    }", reload, StringComparison.Ordinal)];
        Assert.Contains("var settings = CurrentSettings;", body, StringComparison.Ordinal);
        Assert.Contains("_postProcessor.Reload(settings.EnabledDictionaryLibraryIds);", body, StringComparison.Ordinal);
        Assert.Contains("ReconfigureCleanup(settings);", body, StringComparison.Ordinal);

        // Quick add's conflict check and the usage report take the selection in use from the shell.
        var app = File.ReadAllText(Path.Combine(root, "src", "Scribe.App", "App.xaml.cs"));
        Assert.Contains(".GetEnabledLibraryEntries(controller.CurrentSettings.EnabledDictionaryLibraryIds)", app, StringComparison.Ordinal);
        Assert.Contains("() => [.. _controller!.CurrentSettings.EnabledDictionaryLibraryIds],", app, StringComparison.Ordinal);
        var window = File.ReadAllText(Path.Combine(root, "src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs"));
        Assert.Contains("var libraryIds = _enabledLibrariesInUse();", window, StringComparison.Ordinal);
        Assert.Contains("_history, _dictionary, _libraries, libraryIds, request.Value.Days", window, StringComparison.Ordinal);
    }

    // What DictationController.Start and ReloadVocabulary do with the settings in use: the post-processor and the
    // glossary, both from that one library selection, the glossary composed and budgeted as BuildGlossary does it.
    private void ApplyVocabulary(AppSettings settingsInUse, TextCleanupService cleanup)
    {
        _processor.Reload(settingsInUse.EnabledDictionaryLibraryIds);
        var vocabulary = CleanupPrompt.ComposeVocabulary(
            _dictionary.GetEnabled(), _libraries.GetEnabledLibraryEntries(settingsInUse.EnabledDictionaryLibraryIds));
        var glossary = CleanupPrompt.BuildGlossary(
            vocabulary, CleanupPrompt.GlossaryTermBudget(settingsInUse.AiCleanupPromptStyle, settingsInUse.AiCleanupProvider));
        cleanup.Configure(new CleanupOptions(
            settingsInUse.EnableAiCleanup,
            settingsInUse.AiCleanupProvider,
            settingsInUse.AiCleanupModel,
            settingsInUse.AiCleanupAzureEndpoint,
            settingsInUse.AiCleanupAzureDeployment,
            WritingStyle: settingsInUse.AiCleanupWritingStyle,
            Glossary: string.IsNullOrEmpty(glossary) ? null : glossary,
            PromptStyle: settingsInUse.AiCleanupPromptStyle));
    }

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
