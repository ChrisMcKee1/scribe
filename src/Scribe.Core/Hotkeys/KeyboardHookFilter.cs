using System.Runtime.InteropServices;

namespace Scribe.Core.Hotkeys;

/// <summary>
/// What the keyboard callback reads from the KBDLLHOOKSTRUCT its lParam points at, and how it routes the event
/// (<see cref="Route"/>): whether it is Scribe's own input, the watchdog's probe among it, which it swallows once counted;
/// whether it is the echo of an event this thread is already passing on (<see cref="KeyEventPassOn"/>); and otherwise the
/// engine's decision, which a registration a move ahead replaced may not make a swallow. Like the callback, nothing here
/// takes a lock, logs or allocates, and the only call into Windows is the engine's own, as it always was.
/// </summary>
internal static class KeyboardHookFilter
{
    // KBDLLHOOKSTRUCT: vkCode, scanCode, flags and time are DWORDs at 0, 4, 8 and 12, and dwExtraInfo, a ULONG_PTR, follows
    // at 16 in both 32-bit and 64-bit processes (four DWORDs already align it). Written out, as MouseHookFilter's are, so
    // the first callback runs no reflection; KeyboardHookFilterTests pins them against the struct.
    internal const int VkCodeOffset = 0;
    internal const int ScanCodeOffset = 4;
    internal const int FlagsOffset = 8;
    internal const int TimeOffset = 12;
    internal const int ExtraInfoOffset = 16;

    /// <summary>
    /// Whether this is the watchdog's liveness probe: Scribe's own marker-tagged key-up of the unassigned VK_PROBE
    /// (<c>HotkeyService.ProbeKeyboardHookLocked</c>). The callback counts it, as every callback is counted, and then
    /// swallows it, so it goes no further than the first of Scribe's registrations it reaches: passed on, a Remote Desktop
    /// client's hook behind Scribe's would forward a key-up with scan code 0 into the remote session every watchdog
    /// period. Nothing else is swallowed for carrying the marker: text Scribe types, and every key-down, pass as before.
    /// </summary>
    public static bool IsProbe(uint virtualKey, bool isUp, nuint extraInfo) =>
        isUp && virtualKey == NativeMethods.VK_PROBE && IsScribesOwn(extraInfo);

    /// <summary>
    /// Whether a key event carries Scribe's own marker (<see cref="SyntheticInputMarker"/>): the whole value, or exactly its
    /// low half. Windows keeps only the low 32 bits of a mouse event's extra information (measured by stream MB on both CI
    /// runners), while a keyboard hook is handed the whole value (measured on both, stream RD); the low half is accepted in
    /// case a later Windows narrows the keyboard's too. Anything else, a value that shares only the low half included, is
    /// not Scribe's.
    /// </summary>
    public static bool IsScribesOwn(nuint extraInfo) =>
        extraInfo == SyntheticInputMarker.Value || extraInfo == (nuint)(uint)SyntheticInputMarker.Value;

    /// <summary>
    /// What the keyboard callback does with one key event, before CallNextHookEx: swallow it, or pass it on, and if so
    /// whether the pass is recorded (<see cref="KeyEventPassOn"/>) because the engine judged it, or not because it is
    /// Scribe's own input or the echo of an event already judged; and the key view epoch of a leaked-key repair it asks for
    /// (zero for none). In order:
    /// <list type="bullet">
    /// <item>Scribe's own input passes unjudged, except the watchdog's probe, which stops here (<see cref="IsProbe"/>).</item>
    /// <item>An echo passes unjudged: the event one of this thread's passes is handing on, back through a registration a
    /// move ahead replaced, further down the chain. Only a replaced registration is asked: an event entering the current
    /// registration is always a new one, whatever it matches.</item>
    /// <item>Anything else is judged by the engine. Through the current registration it may be swallowed. Through a
    /// replaced one it may not (<see cref="HotkeyEngine.OnKeyEvent"/> with mayBeSwallowed false): such an event entered
    /// the chain before the move and has passed every hook registered between the two registrations, which may have
    /// forwarded it into a remote session, so the rest of its keystroke has to reach them too; judged, it still starts or
    /// ends a dictation and keeps the key view whole. The engine is handed the event's time stamp too: for a while after the
    /// hook becomes the newest registration it swallows no key-down of a key it has not seen, and none of the rest of that
    /// keystroke (<see cref="HotkeyEngine.OnRegisteredAhead"/>).</item>
    /// </list>
    /// Nothing here takes a lock, logs or allocates; the engine's own path is the keyboard callback's as it always was,
    /// GetAsyncKeyState included where ChordStateMachine describes it.
    /// </summary>
    /// <param name="throughCurrentRegistration">
    /// Whether the event came through the installation's current keyboard hook registration rather than one a move ahead
    /// replaced and still keeps.
    /// </param>
    public static KeyboardHookRoute Route(
        HotkeyEngine engine,
        KeyEventPassOn passOn,
        bool throughCurrentRegistration,
        in KeyEventIdentity identity,
        bool isDown,
        nuint extraInfo)
    {
        if (IsScribesOwn(extraInfo))
        {
            return new KeyboardHookRoute(Swallow: IsProbe(identity.VirtualKey, !isDown, extraInfo), TrackPass: false, Echo: false, RepairAt: 0);
        }

        // Only a replaced registration can be handed an echo: it sits further down the chain than the current one, inside
        // whose CallNextHookEx the event it passed on travels. An event entering the current registration is always new,
        // even one that matches a pass on the stack field for field (review round 2, A2): KEYBDINPUT.time is the caller's,
        // so a key remapper behind Scribe's hook can synthesize a press and a release identical to an event Scribe is still
        // passing on, and injected input enters the chain at its head. Taken for an echo, that release never reached the
        // engine, and a hold it ended kept recording.
        if (!throughCurrentRegistration && passOn.IsEcho(identity))
        {
            return new KeyboardHookRoute(Swallow: false, TrackPass: false, Echo: true, RepairAt: 0);
        }

        var decision = engine.OnKeyEvent(
            identity.VirtualKey, isDown, mayBeSwallowed: throughCurrentRegistration, eventTime: identity.Time);
        return new KeyboardHookRoute(
            Swallow: decision.Suppress,
            TrackPass: !decision.Suppress,
            Echo: false,
            RepairAt: decision.RequestReconcile ? engine.KeyViewEpoch : 0);
    }

    /// <summary>The event's identity (<see cref="KeyEventIdentity"/>), four direct reads.</summary>
    public static KeyEventIdentity Identity(nint lParam) => new(
        (uint)Marshal.ReadInt32(lParam, VkCodeOffset),
        (uint)Marshal.ReadInt32(lParam, ScanCodeOffset),
        (uint)Marshal.ReadInt32(lParam, FlagsOffset),
        (uint)Marshal.ReadInt32(lParam, TimeOffset));
}

/// <summary>
/// What the keyboard callback does with one key event (<see cref="KeyboardHookFilter.Route"/>): swallow it, or pass it on,
/// recording the pass when the engine judged the event; whether it was the echo of an event already judged; and the key
/// view epoch of the leaked-key repair it asks for, zero for none.
/// </summary>
internal readonly record struct KeyboardHookRoute(bool Swallow, bool TrackPass, bool Echo, long RepairAt);

/// <summary>
/// One keyboard event as a low-level hook receives it: its virtual key, scan code, flags and time stamp. Windows hands every
/// hook in the chain the same event, so two of Scribe's registrations see the same four values for one event. Two events
/// usually differ in at least one of them (a key's release differs from its press in its flags, LLKHF_UP, and a physical
/// key's repeat in its time), but not always: KEYBDINPUT.time is whatever the injecting program passes, so two injected
/// events can match field for field. That is why only a registration a move replaced ever takes an event for an echo
/// (<see cref="KeyboardHookFilter.Route"/>). Compared field by field, with no equality comparer, so the comparison
/// allocates nothing.
/// </summary>
internal readonly struct KeyEventIdentity(uint virtualKey, uint scanCode, uint flags, uint time)
{
    public uint VirtualKey { get; } = virtualKey;

    public uint ScanCode { get; } = scanCode;

    public uint Flags { get; } = flags;

    public uint Time { get; } = time;

    public static bool Same(in KeyEventIdentity a, in KeyEventIdentity b) =>
        a.VirtualKey == b.VirtualKey && a.ScanCode == b.ScanCode && a.Flags == b.Flags && a.Time == b.Time;
}

/// <summary>
/// Hook thread only: the key events this installation's keyboard callback is passing on right now, one per
/// CallNextHookEx on the thread's stack. While a move ahead keeps a replaced registration a while (see
/// <c>HotkeyService.HookInstallation</c>), an event the new registration decided and passed on comes back through the
/// replaced one, further down the chain, inside that CallNextHookEx: the thread is handed it while it waits. Such an echo
/// is passed through untouched, so no event is decided twice; an event that reached only the replaced registration (it
/// entered the chain before the new one existed) is not an echo, and is decided there, so no event is missed either.
/// A different event can be handed to the thread inside a pass too (injected input runs the hooks in the injecting
/// thread's context, beside physical input), so every pass on the stack is checked, not only the innermost. The buffer
/// is made with the installation, never on the callback path; passes nested deeper than it are counted but not
/// recorded, and an echo of one of those is decided again, which the chord machines' state makes a no-op.
/// </summary>
internal sealed class KeyEventPassOn
{
    /// <summary>How many nested passes are recorded.</summary>
    public const int MaxDepth = 8;

    private readonly KeyEventIdentity[] _passing = new KeyEventIdentity[MaxDepth];
    private int _depth;

    /// <summary>How many passes are on the stack, recorded or not.</summary>
    public int Depth => _depth;

    /// <summary>Whether <paramref name="identity"/> is an event one of the passes on the stack is passing on.</summary>
    public bool IsEcho(in KeyEventIdentity identity)
    {
        var recorded = _depth < MaxDepth ? _depth : MaxDepth;
        for (var i = 0; i < recorded; i++)
        {
            if (KeyEventIdentity.Same(_passing[i], identity))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A pass of <paramref name="identity"/> begins: right before its CallNextHookEx.</summary>
    public void Enter(in KeyEventIdentity identity)
    {
        if (_depth < MaxDepth)
        {
            _passing[_depth] = identity;
        }

        _depth++;
    }

    /// <summary>The innermost pass ended: its CallNextHookEx returned.</summary>
    public void Leave() => _depth--;
}
