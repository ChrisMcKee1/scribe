using Scribe.Core.Overlay;

namespace Scribe.Core.Tests;

/// <summary>
/// The recording indicator's level: a peak's dBFS over the window ordinary speech fills, reached fast and left more
/// slowly, timed by the audio rather than by how it is split into callbacks.
/// </summary>
public sealed class PillLevelMeterTests
{
    private static readonly TimeSpan Callback = TimeSpan.FromMilliseconds(10);

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.5f)]
    [InlineData(float.NaN)]
    [InlineData(0.000_5f)] // -66 dBFS, below the window
    public void Silence_and_a_peak_below_the_window_leave_the_bars_on_their_floor(float peak)
    {
        var meter = new PillLevelMeter();
        for (var i = 0; i < 100; i++)
        {
            meter.Update(peak, Callback);
        }

        Assert.Equal(0, meter.Level);
    }

    [Fact]
    public void The_window_runs_from_the_quiet_edge_to_the_full_edge_in_dBFS()
    {
        Assert.Equal(0, PillLevelMeter.TargetOf(Linear(PillLevelMeter.QuietDbfs)), 6);
        Assert.Equal(1, PillLevelMeter.TargetOf(Linear(PillLevelMeter.FullDbfs)), 6);
        Assert.Equal(0.5, PillLevelMeter.TargetOf(Linear((PillLevelMeter.QuietDbfs + PillLevelMeter.FullDbfs) / 2)), 6);
        Assert.Equal(1, PillLevelMeter.TargetOf(1f));
        Assert.Equal(1, PillLevelMeter.TargetOf(4f)); // an overloaded device holds at full height
        Assert.Equal(1, PillLevelMeter.TargetOf(float.PositiveInfinity));
    }

    [Fact]
    public void Ordinary_speech_moves_the_bars_most_of_their_height_and_a_raised_voice_still_raises_them()
    {
        // 10 ms peaks measured on a speech fixture scaled to the median capture level in the field (RMS -26 dBFS): about
        // -17 dBFS at the median and -12 at the 90th percentile. The 0.5.0 meter showed these at 0.37 and 0.50.
        var median = PillLevelMeter.TargetOf(Linear(-17));
        var loud = PillLevelMeter.TargetOf(Linear(-12));
        var raised = PillLevelMeter.TargetOf(Linear(-6));

        Assert.InRange(median, 0.7, 0.85);
        Assert.InRange(loud, 0.8, 0.95);
        Assert.Equal(1, raised, 6);
        Assert.True(median < loud && loud < raised);
    }

    [Fact]
    public void A_quiet_microphone_still_moves_the_bars()
    {
        // Twelve dB below the field median, as a laptop array can be: peaks near -29 dBFS still show about half the travel.
        Assert.InRange(PillLevelMeter.TargetOf(Linear(-29)), 0.45, 0.6);
    }

    [Fact]
    public void A_syllable_shows_at_once_and_a_pause_lets_the_bars_fall_more_slowly()
    {
        var meter = new PillLevelMeter();
        var peak = Linear(-12);
        var target = PillLevelMeter.TargetOf(peak);

        meter.Update(peak, Callback);
        meter.Update(peak, Callback);
        meter.Update(peak, Callback);
        Assert.True(meter.Level > 0.9 * target, $"after 30 ms the level was {meter.Level:F3} of {target:F3}");

        for (var i = 0; i < 30; i++)
        {
            meter.Update(peak, Callback);
        }

        var held = meter.Level;
        meter.Update(0f, Callback);
        meter.Update(0f, Callback);
        meter.Update(0f, Callback);
        var fallen = held - meter.Level;
        Assert.InRange(fallen / held, 0.15, 0.35); // 1 - e^(-30/120) = 0.22 of the way down after 30 ms

        for (var i = 0; i < 60; i++)
        {
            meter.Update(0f, Callback);
        }

        Assert.True(meter.Level < 0.01, $"after a 630 ms pause the level was still {meter.Level:F3}");
    }

    [Fact]
    public void The_response_is_timed_by_the_audio_not_by_how_it_is_split_into_callbacks()
    {
        var tenMs = new PillLevelMeter();
        var thirtyMs = new PillLevelMeter();
        var peaks = new[] { Linear(-40), Linear(-15), Linear(-9), Linear(-30), Linear(-60), Linear(-20) };

        // The same audio delivered as three 10 ms callbacks or one 30 ms callback per peak.
        tenMs.Update(peaks[0], Callback);
        thirtyMs.Update(peaks[0], Callback);
        tenMs.Update(peaks[0], Callback);
        tenMs.Update(peaks[0], Callback);
        thirtyMs.Update(peaks[0], TimeSpan.FromMilliseconds(20));
        foreach (var peak in peaks.Skip(1))
        {
            tenMs.Update(peak, Callback);
            tenMs.Update(peak, Callback);
            tenMs.Update(peak, Callback);
            thirtyMs.Update(peak, TimeSpan.FromMilliseconds(30));
        }

        Assert.Equal(tenMs.Level, thirtyMs.Level, 9);
    }

    [Fact]
    public void A_stalled_device_that_resumes_moves_the_level_no_further_than_250_ms_would()
    {
        var stalled = new PillLevelMeter();
        var bounded = new PillLevelMeter();
        for (var i = 0; i < 20; i++)
        {
            stalled.Update(Linear(-10), Callback);
            bounded.Update(Linear(-10), Callback);
        }

        stalled.Update(Linear(-40), TimeSpan.FromSeconds(30));
        bounded.Update(Linear(-40), TimeSpan.FromMilliseconds(PillLevelMeter.MaxStepMs));

        Assert.Equal(bounded.Level, stalled.Level, 12);
    }

    [Fact]
    public void Reset_starts_again_from_silence_and_times_the_first_piece_as_one_callback()
    {
        var meter = new PillLevelMeter();
        for (var i = 0; i < 20; i++)
        {
            meter.Update(Linear(-8), Callback);
        }

        meter.Reset();
        Assert.Equal(0, meter.Level);

        // The first update after a reset ignores the time it is handed, which spans the gap since the last recording.
        var first = meter.Update(Linear(-12), TimeSpan.FromMinutes(5));
        var fresh = new PillLevelMeter().Update(Linear(-12), Callback);
        Assert.Equal(fresh, first, 12);
    }

    [Fact]
    public void A_louder_peak_never_leaves_the_bars_lower()
    {
        var previous = -1.0;
        for (var dbfs = -80.0; dbfs <= 6; dbfs += 0.5)
        {
            var target = PillLevelMeter.TargetOf(Linear(dbfs));
            Assert.InRange(target, 0, 1);
            Assert.True(target >= previous, $"{dbfs} dBFS gave {target} after {previous}");
            previous = target;
        }
    }

    private static float Linear(double dbfs) => (float)Math.Pow(10, dbfs / 20);
}
