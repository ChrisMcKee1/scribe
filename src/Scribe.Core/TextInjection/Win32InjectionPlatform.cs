using System.Runtime.InteropServices;

namespace Scribe.Core.TextInjection;

/// <summary>The real Win32 focus, input and timing calls behind <see cref="IInjectionPlatform"/>.</summary>
internal sealed class Win32InjectionPlatform : IInjectionPlatform
{
    /// <summary>SendInput's cbSize: the marshaled size of one INPUT, which Windows checks against its own.</summary>
    internal static readonly int InputSize = Marshal.SizeOf<InjectionNativeMethods.INPUT>();

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

    public KeyScanCode ScanCodeOf(ushort virtualKey) => KeyScanCodes.ForForegroundLayout(virtualKey);

    public bool TryInsertIntoStandardEdit(string text, nint expectedForegroundWindow)
    {
        var foreground = InjectionNativeMethods.GetForegroundWindow();
        if (foreground == 0 || (expectedForegroundWindow != 0 && foreground != expectedForegroundWindow))
        {
            return false;
        }

        var threadId = InjectionNativeMethods.GetWindowThreadProcessId(foreground, out _);
        var info = new InjectionNativeMethods.GUITHREADINFO
        {
            cbSize = (uint)Marshal.SizeOf<InjectionNativeMethods.GUITHREADINFO>(),
        };
        if (threadId == 0 || !InjectionNativeMethods.GetGUIThreadInfo(threadId, ref info) || info.hwndFocus == 0)
        {
            return false;
        }

        var className = new char[128];
        var length = InjectionNativeMethods.GetClassName(info.hwndFocus, className, className.Length);
        if (length == 0)
        {
            return false;
        }

        var controlClass = new string(className, 0, length);
        if (!string.Equals(controlClass, "Edit", StringComparison.OrdinalIgnoreCase) &&
            !controlClass.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return InjectionNativeMethods.SendMessageTimeout(
            info.hwndFocus, InjectionNativeMethods.EM_REPLACESEL, (nint)1, text,
            InjectionNativeMethods.SMTO_ABORTIFHUNG, 1000, out _) != 0;
    }
}
