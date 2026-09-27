using Scribe.Core.TextInjection;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.Tests;

/// <summary>
/// The production SendInput takes a span and pins it for the call. With no events it pins nothing and sends nothing, so
/// this reaches the import and its fixed statement on any desktop without typing anything. Every non-empty span typing
/// sends is covered through the recording fake (<see cref="TextInjectorTypingBufferTests"/>).
/// </summary>
public class Win32InjectionPlatformSendInputTests
{
    [Fact]
    public void Sending_no_events_reaches_the_import_and_inserts_nothing()
    {
        Assert.Equal(0u, Win32InjectionPlatform.Instance.SendInput(ReadOnlySpan<INPUT>.Empty));
    }
}
