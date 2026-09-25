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
/// Everything the low-level hook callback touches, owned by one hook thread for the lifetime of one
/// hook installation.
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
    private readonly ChordStateMachine _standard;
    private ChordStateMachine? _dictationOnly;
    private bool _captureMode;
    private bool _paused;
    private bool _retired;
    private long _generation;
    private long _activationEpoch;
    private long _desktopSwitches;
    private long _desktopSwitchNotices;
    private int _wakePending;
    private uint _ownerThreadId;

    /// <param name="isLogicallyDown">
    /// Windows' view of a key, which each binding's machine asks about a modifier its own view holds (see
    /// <see cref="ChordStateMachine"/>). Null trusts the hook's view alone.
    /// </param>
    public HotkeyEngine(
        HotkeyBinding binding,
        HotkeyBinding? dictationOnlyBinding,
        bool captureMode,
        bool paused,
        long generation,
        HotkeyTransitionQueue transitions,
        Func<uint, bool>? isLogicallyDown = null)
    {
        _transitions = transitions;
        _isLogicallyDown = isLogicallyDown;
        _captureMode = captureMode;
        _paused = paused;
        _generation = generation;
        _standard = CreateMachine(binding);
        _dictationOnly = dictationOnlyBinding is null ? null : CreateMachine(dictationOnlyBinding);
    }

    /// <summary>The owner's thread id once it has attached, otherwise zero. Any thread.</summary>
    public uint OwnerThreadId => Volatile.Read(ref _ownerThreadId);

    /// <summary>Any thread. True once a replacement took over the hook or the service stopped.</summary>
    public bool IsRetired => Volatile.Read(ref _retired);

    /// <summary>
    /// Any thread. The hook's view of the physical keyboard, for the leaked-key reconciler: true
    /// while a key the hook saw go down has not come back up.
    /// </summary>
    public bool IsPressed(uint virtualKey) =>
        _standard.IsPressed(virtualKey) || Volatile.Read(ref _dictationOnly)?.IsPressed(virtualKey) == true;

    /// <summary>Any thread. How many desktop switches the owner has applied.</summary>
    public long DesktopSwitches => Interlocked.Read(ref _desktopSwitches);

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
    /// nothing recording starts and stops nothing. The service reaches this through <see cref="OnDesktopSwitchNotice"/>,
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

    /// <summary>Owner thread: one key event from the hook callback.</summary>
    public HookDecision OnKeyEvent(uint virtualKey, bool isDown)
    {
        // Retired: the replacement engine decides now, or nobody does after Stop. A hook thread
        // that outlived its join still reaches here, and swallowing on its stale bindings or pause
        // state would hide keys the current engine let through.
        if (IsRetired)
        {
            return default;
        }

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
