using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

/// <summary>
/// Drives a <see cref="HotkeyEngine"/> the way the hook thread does, without installing a hook:
/// the calling test thread plays the hook thread, requests go through the real
/// <see cref="HotkeyCommandRouter"/>, and transitions are read straight off the queue the
/// dispatcher would consume. <see cref="Service"/> is a service over the same router that is never
/// started, for its real requests and its real consumer step.
/// </summary>
internal sealed class HotkeyEngineHarness : IDisposable
{
    public HotkeyEngineHarness(
        HotkeyBinding binding,
        HotkeyBinding? dictationOnly = null,
        object? gate = null,
        Func<uint, bool>? isLogicallyDown = null,
        Func<uint, bool>? buttonDownInWindows = null,
        Func<uint, bool>? keyDownInWindows = null,
        Func<uint, bool>? releaseLeakedKey = null)
    {
        Router = new HotkeyCommandRouter(binding, gate ?? new object(), isLogicallyDown, buttonDownInWindows);
        if (dictationOnly is not null)
        {
            Router.UpdateBindings(binding, dictationOnly);
        }

        (Engine, _) = Router.BeginEngine(Transitions);
        Service = new HotkeyService(
            NullLogger<HotkeyService>.Instance, Router, () => true, keyDownInWindows, releaseLeakedKey);
    }

    public HotkeyTransitionQueue Transitions { get; } = new();

    public HotkeyCommandRouter Router { get; }

    public HotkeyEngine Engine { get; }

    /// <summary>
    /// A service over <see cref="Router"/> that installs no hook: its requests (CancelToggle, SetPaused and the rest) reach
    /// the engines this harness drives, where the test applies them as the hook thread would (a key event or
    /// <see cref="HotkeyEngine.OnWake"/>), and <see cref="DispatchAll"/> runs its consumer step.
    /// </summary>
    public HotkeyService Service { get; }

    public HookDecision Down(uint key) => Engine.OnKeyEvent(key, isDown: true);

    public HookDecision Up(uint key) => Engine.OnKeyEvent(key, isDown: false);

    public void Tap(uint key)
    {
        Down(key);
        Up(key);
    }

    /// <summary>A middle or side mouse button going down, as the mouse hook reports it.</summary>
    public HookDecision ButtonDown(uint button) => Engine.OnMouseButtonEvent(button, isDown: true);

    /// <summary>A middle or side mouse button going up, as the mouse hook reports it.</summary>
    public HookDecision ButtonUp(uint button) => Engine.OnMouseButtonEvent(button, isDown: false);

    public (HookDecision Down, HookDecision Up) Click(uint button) => (ButtonDown(button), ButtonUp(button));

    public List<HotkeyService.QueuedTransition> TakeTransitions()
    {
        var taken = new List<HotkeyService.QueuedTransition>();
        foreach (var transition in Transitions.TakeAll())
        {
            taken.Add(transition);
        }

        return taken;
    }

    /// <summary>
    /// The consumer thread catching up: everything queued so far goes through the service's own step
    /// (<see cref="HotkeyService.DispatchTransition"/>), which raises its events on this thread.
    /// </summary>
    public List<HotkeyService.QueuedTransition> DispatchAll()
    {
        var taken = TakeTransitions();
        foreach (var transition in taken)
        {
            Service.DispatchTransition(transition);
        }

        return taken;
    }

    /// <summary>
    /// What the dispatcher would raise for this transition, by the consumer's own rule
    /// (<see cref="HotkeyService.ShouldDispatch"/>): a Deactivated always, an Activated only while it is still current.
    /// </summary>
    public bool WouldDispatch(HotkeyService.QueuedTransition transition) =>
        HotkeyService.ShouldDispatch(transition, Router);

    /// <summary>
    /// Runs <paramref name="body"/> on a dedicated thread and fails the test, rather than hanging,
    /// if it blocks. The bound is a deadlock detector: nothing in the passing case waits on it.
    /// </summary>
    public static T RunBounded<T>(Func<T> body)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "hotkey-test-hook-thread",
        };

        thread.Start();
        Assert.True(
            thread.Join(TimeSpan.FromSeconds(10)),
            "The code under test blocked; the hook path must never wait on another thread.");
        failure?.Throw();
        return result;
    }

    public void Dispose()
    {
        Service.Dispose();
        Transitions.Dispose();
    }
}
