namespace Scribe.Core.Infrastructure;

/// <summary>Whether a flip of the Start with Windows switch may start, and if not, why.</summary>
public enum StartupFlipDecision
{
    /// <summary>Start applying the change.</summary>
    Apply,

    /// <summary>The switch already shows the requested state; nothing to do.</summary>
    Unchanged,

    /// <summary>Windows' state is unknown, or the user or an administrator locked it in Windows.</summary>
    NotChangeable,

    /// <summary>A settings save is running.</summary>
    SaveInProgress,

    /// <summary>An earlier change is still being applied.</summary>
    ApplyInFlight,

    /// <summary>The stored settings were recovered from a damaged copy and must be saved first.</summary>
    RecoveredSettings,
}

/// <summary>
/// The Start with Windows row in Settings: what it shows, and when a read or a flip may proceed.
/// </summary>
/// <remarks>
/// <para>
/// Only the first read disables the switch, because there is no state to flip from yet. Later
/// reads, one per window activation, leave it usable. On the Store build a read is an asynchronous
/// WinRT call, and a click that also activated the window used to land on a switch that had just
/// been disabled for the read, so the first click after returning to Settings was dropped.
/// </para>
/// <para>
/// A flip is therefore allowed while a read is running. That is safe: the registration re-reads
/// Windows inside its own gate before changing anything, and a read that began before the flip, or
/// before a save showed a fresher state, is discarded when it finishes, so it can never paint an
/// older state over a newer one.
/// </para>
/// <para>
/// Not thread-safe by design: the Settings window drives it from its dispatcher thread.
/// </para>
/// </remarks>
public sealed class StartupSwitchState
{
    // Advances whenever something newer than a running read is shown or started, so that read can
    // tell it has been overtaken.
    private long _version;
    private bool _refreshing;

    /// <summary>The Windows state the switch last showed, or null before the first read.</summary>
    public StartupRegistrationStatus? Shown { get; private set; }

    public bool IsApplying { get; private set; }

    /// <summary>True only for the first read, when the switch has nothing to show yet.</summary>
    public bool DisablesSwitchWhileRefreshing => Shown is null;

    /// <summary>
    /// Starts a read of Windows' state. False when a read, a flip or a save is already running;
    /// each of those shows a state of its own.
    /// </summary>
    public bool TryBeginRefresh(bool saveInProgress, out long ticket)
    {
        ticket = _version;
        if (_refreshing || IsApplying || saveInProgress)
        {
            return false;
        }

        _refreshing = true;
        return true;
    }

    /// <summary>
    /// Ends the read started with <paramref name="ticket"/>. Returns true when its result should be
    /// shown, false when a flip or a save overtook it.
    /// </summary>
    public bool CompleteRefresh(long ticket, StartupRegistrationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _refreshing = false;
        if (IsApplying || ticket != _version)
        {
            return false;
        }

        Shown = status;
        return true;
    }

    /// <summary>What a flip to <paramref name="requested"/> would do now. Changes nothing.</summary>
    public StartupFlipDecision Decide(bool requested, bool saveInProgress, bool settingsRecovered)
    {
        if (IsApplying)
        {
            return StartupFlipDecision.ApplyInFlight;
        }

        if (saveInProgress)
        {
            return StartupFlipDecision.SaveInProgress;
        }

        if (Shown is not { CanChange: true } shown)
        {
            return StartupFlipDecision.NotChangeable;
        }

        if (shown.IsEnabled == requested)
        {
            return StartupFlipDecision.Unchanged;
        }

        return settingsRecovered ? StartupFlipDecision.RecoveredSettings : StartupFlipDecision.Apply;
    }

    /// <summary>
    /// Decides a flip and, when the answer is <see cref="StartupFlipDecision.Apply"/>, marks it as
    /// applying so a read still running is discarded.
    /// </summary>
    public StartupFlipDecision TryBeginApply(bool requested, bool saveInProgress, bool settingsRecovered)
    {
        var decision = Decide(requested, saveInProgress, settingsRecovered);
        if (decision == StartupFlipDecision.Apply)
        {
            IsApplying = true;
            _version++;
        }

        return decision;
    }

    /// <summary>The flip finished; <paramref name="status"/> is what Windows now reports.</summary>
    public void CompleteApply(StartupRegistrationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        IsApplying = false;
        Shown = status;
    }

    /// <summary>
    /// Another path, such as Save, read Windows directly. What it read supersedes any read still
    /// running.
    /// </summary>
    public void Show(StartupRegistrationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Shown = status;
        _version++;
    }
}
