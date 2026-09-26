using Scribe.Core.Lifecycle;

namespace Scribe.Core.Tests;

public sealed class FirstRunWelcomeTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void Should_show_only_for_uncompleted_first_run_without_settings_recovery(bool completed, bool recovered, bool expected)
    {
        Assert.Equal(expected, FirstRunWelcome.ShouldShow(completed, recovered));
    }
}
