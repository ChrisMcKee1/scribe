using Scribe.Core.TextInjection;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class TextInjectorTests
{
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
}