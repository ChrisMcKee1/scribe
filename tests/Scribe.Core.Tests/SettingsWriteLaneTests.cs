using System.Collections.Concurrent;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// The lane the tray's AI cleanup switch saves through. The test thread plays the UI thread: it submits, applies the
/// Settings window's saves, and runs the callbacks the lane posts back, one at a time, so every interleaving is chosen by
/// the test rather than by timing.
/// </summary>
public sealed class SettingsWriteLaneTests
{
    [Fact]
    public void Changes_are_written_one_at_a_time_in_submission_order_off_the_owner_thread()
    {
        var repository = new ScriptedSettingsRepository();
        var owner = new OwnerThread();
        var lane = new SettingsWriteLane(repository, owner.Post);
        var delivered = new List<int>();
        var firstWrite = repository.HoldNextUpdate();

        for (var minutes = 1; minutes <= 3; minutes++)
        {
            var value = minutes;
            Assert.True(lane.Submit(
                stored => stored.ReleaseModelsAfterIdleMinutes = value,
                saved => delivered.Add(saved.ReleaseModelsAfterIdleMinutes),
                error => throw error));
        }

        firstWrite.WaitUntilEntered();
        firstWrite.Release();
        owner.RunNext(3);

        Assert.Equal(new[] { 1, 2, 3 }, delivered);
        Assert.Equal(new[] { 1, 2, 3 }, repository.UpdateResults);
        Assert.Equal(1, repository.MaxConcurrentUpdates);
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, repository.UpdateThreads);
        Assert.Equal(0, repository.Loads); // nothing interleaved, so nothing was read back
        Assert.Equal(3, repository.Stored.ReleaseModelsAfterIdleMinutes);
    }

    [Fact]
    public void A_failed_write_is_reported_in_its_place_and_the_changes_behind_it_still_run()
    {
        var repository = new ScriptedSettingsRepository();
        var owner = new OwnerThread();
        var lane = new SettingsWriteLane(repository, owner.Post);
        var events = new List<string>();
        repository.FailUpdateNumber(2, new InvalidOperationException("disk full"));

        foreach (var minutes in new[] { 1, 2, 3 })
        {
            lane.Submit(
                stored => stored.ReleaseModelsAfterIdleMinutes = minutes,
                saved => events.Add($"saved {saved.ReleaseModelsAfterIdleMinutes}"),
                error => events.Add($"failed {minutes}: {error.Message}"));
        }

        owner.RunNext(3);

        Assert.Equal(new[] { "saved 1", "failed 2: disk full", "saved 3" }, events);
        Assert.Equal(3, repository.Stored.ReleaseModelsAfterIdleMinutes);
    }

    [Fact]
    public void A_window_save_that_lands_after_the_change_is_never_undone_by_the_change_result()
    {
        // The lane's write commits first, then the Settings window saves its whole document (carrying a stale switch)
        // and applies it before the lane's result reaches the owner. The lane's copy is older than what the window just
        // applied, so it must not be handed back; what is stored now is.
        var repository = new ScriptedSettingsRepository();
        var owner = new OwnerThread();
        var lane = new SettingsWriteLane(repository, owner.Post);
        var delivered = new List<AppSettings>();

        lane.Submit(stored => stored.EnableAiCleanup = true, delivered.Add, error => throw error);
        var laneResult = owner.TakeNext();

        repository.SaveBundle(new AppSettings { EnableAiCleanup = false, HistoryRetentionDays = 30 }, null, null);
        lane.NoteExternalApply();

        laneResult();
        Assert.Empty(delivered);
        owner.RunNext(1);

        var applied = Assert.Single(delivered);
        Assert.False(applied.EnableAiCleanup);
        Assert.Equal(30, applied.HistoryRetentionDays);
        Assert.Equal(1, repository.Loads);
    }

    [Fact]
    public void A_window_save_that_lands_before_the_change_is_kept_together_with_it()
    {
        // The window's save commits while the lane's write is waiting, so the lane's change lands on top of it. The
        // owner already applied the window's document, which lacks the change, so what is stored now (both) is handed
        // back.
        var repository = new ScriptedSettingsRepository();
        var owner = new OwnerThread();
        var lane = new SettingsWriteLane(repository, owner.Post);
        var delivered = new List<AppSettings>();
        var write = repository.HoldNextUpdate();

        lane.Submit(stored => stored.EnableAiCleanup = true, delivered.Add, error => throw error);
        write.WaitUntilEntered();
        repository.SaveBundle(new AppSettings { EnableAiCleanup = false, HistoryRetentionDays = 30 }, null, null);
        lane.NoteExternalApply();
        write.Release();

        owner.RunNext(2);

        var applied = Assert.Single(delivered);
        Assert.True(applied.EnableAiCleanup);
        Assert.Equal(30, applied.HistoryRetentionDays);
    }

    [Fact]
    public void A_window_save_before_the_change_was_submitted_needs_no_second_read()
    {
        var repository = new ScriptedSettingsRepository();
        var owner = new OwnerThread();
        var lane = new SettingsWriteLane(repository, owner.Post);
        var delivered = new List<AppSettings>();

        repository.SaveBundle(new AppSettings { HistoryRetentionDays = 30 }, null, null);
        lane.NoteExternalApply();
        lane.Submit(stored => stored.EnableAiCleanup = true, delivered.Add, error => throw error);
        owner.RunNext(1);

        var applied = Assert.Single(delivered);
        Assert.True(applied.EnableAiCleanup);
        Assert.Equal(30, applied.HistoryRetentionDays);
        Assert.Equal(0, repository.Loads);
    }

    [Fact]
    public void A_window_save_during_the_second_read_is_answered_with_another_read_until_nothing_interleaves()
    {
        var repository = new ScriptedSettingsRepository();
        var owner = new OwnerThread();
        var lane = new SettingsWriteLane(repository, owner.Post);
        var delivered = new List<AppSettings>();

        lane.Submit(stored => stored.EnableAiCleanup = true, delivered.Add, error => throw error);
        var laneResult = owner.TakeNext();
        repository.SaveBundle(new AppSettings { EnableAiCleanup = true, HistoryRetentionDays = 30 }, null, null);
        lane.NoteExternalApply();
        laneResult();

        var firstRead = owner.TakeNext();
        repository.SaveBundle(new AppSettings { EnableAiCleanup = true, HistoryRetentionDays = 45 }, null, null);
        lane.NoteExternalApply();
        firstRead();
        Assert.Empty(delivered);

        owner.RunNext(1);

        Assert.Equal(45, Assert.Single(delivered).HistoryRetentionDays);
        Assert.Equal(2, repository.Loads);
    }

    [Fact]
    public void A_second_read_that_finds_an_unreadable_document_reports_a_failure_instead_of_handing_back_defaults()
    {
        var repository = new ScriptedSettingsRepository();
        var owner = new OwnerThread();
        var lane = new SettingsWriteLane(repository, owner.Post);
        var delivered = new List<AppSettings>();
        var failures = new List<Exception>();

        lane.Submit(stored => stored.EnableAiCleanup = true, delivered.Add, failures.Add);
        var laneResult = owner.TakeNext();
        lane.NoteExternalApply();
        repository.LoadFails = true;

        laneResult();
        owner.RunNext(1);

        Assert.Empty(delivered);
        Assert.IsType<InvalidOperationException>(Assert.Single(failures));
    }

    [Fact]
    public void A_result_callback_that_throws_is_reported_as_a_failure_and_never_reaches_the_owner()
    {
        var repository = new ScriptedSettingsRepository();
        var owner = new OwnerThread();
        var lane = new SettingsWriteLane(repository, owner.Post);
        var failures = new List<Exception>();

        lane.Submit(
            stored => stored.EnableAiCleanup = true,
            _ => throw new InvalidOperationException("apply failed"),
            failures.Add);
        owner.RunNext(1);

        Assert.Equal("apply failed", Assert.Single(failures).Message);
    }

    [Fact]
    public void Closing_admits_nothing_new_and_hands_back_nothing_but_lets_a_running_write_finish()
    {
        var repository = new ScriptedSettingsRepository();
        var owner = new OwnerThread();
        var lane = new SettingsWriteLane(repository, owner.Post);
        var callbacks = 0;
        var write = repository.HoldNextUpdate();

        lane.Submit(stored => stored.EnableAiCleanup = true, _ => callbacks++, _ => callbacks++);
        write.WaitUntilEntered();
        lane.Close();

        Assert.False(lane.Submit(stored => stored.HistoryRetentionDays = 1, _ => callbacks++, _ => callbacks++));
        Assert.False(lane.WaitForIdle(TimeSpan.Zero)); // the write is held open by this test, so it cannot be idle

        write.Release();
        Assert.True(lane.WaitForIdle(BlockedThreads.SafetyTimeout));
        owner.RunAll();

        Assert.True(repository.Stored.EnableAiCleanup);
        Assert.Equal(90, repository.Stored.HistoryRetentionDays);
        Assert.Equal(0, callbacks);
    }

    // The tray, an open Settings window and dictation around the real lane and the real repository, wired as App and
    // SettingsWindow wire them (TrayAndWindow). Each test fixes the order in which the tray's write, the window's save
    // and the results reach the database and the UI thread, then checks what is stored, what the window shows and
    // saves, and everything dictation was given.

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_saved_opt_out_is_never_undone_by_an_older_tray_write_that_was_waiting(bool windowStaysOpen)
    {
        // The tray turns cleanup on and its write waits for the database; the user turns cleanup off in Settings, saves
        // and perhaps closes the window; only then does the tray's write get the database. It used to commit on and
        // turn cleanup back on for dictation.
        using var world = new TrayAndWindow(enabledAtStart: false);
        var window = world.OpenWindow();
        var waiting = world.Settings.HoldNextCheckedUpdate();

        world.TrayToggle(true);
        waiting.WaitUntilEntered();
        Assert.True(window.Shown);

        window.UserSets(false);
        world.SaveWindow();
        if (!windowStaysOpen)
        {
            world.CloseWindow();
        }

        waiting.Release();
        world.Owner.RunNext(2); // the superseded result, then the second read the window's save calls for

        Assert.False(world.Stored());
        Assert.Equal(new[] { false, false }, world.Applied);
        Assert.False(window.Shown);
        Assert.False(window.ForSave());
        Assert.Empty(world.Failures);
    }

    [Fact]
    public void A_tray_write_that_lands_first_gives_way_to_the_opt_out_saved_after_it()
    {
        using var world = new TrayAndWindow(enabledAtStart: false);
        var window = world.OpenWindow();

        world.TrayToggle(true);
        var trayResult = world.Owner.TakeNext(); // written, and not yet back on the UI thread
        Assert.True(world.Stored());

        window.UserSets(false);
        world.SaveWindow();
        trayResult();
        world.Owner.RunNext(1); // the second read the window's save calls for

        Assert.False(world.Stored());
        Assert.Equal(new[] { false, false }, world.Applied);
        Assert.False(window.Shown);
        Assert.False(window.ForSave());
        Assert.Empty(world.Failures);
    }

    [Fact]
    public void A_tray_change_asked_for_after_a_save_is_written_while_the_one_before_it_is_superseded()
    {
        using var world = new TrayAndWindow(enabledAtStart: false);
        var window = world.OpenWindow();
        var waiting = world.Settings.HoldNextCheckedUpdate();

        world.TrayToggle(true);
        waiting.WaitUntilEntered();
        world.SaveWindow(); // carries the tray's on, which it therefore accounts for
        world.TrayToggle(false); // asked for after that save
        waiting.Release();
        world.Owner.RunNext(3); // the first change's result, the second's, then the first's second read

        Assert.False(world.Stored());
        Assert.Equal(new[] { true, false, false }, world.Applied);
        Assert.False(window.Shown);
        Assert.False(window.ForSave());
        Assert.Empty(world.Failures);
    }

    [Fact]
    public void A_tray_opt_out_asked_for_before_the_window_opened_survives_a_save_that_never_saw_it()
    {
        // Superseding a tray change on every later save would get this one wrong: the window loaded the document before
        // the tray's write landed and the user saved something else, so cleanup would have stayed on after the user
        // had turned it off. A save supersedes only the changes it accounts for.
        using var world = new TrayAndWindow(enabledAtStart: true);
        var waiting = world.Settings.HoldNextCheckedUpdate();

        world.TrayToggle(false);
        waiting.WaitUntilEntered();
        var window = world.OpenWindow();
        Assert.True(window.Shown);
        world.SaveWindow();

        waiting.Release();
        world.Owner.RunNext(2);

        Assert.False(world.Stored());
        Assert.Equal(new[] { true, false }, world.Applied);
        Assert.False(window.Shown);
        Assert.Empty(world.Failures);
    }

    [Fact]
    public void A_committed_tray_opt_out_survives_an_unrelated_save_from_a_window_that_opened_before_it()
    {
        // Cleanup is on and Settings is closed; the tray asks for off. Settings opens before that write commits, so it
        // shows on and has no intent for the switch. The write commits, and before its result reaches the UI thread the
        // user saves something else. That save used to write the window's stale on over the stored off, and the tray's
        // result then read on back and applied it.
        using var world = new TrayAndWindow(enabledAtStart: true);
        var waiting = world.Settings.HoldNextCheckedUpdate();

        world.TrayToggle(false);
        waiting.WaitUntilEntered();
        var window = world.OpenWindow();
        Assert.True(window.Shown);

        waiting.Release();
        var trayResult = world.Owner.TakeNext(); // committed, and not yet back on the UI thread
        Assert.False(world.Stored());

        world.SaveWindow();
        Assert.False(world.Stored());
        trayResult();
        world.Owner.RunNext(1); // the second read the window's save calls for

        Assert.False(world.Stored());
        Assert.Equal(new[] { false, false }, world.Applied);
        Assert.False(window.Shown);
        Assert.False(window.ForSave());
        Assert.Empty(world.Failures);
    }

    /// <summary>A stand-in for the UI thread's queue: the lane posts here and the test decides when each item runs.</summary>
    private sealed class OwnerThread
    {
        private readonly BlockingCollection<Action> _queue = new();

        public void Post(Action callback) => _queue.Add(callback);

        public Action TakeNext() =>
            _queue.TryTake(out var next, BlockedThreads.SafetyTimeout)
                ? next
                : throw new TimeoutException("The lane never posted back.");

        public void RunNext(int count)
        {
            for (var i = 0; i < count; i++)
            {
                TakeNext()();
            }
        }

        public void RunAll()
        {
            while (_queue.TryTake(out var next))
            {
                next();
            }
        }
    }

    /// <summary>A write that stays open until the test lets it finish.</summary>
    private sealed class HeldWrite
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();

        public void Enter()
        {
            _entered.Set();
            _release.Wait(BlockedThreads.SafetyTimeout);
        }

        public void WaitUntilEntered()
        {
            if (!_entered.Wait(BlockedThreads.SafetyTimeout))
            {
                throw new TimeoutException("The write never started.");
            }
        }

        public void Release() => _release.Set();
    }

    private sealed class ScriptedSettingsRepository : ISettingsRepository
    {
        private readonly object _sync = new();
        private readonly List<int> _updateResults = [];
        private readonly ConcurrentBag<int> _updateThreads = new();
        private HeldWrite? _held;
        private int _updateCount;
        private int _failUpdateNumber;
        private Exception? _failure;
        private int _running;
        private int _maxRunning;
        private int _loads;
        private AppSettings _stored = new() { HistoryRetentionDays = 90 };

        public bool LastLoadFailed { get; private set; }

        public bool LoadFails { get; set; }

        public AppSettings Stored
        {
            get { lock (_sync) { return _stored.Clone(); } }
        }

        public IReadOnlyList<int> UpdateResults
        {
            get { lock (_sync) { return [.. _updateResults]; } }
        }

        public IReadOnlyCollection<int> UpdateThreads => _updateThreads.ToArray();

        public int MaxConcurrentUpdates => Volatile.Read(ref _maxRunning);

        public int Loads => Volatile.Read(ref _loads);

        public HeldWrite HoldNextUpdate()
        {
            var held = new HeldWrite();
            lock (_sync)
            {
                _held = held;
            }

            return held;
        }

        public void FailUpdateNumber(int number, Exception failure)
        {
            lock (_sync)
            {
                _failUpdateNumber = number;
                _failure = failure;
            }
        }

        public AppSettings Update(Action<AppSettings> mutate)
        {
            _updateThreads.Add(Environment.CurrentManagedThreadId);
            var running = Interlocked.Increment(ref _running);
            InterlockedMax(ref _maxRunning, running);
            try
            {
                HeldWrite? held;
                int number;
                lock (_sync)
                {
                    held = _held;
                    _held = null;
                    number = ++_updateCount;
                }

                // Held before the read, like a writer waiting for the database's write lock.
                held?.Enter();

                lock (_sync)
                {
                    if (number == _failUpdateNumber)
                    {
                        throw _failure!;
                    }

                    var next = _stored.Clone();
                    mutate(next);
                    _stored = next;
                    _updateResults.Add(next.ReleaseModelsAfterIdleMinutes);
                    return next.Clone();
                }
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        // The lane tests here never ask for a checked change; the tray and window tests below use the real repository.
        public AppSettings Update(Action<AppSettings> mutate, long revision, out bool superseded)
        {
            superseded = false;
            return Update(mutate);
        }

        public AppSettings Load()
        {
            Interlocked.Increment(ref _loads);
            lock (_sync)
            {
                LastLoadFailed = LoadFails;
                return LoadFails ? AppSettings.CreateDefault() : _stored.Clone();
            }
        }

        public void Save(AppSettings settings)
        {
            lock (_sync)
            {
                _stored = settings.Clone();
            }
        }

        public void SaveBundle(
            AppSettings settings,
            IReadOnlyList<DictionaryEntry>? dictionaryEntries,
            IReadOnlyList<Snippet>? snippets,
            long aiCleanupIntent = 0) => Save(settings);

        public string? Get(string key) => null;

        public void Set(string key, string value)
        {
        }

        private static void InterlockedMax(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    /// <summary>
    /// The tray, one Settings window and dictation around the real lane and the real repository, wired the way App and
    /// SettingsWindow wire them. <see cref="Applied"/> is everything dictation was given, in order.
    /// </summary>
    private sealed class TrayAndWindow : IDisposable
    {
        private readonly TempDatabaseFolder _folder = new();
        private readonly ScribeDatabase _database;
        private Editor? _window;

        public TrayAndWindow(bool enabledAtStart)
        {
            _database = _folder.Open();
            var repository = new SettingsRepository(_database);
            repository.Save(new AppSettings { EnableAiCleanup = enabledAtStart });
            Settings = new HeldSettingsRepository(repository);
            Lane = new SettingsWriteLane(Settings, Owner.Post);
        }

        public HeldSettingsRepository Settings { get; }

        public OwnerThread Owner { get; } = new();

        public SettingsWriteLane Lane { get; }

        public List<bool> Applied { get; } = [];

        public List<Exception> Failures { get; } = [];

        public Editor OpenWindow() => _window = new Editor(Settings.Load().EnableAiCleanup);

        public void CloseWindow() => _window = null;

        public bool Stored() => Settings.Load().EnableAiCleanup;

        // App.ToggleAiCleanup, with OnAiCleanupToggleSaved as the result.
        public void TrayToggle(bool enabled)
        {
            var revision = ExternalSwitchSync.NextRevision();
            if (Lane.Submit(
                    stored => stored.EnableAiCleanup = enabled,
                    revision,
                    (stored, superseded) =>
                    {
                        Applied.Add(stored.EnableAiCleanup);
                        if (!superseded)
                        {
                            _window?.Adopt(stored.EnableAiCleanup, revision);
                        }
                    },
                    Failures.Add))
            {
                _window?.Adopt(enabled, revision);
            }
        }

        // SettingsWindow's save, then the apply callback App hands it.
        public void SaveWindow()
        {
            var window = _window ?? throw new InvalidOperationException("No Settings window is open.");
            var document = Settings.Load();
            document.EnableAiCleanup = window.ForSave();
            Settings.SaveBundle(document, null, null, window.Sync.NewestRevision);
            window.Sync.Saved();
            window.ShowStored(document.EnableAiCleanup);
            Lane.NoteExternalApply();
            Applied.Add(document.EnableAiCleanup);
        }

        public void Dispose()
        {
            Lane.Close();
            Lane.WaitForIdle(BlockedThreads.SafetyTimeout);
            _database.Dispose();
            _folder.Dispose();
        }
    }

    /// <summary>The Settings window's AI cleanup switch as SettingsWindow drives it, with no hotkey being recorded.</summary>
    private sealed class Editor(bool shown)
    {
        public ExternalSwitchSync Sync { get; } = new();

        public bool Shown { get; private set; } = shown;

        // AdoptExternalAiCleanup.
        public void Adopt(bool enabled, long revision)
        {
            if (Sync.TryAdopt(enabled, revision, canShowNow: true, out var showNow) && showNow)
            {
                Shown = enabled;
            }
        }

        // AiCleanupCheck_Toggled.
        public void UserSets(bool enabled)
        {
            Shown = enabled;
            Sync.UserChanged();
        }

        // After a save, the switch shows what was stored (ShowExternalAiCleanup, not a click).
        public void ShowStored(bool stored) => Shown = stored;

        public bool ForSave() => Sync.ForSave(Shown);
    }

    /// <summary>
    /// The real repository, except that the next checked change waits before it reaches the database, as a lane write
    /// waits behind another connection's write lock.
    /// </summary>
    private sealed class HeldSettingsRepository(SettingsRepository inner) : ISettingsRepository
    {
        private HeldWrite? _held;

        public bool LastLoadFailed => inner.LastLoadFailed;

        public HeldWrite HoldNextCheckedUpdate()
        {
            var held = new HeldWrite();
            Volatile.Write(ref _held, held);
            return held;
        }

        public AppSettings Load() => inner.Load();

        public void Save(AppSettings settings) => inner.Save(settings);

        public AppSettings Update(Action<AppSettings> mutate) => inner.Update(mutate);

        public AppSettings Update(Action<AppSettings> mutate, long revision, out bool superseded)
        {
            Interlocked.Exchange(ref _held, null)?.Enter();
            return inner.Update(mutate, revision, out superseded);
        }

        public void SaveBundle(
            AppSettings settings,
            IReadOnlyList<DictionaryEntry>? dictionaryEntries,
            IReadOnlyList<Snippet>? snippets,
            long aiCleanupIntent = 0) =>
            inner.SaveBundle(settings, dictionaryEntries, snippets, aiCleanupIntent);

        public string? Get(string key) => inner.Get(key);

        public void Set(string key, string value) => inner.Set(key, value);
    }
}
