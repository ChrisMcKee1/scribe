using Scribe.Core.TextInjection;

namespace Scribe.Core.Tests;

public sealed class InjectionLabValidationTests
{
    [Theory]
    [InlineData("one\n", "one")]
    [InlineData("one", "one\n")]
    [InlineData("one\r\n\r\n", "one\n")]
    public void Strict_comparison_never_discards_terminal_line_breaks(string expected, string actual)
    {
        Assert.True(InjectionLabValidation.Matches(expected, actual, strict: false));
        Assert.False(InjectionLabValidation.Matches(expected, actual, strict: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Equivalent_native_line_break_forms_stay_equivalent(bool strict) =>
        Assert.True(InjectionLabValidation.Matches("one\r\ntwo\r", "one\ntwo\n", strict));

    [Theory]
    [InlineData("w0001 w0002", "w0002 w0001")]
    [InlineData("w0001 w0002", "w0001 w0001 w0002")]
    [InlineData("w0001 w0002 ", "w0001 w0002")]
    public void Strict_comparison_detects_reorder_duplicate_and_missing_space(string expected, string actual) =>
        Assert.False(InjectionLabValidation.Matches(expected, actual, strict: true));

    [Theory]
    [InlineData(true, 0, 2, true)]
    [InlineData(true, 2, 0, false)]
    [InlineData(true, 0, 1, false)]
    [InlineData(false, 2, 0, true)]
    [InlineData(false, 0, 2, false)]
    public void The_arm_must_match_the_observed_enter_modifiers(bool shifted, int plain, int shift, bool matches) =>
        Assert.Equal(matches, InjectionLabValidation.EnterCountsMatch("one\r\n\r\ntwo", shifted, plain, shift));

    [Fact]
    public void Finishing_the_producer_does_not_hide_a_later_consumer()
    {
        Assert.Equal(150.0, InjectionLabValidation.CompletedMilliseconds(1, 150));
        Assert.Equal(160.0, InjectionLabValidation.CompletedMilliseconds(160, 2));
        Assert.Null(InjectionLabValidation.CompletedMilliseconds(1, null));
        Assert.Null(InjectionLabValidation.CompletedMilliseconds(1, double.NaN));
    }

    [Fact]
    public void The_lab_reports_the_same_flag_snapshot_in_its_header_and_summary()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root.FullName, "tools", "Scribe.InjectionLab", "Program.cs"));
        Assert.Equal(1, source.Split("PerfFlags.FromEnvironment()", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, source.Split("perfFlags.Describe()", StringSplitOptions.None).Length - 1);
        Assert.Contains("NullLogger<TextInjector>.Instance, perfFlags", source);
        Assert.Contains("Report(rows, perfFlags)", source);
        Assert.Contains("private static void Report(IReadOnlyList<Row> rows, PerfFlags perfFlags)", source);
    }
}
