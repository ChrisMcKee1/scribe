using Scribe.Core.TextInjection;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class TextInjectorTests
{
    [Theory]
    [InlineData("plain text", true)]
    [InlineData("first\r\nsecond", true)]
    [InlineData("first\r\nsecond", false)]
    [InlineData("emoji \ud83d\ude00 text", true)]
    public void Unicode_chunk_builds_the_counted_events_in_order(string text, bool shiftEnter)
    {
        var inputs = TextInjector.BuildUnicodeChunk(text, 0, text.Length, shiftEnter);

        Assert.Equal(TextInjector.CountKeyEvents(text, 0, text.Length, shiftEnter), inputs.Length);
        Assert.Equal(Replay(text, shiftEnter), Replay(inputs));
    }

    [Fact]
    public void Partial_paste_cleanup_releases_control_before_v_and_marks_both_as_scribe_input()
    {
        var inputs = TextInjector.BuildCtrlVReleaseInputs();

        Assert.Equal(2, inputs.Length);
        Assert.Equal(InjectionNativeMethods.VK_CONTROL, inputs[0].U.ki.wVk);
        Assert.Equal(InjectionNativeMethods.KEYEVENTF_KEYUP, inputs[0].U.ki.dwFlags);
        Assert.Equal(InjectionNativeMethods.VK_V, inputs[1].U.ki.wVk);
        Assert.Equal(InjectionNativeMethods.KEYEVENTF_KEYUP, inputs[1].U.ki.dwFlags);
        Assert.All(inputs, input => Assert.NotEqual((nuint)0, input.U.ki.dwExtraInfo));
    }

    [Fact]
    public void Short_send_on_plain_text_without_line_breaks_does_not_send_shift_release()
    {
        const nint Target = 0x1234;
        var platform = new TextInjectionFakes.Platform
        {
            Foreground = Target,
            Deliver = (index, _) => index == 0 ? 3u : 0u,
        };
        var injector = new TextInjector(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TextInjector>.Instance,
            platform,
            new TextInjectionFakes.Clipboard());

        var result = injector.Inject("abcd", Models.InjectionMethod.UnicodeType, Target, shiftEnterLineBreaks: true);

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.Sent);
        Assert.DoesNotContain(
            platform.Batches.SelectMany(batch => batch),
            input => input.U.ki.wVk == InjectionNativeMethods.VK_SHIFT);

        // Cut after Shift-up (6 events of "a\nb": a down, a up, Shift down, Return down, Return up, Shift up):
        // Shift is already released, so no extra Shift release is sent.
        var afterChordPlatform = new TextInjectionFakes.Platform
        {
            Foreground = Target,
            Deliver = (index, _) => index == 0 ? 6u : 0u,
        };
        var afterChordInjector = new TextInjector(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TextInjector>.Instance,
            afterChordPlatform,
            new TextInjectionFakes.Clipboard());
        var afterChordResult = afterChordInjector.Inject("a\nb", Models.InjectionMethod.UnicodeType, Target, shiftEnterLineBreaks: true);
        Assert.False(afterChordResult.Succeeded);
        Assert.Equal(6, afterChordResult.Sent);
        Assert.NotEqual(1, afterChordPlatform.Batches.Last().Length);
    }

    [Fact]
    public void Short_send_stops_retrying_immediately_when_foreground_window_changes_and_still_releases_held_shift()
    {
        const nint Target = 0x1234;
        var platform = new TextInjectionFakes.Platform();
        platform.Foreground = Target;
        platform.Deliver = (_, _) =>
        {
            platform.Foreground = 0x9999;
            return 2u;
        };
        var injector = new TextInjector(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TextInjector>.Instance,
            platform,
            new TextInjectionFakes.Clipboard());

        var result = injector.Inject("abcd", Models.InjectionMethod.UnicodeType, Target, shiftEnterLineBreaks: true);

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Sent);
        Assert.Single(platform.Batches);
        Assert.Empty(platform.Sleeps);

        // When focus changes mid-chord after Shift-down (3 events of "a\nb"), retry stops immediately
        // AND the Shift key-up still goes out regardless of focus.
        var shiftPlatform = new TextInjectionFakes.Platform();
        shiftPlatform.Foreground = Target;
        shiftPlatform.Deliver = (_, inputs) =>
        {
            shiftPlatform.Foreground = 0x9999;
            return inputs.Length == 1 ? 1u : 3u;
        };
        var shiftInjector = new TextInjector(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TextInjector>.Instance,
            shiftPlatform,
            new TextInjectionFakes.Clipboard());
        var shiftResult = shiftInjector.Inject("a\nb", Models.InjectionMethod.UnicodeType, Target, shiftEnterLineBreaks: true);
        Assert.False(shiftResult.Succeeded);
        Assert.Equal(3, shiftResult.Sent);
        Assert.Empty(shiftPlatform.Sleeps);
        var lastBatch = shiftPlatform.Batches.Last();
        Assert.Single(lastBatch);
        Assert.Equal(InjectionNativeMethods.VK_SHIFT, lastBatch[0].U.ki.wVk);
        Assert.Equal(InjectionNativeMethods.KEYEVENTF_KEYUP, lastBatch[0].U.ki.dwFlags);
    }

    private static string Replay(string text, bool shiftEnter) =>
        text.Replace("\r\n", shiftEnter ? "\u21B5" : "\u23CE").Replace("\r", shiftEnter ? "\u21B5" : "\u23CE")
            .Replace("\n", shiftEnter ? "\u21B5" : "\u23CE");

    private static string Replay(InjectionNativeMethods.INPUT[] inputs)
    {
        var replay = new System.Text.StringBuilder();
        bool shiftDown = false;
        foreach (var input in inputs)
        {
            var key = input.U.ki;
            if (key.wVk == InjectionNativeMethods.VK_SHIFT)
            {
                shiftDown = (key.dwFlags & InjectionNativeMethods.KEYEVENTF_KEYUP) == 0;
                continue;
            }

            if ((key.dwFlags & InjectionNativeMethods.KEYEVENTF_KEYUP) != 0)
            {
                continue;
            }

            if ((key.dwFlags & InjectionNativeMethods.KEYEVENTF_UNICODE) != 0)
            {
                replay.Append((char)key.wScan);
            }
            else if (key.wVk == InjectionNativeMethods.VK_RETURN)
            {
                replay.Append(shiftDown ? '\u21B5' : '\u23CE');
            }
        }

        return replay.ToString();
    }
}
