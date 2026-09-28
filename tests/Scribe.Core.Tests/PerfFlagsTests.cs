using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

/// <summary>
/// The performance changes of a release ship switched off and are turned on by name through SCRIBE_PERF_FLAGS. Off must
/// mean the old path, the variable is read once, and the session banner says which flags ran without echoing the value.
/// </summary>
public sealed class PerfFlagsTests
{
    private static readonly string[] Sample = ["Alpha", "Beta", "Gamma"];

    [Theory]
    [InlineData("ExactInputArray")]
    [InlineData("ReuseInjectionWorker")]
    public void Retired_input_experiments_are_not_registered(string name)
    {
        var flags = PerfFlags.Parse(name);
        Assert.DoesNotContain(name, PerfFlags.Known);
        Assert.False(flags.IsOn(name));
        Assert.Equal(1, flags.UnknownCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" ,; ")]
    public void An_unset_or_empty_variable_turns_nothing_on(string? value)
    {
        var flags = PerfFlags.Parse(value, Sample);

        Assert.Same(PerfFlags.None, flags);
        Assert.Empty(flags.On);
        Assert.Equal(0, flags.UnknownCount);
        Assert.Equal("flags=none", flags.Describe());
    }

    [Fact]
    public void Names_are_matched_without_case_in_any_order_and_listed_in_known_order()
    {
        var flags = PerfFlags.Parse("gamma; ALPHA ,\talpha", Sample);

        Assert.Equal(["Alpha", "Gamma"], flags.On);
        Assert.True(flags.IsOn("alpha"));
        Assert.True(flags.IsOn("GAMMA"));
        Assert.False(flags.IsOn("Beta"));
        Assert.Equal("flags=Alpha,Gamma", flags.Describe());
    }

    [Fact]
    public void An_unknown_name_is_counted_once_and_never_echoed()
    {
        var flags = PerfFlags.Parse("Beta, C:\\Users\\someone\\secret, delta, DELTA", Sample);

        Assert.Equal(["Beta"], flags.On);
        Assert.Equal(2, flags.UnknownCount);
        var described = flags.Describe();
        Assert.Equal("flags=Beta unknown=2", described);
        Assert.DoesNotContain("secret", described, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delta", described, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Only_unknown_names_turn_nothing_on()
    {
        var flags = PerfFlags.Parse("nothing-we-know", Sample);

        Assert.Empty(flags.On);
        Assert.Equal("flags=none unknown=1", flags.Describe());
    }

    [Fact]
    public void Every_known_flag_name_is_a_unique_code_shape()
    {
        // A name reaches the log as it is written here, so it stays a short code: letters and digits only.
        Assert.Equal(PerfFlags.Known.Count, PerfFlags.Known.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(PerfFlags.Known, name =>
        {
            Assert.InRange(name.Length, 1, 48);
            Assert.All(name, c => Assert.True(char.IsAsciiLetterOrDigit(c), name));
        });
    }

    [Fact]
    public void The_banner_says_which_flags_ran()
    {
        var root = Path.Combine(Path.GetTempPath(), "scribe-perfflags-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = new SessionIdentity("abc123", 4242, new DateTimeOffset(2026, 9, 27, 9, 30, 0, TimeSpan.Zero));
            var paths = new AppPaths(root);

            var unset = SessionBanner.Compose(session, "0.5.1", InstallChannel.Packaged, paths, AppSettings.CreateDefault());
            Assert.Contains("perf: flags=none", unset);

            var on = SessionBanner.Compose(
                session, "0.5.1", InstallChannel.Packaged, paths, AppSettings.CreateDefault(),
                perfFlags: PerfFlags.Parse("beta, whatever", Sample));
            Assert.Contains("perf: flags=Beta unknown=1", on);
            Assert.DoesNotContain(on, line => line.Contains("whatever", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void The_flags_are_read_once_for_the_whole_process()
    {
        var code = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Scribe.Core", "DependencyInjection", "CoreServiceCollectionExtensions.cs"));

        Assert.Contains("services.AddSingleton(_ => Scribe.Core.Diagnostics.PerfFlags.FromEnvironment());", code, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Scribe.slnx")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
