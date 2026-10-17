using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;

namespace Scribe.Core.Tests;

public sealed class ApplicationRestartRegistrationTests
{
    [Fact]
    public void Registers_with_restarted_switch_and_no_crash_or_hang_flags()
    {
        string? capturedCommandLine = null;
        uint capturedFlags = 0;

        var ok = ApplicationRestartRegistration.TryRegisterForUpdateRestart(
            isIsolatedRoot: false,
            NullLogger.Instance,
            (commandLine, flags) =>
            {
                capturedCommandLine = commandLine;
                capturedFlags = flags;
                return 0;
            });

        Assert.True(ok);
        Assert.Equal(ApplicationRestartRegistration.RestartCommandLine, capturedCommandLine);
        Assert.Equal(
            ApplicationRestartRegistration.RestartNoCrash | ApplicationRestartRegistration.RestartNoHang,
            capturedFlags);
        Assert.True(ApplicationRestartRegistration.HasRestartedSwitch(["--restarted"]));
        Assert.True(ApplicationRestartRegistration.HasRestartedSwitch(["--RESTARTED"]));
        Assert.False(ApplicationRestartRegistration.HasRestartedSwitch(["--settings"]));
        Assert.False(ApplicationRestartRegistration.HasRestartedSwitch(null));
    }

    [Fact]
    public void Skips_registration_for_isolated_data_folder()
    {
        var called = false;

        var ok = ApplicationRestartRegistration.TryRegisterForUpdateRestart(
            isIsolatedRoot: true,
            NullLogger.Instance,
            (_, _) =>
            {
                called = true;
                return 0;
            });

        Assert.False(ok);
        Assert.False(called);
    }

    [Fact]
    public void Returns_false_without_throwing_when_win32_returns_error_or_throws()
    {
        Assert.False(ApplicationRestartRegistration.TryRegisterForUpdateRestart(
            isIsolatedRoot: false,
            NullLogger.Instance,
            (_, _) => unchecked((int)0x80070057)));

        Assert.False(ApplicationRestartRegistration.TryRegisterForUpdateRestart(
            isIsolatedRoot: false,
            NullLogger.Instance,
            (_, _) => throw new InvalidOperationException("simulated failure")));
    }
}
