using System.Runtime.ExceptionServices;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

/// <summary>
/// Drives a <see cref="HotkeyEngine"/> the way the hook thread does, without installing a hook:
/// the calling test thread plays the hook thread, requests go through the real
/// <see cref="HotkeyCommandRouter"/>, and transitions are read straight off the queue the
/// dispatcher would consume.
/// </summary>
internal sealed class HotkeyEngineHarness : IDisposable
{
    public HotkeyEngineHarness(
        HotkeyBinding binding,
        HotkeyBinding? dictationOnly = null,
        object? gate = null,
        Func<uint, bool>? isLogicallyDown = null)
    {
        Router = new HotkeyCommandRouter(binding, gate ?? new object(), isLogicallyDown);
        if (dictationOnly is not null)
        {
            Router.UpdateBindings(binding, dictationOnly);
        }

        (Engine, _) = Router.BeginEngine(Transitions);
    }

    public HotkeyTransitionQueue Transitions { get; } = new();

    public HotkeyCommandRouter Router { get; }

    public HotkeyEngine Engine { get; }

    public HookDecision Down(uint key) => Engine.OnKeyEvent(key, isDown: true);

    public HookDecision Up(uint key) => Engine.OnKeyEvent(key, isDown: false);

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
    /// What the dispatcher would raise for this transition, by the consumer's own rule
    /// (<see cref="HotkeyService.ShouldDispatch"/>): a Deactivated always, an Activated only while it is still current.
    /// </summary>
    public bool WouldDispatch(HotkeyService.QueuedTransition transition) =>
        HotkeyService.ShouldDispatch(transition, Router, Transitions);

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

    public void Dispose() => Transitions.Dispose();
}
