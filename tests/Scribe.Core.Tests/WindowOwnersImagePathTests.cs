using System.Diagnostics;
using System.Runtime.InteropServices;
using Scribe.Core.TextInjection;

namespace Scribe.Core.Tests;

/// <summary>
/// <see cref="WindowOwners.ProcessNameOf"/> reads the image path into a stack buffer and names the process from a slice of
/// it. A message-only window of the test's own gives it a real window without touching the desktop: such a window is never
/// shown, activated or enumerated, and it is destroyed on the thread that made it.
/// </summary>
public class WindowOwnersImagePathTests
{
    [Fact]
    public void A_window_s_process_is_named_as_Process_names_it()
    {
        using var window = MessageOnlyWindow.Create();
        using var self = Process.GetCurrentProcess();

        Assert.Equal(self.ProcessName, WindowOwners.ProcessNameOf(window.Handle));
    }

    [Fact]
    public void No_window_names_no_process() => Assert.Null(WindowOwners.ProcessNameOf(0));

    // In the collection that runs alone (stream TR, item 1): no other test runs while it measures.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Fact]
        public void Naming_a_window_s_process_allocates_only_the_name()
        {
            using var window = MessageOnlyWindow.Create();
            var name = WindowOwners.ProcessNameOf(window.Handle);
            Assert.NotNull(name);

            // Warm the JIT and the imports' stubs for every call measured below, and the readings around the window.
            for (var warm = 0; warm < 3; warm++)
            {
                _ = WindowOwners.ProcessNameOf(window.Handle);
            }

            _ = RuntimeWork.Now().Since(RuntimeWork.Now());
            _ = BytesOfAString(1);

            // What a string of the name's length costs on this runtime, measured the same way: the lookup below may
            // allocate that and nothing else.
            var nameBytes = BytesOfAString(name.Length);

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var named = WindowOwners.ProcessNameOf(window.Handle);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            AllocationMeasurement.AssertZero(
                allocated - nameBytes,
                during,
                $"Naming the process of a window, beyond its name of {nameBytes} bytes",
                () => WindowOwners.ProcessNameOf(window.Handle));
            Assert.Equal(name, named);
        }

        private static long BytesOfAString(int length)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var text = new string('x', length);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(text);
            return bytes;
        }
    }

    private sealed class MessageOnlyWindow : IDisposable
    {
        private static readonly nint HwndMessage = -3;

        private MessageOnlyWindow(nint handle) => Handle = handle;

        public nint Handle { get; }

        public static MessageOnlyWindow Create()
        {
            var handle = CreateWindowEx(0, "STATIC", null, 0, 0, 0, 0, 0, HwndMessage, 0, 0, 0);
            Assert.True(handle != 0, $"CreateWindowEx failed with Win32 error {Marshal.GetLastPInvokeError()}.");
            return new MessageOnlyWindow(handle);
        }

        public void Dispose() => DestroyWindow(Handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
        private static extern nint CreateWindowEx(
            uint exStyle, string className, string? windowName, uint style, int x, int y, int width, int height,
            nint parent, nint menu, nint instance, nint parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(nint window);
    }
}
