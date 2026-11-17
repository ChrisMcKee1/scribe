using System.Runtime.InteropServices;

namespace Scribe.Core.TextInjection;

/// <summary>The real Win32 focus, input and timing calls behind <see cref="IInjectionPlatform"/>.</summary>
internal sealed class Win32InjectionPlatform : IInjectionPlatform
{
    /// <summary>SendInput's cbSize: the marshaled size of one INPUT, which Windows checks against its own.</summary>
    internal static readonly int InputSize = Marshal.SizeOf<InjectionNativeMethods.INPUT>();

    // How long a control has to answer EM_REPLACESEL. Past it the answer is unknown, not no (see ClassifyEditMessage).
    private const uint EditMessageTimeoutMs = 1000;

    public static Win32InjectionPlatform Instance { get; } = new();

    private Win32InjectionPlatform()
    {
    }

    public nint GetForegroundWindow() => InjectionNativeMethods.GetForegroundWindow();

    public unsafe uint SendInput(ReadOnlySpan<InjectionNativeMethods.INPUT> inputs)
    {
        fixed (InjectionNativeMethods.INPUT* first = inputs)
        {
            return InjectionNativeMethods.SendInput((uint)inputs.Length, first, InputSize);
        }
    }

    public void Sleep(int milliseconds) => Thread.Sleep(milliseconds);

    public IPreciseTypingWait CreatePreciseWait() => new PreciseTypingWait();

    public KeyScanCode ScanCodeOf(ushort virtualKey) => KeyScanCodes.ForForegroundLayout(virtualKey);

    public InjectionKeys SnapshotKeys()
    {
        var layout = KeyScanCodes.ForegroundLayout();
        return new InjectionKeys(
            KeyScanCodes.ForLayout(InjectionNativeMethods.VK_SHIFT, layout),
            KeyScanCodes.ForLayout(InjectionNativeMethods.VK_RETURN, layout),
            KeyScanCodes.ForLayout(InjectionNativeMethods.VK_CONTROL, layout),
            KeyScanCodes.ForLayout(InjectionNativeMethods.VK_V, layout));
    }

    public bool TryInsertIntoStandardEdit(string text, nint expectedForegroundWindow) =>
        InsertIntoStandardEdit(text, expectedForegroundWindow) == StandardEditInsertion.Inserted;

    public StandardEditInsertion InsertIntoStandardEdit(string text, nint expectedForegroundWindow)
    {
        var foreground = InjectionNativeMethods.GetForegroundWindow();
        if (foreground == 0 || (expectedForegroundWindow != 0 && foreground != expectedForegroundWindow))
        {
            return StandardEditInsertion.Declined;
        }

        var threadId = InjectionNativeMethods.GetWindowThreadProcessId(foreground, out _);
        var info = new InjectionNativeMethods.GUITHREADINFO
        {
            cbSize = (uint)Marshal.SizeOf<InjectionNativeMethods.GUITHREADINFO>(),
        };
        if (threadId == 0 || !InjectionNativeMethods.GetGUIThreadInfo(threadId, ref info) || info.hwndFocus == 0)
        {
            return StandardEditInsertion.Declined;
        }

        var className = new char[128];
        var length = InjectionNativeMethods.GetClassName(info.hwndFocus, className, className.Length);
        if (length == 0)
        {
            return StandardEditInsertion.Declined;
        }

        var controlClass = new string(className, 0, length);
        if (!string.Equals(controlClass, "Edit", StringComparison.OrdinalIgnoreCase) &&
            !controlClass.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase))
        {
            return StandardEditInsertion.Declined;
        }

        return SendReplaceSelection(info.hwndFocus, text);
    }

    /// <summary>Sends EM_REPLACESEL with <paramref name="text"/> to <paramref name="control"/> and says how that ended.</summary>
    internal static StandardEditInsertion SendReplaceSelection(nint control, string text)
    {
        // SendMessageTimeout does not always set the last error when it fails (its documentation says so), so a code an
        // earlier call left would be read as this one's reason. Cleared first, as Microsoft advises.
        Marshal.SetLastSystemError(0);
        nint answered = InjectionNativeMethods.SendMessageTimeout(
            control, InjectionNativeMethods.EM_REPLACESEL, (nint)1, text,
            InjectionNativeMethods.SMTO_ABORTIFHUNG, EditMessageTimeoutMs, out _);
        return ClassifyEditMessage(answered, Marshal.GetLastPInvokeError());
    }

    /// <summary>
    /// What a SendMessageTimeout of EM_REPLACESEL means. An answer is an insertion. Without one, only the two failures
    /// Windows reports before it queues a message are refusals: a window handle that is not valid, and access denied, which
    /// is how a message to a window of a higher integrity level is blocked (SendMessage, Remarks). Everything else is
    /// unconfirmed: ERROR_TIMEOUT, a zero with ERROR_SUCCESS (Microsoft says to treat that as "a generic failure") and any
    /// code not listed. In each of those the control may already hold the text. A refusal read as unconfirmed costs the
    /// user a recovery copy, while the reverse puts the text in twice, so the list of refusals is closed.
    /// </summary>
    internal static StandardEditInsertion ClassifyEditMessage(nint answered, int lastError)
    {
        if (answered != 0)
        {
            return StandardEditInsertion.Inserted;
        }

        return lastError is InjectionNativeMethods.ERROR_ACCESS_DENIED or InjectionNativeMethods.ERROR_INVALID_WINDOW_HANDLE
            ? StandardEditInsertion.Declined
            : StandardEditInsertion.Unconfirmed;
    }
}
