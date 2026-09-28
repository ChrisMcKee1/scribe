using System.Net;
using Microsoft.Extensions.Logging;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;
using Scribe.Core.Vocabulary;
using static Scribe.Core.Tests.Vocabulary.TestVocabularies;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// PLAT-R-01: the attempt log runs in the attempt's finally, over a result, a failure or a cancellation, so a logger that
/// fails there (at IsEnabled, or when writing the attempt line) must change nothing a caller sees. Each case runs twice
/// through the production service, transport and hand-off, once with a healthy logger and once with one that fails, and
/// the two outcomes must be the same: a successful cleanup, a failed one, a request held back at the hand-off, and a
/// cancellation by the caller. Nothing leaves the process.
/// </summary>
public sealed class CleanupAttemptLogResilienceTests
{
    private const string Dictated = "please ask about the harbour lanterns today";
    private const string LibraryId = "kestrelmoor-private";

    private static readonly LibraryVocabulary Permitted = Of(1, new Library(LibraryId, H1, true, Entry("zeb ra quill", "Zebraquill")));
    private static readonly LibraryVocabulary Revoked = Of(2, new Library(LibraryId, H1, false, Entry("zeb ra quill", "Zebraquill")));

    public enum Scenario
    {
        Success,
        Failure,
        HeldBack,
        CallerCancellation,
    }

    public enum Failing
    {
        IsEnabled,
        Log,
    }

    public static TheoryData<Scenario, Failing> Cases()
    {
        var cases = new TheoryData<Scenario, Failing>();
        foreach (var scenario in Enum.GetValues<Scenario>())
        {
            foreach (var failing in Enum.GetValues<Failing>())
            {
                cases.Add(scenario, failing);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_logger_that_fails_in_the_attempt_log_changes_nothing_the_caller_sees(Scenario scenario, Failing failing)
    {
        var healthy = await RunAsync(scenario, logger: null);
        var failingLogger = new FailingLogger(failing);

        var withFailingLogger = await RunAsync(scenario, failingLogger);

        Assert.Equal(healthy, withFailingLogger);
        Assert.True(failingLogger.Failures > 0, "The logger never failed, so this proved nothing.");
    }

    [Fact]
    public async Task The_healthy_runs_are_the_outcomes_each_case_is_about()
    {
        Assert.StartsWith("result Unchanged|", await RunAsync(Scenario.Success, logger: null), StringComparison.Ordinal);
        Assert.StartsWith("result Failed|", await RunAsync(Scenario.Failure, logger: null), StringComparison.Ordinal);
        Assert.StartsWith("result Skipped|", await RunAsync(Scenario.HeldBack, logger: null), StringComparison.Ordinal);
        Assert.Contains("Canceled", await RunAsync(Scenario.CallerCancellation, logger: null), StringComparison.Ordinal);
    }

    // What the caller of CleanAsync sees: the result's shape and text, or the exception's type.
    private static async Task<string> RunAsync(Scenario scenario, FailingLogger? logger)
    {
        var source = new TestVocabularySource(Permitted);
        await using var harness = new VocabularyCleanupHarness(source, serviceLog: logger);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom());
        logger?.Arm();

        using var caller = new CancellationTokenSource();
        switch (scenario)
        {
            case Scenario.Failure:
                harness.Network.Respond = (request, _) => Task.FromResult(request.IsProbe
                    ? CanaryNetwork.Echo(request)
                    : CanaryNetwork.Json(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"down\"}}"));
                break;
            case Scenario.HeldBack:
                source.Publish(Revoked);
                break;
            case Scenario.CallerCancellation:
                harness.Network.Respond = async (request, ct) =>
                {
                    if (!request.IsProbe)
                    {
                        await caller.CancelAsync();
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }

                    return CanaryNetwork.Echo(request);
                };
                break;
        }

        try
        {
            var result = scenario == Scenario.HeldBack
                ? await harness.Service.Admit(GenerationOf(Permitted).Cleanup).CleanAsync(Dictated, caller.Token)
                : await harness.Service.CleanAsync(Dictated, caller.Token);
            return $"result {result.Outcome}|{result.Text}|{result.FailureReason}|{result.SkipReason}";
        }
        catch (Exception ex)
        {
            return $"threw {(ex is OperationCanceledException ? "Canceled" : ex.GetType().Name)}";
        }
    }

    private static VocabularyGeneration GenerationOf(LibraryVocabulary libraries) =>
        new(libraries.Generation, [], libraries, CompiledDictionaryRules.Empty);

    // Healthy until armed (after cleanup is ready); then it throws where the case says, and nowhere else.
    private sealed class FailingLogger(Failing failing) : ILogger<TextCleanupService>
    {
        private volatile bool _armed;
        private int _failures;

        public int Failures => Volatile.Read(ref _failures);

        public void Arm() => _armed = true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
        {
            if (_armed && failing == Failing.IsEnabled)
            {
                Interlocked.Increment(ref _failures);
                throw new IOException("Synthetic logger failure at IsEnabled");
            }

            return true;
        }

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (_armed && failing == Failing.Log &&
                formatter(state, exception).StartsWith("AI cleanup attempt:", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _failures);
                throw new IOException("Synthetic logger failure while writing");
            }
        }
    }
}
