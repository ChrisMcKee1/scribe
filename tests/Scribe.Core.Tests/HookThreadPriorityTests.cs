using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Diagnostics;
using Scribe.Core.Hotkeys;

namespace Scribe.Core.Tests;

public sealed class HookThreadPriorityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Only_the_flagged_hook_thread_requests_above_normal(bool enabled)
    {
        var requested = new List<ThreadPriority>();
        HookThreadPriority.Apply(enabled ? PerfFlags.Parse(PerfFlags.HookPriorityAboveNormal) : PerfFlags.None,
            requested.Add, NullLogger.Instance);
        Assert.Equal(enabled ? new[] { ThreadPriority.AboveNormal } : [], requested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_plain_thread_observes_the_priority_selected_before_it_starts(bool enabled)
    {
        ThreadPriority? observed = null;
        var thread = new Thread(() => observed = Thread.CurrentThread.Priority) { IsBackground = true };
        HookThreadPriority.Apply(enabled ? PerfFlags.Parse(PerfFlags.HookPriorityAboveNormal) : PerfFlags.None,
            priority => thread.Priority = priority, NullLogger.Instance);

        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The plain priority-test thread did not finish.");
        Assert.Equal(enabled ? ThreadPriority.AboveNormal : ThreadPriority.Normal, observed);
    }

    [Fact]
    public void A_refused_priority_change_does_not_prevent_hook_creation()
    {
        var log = new TextInjectionFakes.CapturingLogger<HotkeyService>();
        HookThreadPriority.Apply(PerfFlags.Parse(PerfFlags.HookPriorityAboveNormal),
            _ => throw new ThreadStateException("PRIVATE-STATE"), log);

        var entry = Assert.Single(log.Entries);
        Assert.DoesNotContain("PRIVATE-STATE", entry);
        Assert.Contains("ThreadStateException", entry);
    }

    [Fact]
    public void No_higher_priority_arm_is_selected()
    {
        Assert.Equal(ThreadPriority.Normal, HookThreadPriority.Select(PerfFlags.Parse("HookPriorityHighest")));
        Assert.Equal(ThreadPriority.Normal, HookThreadPriority.Select(PerfFlags.Parse("HookPriorityTimeCritical")));
    }
}
