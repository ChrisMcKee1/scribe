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
