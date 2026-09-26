using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Tests.Libraries.Storage;
using Scribe.Core.Tests.StorageTime;

namespace Scribe.Core.Tests.Libraries.Integration;

/// <summary>
/// The bounded background retry after a hold-back (contract 9.5 and 9.1 step 9), through the real library service and
/// the real storage maintenance, each on its own manual clock: a fresh process that cannot list the pending manifests
/// holds every library back (J's A15), and until this retry the libraries came back only at the next publishing
/// recovery, whose idle backstop is the hourly pass. Nothing here sleeps; the clocks move and the timers fire when the
/// test says so.
/// </summary>
public sealed class LibraryRecoveryRetryTests : IDisposable
{
    private static readonly StorageMaintenanceOptions Options = StorageMaintenanceOptions.Default;

    private readonly LibraryStorageFixture _fixture = new();
    private readonly ManualTimeProvider _maintenanceClock = new(LibraryStorageFixture.Start);

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 300)]
    [InlineData(6, 300)]
    [InlineData(40, 300)]
    public void The_retry_doubles_from_the_minimum_spacing_up_to_five_minutes(int retry, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), LibraryRecoveryRetry.DelayBefore(retry));
        Assert.Equal(Options.MinimumSpacing, LibraryRecoveryRetry.FirstDelay);
        Assert.InRange(LibraryRecoveryRetry.DelayBefore(retry), Options.MinimumSpacing, LibraryRecoveryRetry.MaximumDelay);
    }

    [Fact]
    public void A_hold_back_asks_for_a_pass_at_once_and_ends_at_the_first_pass_that_can_list_the_journal()
    {
        var (service, files) = HeldBackStart();
        using var maintenance = Started(service);
        var pass = _maintenanceClock.SingleTimer;
        Assert.Equal(Options.InitialDelay, pass.DueTime);

        // The publication holds everything back and asks for a pass through maintenance's trigger, sooner than its first.
        Assert.Empty(service.Current.Entries);
        Assert.True(service.Janitor.Retry.Pending);
        Assert.Equal(Options.TriggerDelay, pass.DueTime);
        var retry = _fixture.Time.SingleTimer;
        Assert.Equal(LibraryRecoveryRetry.FirstDelay, retry.DueTime);

        // The failure was transient: that pass's library step lists the journal, and the libraries come back.
        files.EnumerateFault = null;
        RunPass(pass);

        Assert.Contains(service.Current.Entries, entry => entry.Replacement == "Kubernetes");
        Assert.Contains("team", service.Current.AiScope.PermittedLibraryIds);
        Assert.False(service.Janitor.Retry.Pending);
        Assert.Null(retry.DueTime);
        Assert.Equal(0, service.Janitor.Retry.Retries);
    }

    [Fact]
    public void A_hold_back_that_persists_asks_again_on_a_bounded_backoff_never_sooner_than_the_minimum_spacing()
    {
        var (service, _) = HeldBackStart();
        using var maintenance = Started(service);
        var pass = _maintenanceClock.SingleTimer;
        Assert.Empty(service.Current.Entries);
        var retry = _fixture.Time.SingleTimer;

        int[] expected = [30, 60, 120, 240, 300, 300, 300];
        for (var i = 0; i < expected.Length; i++)
        {
            // Each requested pass runs the library step, which still cannot list the journal: nothing comes back.
            RunPass(pass);
            Assert.Empty(service.Current.Entries);
            Assert.Equal(Options.Interval, pass.DueTime);

            Assert.Equal(TimeSpan.FromSeconds(expected[i]), retry.DueTime);
            Advance(retry.DueTime!.Value);
            retry.Fire();

            // The retry asks for a pass of its own, which maintenance brings forward from the hourly one to its
            // trigger delay and never closer than its minimum spacing after the pass before.
            Assert.Equal(i + 1, service.Janitor.Retry.Retries);
            Assert.InRange(pass.DueTime!.Value, TimeSpan.Zero, Options.MinimumSpacing);
            Assert.True(service.Janitor.Retry.Pending);
        }
    }

    [Fact]
    public void A_load_that_ends_the_hold_back_in_between_stops_the_retries_and_a_later_hold_back_starts_afresh()
    {
        var (service, files) = HeldBackStart();
        using var maintenance = Started(service);
        Assert.Empty(service.Current.Entries);
        var retry = _fixture.Time.SingleTimer;
        var fault = files.EnumerateFault;

        // One retry while the hold-back lasts.
        Advance(retry.DueTime!.Value);
        retry.Fire();
        Assert.Equal(1, service.Janitor.Retry.Retries);
        Assert.Equal(LibraryRecoveryRetry.DelayBefore(2), retry.DueTime);

        // Settings opens once the listing works again: its load publishes the libraries, and the retries end.
        files.EnumerateFault = null;
        Assert.Equal(LibraryFileState.Available, service.LoadCatalog().Find("team")!.State);

        Assert.False(service.Janitor.Retry.Pending);
        Assert.Null(retry.DueTime);
        Assert.Equal(0, service.Janitor.Retry.Retries);

        // A tick the timer had already queued asks for nothing.
        Advance(LibraryRecoveryRetry.DelayBefore(2));
        retry.Fire();
        Assert.Equal(0, service.Janitor.Retry.Retries);
        Assert.Contains(service.Current.Entries, entry => entry.Replacement == "Kubernetes");

        // A later hold-back is a new one: it asks at once and starts again from the first delay.
        files.EnumerateFault = fault;
        Assert.Equal(LibraryFileState.AwaitingRelease, service.LoadCatalog().Find("team")!.State);
        Assert.True(service.Janitor.Retry.Pending);
        Assert.Equal(0, service.Janitor.Retry.Retries);
        Assert.Equal(LibraryRecoveryRetry.FirstDelay, retry.DueTime);
    }

    [Fact]
    public void Shutdown_with_a_retry_pending_asks_for_nothing_after_it()
    {
        var (service, _) = HeldBackStart();
        var maintenance = Started(service);
        var pass = _maintenanceClock.SingleTimer;
        Assert.Empty(service.Current.Entries);
        var retry = _fixture.Time.SingleTimer;
        Assert.NotNull(retry.DueTime);

        Assert.True(maintenance.Stop(TimeSpan.FromSeconds(3)));

        Assert.False(service.Janitor.Retry.Pending);
        Assert.True(retry.Disposed);

        // A tick already queued, and a publication that still holds everything back, ask for no pass, and none runs.
        Advance(LibraryRecoveryRetry.FirstDelay);
        retry.Fire();
        service.LoadCatalog();
        Assert.False(service.Janitor.Retry.Pending);
        Assert.Equal(0, service.Janitor.Retry.Retries);
        _maintenanceClock.Advance(Options.TriggerDelay);
        pass.Fire();
        Assert.Null(maintenance.LastReport);
        maintenance.Dispose();
    }

    [Fact]
    public void A_heavy_backoff_in_force_does_not_delay_the_retry()
    {
        var (service, files) = HeldBackStart();

        // Foreground activity lands during the first pass's light steps, as a dictation's write would.
        var yields = 0;
        var history = new HookedHistory(new HistoryRepository(_fixture.Database))
        {
            OnClearAudio = () =>
            {
                if (yields++ == 0)
                {
                    _fixture.Database.RequestYield();
                }
            },
        };
        using var maintenance = Started(service, history);
        var pass = _maintenanceClock.SingleTimer;
        Assert.Empty(service.Current.Entries);
        var retry = _fixture.Time.SingleTimer;

        // That pass stopped before its library step, and its own follow-up waits out the heavy-work backoff.
        RunPass(pass);
        var first = maintenance.LastReport!;
        Assert.True(first.Yielded);
        Assert.Equal(Options.ConversionQuietPeriod, first.RetryIn);
        Assert.Equal(Options.ConversionQuietPeriod, pass.DueTime);
        Assert.Empty(service.Current.Entries);

        // The retry comes at its own time and brings the next pass forward, well inside that backoff.
        files.EnumerateFault = null;
        Advance(retry.DueTime!.Value);
        retry.Fire();
        Assert.Equal(1, service.Janitor.Retry.Retries);
        Assert.True(pass.DueTime < Options.ConversionQuietPeriod);

        // A light-only pass: heavy work is still deferred, and the library step restores the libraries.
        RunPass(pass);
        var second = maintenance.LastReport!;
        Assert.True(second.HeavyDeferred);
        Assert.Contains(service.Current.Entries, entry => entry.Replacement == "Kubernetes");
        Assert.False(service.Janitor.Retry.Pending);
    }

    // A committed library, then a fresh process that cannot list the pending manifests (J's A15): it cannot tell whether
    // a Save of the stored generation is still pending, so every library is held back until a listing works.
    private (DictionaryLibraryService Service, FaultingFileSystem Files) HeldBackStart()
    {
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.SaveEnabled("team");
        Assert.Equal(1, Real().LoadCatalog().Generation);
        _fixture.Restart();
        var files = new FaultingFileSystem
        {
            EnumerateFault = (directory, pattern) =>
                IsJournal(directory) && pattern.EndsWith(LibraryJournalNames.ManifestSuffix, StringComparison.OrdinalIgnoreCase)
                    ? FaultingFileSystem.SharingViolation()
                    : null,
        };
        return (Real(files), files);
    }

    private DictionaryLibraryService Real(ILibraryFileSystem? files = null) =>
        _fixture.Service(files, composer: LibraryComposer.Instance, overlay: BuiltInLibraryOverlay.Instance);

    private StorageMaintenance Started(DictionaryLibraryService service, IHistoryMaintenance? history = null)
    {
        var maintenance = new StorageMaintenance(
            _fixture.Database, history ?? new HistoryRepository(_fixture.Database), new CleanupFailureLog(_fixture.Database),
            NullLogger.Instance, _maintenanceClock, Options, service.Janitor);
        maintenance.Start(AppSettings.CreateDefault);
        return maintenance;
    }

    // Both clocks stand for the same wall time.
    private void Advance(TimeSpan by)
    {
        _fixture.Time.Advance(by);
        _maintenanceClock.Advance(by);
    }

    private void RunPass(ManualTimer pass)
    {
        Assert.NotNull(pass.DueTime);
        Advance(pass.DueTime!.Value);
        pass.Fire();
    }

    private bool IsJournal(string directory) =>
        string.Equals(
            Path.GetFullPath(directory).TrimEnd('\\'), Path.GetFullPath(_fixture.Paths.LibraryJournalDir).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
}
