using System.Text.RegularExpressions;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

/// <summary>
/// StartupStageTiming: the startup and Settings-open stage lines carry fixed ASCII codes and whole milliseconds from one
/// anchor, nothing else, and marking never throws on the startup path.
/// </summary>
public sealed class StageTimelineTests
{
    private sealed class FakeClock(long start)
    {
        public long Now { get; set; } = start;

        public long Read() => Now;
    }

    [Fact]
    public void Each_mark_is_its_code_and_whole_milliseconds_after_the_one_anchor()
    {
        // 10,000 ticks a second: one tick is 0.1 ms.
        var clock = new FakeClock(500_000);
        var stages = StageTimeline.Start(clock.Read, 10_000);
        clock.Now += 412; // 41.2 ms
        stages.Mark("velopack");
        clock.Now += 9; // 42.1 ms
        stages.Mark("app");
        clock.Now += 12_345; // 1,276.6 ms
        stages.Mark("started");

        Assert.Equal(500_000, stages.Anchor);
        Assert.Equal(3, stages.Count);
        Assert.Equal("velopack=41 app=42 started=1276", stages.Describe());
    }

    [Fact]
    public void An_offset_moves_every_stage_to_an_earlier_anchor()
    {
        var clock = new FakeClock(0);
        var stages = StageTimeline.Start(clock.Read, 1_000);
        clock.Now = 5;
        stages.Mark("paths");
        clock.Now = 60;
        stages.Mark("hook");

        Assert.Equal("paths=45 hook=100", stages.Describe(offsetMilliseconds: 40));
    }

    [Fact]
    public void Nothing_marked_describes_as_nothing()
    {
        Assert.Equal(string.Empty, StageTimeline.Start(new FakeClock(7).Read, 1_000).Describe());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Paths")]
    [InlineData("host start")]
    [InlineData("host-start")]
    [InlineData("stage_one")]
    [InlineData("seventeenletters1")]
    [InlineData("caf\u00e9")]
    [InlineData("C:\\Users")]
    public void A_code_that_is_not_short_lowercase_ASCII_is_dropped_and_never_throws(string? code)
    {
        var clock = new FakeClock(0);
        var stages = StageTimeline.Start(clock.Read, 1_000);
        clock.Now = 3;

        stages.Mark(code!);
        stages.Mark("ok");

        Assert.False(StageTimeline.IsCode(code));
        Assert.Equal("ok=3", stages.Describe());
    }

    [Fact]
    public void Marks_past_the_limit_are_dropped()
    {
        var clock = new FakeClock(0);
        var stages = StageTimeline.Start(clock.Read, 1_000);
        for (var i = 0; i < StageTimeline.MaxMarks + 5; i++)
        {
            clock.Now = i;
            stages.Mark("s" + i);
        }

        Assert.Equal(StageTimeline.MaxMarks, stages.Count);
        Assert.EndsWith($"s{StageTimeline.MaxMarks - 1}={StageTimeline.MaxMarks - 1}", stages.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_description_holds_only_codes_equals_signs_digits_and_spaces()
    {
        var clock = new FakeClock(123);
        var stages = StageTimeline.Start(clock.Read, 10_000_000);
        foreach (var code in new[] { "velopack", "app", "resources", "mutex", "paths", "build", "started" })
        {
            clock.Now += 1_234_567;
            stages.Mark(code);
        }

        Assert.Matches(new Regex("^[a-z0-9]+=[0-9]+( [a-z0-9]+=[0-9]+)*$"), stages.Describe());
        Assert.Matches(new Regex("^[a-z0-9]+=[0-9]+( [a-z0-9]+=[0-9]+)*$"), stages.Describe(offsetMilliseconds: 57));
    }

    [Fact]
    public void The_anchor_is_placed_after_the_process_creation_by_the_wall_clock()
    {
        var clock = new FakeClock(0);
        var stages = StageTimeline.Start(clock.Read, 1_000);
        clock.Now = 2_000; // two seconds after Main

        var created = new DateTime(2026, 9, 28, 9, 7, 8, 45, DateTimeKind.Utc);
        var now = created.AddMilliseconds(2_041); // Windows says the process was created 2,041 ms ago

        Assert.Equal(41, stages.AnchorAfter(created, now));
    }

    [Fact]
    public void An_implausible_creation_time_gives_no_offset()
    {
        var clock = new FakeClock(0);
        var stages = StageTimeline.Start(clock.Read, 1_000);
        clock.Now = 5_000;
        var now = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

        // Created after the anchor (a clock set back), or days before it (a clock set forward).
        Assert.Null(stages.AnchorAfter(now.AddMilliseconds(-1_000), now));
        Assert.Null(stages.AnchorAfter(now.AddDays(-2), now));
    }

    [Fact]
    public void The_real_clock_timeline_counts_forward()
    {
        var stages = StageTimeline.Start();
        stages.Mark("first");
        Thread.Sleep(20);
        stages.Mark("second");

        var parts = stages.Describe().Split(' ').Select(p => long.Parse(p.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(2, parts.Length);
        Assert.True(parts[1] >= parts[0] + 15, stages.Describe());
    }
}
