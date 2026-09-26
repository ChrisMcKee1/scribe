using Scribe.Core.Models;

namespace Scribe.Core.TextInjection;

/// <summary>
/// Places transcribed text into whatever application currently has keyboard focus.
/// The default strategy sets the clipboard and sends Ctrl+V, then restores the prior
/// clipboard text unless another application has replaced Scribe's text in the meantime;
/// a Unicode keystroke strategy is available as a fallback for fields that block synthetic paste.
/// </summary>
public interface ITextInjector
{
    /// <summary>
    /// Injects <paramref name="text"/> into the focused application using the given
    /// <paramref name="method"/>. Runs the clipboard/SendInput sequence on a dedicated STA
    /// thread; callers should invoke this off the UI thread because it includes short delays.
    /// When <paramref name="shiftEnterLineBreaks"/> is true (the default), typed line breaks are
    /// sent as Shift+Enter so they do not submit a chat message. <paramref name="targetProcessName"/> is the process that
    /// owned the focused window when the dictation started: a Remote Desktop or virtual machine client is always typed into,
    /// never pasted into, whatever <paramref name="method"/> asks, and the typing is paced for the remote session (see
    /// <see cref="RemoteClientProcesses"/>).
    /// </summary>
    InjectionResult Inject(
        string text,
        InjectionMethod method = InjectionMethod.ClipboardPaste,
        nint expectedForegroundWindow = 0,
        bool shiftEnterLineBreaks = true,
        string? targetProcessName = null);
}

/// <summary>Outcome of placing text into the target application.</summary>
public sealed record InjectionResult(bool Succeeded, string Method, int Sent, int Total, string? Error = null)
{
    /// <summary>
    /// The exact <see cref="Error"/> reported when the focused window changed before the text could be
    /// placed. Callers match on it to tell the user focus moved, so it must never be reworded.
    /// </summary>
    public const string FocusChangedError = "The focused window changed while processing.";

    public static InjectionResult Empty { get; } = new(true, "none", 0, 0);

    /// <summary>How the clipboard paste path ended; <see cref="PasteDelivery.NotUsed"/> on every other path.</summary>
    public PasteDelivery Paste { get; init; }

    /// <summary>
    /// What happened to the clipboard content Scribe borrowed. Independent of <see cref="Succeeded"/>:
    /// a delivered paste stays a success when the restore fails, because typing the text again to
    /// "recover" would insert it twice.
    /// </summary>
    public ClipboardRestoreOutcome ClipboardRestore { get; init; }
}
