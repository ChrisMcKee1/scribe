using Scribe.Core.Lifecycle;

namespace Scribe.Core.Tests;

public sealed class StartupFailureNoticeTests
{
    private const string LogFile = @"C:\Users\someone\AppData\Local\ScribeData\logs\scribe-20260923.log";

    [Fact]
    public void The_notice_names_the_log_file_the_failure_was_written_to()
    {
        var text = StartupFailureNotice.Compose("  " + LogFile + "  ");

        Assert.StartsWith("Scribe could not start and will close.", text);
        Assert.Contains("Details were saved to the log:\n" + LogFile + "\n", text);
        Assert.EndsWith("Try restarting your PC. If this keeps happening, reinstall Scribe.", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_log_the_notice_claims_none(string? logFile)
    {
        var text = StartupFailureNotice.Compose(logFile);

        Assert.Equal("Scribe could not start and will close.\n\nTry restarting your PC. If this keeps happening, reinstall Scribe.", text);
        Assert.DoesNotContain("log", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_notice_has_no_dashes()
    {
        foreach (var text in new[] { StartupFailureNotice.Title, StartupFailureNotice.Compose(null), StartupFailureNotice.Compose(LogFile) })
        {
            Assert.DoesNotContain('\u2013', text);
            Assert.DoesNotContain('\u2014', text);
        }
    }
}
