using Scribe.Core.Models;
using Scribe.Core.TextInjection;
using Xunit;

namespace Scribe.Core.Tests;

public class InjectionTextFormatterTests
{
    [Theory]
    [InlineData("WindowsTerminal")]
    [InlineData("windowsterminal")] // process-name comparison is case-insensitive
    [InlineData("pwsh")]
    [InlineData("cmd")]
    [InlineData("conhost")]
    [InlineData("mintty")]
    public void Known_terminals_are_detected(string processName) =>
        Assert.True(InjectionTextFormatter.IsTerminalProcess(processName));

    [Theory]
    [InlineData("chrome")]
    [InlineData("Code")] // IDE terminals are indistinguishable from editors; deliberately excluded
    [InlineData("WINWORD")]
    [InlineData(null)]
    [InlineData("")]
    public void Other_apps_are_not_terminals(string? processName) =>
        Assert.False(InjectionTextFormatter.IsTerminalProcess(processName));

    [Fact]
    public void Smart_mode_flattens_newlines_for_a_terminal_target()
    {
        var text = "First point.\n\nSecond point.\r\nThird point.";

        var result = InjectionTextFormatter.Apply(text, NewlineInjectionMode.SmartFlatten, "WindowsTerminal");

        Assert.Equal("First point. Second point. Third point.", result);
    }

    [Fact]
    public void Smart_mode_keeps_newlines_for_a_non_terminal_target()
    {
        var text = "First paragraph.\n\nSecond paragraph.";

        var result = InjectionTextFormatter.Apply(text, NewlineInjectionMode.SmartFlatten, "WINWORD");

        Assert.Same(text, result);
    }

    [Fact]
    public void Always_mode_flattens_regardless_of_target()
    {
        var result = InjectionTextFormatter.Apply("a\nb", NewlineInjectionMode.AlwaysFlatten, "WINWORD");

        Assert.Equal("a b", result);
    }

    [Fact]
    public void Keep_mode_never_touches_the_text()
    {
        var text = "a\nb";

        var result = InjectionTextFormatter.Apply(text, NewlineInjectionMode.KeepNewlines, "cmd");

        Assert.Same(text, result);
    }

    [Fact]
    public void Flattening_collapses_spaces_hugging_the_line_break()
    {
        var result = InjectionTextFormatter.Apply(
            "end of line.   \n   next line", NewlineInjectionMode.AlwaysFlatten, null);

        Assert.Equal("end of line. next line", result);
    }

    [Fact]
    public void Text_without_newlines_is_returned_unchanged_even_in_a_terminal()
    {
        var text = "single line of dictation";

        var result = InjectionTextFormatter.Apply(text, NewlineInjectionMode.SmartFlatten, "pwsh");

        Assert.Same(text, result);
    }

    [Fact]
    public void Leading_and_trailing_breaks_are_trimmed_when_flattening()
    {
        var result = InjectionTextFormatter.Apply("\nhello\n", NewlineInjectionMode.AlwaysFlatten, null);

        Assert.Equal("hello", result);
    }
}
