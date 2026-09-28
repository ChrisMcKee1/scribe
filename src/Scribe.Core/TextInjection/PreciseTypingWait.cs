using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Scribe.Core.TextInjection;

internal interface IPreciseTypingWait : IDisposable
{
    bool TryWait(int milliseconds);
}

/// <summary>A private one-shot timer, owned by one insertion and never by a callback or another insertion.</summary>
internal sealed partial class PreciseTypingWait : IPreciseTypingWait
{
    private readonly SafeWaitHandle _handle;

    public PreciseTypingWait()
    {
        var handle = CreateWaitableTimerExW(0, 0, 2, 0x00100002);
        if (handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        _handle = new SafeWaitHandle(handle, ownsHandle: true);
    }

    public bool TryWait(int milliseconds)
    {
        long due = -TimeSpan.TicksPerMillisecond * milliseconds;
        return SetWaitableTimer(_handle, ref due, 0, 0, 0, false) &&
            WaitForSingleObject(_handle, (uint)(milliseconds + 1000)) == 0;
    }

    public void Dispose() => _handle.Dispose();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(
        SafeWaitHandle timer, ref long dueTime, int period, nint callback, nint context,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
