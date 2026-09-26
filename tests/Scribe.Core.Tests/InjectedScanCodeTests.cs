using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.TextInjection;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.Tests;

/// <summary>
/// Every VK-based event Scribe injects carries the scan code Windows would produce for its key. Remote Desktop and virtual
/// machine clients forward keys by scan code ([MS-RDPBCGR]: a keyboard event's keyCode is "the scancode of the key"), so
/// a Shift, Return, Ctrl or V with no scan code reached the remote session as scan code 0. KEYEVENTF_SCANCODE is never
/// set, so Windows still takes the virtual key from wVk and local apps get exactly the keys they got before.
/// </summary>
public class InjectedScanCodeTests
{
    private const uint VkShift = 0x10;
    private const uint VkControl = 0x11;
    private const uint VkReturn = 0x0D;
    private const uint VkPause = 0x13;
    private const uint VkPageDown = 0x22;
    private const uint VkNumLock = 0x90;
    private const uint VkLeftControl = 0xA2;
    private const uint VkRightControl = 0xA3;
    private const uint VkRightShift = 0xA1;
    private const uint VkRightAlt = 0xA5;
    private const uint VkLeftWin = 0x5B;
    private const uint VkApps = 0x5D;
    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventScanCode = 0x0008;
    private static readonly nint Target = 0x4242;

    [Theory]
    [InlineData(VkShift, 0x002Au, 0x2A, false)]
    [InlineData(VkReturn, 0x001Cu, 0x1C, false)]
    [InlineData(VkControl, 0x001Du, 0x1D, false)]
    [InlineData(VkRightControl, 0xE01Du, 0x1D, true)] // MAPVK_VK_TO_VSC_EX: "the high byte ... will contain either 0xe0 or 0xe1"
    [InlineData(VkRightAlt, 0xE038u, 0x38, true)]
    [InlineData(VkLeftWin, 0xE05Bu, 0x5B, true)]
    [InlineData(VkApps, 0xE05Du, 0x5D, true)] // the prefix alone makes it extended: the Menu key is not on the repair's list
    [InlineData(VkPageDown, 0xE051u, 0x51, true)]
    [InlineData(VkPageDown, 0x0051u, 0x51, true)] // what the US layout answers: the keypad's 3; the dedicated key is E0 51
    [InlineData(VkNumLock, 0x0045u, 0x45, true)]
    [InlineData(VkRightShift, 0x0036u, 0x36, false)]
    [InlineData(VkPause, 0xE11Du, 0x00, false)] // an 0xE1 key has no scan code KEYBDINPUT can express: none, as before
    [InlineData(0xFFu, 0x0000u, 0x00, false)] // no translation: none
    public void The_layout_s_scan_code_is_decoded_with_its_extended_prefix(
        uint virtualKey, uint vscEx, int code, bool extended) =>
        Assert.Equal(new KeyScanCode((ushort)code, extended), KeyScanCodes.Decode(virtualKey, vscEx));

    [Fact]
    public void The_flags_never_ask_windows_to_take_the_key_from_the_scan_code()
    {
        foreach (var scan in new[] { KeyScanCode.None, new KeyScanCode(0x2A, false), new KeyScanCode(0x1D, true) })
        {
            foreach (var up in new[] { false, true })
            {
                var flags = KeyScanCodes.Flags(scan, up);
                Assert.Equal(0u, flags & KeyEventScanCode);
                Assert.Equal(up, (flags & KEYEVENTF_KEYUP) != 0);
                Assert.Equal(scan.Extended, (flags & KeyEventExtended) != 0);
            }
        }
    }

    [Theory]
    [InlineData(VkShift, 0x2A, false)]
    [InlineData(VkReturn, 0x1C, false)]
    [InlineData(VkControl, 0x1D, false)]
    [InlineData(VkLeftControl, 0x1D, false)]
    [InlineData(VkRightControl, 0x1D, true)]
    [InlineData(VkRightShift, 0x36, false)]
    [InlineData(VkRightAlt, 0x38, true)]
    [InlineData(VkLeftWin, 0x5B, true)]
    [InlineData(VkApps, 0x5D, true)]
    [InlineData(VkPageDown, 0x51, true)] // the layout answers the keypad's code without a prefix; the flag names the dedicated key
    public void This_machine_s_layout_gives_the_modifiers_and_return_their_scan_codes(uint virtualKey, int code, bool extended) =>
        // Keys every PC keyboard layout places alike, so this holds on any machine and on both CI runners.
        Assert.Equal(new KeyScanCode((ushort)code, extended), KeyScanCodes.ForForegroundLayout(virtualKey));

    [Fact]
    public void A_typed_line_break_carries_the_scan_codes_of_shift_and_return()
    {
        var platform = new TextInjectionFakes.Platform { Foreground = Target };
        var injector = new TextInjector(NullLogger<TextInjector>.Instance, platform, new TextInjectionFakes.Clipboard());

        var result = injector.Inject("a\nb", InjectionMethod.UnicodeType, Target, shiftEnterLineBreaks: true);

        Assert.True(result.Succeeded);
        var events = platform.Batches.SelectMany(batch => batch).Select(input => input.U.ki).ToArray();
        var chord = events.Where(key => (key.dwFlags & KEYEVENTF_UNICODE) == 0).ToArray();
        Assert.Equal(
            [
                (VK_SHIFT, (ushort)0x2A, 0u),
                (VK_RETURN, (ushort)0x1C, 0u),
                (VK_RETURN, (ushort)0x1C, KEYEVENTF_KEYUP),
                (VK_SHIFT, (ushort)0x2A, KEYEVENTF_KEYUP),
            ],
            chord.Select(key => (key.wVk, key.wScan, key.dwFlags)).ToArray());

        // The characters are untouched: a Unicode event's wScan is the character and its wVk is zero.
        var characters = events.Where(key => (key.dwFlags & KEYEVENTF_UNICODE) != 0).ToArray();
        Assert.Equal("aabb", new string(characters.Select(key => (char)key.wScan).ToArray()));
        Assert.All(characters, key => Assert.Equal(0, key.wVk));
    }

    [Fact]
    public void A_plain_return_carries_its_scan_code_too()
    {
        var inputs = TextInjector.BuildLineBreak(shiftEnter: false, UsKeys()).Select(input => input.U.ki).ToArray();

        Assert.Equal(
            [(VK_RETURN, (ushort)0x1C, 0u), (VK_RETURN, (ushort)0x1C, KEYEVENTF_KEYUP)],
            inputs.Select(key => (key.wVk, key.wScan, key.dwFlags)).ToArray());
    }

    [Fact]
    public void The_paste_chord_and_its_cleanup_carry_the_scan_codes_of_ctrl_and_v()
    {
        var clipboard = new TextInjectionFakes.Clipboard();
        clipboard.SeedText("before");
        var platform = new TextInjectionFakes.Platform { Foreground = Target };
        var injector = new TextInjector(NullLogger<TextInjector>.Instance, platform, clipboard);

        var result = injector.Inject("pasted", InjectionMethod.ClipboardPaste, Target);

        Assert.Equal(PasteDelivery.ChordInserted, result.Paste);
        var chord = Assert.Single(platform.Batches).Select(input => input.U.ki).ToArray();
        Assert.Equal(
            [
                (VK_CONTROL, (ushort)0x1D, 0u),
                (VK_V, (ushort)0x2F, 0u),
                (VK_V, (ushort)0x2F, KEYEVENTF_KEYUP),
                (VK_CONTROL, (ushort)0x1D, KEYEVENTF_KEYUP),
            ],
            chord.Select(key => (key.wVk, key.wScan, key.dwFlags)).ToArray());

        Assert.Equal(
            [(VK_CONTROL, (ushort)0x1D, KEYEVENTF_KEYUP), (VK_V, (ushort)0x2F, KEYEVENTF_KEYUP)],
            TextInjector.BuildCtrlVReleaseInputs(UsKeys()).Select(input => input.U.ki).Select(key => (key.wVk, key.wScan, key.dwFlags)));
    }

    [Fact]
    public void Shift_released_after_a_short_count_carries_its_scan_code()
    {
        var platform = new TextInjectionFakes.Platform
        {
            Foreground = Target,

            // The first batch stops after Shift went down and never takes the rest: the chord is cut before its release,
            // and the one-event release that follows is delivered.
            Deliver = (index, inputs) => inputs.Length == 1 ? 1u : index == 0 ? 3u : 0u,
        };
        var injector = new TextInjector(NullLogger<TextInjector>.Instance, platform, new TextInjectionFakes.Clipboard());

        injector.Inject("a\nb", InjectionMethod.UnicodeType, Target, shiftEnterLineBreaks: true);

        var release = platform.Batches.Last().Select(input => input.U.ki).ToArray();
        Assert.Equal([(VK_SHIFT, (ushort)0x2A, KEYEVENTF_KEYUP)], release.Select(key => (key.wVk, key.wScan, key.dwFlags)));
    }

    [Fact]
    public void The_scan_codes_are_asked_of_the_target_s_layout_once_per_insertion()
    {
        var platform = new TextInjectionFakes.Platform { Foreground = Target };
        var injector = new TextInjector(NullLogger<TextInjector>.Instance, platform, new TextInjectionFakes.Clipboard());

        injector.Inject(new string('x', 120) + "\n" + new string('y', 120), InjectionMethod.UnicodeType, Target);

        Assert.Equal([VK_SHIFT, VK_RETURN, VK_CONTROL, VK_V], platform.ScanCodeRequests);
    }

    [Fact]
    public void A_leaked_key_s_release_carries_its_scan_code_and_extended_flag()
    {
        var input = NativeMethods.BuildMarkedKeyEvent((ushort)VkRightControl, keyUp: true, new KeyScanCode(0x1D, true));

        Assert.Equal(NativeMethods.INPUT_KEYBOARD, input.type);
        Assert.Equal((ushort)VkRightControl, input.U.ki.wVk);
        Assert.Equal((ushort)0x1D, input.U.ki.wScan);
        Assert.Equal(NativeMethods.KEYEVENTF_KEYUP | NativeMethods.KEYEVENTF_EXTENDEDKEY, input.U.ki.dwFlags);
        Assert.Equal(SyntheticInputMarker.Value, input.U.ki.dwExtraInfo);

        var left = NativeMethods.BuildMarkedKeyEvent((ushort)VkLeftControl, keyUp: true, new KeyScanCode(0x1D, false));
        Assert.Equal((ushort)0x1D, left.U.ki.wScan);
        Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, left.U.ki.dwFlags);
    }

    [Fact]
    public void The_production_release_of_a_leaked_key_takes_its_scan_code_from_the_layout()
    {
        var input = NativeMethods.MarkedKeyEvent((ushort)VkRightControl, keyUp: true);

        Assert.Equal((ushort)0x1D, input.U.ki.wScan);
        Assert.Equal(NativeMethods.KEYEVENTF_KEYUP | NativeMethods.KEYEVENTF_EXTENDEDKEY, input.U.ki.dwFlags);
        Assert.Equal(0u, input.U.ki.dwFlags & KeyEventScanCode);
    }

    [Fact]
    public void The_watchdog_s_probe_keeps_no_scan_code_and_never_asks_the_layout()
    {
        var asked = new List<uint>();
        var scan = NativeMethods.MarkedKeyScanCode(NativeMethods.VK_PROBE, key =>
        {
            asked.Add(key);
            return new KeyScanCode(0x11, true);
        });

        Assert.Equal(KeyScanCode.None, scan);
        Assert.Empty(asked);

        var probe = NativeMethods.MarkedKeyEvent(NativeMethods.VK_PROBE, keyUp: true);
        Assert.Equal((ushort)0, probe.U.ki.wScan);
        Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, probe.U.ki.dwFlags);

        // Any other key is asked of the layout.
        Assert.Equal(new KeyScanCode(0x11, true), NativeMethods.MarkedKeyScanCode((ushort)VkLeftControl, _ => new KeyScanCode(0x11, true)));
    }

    private static InjectionKeys UsKeys() => InjectionKeys.From(new TextInjectionFakes.Platform());
}
