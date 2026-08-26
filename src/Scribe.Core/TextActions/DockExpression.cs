namespace Scribe.Core.TextActions;

/// <summary>
/// What the floating dock is currently communicating.
/// </summary>
/// <remarks>
/// Ordered by the priority the resolver applies, most urgent first, so the enum reads as the ladder
/// it drives.
/// </remarks>
public enum DockExpression
{
    /// <summary>A result is on screen waiting for the user to accept, copy or discard it.</summary>
    Waiting,

    /// <summary>Reading the selection out of the foreground app right now.</summary>
    Reading,

    /// <summary>A model call is in flight.</summary>
    Working,

    /// <summary>The last action failed. Latched until the user does something else.</summary>
    Failed,

    /// <summary>The last action succeeded. Latched until the user does something else.</summary>
    Done,

    /// <summary>Nothing happening, and Scribe is reading nothing. The resting state.</summary>
    Idle,
}

/// <summary>
/// The inputs the dock's expression is derived from. A record so the resolver stays a pure function
/// of its arguments and can be exhaustively tested without a UI.
/// </summary>
/// <param name="AwaitingDecision">A finished result is on screen and the user has not chosen yet.</param>
/// <param name="Reading">A selection capture is in progress.</param>
/// <param name="Working">A model call is in flight.</param>
/// <param name="SuccessLatched">The last completed action succeeded and has not been acknowledged.</param>
/// <param name="FailureLatched">The last completed action failed and has not been acknowledged.</param>
public readonly record struct DockSignals(
    bool AwaitingDecision = false,
    bool Reading = false,
    bool Working = false,
    bool SuccessLatched = false,
    bool FailureLatched = false);

/// <summary>
/// Resolves the dock's expression from the current signals, and owns the latch lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// The outcome states are <b>latched rather than timed</b>. A three second celebration that fires
/// while the user is looking at a different monitor communicates nothing, and one that expires
/// mid-glance is worse than none. A latch instead persists the outcome until the user does the next
/// thing, so "it worked" is still on screen whenever they get around to looking. This is the one
/// piece of the design that a timer genuinely cannot express.
/// </para>
/// <para>
/// Pure and allocation-free: no timers, no clock, no UI types. The caller owns when to set and clear
/// the latches; this decides only what the dock should show given the state it is in.
/// </para>
/// </remarks>
public static class DockExpressionResolver
{
    /// <summary>
    /// Returns the expression to display. A strict priority ladder rather than a state machine with
    /// transitions, because every combination of signals has exactly one right answer and a ladder
    /// makes that answer obvious to read.
    /// </summary>
    public static DockExpression Resolve(DockSignals signals)
    {
        // A decision the user owes beats everything: it is the only state where Scribe is blocked on
        // them rather than the other way round.
        if (signals.AwaitingDecision)
        {
            return DockExpression.Waiting;
        }

        // Live activity outranks a latched outcome, so starting new work visibly clears the last one
        // even if the caller has not got round to clearing the latch yet.
        if (signals.Reading)
        {
            return DockExpression.Reading;
        }

        if (signals.Working)
        {
            return DockExpression.Working;
        }

        // Failure before success: if a session somehow latched both, the problem is the thing worth
        // surfacing.
        if (signals.FailureLatched)
        {
            return DockExpression.Failed;
        }

        if (signals.SuccessLatched)
        {
            return DockExpression.Done;
        }

        return DockExpression.Idle;
    }

    /// <summary>
    /// The signals after new work begins. Clears both outcome latches, because the previous result is
    /// no longer what the dock should be reporting.
    /// </summary>
    public static DockSignals BeginWork(DockSignals signals) => signals with
    {
        SuccessLatched = false,
        FailureLatched = false,
        AwaitingDecision = false,
        Reading = false,
        Working = true,
    };

    /// <summary>The signals after a selection read starts.</summary>
    public static DockSignals BeginReading(DockSignals signals) => signals with
    {
        SuccessLatched = false,
        FailureLatched = false,
        AwaitingDecision = false,
        Reading = true,
        Working = false,
    };

    /// <summary>The signals once a result is on screen and the user has to choose.</summary>
    public static DockSignals AwaitDecision(DockSignals signals) => signals with
    {
        Reading = false,
        Working = false,
        AwaitingDecision = true,
    };

    /// <summary>
    /// The signals after an action finishes. Latches the outcome so it survives until the user acts,
    /// rather than expiring on a timer they may never be looking at.
    /// </summary>
    public static DockSignals Complete(DockSignals signals, bool succeeded) => signals with
    {
        Reading = false,
        Working = false,
        AwaitingDecision = false,
        SuccessLatched = succeeded,
        FailureLatched = !succeeded,
    };

    /// <summary>
    /// The signals once the user has acknowledged the outcome, by invoking the palette again, taking
    /// the result, or dismissing it. Returns the dock to rest.
    /// </summary>
    public static DockSignals Acknowledge(DockSignals signals) => signals with
    {
        SuccessLatched = false,
        FailureLatched = false,
        AwaitingDecision = false,
        Reading = false,
        Working = false,
    };
}
