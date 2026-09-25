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

/// <summary>What the hook callback does with one key event.</summary>
internal readonly record struct HookDecision(bool Suppress, bool RequestReconcile);

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

    private readonly LockFreeInbox<HotkeyCommand> _commands = new();
    private readonly HotkeyTriggerArbiter _arbiter = new();
    private readonly HotkeyTransitionQueue _transitions;
    private readonly Func<uint, bool>? _isLogicallyDown;
    private readonly WindowsMouseView? _windowsView;
    private readonly ChordStateMachine _standard;
    private ChordStateMachine? _dictationOnly;
    private bool _captureMode;
    private bool _paused;
    private bool _retired;
    private bool _usesMouseButtons;
    private long _generation;
    private long _activationEpoch;
    private long _desktopSwitches;
    private long _desktopSwitchNotices;
    private long _mouseHookLossesHandled;
    private long _mouseButtonEvents;
    private int _wakePending;
    private uint _ownerThreadId;

    // The mouse buttons whose press was swallowed and whose release has not been seen since, one bit per button code; the
    // same bits shifted by UncertainShift for the debts a time no hook saw the mouse has made uncertain (a mouse hook found
    // gone, or the gap a reinstall leaves); and SealedDebts once the engine is retired. A new press of the button retires
    // its debt, since buttons never repeat: the release went up where no hook could see it. What a release that pays a
    // debt does is decided when it is made, on Windows' own view at that moment (SwallowsOwedRelease), and DefWindowProc
    // is why it matters: it turns a side button's lone release into a Back or Forward command. One word, changed by
    // compare-exchange, so a debt is either committed before the retirement seals it, and handed on, or refused, and then
    // its press is not swallowed either (see OnMouseButtonEvent).
    private int _owedButtonReleases;

    private const int SealedDebts = 1 << 30;
    private const int ButtonDebts = (1 << (int)MouseButtons.Middle) | (1 << (int)MouseButtons.Back) | (1 << (int)MouseButtons.Forward);
    private const int UncertainShift = 8;
    private const int UncertainDebts = ButtonDebts << UncertainShift;

    // What a release, or a new press, found owed for its button.
    private enum OwedRelease
    {
        None,
        Certain,
        Uncertain,
    }

    /// <param name="isLogicallyDown">
    /// Windows' view of a key, which each binding's machine asks about a modifier its own view holds (see
    /// <see cref="ChordStateMachine"/>). Null trusts the hook's view alone.
    /// </param>
    /// <param name="owedButtonReleases">
    /// The releases still owed to button presses the engine this one replaces had swallowed
    /// (<see cref="OwedButtonReleases"/>), so a reinstall, whose state otherwise starts over, still keeps them from the
    /// app. They cross the time between the old thread's exit and this engine's registration, when no hook saw the mouse,
    /// so this engine takes them as uncertain, each decided on Windows' view when its release is made
    /// (<see cref="OnMouseButtonEvent"/>). Zero for none.
    /// </param>
    /// <param name="windowsView">
    /// Windows' own view of the mouse buttons, read in the callback for a release this engine owes
    /// (<see cref="WindowsMouseView"/>). Null for the tests that do not script Windows, where the engine's own state
    /// decides an owed release: it is swallowed.
    /// </param>
    public HotkeyEngine(
        HotkeyBinding binding,
        HotkeyBinding? dictationOnlyBinding,
        bool captureMode,
        bool paused,
        long generation,
        HotkeyTransitionQueue transitions,
        Func<uint, bool>? isLogicallyDown = null,
        int owedButtonReleases = 0,
        WindowsMouseView? windowsView = null)
    {
        _transitions = transitions;
        _isLogicallyDown = isLogicallyDown;
        _windowsView = windowsView;
        _captureMode = captureMode;
        _paused = paused;
        _generation = generation;
        _standard = CreateMachine(binding);
        _dictationOnly = dictationOnlyBinding is null ? null : CreateMachine(dictationOnlyBinding);
        _usesMouseButtons = MouseButtons.Uses(binding) || MouseButtons.Uses(dictationOnlyBinding);
        var inherited = owedButtonReleases & ButtonDebts;
        _owedButtonReleases = inherited | (inherited << UncertainShift);
    }

    /// <summary>
    /// Any thread: the buttons whose press this engine swallowed and whose release it has not seen, as bits by code
    /// (1 &lt;&lt; <see cref="MouseButtons.Back"/> and so on). Read after <see cref="Retire"/>, it is exactly the debts
    /// committed before the retirement, which no callback can change any more, for the router to hand on.
    /// </summary>
    public int OwedButtonReleases => Volatile.Read(ref _owedButtonReleases) & ButtonDebts;

    /// <summary>
    /// Any thread, for tests: of <see cref="OwedButtonReleases"/>, the ones a time no hook saw the mouse has made uncertain
    /// (a mouse hook found gone, a reinstall's inheritance), whose release is swallowed only on an up Windows' reading can
    /// vouch for.
    /// </summary>
    internal int UncertainButtonReleases => (Volatile.Read(ref _owedButtonReleases) & UncertainDebts) >> UncertainShift;

    /// <summary>
    /// Any thread: whether a swallowed button press still owes its release, so the hook thread keeps a drain-only mouse
    /// hook for it even once no binding presses a mouse button.
    /// </summary>
    public bool OwesButtonRelease => OwedButtonReleases != 0;

    // Owner thread, between messages: every release still owed now crosses a time no hook saw the mouse, when a press may
    // have reached Windows unseen, so each is decided by what Windows shows when it is made, and swallowed only on an up
    // the reading can vouch for. Sealed, nothing changes: the debts are the replacement's.
    private void MarkOwedReleasesUncertain()
    {
        var debts = Volatile.Read(ref _owedButtonReleases);
        while ((debts & SealedDebts) == 0)
        {
            var marked = debts | ((debts & ButtonDebts) << UncertainShift);
            var observed = Interlocked.CompareExchange(ref _owedButtonReleases, marked, debts);
            if (observed == debts)
            {
                return;
            }

            debts = observed;
        }
    }

    // Owner thread, for a press the machines swallow: commits the debt of its release unless the retirement sealed the
    // debts first, which is the one way this returns false. The debt is certain: the press itself went through this hook,
    // and SettleRelease has just cleared whatever this button owed before, its uncertainty included (an uncertainty is
    // only ever set for a debt that exists, and goes with it).
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

    // Owner thread: a release, or a new press, of this button pays or forgives its debt. Returns what was owed. Sealed, the
    // debts are the replacement's and stay as they are, but one owed is still reported. That matters only in the in-flight
    // window: a callback this engine admitted before the retirement and is still running, whose release is then judged as
    // an owed one rather than passed on alone. A callback that starts after the retirement returns at the top of
    // OnMouseButtonEvent and swallows nothing.
    private OwedRelease SettleRelease(uint button)
    {
        var bit = 1 << (int)button;
        var paid = bit | (bit << UncertainShift);
        var debts = Volatile.Read(ref _owedButtonReleases);
        while ((debts & bit) != 0 && (debts & SealedDebts) == 0)
        {
            var observed = Interlocked.CompareExchange(ref _owedButtonReleases, debts & ~paid, debts);
            if (observed == debts)
            {
                return KindOfDebt(debts, bit);
            }

            debts = observed;
        }

        return (debts & bit) == 0 ? OwedRelease.None : KindOfDebt(debts, bit);
    }

    private static OwedRelease KindOfDebt(int debts, int bit) =>
        (debts & (bit << UncertainShift)) != 0 ? OwedRelease.Uncertain : OwedRelease.Certain;

    // Owner thread, in the mouse hook callback: whether the release of a button whose press this engine swallowed is
    // swallowed too, decided now, on Windows' own view of that button. The callback runs before Windows applies this
    // release, and Windows takes input one event at a time, so the view shows every earlier press, a press still inside
    // another program's hook when this one registered included. A button Windows shows down received a press this hook
    // did not swallow, and its release must reach the app, or the app keeps the button down. Otherwise a certain debt (no
    // time without the hook since its press) is swallowed on the plain reading: every press since went through this hook,
    // so the up is right whatever else could have made the read zero, short of a press that reached Windows some way the
    // hook does not cover (a second mouse, the secure desktop, an event lost in a renewal's swap) while the read fails. An
    // uncertain one is swallowed only on an up the reading can vouch for (WindowsMouseView.Reading), and let through on
    // any doubt, which at worst sends one release to the app. A zero cannot say why it is zero, so a foreground that flips
    // to a window the read cannot reach and back inside the read defeats that reading too; either way the button stays
    // down in Windows only until a later click's release is read as down and reaches the app. Without a view (the tests
    // that do not script Windows) the engine's own state decides: swallowed.
    private bool SwallowsOwedRelease(uint button, bool uncertain)
    {
        if (_windowsView is not { } windows)
        {
            return true;
        }

        return uncertain ? windows.Reading(button) == false : !windows.IsDown(button);
    }

    /// <summary>The owner's thread id once it has attached, otherwise zero. Any thread.</summary>
    public uint OwnerThreadId => Volatile.Read(ref _ownerThreadId);

    /// <summary>Any thread. True once a replacement took over the hook or the service stopped.</summary>
    public bool IsRetired => Volatile.Read(ref _retired);

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
    /// apply first; every release still owed stays owed but becomes uncertain, since a press may have reached Windows
    /// while no hook saw the mouse, so each is decided on Windows' view when it is made, not now
    /// (<see cref="OnMouseButtonEvent"/>): a press still inside another program's hook as the renewal lands reaches
    /// Windows only after it, and a reading taken now can be one UIPI denied; the machines forget their mouse buttons,
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
        MarkOwedReleasesUncertain();
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
        _ = _standard.Reset();
        _ = _dictationOnly?.Reset();
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
    public HookDecision OnKeyEvent(uint virtualKey, bool isDown)
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

        return OnInput(virtualKey, isDown);
    }

    /// <summary>
    /// Owner thread: the middle or a side mouse button (<see cref="MouseButtons.Middle"/>, <see cref="MouseButtons.Back"/>
    /// or <see cref="MouseButtons.Forward"/>) went down or up, from the mouse hook callback (<see cref="MouseHookFilter"/>).
    /// A button is one more key to the machines: it completes a binding, is swallowed while it drives a dictation, is
    /// let through while paused or capturing, and a bare binding of one lets a press made with a modifier through, as a
    /// bare Page Up or Page Down does (see <see cref="ChordStateMachine"/>). The engine itself keeps what the machines'
    /// resets must not lose, the release owed to a press it swallowed, which it judges even after a reset or in capture;
    /// a press is swallowed only once that debt is committed, so a press the retirement overtakes reaches the app whole,
    /// with its release. The release of a press it swallowed is decided now, on Windows' own view of the button
    /// (<see cref="SwallowsOwedRelease"/>). Nothing here allocates, takes a lock of Scribe's or waits for another thread:
    /// two compare-exchanges at most, and for such a release one GetAsyncKeyState, or for an uncertain debt the reading of
    /// <see cref="NativeMethods.ReadMouseButtonState"/>.
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
        else if (suppress || owed != OwedRelease.None)
        {
            suppress = SwallowsOwedRelease(button, owed == OwedRelease.Uncertain);
        }

        return new HookDecision(suppress, RequestReconcile: !isDown && suppress);
    }

    private HookDecision OnInput(uint virtualKey, bool isDown)
    {
        // Commands requested before this event took effect before it, exactly as if they had
        // been applied synchronously on the requesting thread.
        ApplyPendingCommands();

        var primary = _standard.Process(virtualKey, isDown);
        var secondary = _dictationOnly?.Process(virtualKey, isDown);
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
        var (transition, _) = _standard.SetCaptureMode(enabled);
        var secondary = _dictationOnly?.SetCaptureMode(enabled);
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
        // the consumer judges it by.
        _transitions.TryEnqueue(new HotkeyService.QueuedTransition(
            transition, trigger, _generation, AllowReconcile: true, ActivationEpoch: _activationEpoch, Engine: this,
            Activation: activation));
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
