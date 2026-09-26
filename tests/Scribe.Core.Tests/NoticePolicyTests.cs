using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class NoticePolicyTests
{
    [Theory]
    [InlineData(NoticeSeverity.Success, 8)]
    [InlineData(NoticeSeverity.Information, 10)]
    public void Success_and_information_notices_auto_close(NoticeSeverity severity, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), NoticePolicy.AutoCloseAfter(severity));
    }

    [Theory]
    [InlineData(NoticeSeverity.Warning)]
    [InlineData(NoticeSeverity.Error)]
    public void Warnings_and_errors_stay_until_closed(NoticeSeverity severity)
    {
        Assert.Null(NoticePolicy.AutoCloseAfter(severity));
    }
}
