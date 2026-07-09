using Scribe.Core.Models;

namespace Scribe.Core.TextInjection;

/// <summary>
/// Places transcribed text into whatever application currently has keyboard focus.
/// The default strategy sets the clipboard and sends Ctrl+V, then restores the prior
/// clipboard text; a Unicode keystroke strategy is available as a fallback for fields
/// that block synthetic paste.
/// </summary>
public interface ITextInjector
{
    /// <summary>
    /// Injects <paramref name="text"/> into the focused application using the given
    /// <paramref name="method"/>. Runs the clipboard/SendInput sequence on a dedicated STA
    /// thread; callers should invoke this off the UI thread because it includes short delays.
    /// </summary>
    InjectionResult Inject(
        string text,
        InjectionMethod method = InjectionMethod.ClipboardPaste,
        nint expectedForegroundWindow = 0);
}

/// <summary>Outcome of placing text into the target application.</summary>
public sealed record InjectionResult(bool Succeeded, string Method, int Sent, int Total, string? Error = null)
{
    public static InjectionResult Empty { get; } = new(true, "none", 0, 0);
}
