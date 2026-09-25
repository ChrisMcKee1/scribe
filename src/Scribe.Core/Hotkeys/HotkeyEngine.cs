using Scribe.Core.Models;

namespace Scribe.Core.Hotkeys;

internal enum HotkeyCommandKind
{
    UpdateBindings,
    SetCaptureMode,
    SetPaused,
    CancelToggle,
}

/// <summary>
/// A change another thread asks the hook thread to make. It is built on the requesting thread, so
/// the allocation never happens inside the hook callback, and the engine applies commands in the
/// order they were requested. <paramref name="Generation"/> is the state epoch in force once the
/// command has been applied, and <paramref name="Activation"/> the press a CancelToggle releases.
/// </summary>
internal sealed record HotkeyCommand(
    HotkeyCommandKind Kind,
    long Generation,
    HotkeyBinding? Binding = null,
    HotkeyBinding? DictationOnlyBinding = null,
    bool Enabled = false,
    long Activation = 0)
{
    public static HotkeyCommand Bindings(HotkeyBinding binding, HotkeyBinding? dictationOnlyBinding, long generation) =>
        new(HotkeyCommandKind.UpdateBindings, generation, binding, dictationOnlyBinding);

    public static HotkeyCommand CaptureMode(bool enabled, long generation) =>
        new(HotkeyCommandKind.SetCaptureMode, generation, Enabled: enabled);

    public static HotkeyCommand Paused(bool paused, long generation) =>
        new(HotkeyCommandKind.SetPaused, generation, Enabled: paused);

    public static HotkeyCommand CancelToggle(long generation, long activation) =>
        new(HotkeyCommandKind.CancelToggle, generation, Activation: activation);
}

/// <summary>
/// What a hook callback does with one key or button event: whether to swallow it, whether to ask for the leaked-key
/// repair (a key or button release the bindings swallowed, <see cref="RequestReconcile"/>), and, for the mouse hook
/// alone, whether to ask only for the mouse hook's sync (an event that settled a release owed to a swallowed press,
/// <see cref="RequestMouseHookSync"/>), which never repairs a key.
/// </summary>
internal readonly record struct HookDecision(bool Suppress, bool RequestReconcile, bool RequestMouseHookSync = false);

/// <summary>
/// Everything the low-level keyboard and mouse hook callbacks touch, owned by one hook thread for the lifetime of one
/// hook installation. Both hooks run on that thread and feed this one engine, so a chord of a key and a mouse button,
/// the modifier rule, pause, capture and a desktop switch see keys and buttons as one input state.
///
/// Only the owner thread mutates this state. It applies queued commands at the start of every key
/// event and whenever it is woken, so the callback never waits for another thread: a monitor shared
/// with settings reads and configuration changes used to sit on this path, and the callback races
/// an OS deadline (LowLevelHooksTimeout) that silently removes the hook when it is missed. Other
/// threads reach the engine only through <see cref="Post"/>, which never blocks, and
/// <see cref="IsPressed"/>, which reads published state. A hook reinstall builds a new engine rather
/// than handing this one to the new thread, so a previous hook thread that has not exited yet can
/// never share state with its replacement, and it retires this one (<see cref="Retire"/>), so that
/// thread can no longer swallow a key or start or stop a dictation either.
/// </summary>
internal sealed class HotkeyEngine
{
    // Numbers every press that claims a dictation, across every installation and service in the process, so a release
    // asked for one press can never match a newer press, on this engine or a replacement. Only hook threads take a
    // number, with one interlocked add; it never wraps in practice (2^55 presses).
    private static long _lastActivation;

    // Numbers every key view an engine has had, across every engine in the process, so a value names one view of one
    // engine: a new engine takes a fresh one, and so does each clear of an engine's view (see KeyViewEpoch).
    private static long _lastKeyViewEpoch;

    private readonly LockFreeInbox<HotkeyCommand> _commands = new();
    private readonly HotkeyTriggerArbiter _arbiter = new();
    private readonly HotkeyTransitionQueue _transitions;
    private readonly Func<uint, bool>? _isLogicallyDown;
    private readonly Func<uint, bool>? _buttonDownInWindows;
    private readonly ChordStateMachine _standard;
    private ChordStateMachine? _dictationOnly;
    private bool _captureMode;
    private bool _paused;
    private bool _retired;
    private bool _usesMouseButtons;
    private long _generation;
    private long _appliedCaptureGeneration;
    private long _keyViewEpoch;
    private long _activationEpoch;
    private long _desktopSwitches;
    private long _desktopSwitchNotices;
    private long _mouseHookLossesHandled;
    private long _mouseButtonEvents;
    private int _wakePending;
    private uint _ownerThreadId;

    // What makes a key-down uncertain once Scribe's hook has become the newest registration (OnRegisteredAhead; review round
    // 2, item 1): when that happened, on the tick-count clock key events are stamped with; whether the window it opened may
    // still be open; the keys seen go up since; and how long the window is (the user's keyboard repeat settings,
    // KeyRepeatTiming), which the service publishes from another thread. Everything else is the owner thread's alone.
    private readonly KeySet _releasedSinceAhead = new();
    private uint _aheadSince;
    private bool _uncertaintyArmed;
    private int _uncertaintyWindowMs = KeyRepeatTiming.WorstCaseWindowMs;
    private long _uncertainPresses;

    // The mouse buttons whose press was swallowed and whose release has not been seen since, one bit per button code, and
    // SealedDebts once the engine is retired. A new press of the button retires its debt, since buttons never repeat: the
    // release went up where no hook could see it. A time no hook saw the mouse (a mouse hook found gone, the gap a
    // reinstall leaves) drops every debt: a press may have reached Windows then, and nothing the callback can read tells
    // that press from a swallowed one when Windows' answer may have failed. A debt that stays is decided when its release
    // is made, on Windows' own view of the button (WindowsHoldsButton), and DefWindowProc is why it matters: it turns a
    // side button's lone release into a Back or Forward command. One word, changed by compare-exchange, so a debt is
    // either committed before the retirement seals it, or refused, and then its press is not swallowed either (see
    // OnMouseButtonEvent).
    private int _owedButtonReleases;

    private const int SealedDebts = 1 << 30;
    private const int ButtonDebts = (1 << (int)MouseButtons.Middle) | (1 << (int)MouseButtons.Back) | (1 << (int)MouseButtons.Forward);

    /// <param name="isLogicallyDown">
    /// Windows' view of a key, which each binding's machine asks about a modifier its own view holds (see
    /// <see cref="ChordStateMachine"/>). Null trusts the hook's view alone.
    /// </param>
    /// <param name="buttonDownInWindows">
    /// Windows' own view of a mouse button, GetAsyncKeyState's high bit in production, which the callback reads for the
    /// release of a press this engine swallowed (<see cref="OnMouseButtonEvent"/>). The delegate is made before any hook
    /// exists, by whoever builds the router, and the callback only invokes it. Null for the tests that do not script
    /// Windows, where the engine's own state decides: the release is swallowed.
    /// </param>
    /// <param name="appliedCaptureGeneration">
    /// The generation of the latest capture request, which <paramref name="captureMode"/> already reflects
    /// (<see cref="AppliedCaptureGeneration"/>).
    /// </param>
    public HotkeyEngine(
        HotkeyBinding binding,
        HotkeyBinding? dictationOnlyBinding,
        bool captureMode,
        bool paused,
        long generation,
        HotkeyTransitionQueue transitions,
        Func<uint, bool>? isLogicallyDown = null,
        Func<uint, bool>? buttonDownInWindows = null,
        long appliedCaptureGeneration = 0)
    {
        _transitions = transitions;
        _isLogicallyDown = isLogicallyDown;
        _buttonDownInWindows = buttonDownInWindows;
        _captureMode = captureMode;
        _paused = paused;
        _generation = generation;
        _appliedCaptureGeneration = appliedCaptureGeneration;
        _keyViewEpoch = Interlocked.Increment(ref _lastKeyViewEpoch);
        _standard = CreateMachine(binding);
        _dictationOnly = dictationOnlyBinding is null ? null : CreateMachine(dictationOnlyBinding);
        _usesMouseButtons = MouseButtons.Uses(binding) || MouseButtons.Uses(dictationOnlyBinding);
    }

    /// <summary>
    /// Any thread: the buttons whose press this engine swallowed and whose release it has not seen, as bits by code
    /// (1 &lt;&lt; <see cref="MouseButtons.Back"/> and so on). After <see cref="Retire"/> it no longer changes.
    /// </summary>
    public int OwedButtonReleases => Volatile.Read(ref _owedButtonReleases) & ButtonDebts;

    /// <summary>
    /// Any thread: whether a swallowed button press still owes its release, so the hook thread keeps a drain-only mouse
    /// hook for it even once no binding presses a mouse button.
    /// </summary>
    public bool OwesButtonRelease => OwedButtonReleases != 0;

    // Owner thread, between messages: a time no hook saw the mouse ends. Every release still owed is dropped, and so reaches
    // the app when it is made (fail-open): a press may have reached Windows while no hook saw the mouse, and the one read
    // the callback makes cannot tell that press from a swallowed one when the read itself may have failed. At worst that
    // release is one the app never needed, for Back or Forward one navigation; never one Windows is left waiting for.
    // Sealed, nothing changes.
    private void DropOwedReleases()
    {
        var debts = Volatile.Read(ref _owedButtonReleases);
        while ((debts & SealedDebts) == 0 && (debts & ButtonDebts) != 0)
        {
            var observed = Interlocked.CompareExchange(ref _owedButtonReleases, debts & ~ButtonDebts, debts);
            if (observed == debts)
            {
                return;
            }

            debts = observed;
        }
    }

    // Owner thread, for a press the machines swallow: commits the debt of its release unless the retirement sealed the
    // debts first, which is the one way this returns false.
    private bool TryOweRelease(uint button)
    {
        var bit = 1 << (int)button;
        var debts = Volatile.Read(ref _owedButtonReleases);
        while ((debts & SealedDebts) == 0)
        {
            var observed = Interlocked.CompareExchange(ref _owedButtonReleases, debts | bit, debts);
            if (observed == debts)
            {
                return true;
            }

            debts = observed;
        }

        return false;
    }

    // Owner thread: a release, or a new press, of this button pays or forgives its debt. Returns whether one was owed.
    // Sealed, the debts stay as they are, but one owed is still reported. That matters only in the in-flight window: a
    // callback this engine admitted before the retirement and is still running, whose release is then judged as an owed
    // one rather than passed on alone. A callback that starts after the retirement returns at the top of
    // OnMouseButtonEvent and swallows nothing.
    private bool SettleRelease(uint button)
    {
        var bit = 1 << (int)button;
        var debts = Volatile.Read(ref _owedButtonReleases);
        while ((debts & bit) != 0 && (debts & SealedDebts) == 0)
        {
            var observed = Interlocked.CompareExchange(ref _owedButtonReleases, debts & ~bit, debts);
            if (observed == debts)
            {
                return true;
            }

            debts = observed;
        }

        return (debts & bit) != 0;
    }

    // Owner thread, in the mouse hook callback, for the release of a press this engine swallowed: whether Windows holds
    // the button, which lets that release through. One GetAsyncKeyState, through a delegate made before any hook existed.
    // The callback runs before Windows applies this release, and Windows takes input one event at a time, so the answer
    // shows every earlier press: a button Windows holds received a press this hook did not swallow (a second mouse, the
    // secure desktop, an event lost in a renewal's swap), and its release must reach the app, or the app keeps the button
    // down. Anything else, up or a read that failed and returned zero, keeps the release swallowed: no time without the
    // hook has passed since the press (such a time drops the debt), so every press since went through this hook. A zero
    // that is a failed read while Windows does hold the button leaves it down there until a later release of it is read
    // as down.
    private bool WindowsHoldsButton(uint button) => _buttonDownInWindows?.Invoke(button) == true;

    /// <summary>The owner's thread id once it has attached, otherwise zero. Any thread.</summary>
    public uint OwnerThreadId => Volatile.Read(ref _ownerThreadId);

    /// <summary>Any thread. True once a replacement took over the hook or the service stopped.</summary>
    public bool IsRetired => Volatile.Read(ref _retired);

    /// <summary>
    /// Any thread. The generation of the latest capture request (<see cref="HotkeyCommand.CaptureMode"/>) this engine has
    /// applied with both its machines, published only once the second has, so until then the router counts capture as
    /// owning input (<see cref="HotkeyCommandRouter.CaptureOwnsInput"/>): at capture's end both machines' views are
    /// empty, and the one that has left capture tracks keys again only from there.
    /// </summary>
    public long AppliedCaptureGeneration => Volatile.Read(ref _appliedCaptureGeneration);

    /// <summary>
    /// Any thread. Which key view this engine has now: a value no other view of any engine in the process has had. The
    /// owner takes a new one immediately before its machines clear or replace their keys (new bindings, capture's start
    /// and end, a desktop reset), and a new engine starts with its own (a reinstall), so a leaked-key repair asked for at
    /// one epoch judges only while the current engine's view still has it (review round 10, A12;
    /// <c>HotkeyService.KeyViewIsWhole</c>).
    /// </summary>
    public long KeyViewEpoch => Volatile.Read(ref _keyViewEpoch);

    /// <summary>
    /// Test seam, null in production: run by the owner between the two machines' capture changes, where a test pauses
    /// the change it is applying.
    /// </summary>
    internal Action? BetweenCaptureMachinesForTests { get; set; }

    /// <summary>
    /// Test seam, null in production: run by the owner right after its machines have cleared or replaced their keys (new
    /// bindings, capture's start or end, a desktop reset), where a test checks that the view's new epoch is already out.
    /// </summary>
    internal Action? AfterKeyViewClearedForTests { get; set; }

    /// <summary>
    /// Any thread. Whether a binding this engine applies presses a mouse button (<see cref="MouseButtons.Uses"/>). It is
    /// one of the two reasons the hook thread keeps a mouse hook, not the only one: the hook is installed while
    /// <c>HotkeyService.HookInstallation.MouseHookWanted</c> holds, which is this or a release still owed
    /// (<see cref="OwesButtonRelease"/>), so nobody who binds keys alone gets a system-wide mouse hook except to drain
    /// such a release. Updated by the owner when it applies new bindings.
    /// </summary>
    public bool UsesMouseButtons => Volatile.Read(ref _usesMouseButtons);

    /// <summary>
    /// Any thread, for tests: how many middle and side button events reached this engine from the mouse hook. Moves,
    /// wheels and the left and right buttons never do.
    /// </summary>
    internal long MouseButtonEvents => Interlocked.Read(ref _mouseButtonEvents);

    /// <summary>
    /// Any thread. The hook's view of the physical keyboard, for the leaked-key reconciler: true
    /// while a key the hook saw go down has not come back up.
    /// </summary>
    public bool IsPressed(uint virtualKey) =>
        _standard.IsPressed(virtualKey) || Volatile.Read(ref _dictationOnly)?.IsPressed(virtualKey) == true;

    /// <summary>Any thread. How many desktop switches the owner has applied.</summary>
    public long DesktopSwitches => Interlocked.Read(ref _desktopSwitches);

    /// <summary>Any thread, for tests: how many times the owner was told its mouse hook had been found removed.</summary>
    internal long MouseHookLossesHandled => Interlocked.Read(ref _mouseHookLossesHandled);

    /// <summary>
    /// Owner thread, between messages: the mouse hook's renewal found the previous registration already gone, which is
    /// how a hook Windows removed after a missed deadline shows (the <c>LowLevelMouseProc</c> documentation: such a hook
    /// is removed silently, with no way for the application to know). Until the renewal no hook saw the mouse, so a
    /// button the machines hold may have been released unseen, and a toggle's second click may have gone to the app.
    /// The Core transition that follows keeps every guarantee the other state clears keep: commands requested before it
    /// apply first; every release still owed is dropped, so it reaches the app when it is made, since a press may have
    /// reached Windows while no hook saw the mouse and the one read the callback makes cannot tell that press from a
    /// swallowed one when the read may have failed (review round 7: at worst one stray release, for Back or Forward one
    /// navigation, never a button Windows is left holding); the machines forget their mouse buttons,
    /// and a binding that presses one gives up its latch (<see cref="ChordStateMachine.ForgetMouseButtons"/>); and if the
    /// arbiter's owner is a binding that presses a mouse button, this engine's activation epoch advances first, so its
    /// Activated still waiting for the dispatcher can never open the microphone, and then the owner is released and its
    /// dictation ended, reported as <see cref="HotkeyDeactivation.MouseHookLost"/>. A keyboard binding's dictation, its
    /// queued Activated included, is left alone: the keyboard hook saw everything. A retired engine does nothing, as for
    /// a desktop switch.
    /// </summary>
    public void OnMouseHookLost()
    {
        if (!IsOwnerCall())
        {
            return;
        }

        ApplyPendingCommands();
        Interlocked.Increment(ref _mouseHookLossesHandled);
        DropOwedReleases();
        _standard.ForgetMouseButtons();
        _dictationOnly?.ForgetMouseButtons();

        // One owner at most, so one of these ends it, or neither does.
        if (!EndDictationOf(HotkeyTrigger.Standard, _standard.UsesMouseButtons))
        {
            _ = EndDictationOf(HotkeyTrigger.DictationOnly, _dictationOnly?.UsesMouseButtons == true);
        }
    }

    // The owner, if it is this trigger and its binding presses a mouse button: the epoch first, so its queued Activated is
    // stale before the stop that follows it is queued.
    private bool EndDictationOf(HotkeyTrigger trigger, bool pressesAMouseButton)
    {
        if (!pressesAMouseButton || !_arbiter.TryDeactivate(trigger))
        {
            return false;
        }

        Interlocked.Increment(ref _activationEpoch);
        EmitStateClear(HotkeyTransition.Deactivated, trigger, HotkeyDeactivation.MouseHookLost);
        return true;
    }

    /// <summary>
    /// Any thread. The epoch this engine's own queued activations are judged by (see <see cref="HotkeyService.ShouldDispatch"/>):
    /// the owner advances it when it applies a desktop switch, before anything it queues for the switch. It belongs to this
    /// installation alone. A retired engine's hook thread can still be finishing a switch it noticed before the reinstall;
    /// with one epoch shared by every installation, that late switch discarded the replacement's genuine press. Now it
    /// moves only this engine's epoch, which judges only activations its retirement had already made stale.
    /// </summary>
    public long ActivationEpoch => Interlocked.Read(ref _activationEpoch);

    /// <summary>
    /// Any thread. How many desktop-switch notices reached the owner, applied or not, for a test of the service's wiring.
    /// </summary>
    public long DesktopSwitchNotices => Interlocked.Read(ref _desktopSwitchNotices);

    /// <summary>
    /// Owner thread, for tests: whether a press still owns the dictation, or either machine holds a hold or toggle latch.
    /// False means the next press of either binding starts afresh.
    /// </summary>
    internal bool HoldsAnyLatch =>
        _arbiter.HasOwner || _standard.IsLatched || Volatile.Read(ref _dictationOnly)?.IsLatched == true;

    /// <summary>
    /// Owner thread, between messages: whether a key whose press a machine swallowed is still held, so its release will be
    /// swallowed too. A move ahead of another program's keyboard hook waits while one is (<c>HotkeyService.HookInstallation</c>):
    /// that hook may have seen the press, and would then never see the release.
    /// </summary>
    public bool HoldsSwallowedKey => _standard.HoldsSwallowedKey || Volatile.Read(ref _dictationOnly)?.HoldsSwallowedKey == true;

    /// <summary>
    /// Owner thread, between messages: Scribe's keyboard hook has just become the newest registration in the chain (a move
    /// ahead, or the registration a reinstall makes), at <paramref name="tick"/> on the clock key events are stamped with
    /// (the tick count: KBDLLHOOKSTRUCT.time is "equivalent to what GetMessageTime would return", the milliseconds "from
    /// the time the system was started"). A hook ahead of the old registration may have forwarded a key's press into a
    /// remote session and kept it, so that neither this engine nor GetAsyncKeyState knows the key is held (a kept
    /// low-level event never reaches the asynchronous state); its next autorepeat now reaches this engine first. So for
    /// <see cref="UncertaintyWindowMs"/> from here, a key-down of a key this engine neither holds nor has seen go up since is
    /// uncertain (<see cref="OnKeyEvent"/>): judged, so a dictation still starts or ends, but never swallowed, nor is the
    /// rest of its keystroke, so the hook that forwarded the press also gets the release. A key seen going up since, and
    /// any key once the window is over, is judged as ever.
    /// </summary>
    public void OnRegisteredAhead(uint tick)
    {
        _aheadSince = tick;
        _uncertaintyArmed = true;
        _releasedSinceAhead.Clear();
    }

    /// <summary>
    /// Any thread (the service, off the hook thread): how long the window <see cref="OnRegisteredAhead"/> opens lasts, in
    /// milliseconds (<see cref="KeyRepeatTiming"/>); zero or less opens none.
    /// </summary>
    public void SetUncertaintyWindow(int windowMs) => Volatile.Write(ref _uncertaintyWindowMs, windowMs);

    /// <summary>Any thread: the window's length.</summary>
    public int UncertaintyWindowMs => Volatile.Read(ref _uncertaintyWindowMs);

    /// <summary>For tests, read after a barrier: when the hook last became the newest registration (the tick count).</summary>
    internal uint AheadSince => _aheadSince;

    /// <summary>Any thread, for tests: how many key-downs were judged uncertain and so passed with their whole keystroke.</summary>
    internal long UncertainPresses => Interlocked.Read(ref _uncertainPresses);

    // Owner thread, on the keyboard callback's path: two field reads, a subtraction, and for a key-down two bit tests; for a
    // release inside the window one bit set. No lock, no allocation, no call into Windows. The time difference is taken as
    // signed, so the tick count's wrap is ignored (GetMessageTime: "subtract the time of the first message from the time of
    // the second message (ignoring overflow)") and an event stamped a little before the registration, an autorepeat that
    // was on its way when the hook moved, counts as inside the window. The first event stamped past the window closes it,
    // so a time stamp far from the registration's cannot open it again, a wrap of the tick count included.
    private bool IsUncertain(uint virtualKey, bool isDown, uint eventTime)
    {
        var window = Volatile.Read(ref _uncertaintyWindowMs);
        if (window <= 0 || unchecked((int)(eventTime - _aheadSince)) >= window)
        {
            _uncertaintyArmed = false;
            return false;
        }

        if (!isDown)
        {
            _releasedSinceAhead.Add(virtualKey);
            return false;
        }

        if (IsPressed(virtualKey) || _releasedSinceAhead.Contains(virtualKey))
        {
            return false;
        }

        Interlocked.Increment(ref _uncertainPresses);
        return true;
    }

    /// <summary>
    /// Owner thread: an <c>EVENT_SYSTEM_DESKTOPSWITCH</c> notice arrived. It is only a reason to check again: it also
    /// arrives for the switch back, and any process can raise it with <c>NotifyWinEvent</c> (this repository's own wiring
    /// test does). So the switch is applied (<see cref="OnDesktopSwitch"/>) only when this thread's desktop has stopped
    /// receiving input, which the lock screen, Ctrl+Alt+Del and the UAC secure desktop all cause; the switch back, a stray
    /// notice while this desktop still receives input, and an answer <paramref name="desktopReceivesInput"/> cannot give
    /// (null) apply nothing. The notice is counted before the check, and only on the owner thread.
    /// </summary>
    /// <param name="desktopReceivesInput">
    /// Whether this thread's desktop is receiving input (GetUserObjectInformation with UOI_IO in the service), or null
    /// when that cannot be told.
    /// </param>
    public void OnDesktopSwitchNotice(Func<bool?> desktopReceivesInput)
    {
        if (!IsOwnerCall())
        {
            return;
        }

        Interlocked.Increment(ref _desktopSwitchNotices);
        if (desktopReceivesInput() == false)
        {
            ApplyDesktopSwitch();
        }
    }

    /// <summary>
    /// Owner thread: the input desktop switched away, to the lock screen or a secure desktop. The hook is not called
    /// for input there, so any key held as the desktop switched can be released unseen. Every machine forgets its key
    /// state, as a hook reinstall does, so a stale key can neither block a bare Page Up or Page Down (a Narrator key
    /// released on the lock screen) nor swallow the next press as if it were an autorepeat; and the dictation the arbiter
    /// says this engine started, if any, is ended the way its release or second press would have ended it, reported as
    /// <see cref="HotkeyDeactivation.DesktopSwitch"/>, so the microphone does not keep recording while the PC is locked.
    /// An Activated this engine queued that is still waiting for the dispatcher is invalidated first, through this engine's
    /// activation epoch (<see cref="ActivationEpoch"/>), so it cannot open the microphone after the lock. A switch with
    /// nothing recording starts and stops nothing. The release of a mouse button whose press was swallowed stays owed
    /// (see <see cref="OnMouseButtonEvent"/>): a keyboard key's lone release is harmless, but DefWindowProc makes a side
    /// button's lone release a Back or Forward command. The service reaches this through
    /// <see cref="OnDesktopSwitchNotice"/>,
    /// from the WinEvent callback that runs on the thread that set the hook, which is the owner; a call from any other
    /// thread once an owner is attached is ignored rather than allowed to race the keyboard callback.
    /// </summary>
    public void OnDesktopSwitch()
    {
        if (IsOwnerCall())
        {
            ApplyDesktopSwitch();
        }
    }

    // Not retired, and on the owner thread once one is attached (the harness has none, and calls as the owner).
    private bool IsOwnerCall()
    {
        var owner = OwnerThreadId;
        return !IsRetired && (owner == 0 || owner == NativeMethods.GetCurrentThreadId());
    }

    private void ApplyDesktopSwitch()
    {
        // Commands requested before the switch took effect before it, as for a key event. Only this engine's epoch moves:
        // this can run on a retired engine whose hook thread noticed the switch before a reinstall, and the replacement's
        // activations are judged by the replacement's own epoch.
        ApplyPendingCommands();
        Interlocked.Increment(ref _activationEpoch);

        // Both machines are reset whatever they report: one can still hold a press the arbiter refused, or a toggle it
        // ignored, after the dictation it lost to has ended. Only the arbiter's real owner, if there is one, is stopped.
        // A release still owed to a swallowed button press survives the reset (the engine keeps those, not the
        // machines): a button held as the desktop switched and let go once it is back must reach no app, or a side
        // button's release navigates it.
        NewKeyView();
        _ = _standard.Reset();
        _ = _dictationOnly?.Reset();
        AfterKeyViewClearedForTests?.Invoke();
        if (_arbiter.TryTakeActive() is { } active)
        {
            EmitStateClear(HotkeyTransition.Deactivated, active, HotkeyDeactivation.DesktopSwitch);
        }

        Interlocked.Increment(ref _desktopSwitches);
    }

    /// <summary>
    /// Any thread. Queues a command without blocking. Returns true when the caller must wake the
    /// owner, which is at most once per wake the owner has not consumed yet: each thread's queue
    /// holds a finite number of posted messages, so wakes are coalesced rather than sent per command.
    /// </summary>
    public bool Post(HotkeyCommand command)
    {
        _commands.Push(command);
        return Interlocked.Exchange(ref _wakePending, 1) == 0;
    }

    /// <summary>
    /// Any thread. The wake could not be delivered (no owner yet, or the thread message was
    /// refused). The next <see cref="Post"/> asks again, and the owner applies everything queued at
    /// its next key event regardless.
    /// </summary>
    public void CancelWake() => Volatile.Write(ref _wakePending, 0);

    /// <summary>Owner thread, once, as soon as its hook is installed.</summary>
    public void AttachOwner(uint threadId)
    {
        // Publish the id before draining: a command posted after this drain then sees an owner it
        // can wake, and one posted before it is picked up by the drain itself.
        Volatile.Write(ref _ownerThreadId, threadId);
        OnWake();
    }

    /// <summary>Owner thread, when its message loop ends. Stops other threads posting to it.</summary>
    public void DetachOwner() => Volatile.Write(ref _ownerThreadId, 0);

    /// <summary>Owner thread: a wake message arrived.</summary>
    public void OnWake()
    {
        // Cleared before draining, so a command posted during the drain asks for a fresh wake
        // instead of relying on the one being consumed.
        Interlocked.Exchange(ref _wakePending, 0);
        if (!IsRetired)
        {
            ApplyPendingCommands();
        }
    }

    /// <summary>Owner thread: one key event from the keyboard hook callback.</summary>
    /// <param name="mayBeSwallowed">
    /// False for an event that reached a keyboard hook registration a move ahead replaced without passing the current one
    /// (<c>KeyboardHookFilter.Route</c>): it is judged, so the key view stays whole and a dictation can start or end, but
    /// nothing is swallowed, now or for the rest of the keystroke. An uncertain key-down (see <see cref="OnRegisteredAhead"/>)
    /// is judged the same way.
    /// </param>
    /// <param name="eventTime">The event's time stamp (KBDLLHOOKSTRUCT.time, the tick-count clock).</param>
    public HookDecision OnKeyEvent(uint virtualKey, bool isDown, bool mayBeSwallowed = true, uint eventTime = 0)
    {
        // Retired: the replacement engine decides now, or nobody does after Stop. A hook thread
        // that outlived its join still reaches here, and swallowing on its stale bindings or pause
        // state would hide keys the current engine let through.
        if (IsRetired)
        {
            return default;
        }

        // A keyboard event carrying a mouse button's code is not that button, and only injected input can send one: the
        // mouse hook alone reports buttons, so the two hooks never overwrite each other's view of one. No binding a
        // capture could make was ever matched this way, because no keyboard key has these codes.
        if (MouseButtons.IsMouseButton(virtualKey))
        {
            return default;
        }

        if (_uncertaintyArmed && IsUncertain(virtualKey, isDown, eventTime))
        {
            mayBeSwallowed = false;
        }

        return OnInput(virtualKey, isDown, mayBeSwallowed);
    }

    /// <summary>
    /// Owner thread: the middle or a side mouse button (<see cref="MouseButtons.Middle"/>, <see cref="MouseButtons.Back"/>
    /// or <see cref="MouseButtons.Forward"/>) went down or up, from the mouse hook callback (<see cref="MouseHookFilter"/>).
    /// A button is one more key to the machines: it completes a binding, is swallowed while it drives a dictation, is
    /// let through while paused or capturing, and a bare binding of one lets a press made with a modifier through, as a
    /// bare Page Up or Page Down does (see <see cref="ChordStateMachine"/>). The engine itself keeps what the machines'
    /// resets must not lose, the release owed to a press it swallowed, which it judges even after a reset or in capture;
    /// a press is swallowed only once that debt is committed, so a press the retirement overtakes reaches the app whole,
    /// with its release. The release of a press it swallowed is swallowed too unless Windows holds the button
    /// (<see cref="WindowsHoldsButton"/>); a time no hook saw the mouse drops such debts beforehand
    /// (<see cref="OnMouseHookLost"/>, a reinstall), so those releases pass. The decision about a debt makes no delegate,
    /// takes no lock of Scribe's, waits for no other thread and allocates nothing: two compare-exchanges at most, and for
    /// such a release one GetAsyncKeyState, the only native call on this path, prelinked before either hook existed. What
    /// can allocate here is what the keyboard path allocates too, in <see cref="OnInput"/>: a node for each transition it
    /// queues (a press or release that starts or ends a dictation, or a state clear from a command it applies first) and
    /// a new machine when a command it applies first adds a dictation-only binding. A release the bindings swallowed asks
    /// for the leaked-key repair; an event that only settled a debt asks for the mouse hook's sync alone (see the end).
    /// </summary>
    public HookDecision OnMouseButtonEvent(uint button, bool isDown)
    {
        if (IsRetired || !MouseButtons.IsBindable(button))
        {
            return default;
        }

        Interlocked.Increment(ref _mouseButtonEvents);

        // A button never repeats, so every press is new: a release still owed to an earlier swallowed press went up
        // where no hook could see it. A release pays its own.
        var owed = SettleRelease(button);
        var decision = OnInput(button, isDown);
        var suppress = decision.Suppress;
        if (isDown)
        {
            if (suppress && !TryOweRelease(button))
            {
                // The retirement sealed the debts while this press was judged: its release goes to the replacement, which
                // owes it nothing, so the press must reach the app too, or the app gets a release with no press.
                suppress = false;
            }
        }
        else if (suppress || owed)
        {
            suppress = !WindowsHoldsButton(button);
        }

        // Two requests, kept apart (review round 8, A9). A button release asks for the keyboard's leak repair only when the
        // bindings themselves swallowed it (decision.Suppress, their pairing of it with a press they tracked since the last
        // state clear, so their view of the chord's keys is whole) and it is still swallowed. A release swallowed only for
        // a debt from before a state clear (capture, a desktop switch, new bindings) never asks: the clear forgot every
        // key the user still holds, and capture tracks none, so the repair would find a held modifier down in Windows and
        // not in the engine and send its key-up while the user holds it. Any event that settled a debt, a release that
        // paid it, swallowed or let through, or a new press that forgave it, asks for the mouse hook's sync, which removes
        // a drain-only hook kept for that debt at once rather than at the next watchdog period
        // (HotkeyService.RunReconcilePass); on its own that sync repairs no key.
        return new HookDecision(
            suppress,
            RequestReconcile: !isDown && suppress && decision.Suppress,
            RequestMouseHookSync: owed);
    }

    private HookDecision OnInput(uint virtualKey, bool isDown, bool mayBeSwallowed = true)
    {
        // Commands requested before this event took effect before it, exactly as if they had
        // been applied synchronously on the requesting thread.
        ApplyPendingCommands();

        var primary = _standard.Process(virtualKey, isDown, mayBeSwallowed);
        var secondary = _dictationOnly?.Process(virtualKey, isDown, mayBeSwallowed);
        EmitKeyTransition(primary.Transition, HotkeyTrigger.Standard);
        if (secondary is { } update)
        {
            EmitKeyTransition(update.Transition, HotkeyTrigger.DictationOnly);
        }

        // Toggle mode deactivates on key DOWN, so the deactivation-time reconcile still sees the
        // key physically held and correctly skips it. The suppressed key UP is therefore the one
        // moment that covers every mode: check for a leaked logical "down" right after it.
        var suppress = primary.ShouldSuppress || secondary?.ShouldSuppress == true;
        return new HookDecision(suppress, RequestReconcile: !isDown && suppress);
    }

    /// <summary>
    /// Router only, under its gate, when this engine stops owning the hook (a reinstall replaced
    /// it, or the service stopped). From then on it passes every key through and can neither start
    /// nor stop a dictation, even while a hook thread that outlived its join keeps calling it: a
    /// stop from here would otherwise land on whatever the user started on the replacement, because
    /// deactivations skip the epoch check. Returns the trigger whose dictation this engine started
    /// and never stopped, which the caller must now stop. The hand-over is one exchange on the
    /// arbiter, so exactly one side sends that stop: the caller, or the owner thread if its own
    /// release or state clear took the trigger first.
    /// </summary>
    public HotkeyTrigger? Retire()
    {
        Volatile.Write(ref _retired, true);

        // Seals the button debts: from here a press this engine is still judging cannot commit one, so it is not
        // swallowed, and every debt committed before is the replacement's (OwedButtonReleases).
        Interlocked.Or(ref _owedButtonReleases, SealedDebts);
        return _arbiter.Retire();
    }

    private void ApplyPendingCommands()
    {
        foreach (var command in _commands.TakeAll())
        {
            Apply(command);
        }
    }

    private void Apply(HotkeyCommand command)
    {
        _generation = command.Generation;
        switch (command.Kind)
        {
            case HotkeyCommandKind.UpdateBindings:
                ApplyBindings(command.Binding!, command.DictationOnlyBinding);
                break;
            case HotkeyCommandKind.SetCaptureMode:
                ApplyCaptureMode(command.Enabled);

                // Published only now, once both machines have applied the request (review round 9, A11): until then the
                // router counts capture as owning input, at its start and at its end.
                Volatile.Write(ref _appliedCaptureGeneration, command.Generation);
                break;
            case HotkeyCommandKind.SetPaused:
                ApplyPaused(command.Enabled);
                break;
            case HotkeyCommandKind.CancelToggle:
                ReleaseActivation(command.Activation);
                break;
        }
    }

    // Scribe itself ended the dictation this press started (the silence auto-stop, a microphone fault, a pause, the
    // duration ceiling), so the press gives up the dictation and its latch, and the next press starts afresh instead of
    // being swallowed as the missing toggle-off. A newer press that owns the dictation keeps both: clearing its latch would
    // leave the recording it starts with no release to end it. Every other latch owns no dictation (a press the arbiter
    // refused) and is forgotten, as before.
    private void ReleaseActivation(long activation)
    {
        var owner = _arbiter.ReleaseActivation(activation);
        if (owner != HotkeyTrigger.Standard)
        {
            _standard.CancelToggle();
        }

        if (owner != HotkeyTrigger.DictationOnly)
        {
            _dictationOnly?.CancelToggle();
        }
    }

    private void ApplyBindings(HotkeyBinding binding, HotkeyBinding? dictationOnlyBinding)
    {
        var activeTrigger = _arbiter.TryTake(HotkeyTrigger.Standard);

        // Both machines forget their keys (the standard one always, the dictation-only one whether it is updated, reset,
        // created empty or removed), so the view is a new one from here.
        NewKeyView();
        var (transition, _) = _standard.UpdateBinding(binding);
        var secondaryTransition = HotkeyTransition.None;
        if (dictationOnlyBinding is null)
        {
            if (_dictationOnly is not null)
            {
                (secondaryTransition, _) = _dictationOnly.Reset();
            }

            Volatile.Write(ref _dictationOnly, null);
        }
        else if (_dictationOnly is null)
        {
            Volatile.Write(ref _dictationOnly, CreateMachine(dictationOnlyBinding));
        }
        else
        {
            (secondaryTransition, _) = _dictationOnly.UpdateBinding(dictationOnlyBinding);
        }

        AfterKeyViewClearedForTests?.Invoke();
        if (transition != HotkeyTransition.None)
        {
            EmitStateClear(transition, activeTrigger);
        }
        else if (secondaryTransition != HotkeyTransition.None)
        {
            EmitStateClear(secondaryTransition, activeTrigger);
        }

        // The hook thread reads this once the command is applied and installs or removes its mouse hook to match.
        Volatile.Write(ref _usesMouseButtons, MouseButtons.Uses(binding) || MouseButtons.Uses(dictationOnlyBinding));
    }

    private void ApplyCaptureMode(bool enabled)
    {
        _captureMode = enabled;
        NewKeyView();
        var (transition, _) = _standard.SetCaptureMode(enabled);
        BetweenCaptureMachinesForTests?.Invoke();
        var secondary = _dictationOnly?.SetCaptureMode(enabled);
        AfterKeyViewClearedForTests?.Invoke();
        if (transition != HotkeyTransition.None)
        {
            EmitStateClear(transition, _arbiter.TryTake(HotkeyTrigger.Standard));
        }
        else if (secondary is { Transition: not HotkeyTransition.None } secondaryTransition)
        {
            EmitStateClear(secondaryTransition.Transition, _arbiter.TryTake(HotkeyTrigger.DictationOnly));
        }
    }

    // Pausing cancels the latches silently: the caller stops a dictation that was already running,
    // and a Deactivated from here would race that stop and relabel why the recording ended.
    private void ApplyPaused(bool paused)
    {
        _paused = paused;
        _standard.SetPaused(paused);
        _dictationOnly?.SetPaused(paused);
        if (paused)
        {
            _arbiter.Reset();
        }
    }

    // Owner thread, immediately before its machines clear or replace their key view (ApplyBindings, ApplyCaptureMode,
    // ApplyDesktopSwitch; a new engine takes its first epoch in its constructor): the view gets a new epoch, published
    // before the first key is cleared, so a reader that sees any cleared key sees the new epoch too. One interlocked add
    // and one volatile write, no lock and no allocation, since it can run inside a hook callback (a queued command
    // applied at the start of a key event). A mouse hook found gone (OnMouseHookLost) forgets buttons, never keys, and
    // the leaked-key repair judges keys alone, so it takes none.
    private void NewKeyView() => Volatile.Write(ref _keyViewEpoch, Interlocked.Increment(ref _lastKeyViewEpoch));

    // A machine created mid-session (a dictation-only binding added later) joins the modes already
    // in force instead of starting live while Settings is capturing or dictation is paused.
    private ChordStateMachine CreateMachine(HotkeyBinding binding)
    {
        var machine = new ChordStateMachine(binding, _isLogicallyDown);
        if (_captureMode)
        {
            _ = machine.SetCaptureMode(true);
        }

        if (_paused)
        {
            machine.SetPaused(true);
        }

        return machine;
    }

    private void EmitKeyTransition(HotkeyTransition transition, HotkeyTrigger trigger)
    {
        long activation = 0;
        if (transition == HotkeyTransition.Activated)
        {
            activation = Interlocked.Increment(ref _lastActivation);
            if (!_arbiter.TryActivate(trigger, activation))
            {
                return;
            }
        }
        else if (transition == HotkeyTransition.Deactivated)
        {
            if (!_arbiter.TryDeactivate(trigger))
            {
                return;
            }
        }
        else
        {
            return;
        }

        // The activation epoch is this engine's, read here on the owner thread, the only one that advances it, so an
        // Activated computed after a desktop switch always carries the new one; the item names this engine, whose epoch
        // the consumer judges it by. The key view's epoch goes with it too, so the repair a Deactivated asks for judges
        // only the view the release was seen in.
        _transitions.TryEnqueue(new HotkeyService.QueuedTransition(
            transition, trigger, _generation, AllowReconcile: true, ActivationEpoch: _activationEpoch, Engine: this,
            Activation: activation, KeyViewEpoch: _keyViewEpoch));
    }

    // A null trigger means the engine was retired first, and the retirement reported the stop.
    private void EmitStateClear(
        HotkeyTransition transition, HotkeyTrigger? trigger, HotkeyDeactivation deactivation = HotkeyDeactivation.Released)
    {
        if (trigger is { } owner)
        {
            _transitions.TryEnqueue(new HotkeyService.QueuedTransition(
                transition, owner, _generation, AllowReconcile: false, deactivation));
        }
    }
}
