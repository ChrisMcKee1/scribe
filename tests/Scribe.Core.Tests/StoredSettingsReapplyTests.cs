using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
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
/// The Usage page's Add stores a dictionary entry and then puts it into effect. It used to do that by applying the
/// settings window's editing document, and after a Save that failed that document still named the provider the user
/// had picked and never saved, so the Add moved AI cleanup, with every later dictation's text and vocabulary, to a
/// provider no save had chosen. It now applies the settings as stored, and when none can be used it applies no settings
/// and only reloads the vocabulary. The window's side is pinned in
/// <see cref="CleanupDisclosureTests.Only_the_save_that_stored_the_window_s_document_applies_it"/>, and what that
/// vocabulary-only reload does, with a library selection an unreadable document must not replace, in
/// <see cref="LibrarySelectionInUseTests"/>; these tests drive a
/// real settings repository through a real failed save and a real cleanup service through fake providers. Nothing
/// leaves the process.
/// </summary>
public sealed class StoredSettingsReapplyTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const string Term = "Quillmoor";
    private const string Dictated = "please ask quill more about the launch plan";

    private readonly TempDatabaseFolder _folder = new();
    private readonly ScribeDatabase _database;
    private readonly SettingsRepository _settings;
    private readonly DictionaryRepository _dictionary;
    private readonly DictionaryLibraryService _libraries;

    public StoredSettingsReapplyTests()
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
    public async Task After_a_failed_save_the_usage_add_keeps_the_saved_on_device_provider_in_use()
    {
        // Saved: Foundry Local, on this PC. Dictation runs on it, the way the controller applied it at startup.
        var saved = AppSettings.CreateDefault();
        saved.EnableAiCleanup = true;
        saved.AiCleanupProvider = CleanupProvider.FoundryLocal;
        saved.AiCleanupModel = CleanupHarness.FoundryAlias;
        saved.EnabledDictionaryLibraryIds = [];
        _settings.Save(saved);

        await using var harness = new CleanupHarness();
        var providers = new Providers();
        var cleanup = harness.Service;
        cleanup.ProviderFactoryForTesting = providers.Connect;
        cleanup.Configure(OptionsFor(_settings.Load()));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var inUse = cleanup.Recipient!;
        Assert.True(inUse.IsOnDevice);
        await cleanup.CleanAsync(Dictated).WaitAsync(Bound);
        Assert.DoesNotContain(Term, providers.OnDevice.Requests[^1].Instructions, StringComparison.Ordinal);

        // The window's editing document, holding the user's pick: Microsoft Foundry. The Save fails at its commit, as
        // TrySaveAsync's catch meets it, and the pick stays in the document.
        var editing = _settings.Load();
        editing.AiCleanupProvider = CleanupProvider.AzureFoundry;
        editing.AiCleanupAzureEndpoint = "https://picked-canary.example.invalid/";
        editing.AiCleanupAzureDeployment = "picked-deployment";
        FailTheNextSaveAtItsCommit();
        Assert.ThrowsAny<SqliteException>(() => _settings.SaveBundle(editing, null, null, new ExternalIntents(0, 0)));
        _settings.WriteStep = null;
        Assert.Equal(CleanupProvider.FoundryLocal, _settings.Load().AiCleanupProvider);
        Assert.False(
            OptionsFor(editing).MatchesIgnoringPrompt(OptionsFor(_settings.Load())),
            "Applying the editing document would not have moved cleanup anywhere, so this case proves nothing.");

        // The Add stores the entry, as PersistLearnedDictionaryEntries does, then puts it into effect.
        _dictionary.AddRange([DictionaryEntry.New(Term.ToLowerInvariant(), Term)]);
        var applied = new List<AppSettings>();
        var answer = Task.FromResult(new VocabularyRefresh(VocabularyRefreshOutcome.Applied, VocabularyGeneration.Empty));
        var outcome = StoredSettingsReapply.Reapply(
            _settings,
            settings =>
            {
                applied.Add(settings);
                cleanup.Configure(OptionsFor(settings));
                return answer;
            },
            () => throw new InvalidOperationException("The stored settings are readable, so they are what goes live."));

        Assert.Equal(StoredSettingsReapplied.StoredSettings, outcome.Reapplied);
        Assert.Same(answer, outcome.Vocabulary);
        Assert.Equal(CleanupProvider.FoundryLocal, Assert.Single(applied).AiCleanupProvider);
        Assert.Equal(CleanupProvider.AzureFoundry, editing.AiCleanupProvider);
        Assert.Equal("picked-deployment", editing.AiCleanupAzureDeployment);

        // Cleanup still serves exactly the recipient it served before the failed Save, the next dictation reaches it
        // with the new term in its vocabulary, and the picked provider never receives anything.
        Assert.Equal(CleanupStatus.Ready, cleanup.Status);
        Assert.True(cleanup.Recipient!.IsOnDevice);
        await cleanup.CleanAsync(Dictated).WaitAsync(Bound);
        var request = providers.OnDevice.Requests[^1];
        Assert.Contains(Dictated, request.User, StringComparison.Ordinal);
        Assert.Contains(Term, request.Instructions, StringComparison.Ordinal);
        Assert.Equal(
            CompletionOutcome.Completed,
            (await cleanup.CompleteAsync("system", "still the same recipient", inUse).WaitAsync(Bound)).Outcome);
        Assert.Empty(providers.Remote.Requests);
    }

    [Theory]
    [InlineData("unreadable")]
    [InlineData("lost in a repair")]
    public void Without_usable_stored_settings_nothing_is_applied_and_the_vocabulary_still_reloads(string state)
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

        var applied = new List<AppSettings>();
        var reloads = 0;
        var answer = Task.FromResult(new VocabularyRefresh(VocabularyRefreshOutcome.Applied, VocabularyGeneration.Empty));
        var outcome = StoredSettingsReapply.Reapply(
            _settings,
            settings =>
            {
                applied.Add(settings);
                return answer;
            },
            () =>
            {
                reloads++;
                return answer;
            });

        Assert.Equal(StoredSettingsReapplied.VocabularyOnly, outcome.Reapplied);
        Assert.Same(answer, outcome.Vocabulary);
        Assert.Empty(applied);
        Assert.Equal(1, reloads);
        Assert.True(_settings.LastLoadFailed);
    }

    [Theory]
    [InlineData("readable")]
    [InlineData("unreadable")]
    public async Task The_result_completes_only_once_the_generation_built_from_the_stored_dictionary_is_published(string state)
    {
        // Dictation's vocabulary is built by the publisher, with the build held here: the Usage page's Add may say the
        // entry was added only once the result's answer, which it awaits, is in; that is when the next dictation takes it.
        var saved = AppSettings.CreateDefault();
        saved.EnabledDictionaryLibraryIds = [];
        _settings.Save(saved);
        var inUse = _settings.Load();
        var queued = new List<Action>();
        var processor = new TextPostProcessor(_dictionary, NullLogger<TextPostProcessor>.Instance);
        using var publisher = new VocabularyPublisher(
            new InterimLibraryVocabularySource(_libraries, () => inUse.EnabledDictionaryLibraryIds),
            _dictionary,
            processor,
            NullLogger<VocabularyPublisher>.Instance,
            queued.Add);
        var starting = publisher.StartAsync();
        Assert.Single(queued)();
        queued.Clear();
        var before = (await starting.WaitAsync(Bound)).Generation;
        if (state == "unreadable")
        {
            _settings.Set(SettingsRepository.SettingsKey, "{ this is not a settings document");
        }

        _dictionary.AddRange([DictionaryEntry.New(Term.ToLowerInvariant(), Term)]);
        var reapplied = StoredSettingsReapply.Reapply(_settings, _ => publisher.RefreshAsync(), publisher.RefreshAsync);

        Assert.Equal(
            state == "readable" ? StoredSettingsReapplied.StoredSettings : StoredSettingsReapplied.VocabularyOnly,
            reapplied.Reapplied);
        Assert.False(reapplied.Vocabulary.IsCompleted);
        Assert.Same(before, publisher.Current);

        Assert.Single(queued)();
        var refresh = await reapplied.Vocabulary.WaitAsync(Bound);
        Assert.Equal(VocabularyRefreshOutcome.Applied, refresh.Outcome);
        Assert.Same(refresh.Generation, publisher.Current);
        Assert.Contains(refresh.Generation.Dictionary, entry => entry.Replacement == Term);
        Assert.Equal(Term, processor.ProcessDetailed(Term.ToLowerInvariant(), null, publisher.Current.Rules).Text);
    }

    // Checked only at the commit, a reference to an audio blob that does not exist makes SQLite refuse it, and the
    // save rolls back the way a real failure does.
    private void FailTheNextSaveAtItsCommit() => _settings.WriteStep = (step, connection, transaction) =>
    {
        if (step == "save committing")
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "PRAGMA defer_foreign_keys = ON; " +
                "INSERT INTO history (timestamp_utc, text, audio_ms, decode_ms, audio_blob_id) " +
                "VALUES ('2026-01-01T00:00:00.0000000+00:00', 'orphan', 1, 1, 424242);";
            command.ExecuteNonQuery();
        }
    };

    // The fields that decide where a request goes and what vocabulary it carries, mapped the way
    // DictationController.BuildCleanupOptions and BuildGlossary map them, from the dictionary and libraries as stored.
    private CleanupOptions OptionsFor(AppSettings settings)
    {
        var vocabulary = CleanupPrompt.ComposeVocabulary(
            _dictionary.GetEnabled(), _libraries.GetEnabledLibraryEntries(settings.EnabledDictionaryLibraryIds));
        var glossary = CleanupPrompt.BuildGlossary(
            vocabulary, CleanupPrompt.GlossaryTermBudget(settings.AiCleanupPromptStyle, settings.AiCleanupProvider));
        return new CleanupOptions(
            settings.EnableAiCleanup,
            settings.AiCleanupProvider,
            settings.AiCleanupModel,
            settings.AiCleanupAzureEndpoint,
            settings.AiCleanupAzureDeployment,
            settings.AiCleanupAzureApiKey,
            WritingStyle: settings.AiCleanupWritingStyle,
            Glossary: string.IsNullOrEmpty(glossary) ? null : glossary,
            CustomEndpoint: settings.AiCleanupCustomEndpoint,
            CustomModel: settings.AiCleanupCustomModel,
            CustomApiKey: settings.AiCleanupCustomApiKey,
            PromptStyle: settings.AiCleanupPromptStyle,
            CopilotModel: settings.AiCleanupCopilotModel);
    }

    /// <summary>Stands in for each provider's connection: one recording client on this PC, one remote.</summary>
    private sealed class Providers
    {
        public RecordingChatClient OnDevice { get; } = new();

        public RecordingChatClient Remote { get; } = new();

        public Task<Func<string, AIAgent>> Connect(CleanupOptions options, CancellationToken cancellationToken)
        {
            var client = options.Provider == CleanupProvider.FoundryLocal ? OnDevice : Remote;
            return Task.FromResult<Func<string, AIAgent>>(
                instructions => new ChatClientAgent(client, instructions: instructions, name: "ScribeCleanup"));
        }
    }

    private sealed record Request(string Instructions, string User);

    /// <summary>Answers every call and records the instructions and the user message each call carried.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private readonly ConcurrentQueue<Request> _requests = new();

        public IReadOnlyList<Request> Requests => _requests.ToArray();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            var system = list.Where(m => m.Role == ChatRole.System).Select(m => m.Text);
            var user = list.Where(m => m.Role == ChatRole.User).Select(m => m.Text);
            _requests.Enqueue(new Request(
                string.Join("\n", system.Prepend(options?.Instructions ?? string.Empty)), string.Join("\n", user)));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Please ask Quillmoor about the launch plan.")));
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
