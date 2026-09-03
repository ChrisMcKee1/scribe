using Scribe.Core.TextInjection;
using Xunit;

namespace Scribe.Core.Tests;

public class Win32ClipboardTests
{
    [Fact]
    public void Set_then_get_round_trips_unicode_text()
    {
        // Run on an STA thread (clipboard requirement) and restore any prior text afterward.
        string? result = null;
        bool setOk = false;

        var thread = new Thread(() =>
        {
            string? previous = Win32Clipboard.TryGetText();
            try
            {
                setOk = Win32Clipboard.SetText("Scribe café — 测试 🎤");
                result = Win32Clipboard.TryGetText();
            }
            finally
            {
                if (previous is not null)
                {
                    Win32Clipboard.SetText(previous);
                }
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(setOk);
        Assert.Equal("Scribe café — 测试 🎤", result);
    }

    [Fact]
    public void Text_content_is_not_reported_as_non_text()
    {
        // Guards the paste-preservation gate: after putting text on the clipboard, the injector
        // must still be willing to paste (text CAN be saved and restored). The image/files case
        // needs real non-text clipboard data, which a unit test can't stage without clobbering
        // the developer's clipboard with formats this class can't put back.
        bool nonText = true;

        var thread = new Thread(() =>
        {
            string? previous = Win32Clipboard.TryGetText();
            try
            {
                Win32Clipboard.SetText("plain text");
                nonText = Win32Clipboard.HasNonTextContent();
            }
            finally
            {
                if (previous is not null)
                {
                    Win32Clipboard.SetText(previous);
                }
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.False(nonText);
    }
}
