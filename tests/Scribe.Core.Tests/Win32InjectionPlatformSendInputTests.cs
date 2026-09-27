using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Scribe.Core.Hotkeys;
using Scribe.Core.TextInjection;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.Tests;

/// <summary>
/// The production SendInput takes a span and pins it for the call. With no events it pins nothing and sends nothing, so
/// that case reaches the import and its fixed statement on any desktop without typing anything; the size Windows is told
/// each INPUT has is pinned here too. One real event is sent only where injected input is allowed (CI, or a run that asks
/// for it), in a collection that never runs beside the real-hook tests. Every non-empty span typing sends is covered
/// through the recording fake (<see cref="TextInjectorTypingBufferTests"/>).
/// </summary>
public class Win32InjectionPlatformSendInputTests
{
    [Fact]
    public void Sending_no_events_reaches_the_import_and_inserts_nothing()
    {
        Assert.Equal(0u, Win32InjectionPlatform.Instance.SendInput(ReadOnlySpan<INPUT>.Empty));
    }

    [Fact]
    public void Windows_is_told_the_marshaled_size_of_one_INPUT()
    {
        // SendInput fails when cbSize is not the size of an INPUT, so this is what makes every insertion possible.
        Assert.Equal(Marshal.SizeOf<INPUT>(), Win32InjectionPlatform.InputSize);
        Assert.Equal(Environment.Is64BitProcess ? 40 : 28, Win32InjectionPlatform.InputSize);

        // And it is what the call passes: the argument pushed last before the import is called, cbSize, is that field.
        var sendInput = typeof(Win32InjectionPlatform).GetMethod(nameof(Win32InjectionPlatform.SendInput))!;
        var instructions = MouseButtonRound7Tests.Instructions(sendInput).ToArray();
        var call = Array.FindIndex(
            instructions,
            instruction => instruction.Code == OpCodes.Call &&
                sendInput.Module.ResolveMethod(instruction.Token) is { Name: nameof(InjectionNativeMethods.SendInput) } callee &&
                callee.DeclaringType == typeof(InjectionNativeMethods));
        Assert.True(call > 0, "Win32InjectionPlatform.SendInput never calls the import.");
        Assert.Equal(OpCodes.Ldsfld, instructions[call - 1].Code);
        Assert.Equal(
            typeof(Win32InjectionPlatform).GetField(nameof(Win32InjectionPlatform.InputSize), BindingFlags.NonPublic | BindingFlags.Static),
            sendInput.Module.ResolveField(instructions[call - 1].Token));
    }

    // Alone, after every parallel collection, so the one event it sends never reaches the hooks the HotkeyServiceTests
    // install on CI.
    [Collection(RealInputInjectionCollection.Name)]
    public sealed class RealInput
    {
        [Fact]
        public void One_marked_key_up_of_an_unassigned_key_is_inserted()
        {
            if (!InputInjectionAllowed())
            {
                return;
            }

            // The watchdog's probe: a key-up of VK 0xFF, which no key sends and every app ignores, with Scribe's marker.
            INPUT[] probe =
            [
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT { wVk = 0xFF, dwFlags = KEYEVENTF_KEYUP, dwExtraInfo = SyntheticInputMarker.Value },
                    },
                },
            ];

            Assert.Equal(1u, Win32InjectionPlatform.Instance.SendInput(probe));
        }

        // Injected input lands on the input desktop, under whatever the pointer is over, so only a throwaway machine's:
        // CI, or a run that asks for it. CI must have an input desktop, or this would pass without testing anything. The
        // same gate as the mouse hook injection tests (HotkeyServiceMouseTests).
        private static bool InputInjectionAllowed()
        {
            var onCi = string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
            var asked = Environment.GetEnvironmentVariable("SCRIBE_INPUT_INJECTION_TESTS") == "1";
            if (!onCi && !asked)
            {
                return false;
            }

            var receivesInput = NativeMethods.ThreadDesktopReceivesInput();
            Assert.True(
                receivesInput == true,
                $"The input injection test needs the input desktop, and this thread's desktop reports {receivesInput}.");
            return true;
        }
    }
}

/// <summary>
/// The collection of the one test that sends real input through <see cref="Win32InjectionPlatform"/>: it runs alone, after
/// every parallel collection, so never beside the real-hook tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealInputInjectionCollection
{
    public const string Name = "Real input injection";
}
