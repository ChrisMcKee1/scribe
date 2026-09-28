using Scribe.Core.Audio;
using Scribe.Core.Diagnostics;
using Scribe.Core.Tests.Concurrency;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// <see cref="PerfFlags.WarmManagedAudioPath"/>: the warm-up runs only with the flag on and only once per process (the
/// app's one instance), logs numbers only, and a failure is swallowed, logged by its shape and not tried again.
/// </summary>
public sealed class ManagedAudioPathWarmupTests
{
    private static readonly PerfFlags WarmOn = PerfFlags.Parse(PerfFlags.WarmManagedAudioPath);

    [Fact]
    public void With_the_flag_off_nothing_runs_and_nothing_is_logged()
    {
        var runs = 0;
        var log = new CapturingLogger<ManagedAudioPathWarmup>();
        var warmup = new ManagedAudioPathWarmup(PerfFlags.None, log, () => runs++);

        Assert.False(warmup.RunOnce());

        Assert.Equal(0, runs);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void With_the_flag_on_it_runs_once_and_logs_numbers_only()
    {
        var runs = 0;
        var log = new CapturingLogger<ManagedAudioPathWarmup>();
        var warmup = new ManagedAudioPathWarmup(WarmOn, log, () => runs++);

        Assert.True(warmup.RunOnce());
        Assert.False(warmup.RunOnce());

        Assert.Equal(1, runs);
        var line = Assert.Single(log.Entries);
        Assert.Equal("Managed audio path warmed in {ElapsedMs:F1} ms.", line.Value("{OriginalFormat}"));
        Assert.IsType<double>(line.Value("ElapsedMs"));
    }

    [Fact]
    public void The_real_steps_run_to_the_end()
    {
        // The conversion of silence and the seam planner, as the app runs them: nothing to load, nothing kept.
        var log = new CapturingLogger<ManagedAudioPathWarmup>();

        Assert.True(new ManagedAudioPathWarmup(WarmOn, log).RunOnce());

        Assert.Equal("Managed audio path warmed in {ElapsedMs:F1} ms.", Assert.Single(log.Entries).Value("{OriginalFormat}"));
    }

    [Fact]
    public void A_failure_is_swallowed_logged_by_its_shape_and_not_tried_again()
    {
        const string FailureText = "A step failed on user text.";
        var runs = 0;
        var log = new CapturingLogger<ManagedAudioPathWarmup>();
        var warmup = new ManagedAudioPathWarmup(WarmOn, log, () =>
        {
            runs++;
            throw new InvalidOperationException(FailureText);
        });

        Assert.False(warmup.RunOnce());
        Assert.False(warmup.RunOnce());

        Assert.Equal(1, runs);
        var line = Assert.Single(log.Entries);
        Assert.Equal(
            "Warming the managed audio path failed ({Failure}); the first dictation compiles it instead.",
            line.Value("{OriginalFormat}"));
        Assert.Null(line.Exception);
        Assert.DoesNotContain(FailureText, line.Message, StringComparison.Ordinal);
    }
}
