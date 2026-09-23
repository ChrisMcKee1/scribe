using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.TextInjection;

/// <summary>
/// Seam over the focus, input and timing calls <see cref="TextInjector"/> makes, so the paste and typing
/// sequences can be tested without sending real keystrokes. <see cref="Win32InjectionPlatform"/> is the
/// only production implementation.
/// </summary>
internal interface IInjectionPlatform
{
    nint GetForegroundWindow();

    /// <summary>SendInput; returns how many events were inserted into the input stream.</summary>
    uint SendInput(INPUT[] inputs);

    /// <summary>
    /// Inserts <paramref name="text"/> directly when the focused control is a classic Edit or RichEdit,
    /// which accepts EM_REPLACESEL without touching the clipboard or the keyboard.
    /// </summary>
    bool TryInsertIntoStandardEdit(string text, nint expectedForegroundWindow);

    void Sleep(int milliseconds);
}
