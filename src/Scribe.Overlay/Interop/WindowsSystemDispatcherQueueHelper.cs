using System;
using System.Runtime.InteropServices;

namespace Scribe.Overlay.Interop;

/// <summary>
/// Ensures the current (UI) thread has a <see cref="Windows.System.DispatcherQueue"/>, which is a
/// prerequisite for constructing a system <c>Windows.UI.Composition.Compositor</c>. This is the
/// standard Windows App SDK helper used by the Mica/Acrylic and custom-backdrop samples.
/// </summary>
internal sealed class WindowsSystemDispatcherQueueHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        internal int dwSize;
        internal int threadType;
        internal int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        DispatcherQueueOptions options,
        [In, Out, MarshalAs(UnmanagedType.IUnknown)] ref object? dispatcherQueueController);

    private object? _controller;

    public void EnsureDispatcherQueueController()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null)
        {
            return; // the thread already has one
        }

        if (_controller is null)
        {
            DispatcherQueueOptions options;
            options.dwSize = Marshal.SizeOf<DispatcherQueueOptions>();
            options.threadType = 2;    // DQTYPE_THREAD_CURRENT
            options.apartmentType = 2; // DQTAT_COM_STA

            object? controller = null;
            CreateDispatcherQueueController(options, ref controller);
            _controller = controller;
        }
    }
}
