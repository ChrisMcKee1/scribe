using System.Text.RegularExpressions;
using Scribe.Core.Models;

namespace Scribe.Core.TextInjection;

/// <summary>
/// Final formatting applied to dictated text just before injection. Terminals interpret an
/// injected newline — typed as a Unicode key event or pasted — as Enter, so a dictation the AI
/// cleanup split into paragraphs would submit several partial messages instead of one. This
/// flattens line breaks to spaces, either everywhere or only when the focused process is a known
/// terminal, per <see cref="NewlineInjectionMode"/>.
/// </summary>
public static class InjectionTextFormatter
{
    // Process names (no .exe suffix, compared case-insensitively) of hosts whose input line treats
    // Enter as "submit". IDE processes (e.g. Code) are deliberately absent: their integrated
    // terminals are indistinguishable from their editors by process name, and mangling newlines in
    // an editor would be worse than an occasional early send — users who live in an IDE terminal
    // can pick AlwaysFlatten instead.
    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal",
        "wt",
        "OpenConsole",
        "conhost",
        "cmd",
        "powershell",
        "pwsh",
        "alacritty",
        "wezterm",
        "wezterm-gui",
        "ConEmu",
        "ConEmu64",
        "mintty",
        "Hyper",
        "Tabby",
        "putty",
        "kitty",
        "warp",
    };

    // A line-break run plus the spaces/tabs hugging it collapses to one space, so a paragraph
    // break never becomes a double space mid-sentence.
    private static readonly Regex NewlineRun = new(@"[ \t]*[\r\n]+[ \t]*", RegexOptions.Compiled);

    /// <summary>True when <paramref name="processName"/> is a known terminal host.</summary>
    public static bool IsTerminalProcess(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) && TerminalProcesses.Contains(processName.Trim());

    /// <summary>True when the effective mode will remove line breaks for this target.</summary>
    public static bool ShouldFlatten(NewlineInjectionMode mode, string? targetProcessName) =>
        mode switch
        {
            NewlineInjectionMode.AlwaysFlatten => true,
            NewlineInjectionMode.SmartFlatten => IsTerminalProcess(targetProcessName),
            _ => false,
        };

    /// <summary>
    /// Applies the configured newline handling for the given target process. Returns the input
    /// unchanged when no flattening is called for (including text with no line breaks at all).
    /// </summary>
    public static string Apply(string text, NewlineInjectionMode mode, string? targetProcessName)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var flatten = ShouldFlatten(mode, targetProcessName);

        if (!flatten || (text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0))
        {
            return text;
        }

        return NewlineRun.Replace(text, " ").Trim();
    }
}
