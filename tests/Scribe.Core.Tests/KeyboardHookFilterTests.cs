using System.Runtime.InteropServices;
using Scribe.Core.Hotkeys;

namespace Scribe.Core.Tests;

/// <summary>
/// The keyboard callback's decisions before the engine: the watchdog's probe, which it now swallows after counting it,
/// and a key event's echo, the same event coming back through another registration of this installation while a move
/// ahead keeps the replaced one registered (see <c>HotkeyService.HookInstallation</c>).
/// </summary>
public class KeyboardHookFilterTests
{
    private static readonly nuint TestMarker = unchecked((nuint)0x5343524954455354UL);

    [Fact]
    public void Only_scribe_s_own_key_up_of_the_probe_key_is_the_probe()
    {
        Assert.True(KeyboardHookFilter.IsProbe(NativeMethods.VK_PROBE, isUp: true, SyntheticInputMarker.Value));

        // Its key-down, another key of Scribe's (typed text is VK_PACKET, 0xE7), or the probe key from anyone else.
        Assert.False(KeyboardHookFilter.IsProbe(NativeMethods.VK_PROBE, isUp: false, SyntheticInputMarker.Value));
        Assert.False(KeyboardHookFilter.IsProbe(0xE7, isUp: true, SyntheticInputMarker.Value));
        Assert.False(KeyboardHookFilter.IsProbe(0x10, isUp: true, SyntheticInputMarker.Value));
        Assert.False(KeyboardHookFilter.IsProbe(NativeMethods.VK_PROBE, isUp: true, TestMarker));
        Assert.False(KeyboardHookFilter.IsProbe(NativeMethods.VK_PROBE, isUp: true, 0));
    }

    [Fact]
    public void The_field_offsets_are_the_struct_s()
    {
        Assert.Equal(KeyboardHookFilter.VkCodeOffset, (int)Marshal.OffsetOf<NativeMethods.KBDLLHOOKSTRUCT>(nameof(NativeMethods.KBDLLHOOKSTRUCT.vkCode)));
        Assert.Equal(KeyboardHookFilter.ScanCodeOffset, (int)Marshal.OffsetOf<NativeMethods.KBDLLHOOKSTRUCT>(nameof(NativeMethods.KBDLLHOOKSTRUCT.scanCode)));
        Assert.Equal(KeyboardHookFilter.FlagsOffset, (int)Marshal.OffsetOf<NativeMethods.KBDLLHOOKSTRUCT>(nameof(NativeMethods.KBDLLHOOKSTRUCT.flags)));
        Assert.Equal(KeyboardHookFilter.TimeOffset, (int)Marshal.OffsetOf<NativeMethods.KBDLLHOOKSTRUCT>(nameof(NativeMethods.KBDLLHOOKSTRUCT.time)));
        Assert.Equal(KeyboardHookFilter.ExtraInfoOffset, (int)Marshal.OffsetOf<NativeMethods.KBDLLHOOKSTRUCT>(nameof(NativeMethods.KBDLLHOOKSTRUCT.dwExtraInfo)));
    }

    [Fact]
    public void An_event_s_identity_is_read_from_its_hook_struct()
    {
        using var message = new KeyMessage(new NativeMethods.KBDLLHOOKSTRUCT
        {
            vkCode = 0xA3, scanCode = 0x1D, flags = 0x81, time = 123456, dwExtraInfo = TestMarker,
        });

        var identity = KeyboardHookFilter.Identity(message.Pointer);

        Assert.Equal((0xA3u, 0x1Du, 0x81u, 123456u), (identity.VirtualKey, identity.ScanCode, identity.Flags, identity.Time));
    }

    [Fact]
    public void Two_identities_are_the_same_only_when_all_four_fields_are()
    {
        var a = new KeyEventIdentity(0xA3, 0x1D, 0x01, 1000);
        Assert.True(KeyEventIdentity.Same(a, new KeyEventIdentity(0xA3, 0x1D, 0x01, 1000)));
        Assert.False(KeyEventIdentity.Same(a, new KeyEventIdentity(0xA2, 0x1D, 0x01, 1000)));
        Assert.False(KeyEventIdentity.Same(a, new KeyEventIdentity(0xA3, 0x1E, 0x01, 1000)));
        Assert.False(KeyEventIdentity.Same(a, new KeyEventIdentity(0xA3, 0x1D, 0x81, 1000))); // its key-up
        Assert.False(KeyEventIdentity.Same(a, new KeyEventIdentity(0xA3, 0x1D, 0x01, 1001))); // its next repeat
    }

    [Fact]
    public void An_event_is_an_echo_only_while_this_thread_is_passing_it_on()
    {
        var passOn = new KeyEventPassOn();
        var down = new KeyEventIdentity(0x83, 0x6B, 0x10, 500);
        var up = new KeyEventIdentity(0x83, 0x6B, 0x90, 520);

        Assert.False(passOn.IsEcho(down));
        passOn.Enter(down);
        Assert.True(passOn.IsEcho(down)); // back through the registration a move ahead replaced
        Assert.False(passOn.IsEcho(up));
        passOn.Leave();
        Assert.False(passOn.IsEcho(down)); // a later event with the same fields is not an echo once the pass is over
        Assert.Equal(0, passOn.Depth);
    }

    [Fact]
    public void An_echo_is_recognized_under_a_different_event_nested_inside_its_pass()
    {
        // While this thread passes E1 on, it can be handed E2 (injected input runs the hooks in the injecting thread's
        // context, beside the physical input), which it decides and passes on too; E1 can then come back through the
        // replaced registration while E2's pass is still on the stack.
        var passOn = new KeyEventPassOn();
        var e1 = new KeyEventIdentity(0x41, 0x1E, 0x00, 700);
        var e2 = new KeyEventIdentity(0x42, 0x30, 0x10, 701);

        passOn.Enter(e1);
        Assert.False(passOn.IsEcho(e2)); // E2 is new: decided, not passed through
        passOn.Enter(e2);
        Assert.True(passOn.IsEcho(e2));
        Assert.True(passOn.IsEcho(e1));
        passOn.Leave();
        Assert.True(passOn.IsEcho(e1));
        Assert.False(passOn.IsEcho(e2));
        passOn.Leave();
        Assert.False(passOn.IsEcho(e1));
    }

    [Fact]
    public void Passes_nested_past_the_recorded_depth_are_counted_and_never_misread()
    {
        var passOn = new KeyEventPassOn();
        for (uint i = 0; i < KeyEventPassOn.MaxDepth + 3; i++)
        {
            passOn.Enter(new KeyEventIdentity(i, i, 0, i));
        }

        Assert.Equal(KeyEventPassOn.MaxDepth + 3, passOn.Depth);
        Assert.True(passOn.IsEcho(new KeyEventIdentity(0, 0, 0, 0)));
        Assert.False(passOn.IsEcho(new KeyEventIdentity(KeyEventPassOn.MaxDepth + 1, KeyEventPassOn.MaxDepth + 1, 0, KeyEventPassOn.MaxDepth + 1)));
        for (var i = 0; i < KeyEventPassOn.MaxDepth + 3; i++)
        {
            passOn.Leave();
        }

        Assert.Equal(0, passOn.Depth);
        Assert.False(passOn.IsEcho(new KeyEventIdentity(0, 0, 0, 0)));
    }

    // In the collection that runs alone (stream TR, item 1): nothing else in the process runs while it measures.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Fact]
        public void The_callback_s_decisions_before_the_engine_allocate_nothing()
        {
            using var message = new KeyMessage(new NativeMethods.KBDLLHOOKSTRUCT { vkCode = 0x83, scanCode = 0x6B, time = 9 });
            var passOn = new KeyEventPassOn();

            // Warm the JIT for every call measured below, and the readings around the window.
            Run(message.Pointer, passOn);
            _ = RuntimeWork.Now().Since(RuntimeWork.Now());

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var echoes = Run(message.Pointer, passOn);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            AllocationMeasurement.AssertZero(
                allocated, during, "The callback's decisions before the engine", () => Run(message.Pointer, passOn));
            Assert.Equal(1, echoes);

            static int Run(nint lParam, KeyEventPassOn passOn)
            {
                var identity = KeyboardHookFilter.Identity(lParam);
                var probe = KeyboardHookFilter.IsProbe(identity.VirtualKey, isUp: true, SyntheticInputMarker.Value);
                passOn.Enter(identity);
                var echo = passOn.IsEcho(KeyboardHookFilter.Identity(lParam));
                passOn.Leave();
                return (echo ? 1 : 0) + (probe ? 10 : 0);
            }
        }
    }

    /// <summary>A KBDLLHOOKSTRUCT in unmanaged memory, as a low-level keyboard hook's lParam points at one.</summary>
    internal sealed class KeyMessage : IDisposable
    {
        public KeyMessage(NativeMethods.KBDLLHOOKSTRUCT value)
        {
            Pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.KBDLLHOOKSTRUCT>());
            Marshal.StructureToPtr(value, Pointer, fDeleteOld: false);
        }

        public nint Pointer { get; }

        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }
}
