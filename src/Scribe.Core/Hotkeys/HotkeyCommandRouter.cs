using Scribe.Core.Models;

namespace Scribe.Core.Hotkeys;

/// <summary>
/// The configuration side of the global hotkey: the bindings and modes other threads set, the state
/// epoch those changes advance, and the engine that currently owns the hook.
///
/// Requesting threads serialize on one private gate, so commands reach the engine in the same order
/// as their epochs. The hook thread never takes that gate: it only drains commands that are already
/// queued. Everything the hook thread and the dispatcher read here (<see cref="IsCurrent"/>,
/// <see cref="IsPressed"/>, the published bindings) is read without a lock.
///
/// The epoch advances when a change is REQUESTED, not when the hook thread gets round to applying
/// it. That keeps the old guarantee that an activation computed before a state-clearing change can
/// never start a recording after it, even though the change now reaches the hook asynchronously.
/// </summary>
internal sealed class HotkeyCommandRouter
{
    private readonly object _gate;
    private readonly Func<uint, bool>? _isLogicallyDown;
    private readonly Func<uint, bool>? _buttonDownInWindows;
    private volatile HotkeyBinding _binding;
    private volatile HotkeyBinding? _dictationOnlyBinding;
    private bool _captureMode;
    private long _captureGeneration;
    private bool _paused;
    private long _latestPauseRequest;
    private long _generation;
    private HotkeyEngine? _engine;

    public HotkeyCommandRouter(HotkeyBinding binding)
        : this(binding, new object())
    {
    }

    /// <param name="binding">The initial standard binding.</param>
    /// <param name="isLogicallyDown">Windows' view of a key, handed to every engine (see <see cref="HotkeyEngine"/>).</param>
    /// <param name="buttonDownInWindows">
    /// Windows' own view of a mouse button, which every engine reads for the release of a press it swallowed (see
    /// <see cref="HotkeyEngine"/>). Made by the caller, before any hook exists.
    /// </param>
    public HotkeyCommandRouter(HotkeyBinding binding, Func<uint, bool> isLogicallyDown, Func<uint, bool>? buttonDownInWindows = null)
        : this(binding, new object(), isLogicallyDown, buttonDownInWindows)
    {
    }

    /// <param name="binding">The initial standard binding.</param>
    /// <param name="gate">
    /// The lock requesting threads serialize on. Injectable so a test can hold it and prove that
    /// nothing on the hook path ever needs it.
    /// </param>
    /// <param name="isLogicallyDown">Windows' view of a key, or null to trust the hook's view alone.</param>
    /// <param name="buttonDownInWindows">
    /// Windows' own view of a mouse button, or null, for the tests that do not script Windows, to decide an owed release
    /// by the engine's own state alone.
    /// </param>
    internal HotkeyCommandRouter(
        HotkeyBinding binding, object gate, Func<uint, bool>? isLogicallyDown = null, Func<uint, bool>? buttonDownInWindows = null)
    {
        _binding = binding;
        _gate = gate;
        _isLogicallyDown = isLogicallyDown;
        _buttonDownInWindows = buttonDownInWindows;
    }

    public HotkeyBinding Binding => _binding;

    public HotkeyBinding? DictationOnlyBinding => _dictationOnlyBinding;

    /// <summary>Windows' view of a key that every engine is given, or null; for a test of the service's wiring.</summary>
    internal Func<uint, bool>? WindowsKeyState => _isLogicallyDown;

    /// <summary>Windows' view of a mouse button that every engine is given, or null; for a test of the wiring.</summary>
    internal Func<uint, bool>? WindowsButtonState => _buttonDownInWindows;

    /// <summary>The engine that owns the hook right now, or null while the service is stopped.</summary>
    public HotkeyEngine? CurrentEngine => Volatile.Read(ref _engine);

    /// <summary>The epoch of the most recent state-clearing request.</summary>
    public long CurrentGeneration => Interlocked.Read(ref _generation);

    /// <summary>
    /// The dispatcher's staleness test: false when an activation was computed before a later
    /// state-clearing request (capture mode, rebinding, pause, reinstall), whether or not the hook
    /// thread has applied that request yet.
    /// </summary>
    public bool IsCurrent(long generation) => generation == CurrentGeneration;

    /// <summary>The current engine's physical key view; false while stopped.</summary>
    public bool IsPressed(uint virtualKey) => CurrentEngine?.IsPressed(virtualKey) == true;

    /// <summary>Returns whether anything changed, and the engine to wake so it applies the change now.</summary>
    public (bool Changed, HotkeyEngine? Wake) UpdateBindings(HotkeyBinding binding, HotkeyBinding? dictationOnlyBinding)
    {
        lock (_gate)
        {
            if (_binding == binding && _dictationOnlyBinding == dictationOnlyBinding)
            {
                return (false, null);
            }

            _binding = binding;
            _dictationOnlyBinding = dictationOnlyBinding;
            return (true, Post(HotkeyCommand.Bindings(binding, dictationOnlyBinding, AdvanceGeneration())));
        }
    }

    /// <summary>Returns the engine to wake. Every call clears key state, as it always has.</summary>
    public HotkeyEngine? SetCaptureMode(bool enabled)
    {
        lock (_gate)
        {
            // Published at the request, before the command exists, for CaptureOwnsInput, which reads both without the
            // gate: the generation first, so a reader that sees the new mode also sees its generation.
            var generation = AdvanceGeneration();
            Volatile.Write(ref _captureGeneration, generation);
            Volatile.Write(ref _captureMode, enabled);
            return Post(HotkeyCommand.CaptureMode(enabled, generation));
        }
    }

    /// <summary>
    /// Any thread, without the gate. Whether binding capture owns input: from Settings' request for it until the current
    /// engine has applied the latest capture request with both its machines
    /// (<see cref="HotkeyEngine.AppliedCaptureGeneration"/>), which for capture's end is the moment both machines have
    /// left it. Capture tracks no key and each of its edges clears the engine's key view, so the leaked-key repair judges
    /// no key while this holds (review rounds 8 and 9, A9 and A11); the service also serializes capture's start with the
    /// repair's key-ups (<c>HotkeyService.AdmitCapture</c>). A request the current engine has not applied yet, start or
    /// end, counts as owning input: before an engine applies the start, its view is about to be cleared.
    /// </summary>
    public bool CaptureOwnsInput
    {
        get
        {
            if (Volatile.Read(ref _captureMode))
            {
                return true;
            }

            var requested = Volatile.Read(ref _captureGeneration);
            return CurrentEngine is { } engine && engine.AppliedCaptureGeneration != requested;
        }
    }

    /// <summary>Returns whether the pause state changed, and the engine to wake.</summary>
    public (bool Changed, HotkeyEngine? Wake) SetPaused(bool paused)
    {
        lock (_gate)
        {
            return SetPausedLocked(paused);
        }
    }

    /// <summary>
    /// <see cref="SetPaused(bool)"/> for a caller that numbers its requests. Returns null, and
    /// changes nothing, when <paramref name="requestSequence"/> is not newer than a request already
    /// applied, so calls that reach here out of order still leave the state of the latest one.
    /// </summary>
    public (bool Changed, HotkeyEngine? Wake)? SetPaused(bool paused, long requestSequence)
    {
        lock (_gate)
        {
            if (requestSequence <= _latestPauseRequest)
            {
                return null;
            }

            _latestPauseRequest = requestSequence;
            return SetPausedLocked(paused);
        }
    }

    // Callers hold _gate.
    private (bool Changed, HotkeyEngine? Wake) SetPausedLocked(bool paused)
    {
        if (_paused == paused)
        {
            return (false, null);
        }

        _paused = paused;

        // Pausing starts a new epoch so an activation computed just before it cannot start a
        // dictation after it. Resuming invalidates nothing: no activation exists while paused.
        var generation = paused ? AdvanceGeneration() : CurrentGeneration;
        return (true, Post(HotkeyCommand.Paused(paused, generation)));
    }

    /// <summary>
    /// Returns the engine to wake. Releases the press <paramref name="activation"/> names, if it still owns the dictation
    /// (see <see cref="HotkeyEngine"/>). It starts no new epoch: a newer press is left alone, so its queued activation
    /// stays current and its latch stays set.
    /// </summary>
    public HotkeyEngine? CancelToggle(long activation)
    {
        lock (_gate)
        {
            return Post(HotkeyCommand.CancelToggle(CurrentGeneration, activation));
        }
    }

    /// <summary>
    /// Makes a new engine current, built from the published configuration in a fresh epoch, and
    /// retires the engine it replaces (none when the service was stopped). Returns the new engine
    /// and the trigger whose dictation the replaced engine left running, which the caller must stop:
    /// the held key's eventual release can no longer be matched to the new engine's fresh state.
    /// Requests that were queued on the replaced engine but never applied are covered, because every
    /// sticky setting is read from the published configuration, and the retired engine can no longer
    /// act on them. Nothing else is taken from the old engine, the releases it still owed to swallowed
    /// mouse button presses included: between the old thread's exit and the new registration no hook
    /// sees the mouse, so, as for a mouse hook found gone, those releases are let through when they come
    /// (review round 7; <see cref="HotkeyEngine.OnMouseHookLost"/>). The retirement still seals the old
    /// engine's debts, so a press it is judging as the reinstall lands is swallowed only if its debt was
    /// committed first, and otherwise reaches the app whole, with its release.
    /// </summary>
    public (HotkeyEngine Engine, HotkeyTrigger? Interrupted) BeginEngine(HotkeyTransitionQueue transitions)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        lock (_gate)
        {
            var previous = _engine;
            var interrupted = previous?.Retire();

            // The new engine starts in the published capture mode, so it has in effect applied the latest capture request
            // already, with both machines.
            var engine = new HotkeyEngine(
                _binding, _dictationOnlyBinding, _captureMode, _paused, AdvanceGeneration(), transitions, _isLogicallyDown,
                _buttonDownInWindows, _captureGeneration);
            Volatile.Write(ref _engine, engine);
            return (engine, interrupted);
        }
    }

    /// <summary>
    /// Detaches and retires the current engine, or only <paramref name="expected"/> when given (a
    /// failed install must not detach a replacement). Until the next <see cref="BeginEngine"/>,
    /// requests only update the published configuration.
    /// </summary>
    public HotkeyEngine? EndEngine(HotkeyEngine? expected = null)
    {
        lock (_gate)
        {
            var current = _engine;
            if (current is null || (expected is not null && !ReferenceEquals(current, expected)))
            {
                return null;
            }

            // Stopping has never reported a dictation in flight (the app ends that itself), so the
            // interrupted trigger is dropped. Retiring still matters: a hook thread that outlives
            // Stop's join must pass keys through instead of swallowing the binding with nobody
            // listening.
            _ = current.Retire();
            Volatile.Write(ref _engine, null);
            return current;
        }
    }

    // Callers hold _gate, which is what keeps epochs and command order in step. The write is
    // interlocked for the lock-free readers.
    private long AdvanceGeneration() => Interlocked.Increment(ref _generation);

    // Callers hold _gate.
    private HotkeyEngine? Post(HotkeyCommand command)
    {
        var engine = _engine;
        return engine is not null && engine.Post(command) ? engine : null;
    }
}
