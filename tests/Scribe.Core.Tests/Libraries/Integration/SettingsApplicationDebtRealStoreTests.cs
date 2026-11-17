using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Scribe.Core.Diagnostics;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.Tests.Libraries.Storage;
using Scribe.Core.Vocabulary;
using ManualClock = Scribe.Core.Tests.Concurrency.ManualTimeProvider;
using ManualTimer = Scribe.Core.Tests.Concurrency.ManualTimer;

namespace Scribe.Core.Tests.Libraries.Integration;

/// <summary>
/// What a committed Settings Save still owes dictation when its stored settings cannot be read back, over the real settings
/// store: the owner the host made for the process keeps the debt after the window that raised it is gone, and puts the
/// stored settings into effect by its own bounded retry on the clock the test owns, with no window and no second Save
/// (T12). The failure of the read reaches the host's sink to be logged by its shape (T10).
/// </summary>
public sealed class SettingsApplicationDebtRealStoreTests : IDisposable
{
    private const string Canary = "canary-secret-value";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] Waits = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];
    private static readonly VocabularyRefresh Applied = new(VocabularyRefreshOutcome.Applied, VocabularyGeneration.Empty);

    private readonly RealLibraries _libraries = new();

    public void Dispose() => _libraries.Dispose();

    [Fact]
    public async Task A_retention_a_closed_window_could_not_apply_is_put_into_effect_by_the_process_owner_with_no_second_save()
    {
        using var rig = new Rig(_libraries);

        // The window's Save commits a history retention of forever, and the SELECT that reads the stored settings back fails.
        rig.Settings.FailLoad = true;
        var result = await rig.Protocol.SaveAsync(rig.Request(retentionDays: 0));

        // Stored, and not in effect: dictation keeps the previous retention, and the Save says so truthfully.
        Assert.Equal(0, rig.Fixture.Settings.Load().HistoryRetentionDays);
        Assert.False(result.Success);
        Assert.Equal(WordPackSaveProtocolSeverity.Warning, result.Severity);
        Assert.Contains("still using the previous settings", result.Message, StringComparison.Ordinal);
        Assert.Contains("try again in the background", result.Message, StringComparison.Ordinal);
        Assert.True(rig.Debt.IsOwed);
        Assert.Empty(rig.WindowApplied);

        // The window closes: nothing of it is called again. The owner's first wait is up, and the store still cannot be read.
        var first = await rig.NextTimerAsync();
        Assert.Equal(Waits[0], first.DueTime);
        first.Fire();
        var second = await rig.NextTimerAsync();
        Assert.Equal(Waits[1], second.DueTime);
        Assert.Empty(rig.HostApplied);
        Assert.Equal([SettingsApplicationStep.Apply, SettingsApplicationStep.Retry], rig.Reports.Select(report => report.Step).ToArray());

        // The store can be read: the next attempt puts the retention the Save stored into effect, and the debt ends.
        rig.Settings.FailLoad = false;
        second.Fire();

        Assert.True(SpinWait.SpinUntil(() => !rig.Debt.IsOwed, Bound), "The owner never put the stored settings into effect.");
        Assert.Equal([0], rig.HostApplied.ToArray());
        Assert.True(SpinWait.SpinUntil(() => !rig.Debt.IsRetrying, Bound), "The retry outlived the debt.");
        Assert.Equal(1, rig.Commits);
        Assert.Empty(rig.WindowApplied);
        Assert.Equal(2, rig.Time.Timers.Count);
        Assert.All(rig.Time.Timers, timer => Assert.True(timer.IsDisposed));
    }

    [Fact]
    public async Task A_failed_SELECT_of_the_stored_settings_reaches_the_sink_by_its_shape_and_never_by_its_text()
    {
        using var rig = new Rig(_libraries);
        rig.Settings.FailLoad = true;

        await rig.Protocol.SaveAsync(rig.Request(retentionDays: 0));
        (await rig.NextTimerAsync()).Fire();
        await rig.NextTimerAsync();

        // Both the foreground's failure and the retry's reach the host with the failure itself, which it describes.
        Assert.Equal(2, rig.Reports.Count);
        foreach (var (step, failure) in rig.Reports)
        {
            var shape = FailureShape.Describe(failure);
            Assert.IsType<SqliteException>(failure);
            Assert.Contains("SqliteException", shape, StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, shape, StringComparison.Ordinal);
            Assert.NotEqual(SettingsApplicationStep.Settlement, step);
        }
    }

    [Fact]
    public async Task Stored_settings_the_owner_cannot_use_are_not_replaced_by_defaults_or_a_vocabulary_only_reload()
    {
        using var rig = new Rig(_libraries);
        rig.Settings.FailLoad = true;
        await rig.Protocol.SaveAsync(rig.Request(retentionDays: 0));
        rig.Settings.FailLoad = false;

        // The stored document is unreadable now: what a repair or a hand edit can leave. Reading it gives defaults that are
        // no more the user's choice than the editing document, so nothing may be applied in their place.
        rig.Fixture.Settings.Set(SettingsRepository.SettingsKey, "{ this is not a settings document");
        var first = await rig.NextTimerAsync();
        first.Fire();
        var second = await rig.NextTimerAsync();

        Assert.True(rig.Debt.IsOwed);
        Assert.Empty(rig.HostApplied);
        Assert.Equal(SettingsApplicationStep.Retry, rig.Reports.Last().Step);
        Assert.IsType<InvalidOperationException>(rig.Reports.Last().Failure);

        // Once the settings can be read, they are what goes into effect.
        var readable = AppSettings.CreateDefault();
        readable.HistoryRetentionDays = 0;
        rig.Fixture.Settings.Save(readable);
        second.Fire();

        Assert.True(SpinWait.SpinUntil(() => !rig.Debt.IsOwed, Bound), "The owner never put the readable settings into effect.");
        Assert.Equal([0], rig.HostApplied.ToArray());
    }

    [Fact]
    public async Task ApplyStoredOnly_applies_the_settings_as_stored_and_lets_a_failed_read_or_an_unusable_document_fail()
    {
        var applied = new List<int>();
        Task<VocabularyRefresh> Apply(AppSettings stored)
        {
            applied.Add(stored.HistoryRetentionDays);
            return Task.FromResult(Applied);
        }

        var settings = new LoadFailingSettings(_libraries.Fixture.Settings);
        var stored = AppSettings.CreateDefault();
        stored.HistoryRetentionDays = 0;
        _libraries.Fixture.Settings.Save(stored);

        Assert.Same(Applied, await StoredSettingsReapply.ApplyStoredOnly(settings, Apply));
        Assert.Equal([0], applied);
        Assert.Throws<InvalidOperationException>(() => { _ = StoredSettingsReapply.ApplyStoredOnly(settings, _ => null!); });

        settings.FailLoad = true;
        Assert.Throws<SqliteException>(() => { _ = StoredSettingsReapply.ApplyStoredOnly(settings, Apply); });

        settings.FailLoad = false;
        _libraries.Fixture.Settings.Set(SettingsRepository.SettingsKey, "{ this is not a settings document");
        Assert.Throws<InvalidOperationException>(() => { _ = StoredSettingsReapply.ApplyStoredOnly(settings, Apply); });
        Assert.Equal([0], applied);
    }

    [Theory]
    [InlineData("first application", true)]
    [InlineData("settlement", true)]
    [InlineData("refused Save", true)]
    [InlineData("first application", false)]
    [InlineData("settlement", false)]
    [InlineData("refused Save", false)]
    public async Task Foreground_vocabulary_recovery_never_pays_a_committed_retention_or_cleanup_switch_debt(
        string exit, bool vocabularyApplies)
    {
        using var rig = new Rig(_libraries);
        rig.ReloadedVocabulary = vocabularyApplies
            ? Applied
            : new VocabularyRefresh(VocabularyRefreshOutcome.TimedOut, VocabularyGeneration.Empty);
        rig.CurrentSettings.HistoryRetentionDays = 7;
        rig.CurrentSettings.EnableAiCleanup = true;
        rig.Fixture.Settings.Save(rig.CurrentSettings);
        var saved = rig.CurrentSettings.Clone();
        saved.HistoryRetentionDays = 0;
        saved.EnableAiCleanup = false;
        void MakeUnreadable() =>
            rig.Fixture.Settings.Set(SettingsRepository.SettingsKey, "{ this is not a settings document");

        WordPackSaveProtocolResult result;
        if (exit == "first application")
        {
            rig.AfterCommit = MakeUnreadable;
            result = await rig.Protocol.SaveAsync(rig.Request(retentionDays: 0));
        }
        else
        {
            rig.Settings.FailLoad = true;
            await rig.Protocol.SaveAsync(rig.Request(retentionDays: 0));
            await rig.NextTimerAsync();
            rig.Settings.FailLoad = false;
            MakeUnreadable();
            result = exit == "settlement"
                ? await rig.Protocol.TrySettlePendingAsync(rig.Request(0))
                : await rig.Protocol.SaveAsync(rig.Request(0) with { Validate = () => ["injected validation refusal"] });
        }

        var retry = await rig.NextTimerAsync();
        Assert.False(result.Success);
        if (exit != "refused Save")
        {
            Assert.Contains("still using the previous settings", result.Message, StringComparison.Ordinal);
        }

        Assert.True(rig.Debt.IsOwed);
        Assert.Equal(1, rig.VocabularyReloads);
        Assert.Equal(7, rig.CurrentSettings.HistoryRetentionDays);
        Assert.True(rig.CurrentSettings.EnableAiCleanup);
        Assert.Empty(rig.WindowApplied);
        Assert.Equal(exit == "first application" ? 1 : 2,
            rig.Reports.Count(report => report.Step == SettingsApplicationStep.Apply));

        rig.Fixture.Settings.Save(saved);
        retry.Fire();
        Assert.True(SpinWait.SpinUntil(() => !rig.Debt.IsOwed, Bound), "Vocabulary-only recovery stopped the full-settings retry.");
        Assert.Equal(0, rig.CurrentSettings.HistoryRetentionDays);
        Assert.False(rig.CurrentSettings.EnableAiCleanup);
        Assert.Equal(1, rig.Commits);
    }

    private sealed class Rig : IDisposable
    {
        private readonly SemaphoreSlim _timerMade = new(0);
        private readonly ConcurrentQueue<ManualTimer> _timers = new();

        public Rig(RealLibraries libraries)
        {
            Fixture = libraries.Fixture;
            Fixture.SaveEnabled();
            Settings = new LoadFailingSettings(Fixture.Settings);
            var service = libraries.Service(settings: Settings);
            Workspace = RealLibraries.Workspace(service.LoadCatalog());
            Store = new RealStore(service, Settings);
            Time.Created = timer =>
            {
                _timers.Enqueue(timer);
                _timerMade.Release();
            };

            // The host's side: the settings as stored, read and applied with no window, as App wires it.
            Debt = new SettingsApplicationDebt(Time, (step, failure) => Reports.Enqueue((step, failure)), Waits);
            Debt.Attach(() => StoredSettingsReapply.ApplyStoredOnly(Settings, stored =>
            {
                CurrentSettings = stored.Clone();
                HostApplied.Enqueue(stored.HistoryRetentionDays);
                return Task.FromResult(Applied);
            }));
            Protocol = new WordPackSaveProtocol(Store, Debt);
        }

        public LibraryStorageFixture Fixture { get; }

        public LoadFailingSettings Settings { get; }

        public LibraryWorkspace Workspace { get; }

        public RealStore Store { get; }

        public ManualClock Time { get; } = new();

        public SettingsApplicationDebt Debt { get; }

        public WordPackSaveProtocol Protocol { get; }

        public ConcurrentQueue<(SettingsApplicationStep Step, Exception Failure)> Reports { get; } = new();

        /// <summary>The retention stored at each moment the owner's own application ran, with no window.</summary>
        public ConcurrentQueue<int> HostApplied { get; } = new();

        /// <summary>The retention stored at each moment the window asked for the stored settings to be put into effect.</summary>
        public ConcurrentQueue<int> WindowApplied { get; } = new();

        public int Commits { get; private set; }

        public int VocabularyReloads { get; private set; }

        public VocabularyRefresh ReloadedVocabulary { get; set; } = Applied;

        public AppSettings CurrentSettings { get; private set; } = AppSettings.CreateDefault();

        public Action? AfterCommit { get; set; }

        public WordPackSaveProtocolRequest Request(int retentionDays) => new(
            Workspace,
            payload =>
            {
                Commits++;

                // The store itself, never the wrapper whose reads fail: the commit is not what is under test.
                var document = Fixture.Settings.Load();
                document.HistoryRetentionDays = retentionDays;
                document.EnableAiCleanup = false;
                Fixture.Settings.SaveBundle(document, null, null, default, payload);
                AfterCommit?.Invoke();
            },
            () => StoredSettingsReapply.Reapply(
                Settings,
                stored =>
                {
                    CurrentSettings = stored.Clone();
                    WindowApplied.Enqueue(stored.HistoryRetentionDays);
                    return Task.FromResult(Applied);
                },
                () =>
                {
                    VocabularyReloads++;
                    return Task.FromResult(ReloadedVocabulary);
                }),
            () => "draft",
            () => "draft");

        public async Task<ManualTimer> NextTimerAsync()
        {
            Assert.True(await _timerMade.WaitAsync(Bound), "The owner never made its next wait.");
            Assert.True(_timers.TryDequeue(out var timer));
            return timer!;
        }

        public void Dispose() => Debt.Dispose();
    }

    private sealed class RealStore(DictionaryLibraryService service, ISettingsRepository settings) : IWordPackSaveProtocolStore
    {
        public Task<LibraryPrepareResult> PrepareAsync(LibraryChangeSet changes) =>
            Task.FromResult(WordPackSaveSession.Prepare(service, changes));

        public Task<LibrarySaveOutcome> CompleteAsync(PreparedLibrarySave prepared) =>
            Task.FromResult(WordPackSaveSession.Complete(service, prepared));

        public Task<LibraryCatalog> LoadCatalogAsync() => Task.FromResult(WordPackSaveSession.LoadCatalog(service));

        public void CommitRepair(LibrarySavePayload payload) => settings.CommitLibraryState(payload);
    }

    /// <summary>The real settings store, except that with <see cref="FailLoad"/> set the SELECT of the stored document fails.</summary>
    private sealed class LoadFailingSettings(SettingsRepository inner) : ISettingsRepository
    {
        public bool FailLoad { get; set; }

        public bool LastLoadFailed => inner.LastLoadFailed;

        public AppSettings Load() =>
            FailLoad ? throw new SqliteException($"injected: the SELECT of the stored settings failed ({Canary})", 10) : inner.Load();

        public void Save(AppSettings settings) => inner.Save(settings);

        public AppSettings Update(Action<AppSettings> mutate) => inner.Update(mutate);

        public AppSettings Update(Action<AppSettings> mutate, long revision, out bool superseded) => inner.Update(mutate, revision, out superseded);

        public void SaveBundle(AppSettings settings, IReadOnlyList<DictionaryEntry>? dictionaryEntries, IReadOnlyList<Snippet>? snippets, long aiCleanupIntent = 0) =>
            inner.SaveBundle(settings, dictionaryEntries, snippets, aiCleanupIntent);

        public void SaveBundle(
            AppSettings settings, IReadOnlyList<DictionaryEntry>? dictionaryEntries, IReadOnlyList<Snippet>? snippets,
            ExternalIntents intents, LibrarySavePayload? libraries) =>
            inner.SaveBundle(settings, dictionaryEntries, snippets, intents, libraries);

        public void CommitLibraryState(LibrarySavePayload payload) => inner.CommitLibraryState(payload);

        public string? Get(string key) => inner.Get(key);

        public void Set(string key, string value) => inner.Set(key, value);
    }
}
