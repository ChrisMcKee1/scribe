namespace Scribe.Core.TextInjection;

/// <summary>What a non-destructive selection read concluded.</summary>
public enum SelectionProbeOutcome
{
    /// <summary>
    /// The target exposes no readable text surface. Fall through to the next strategy.
    /// </summary>
    Unsupported,

    /// <summary>
    /// The focused control is a password field. Refuse outright and do NOT fall through: the
    /// clipboard path is naturally safe here (a copy in a password box yields nothing), so falling
    /// through would be harmless, but stopping gives the user a truthful message instead of a
    /// misleading "nothing selected".
    /// </summary>
    PasswordField,

    /// <summary>
    /// The caret is present but the selection is empty. Authoritative: do NOT fall through to a
    /// clipboard borrow, which would synthesize a copy that also finds nothing and would report the
    /// same thing more slowly while touching the user's clipboard for no reason.
    /// </summary>
    NothingSelected,

    /// <summary>
    /// The read succeeded but the selection is several disjoint ranges (a table or spreadsheet
    /// selection). The text is usable, but joining the pieces produces something that cannot be
    /// coherently written back, so the action must degrade to copy-result-only.
    /// </summary>
    Disjoint,

    /// <summary>The read succeeded and the selection is a single contiguous range.</summary>
    Success,
}

/// <summary>Result of a non-destructive selection read.</summary>
/// <param name="Outcome">What the probe concluded.</param>
/// <param name="Text">The selected text on a successful read, otherwise null.</param>
/// <param name="CanWriteBack">
/// False when the surface is read-only or exposes no write path. Decided BEFORE the model runs so
/// the palette can offer "Copy result" rather than promising a replacement it cannot perform.
/// </param>
/// <param name="Detail">A user-facing sentence when the outcome is a refusal.</param>
public readonly record struct SelectionProbe(
    SelectionProbeOutcome Outcome,
    string? Text = null,
    bool CanWriteBack = true,
    string? Detail = null)
{
    /// <summary>The probe could not help; the caller should try the next strategy.</summary>
    public static SelectionProbe Unsupported { get; } = new(SelectionProbeOutcome.Unsupported);

    /// <summary>True when the caller should stop rather than trying another strategy.</summary>
    public bool IsTerminal =>
        Outcome is SelectionProbeOutcome.PasswordField or SelectionProbeOutcome.NothingSelected;
}

/// <summary>
/// Reads the selection out of another application WITHOUT touching the clipboard.
/// </summary>
/// <remarks>
/// <para>
/// Implemented in Scribe.App rather than here because the only practical implementation uses
/// <c>System.Windows.Automation</c>, which ships with the Windows Desktop framework and is only
/// referenced by projects that set <c>UseWPF</c>. Scribe.Core deliberately has no UI dependency, and
/// turning WPF on for a class library to reach one assembly would be a poor trade.
/// </para>
/// <para>
/// The interface lives here anyway so that the layered strategy (probe first, clipboard second)
/// stays in Core where it can be reasoned about and tested with a fake, while only the untestable
/// cross-process UI Automation call lives in the shell.
/// </para>
/// </remarks>
public interface ISelectionProbe
{
    /// <summary>
    /// Attempts to read the selection from <paramref name="targetWindow"/>. Never throws; an
    /// implementation that cannot answer returns <see cref="SelectionProbe.Unsupported"/>.
    /// </summary>
    SelectionProbe TryRead(nint targetWindow);
}
