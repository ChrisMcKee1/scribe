using System.Reflection;
using Scribe.Core.Lifecycle;

namespace Scribe.Core.Tests;

public sealed class StartupNoticesTests
{
    [Fact]
    public void Paths_sit_on_their_own_lines()
    {
        var data = StartupNotices.DataFolderProblem(@"C:\Data\Scribe");
        var fallback = StartupNotices.TemporaryDataFolder(@"C:\Primary", @"D:\Fallback");

        Assert.Contains($"{Environment.NewLine}{Environment.NewLine}C:\\Data\\Scribe{Environment.NewLine}{Environment.NewLine}", data.Body);
        Assert.Contains($"{Environment.NewLine}C:\\Primary{Environment.NewLine}{Environment.NewLine}", fallback.Body);
        Assert.Contains($"{Environment.NewLine}D:\\Fallback{Environment.NewLine}{Environment.NewLine}", fallback.Body);
    }

    [Fact]
    public void No_parameter_is_an_exception()
    {
        foreach (var parameter in typeof(StartupNotices).GetMethods(BindingFlags.Public | BindingFlags.Static).SelectMany(method => method.GetParameters()))
        {
            Assert.False(typeof(Exception).IsAssignableFrom(parameter.ParameterType), parameter.Name);
        }
    }

    [Fact]
    public void No_notice_has_dashes_or_raw_exception_guidance()
    {
        var notices = new[]
        {
            StartupNotices.AlreadyRunning(),
            StartupNotices.DataFolderProblem(@"C:\Data"),
            StartupNotices.TemporaryDataFolder(@"C:\Primary", @"D:\Fallback"),
            StartupNotices.NewerDatabase(),
        };

        foreach (var notice in notices)
        {
            Assert.DoesNotContain('\u2013', notice.Title + notice.Body);
            Assert.DoesNotContain('\u2014', notice.Title + notice.Body);
            Assert.DoesNotContain("exception", notice.Body, StringComparison.OrdinalIgnoreCase);
        }
    }
}
