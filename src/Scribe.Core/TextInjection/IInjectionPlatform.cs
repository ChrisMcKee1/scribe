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

    /// <summary>
    /// SendInput; returns how many events were inserted into the input stream. The events are borrowed:
    /// <see cref="TextInjector"/> writes every batch of an insertion into the same buffer, so an implementation that
    /// keeps them past the call must copy them.
    /// </summary>
    uint SendInput(ReadOnlySpan<INPUT> inputs);

    /// <summary>
    /// Inserts <paramref name="text"/> directly when the focused control is a classic Edit or RichEdit,
    /// which accepts EM_REPLACESEL without touching the clipboard or the keyboard. True only when the control
    /// answered; <see cref="InsertIntoStandardEdit"/> says what a no means.
    /// </summary>
    bool TryInsertIntoStandardEdit(string text, nint expectedForegroundWindow);

    /// <summary>
    /// <see cref="TryInsertIntoStandardEdit"/> with the reason for a no, which is what <see cref="TextInjector"/> asks. A
    /// message that was sent and not answered is <see cref="StandardEditInsertion.Unconfirmed"/>, never a refusal: the
    /// control may already hold the text, so pasting or typing it as well could put it in twice. The default is for a
    /// platform that can only say yes or no, and reads a no as a refusal; a platform that sends the message itself overrides it.
    /// </summary>
    StandardEditInsertion InsertIntoStandardEdit(string text, nint expectedForegroundWindow) =>
        TryInsertIntoStandardEdit(text, expectedForegroundWindow)
            ? StandardEditInsertion.Inserted
            : StandardEditInsertion.Declined;

    void Sleep(int milliseconds);

    /// <summary>
    /// The scan code the foreground window's keyboard layout gives <paramref name="virtualKey"/>
    /// (<see cref="KeyScanCodes.ForForegroundLayout"/>), for the VK-based events Scribe injects.
    /// </summary>
    KeyScanCode ScanCodeOf(ushort virtualKey);

    InjectionKeys SnapshotKeys() => InjectionKeys.From(this);

    IPreciseTypingWait CreatePreciseWait() => throw new PlatformNotSupportedException();
}

/// <summary>How a direct insertion into a classic Edit or RichEdit control ended (<see cref="IInjectionPlatform.InsertIntoStandardEdit"/>).</summary>
internal enum StandardEditInsertion
{
    /// <summary>
    /// Nothing was sent: the focused control is not an Edit or RichEdit, the window in front is not the expected one, or
    /// Windows refused the message before it was queued. Nothing can have changed, so the text can still be pasted or typed.
    /// </summary>
    Declined,

    /// <summary>The control answered the message, so the text is in it.</summary>
    Inserted,

    /// <summary>
    /// The message was sent and the control did not answer in time. A control can change its text and then wait on its
    /// parent's handling of the change, and a message can still be queued behind a thread that is not pumping, so the
    /// text may be there now or arrive later, and nothing proves it is not. Pasting or typing it again could duplicate it.
    /// </summary>
    Unconfirmed,
}
