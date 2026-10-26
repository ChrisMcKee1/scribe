using Scribe.Core.Models;

namespace Scribe.Core.TextInjection;

public enum DictationFormatDecision
{
    FormattingOff,
    NoMatchingProfile,
    Plain,
    MarkdownSource,
    PlainOnce,
    MarkdownNewlineConflict,
}

/// <summary>
/// Immutable preferences taken at capture admission. A process match is a preference, not evidence of what a field
/// accepts. It changes neither recognition nor cleanup, and contains nothing to add to a cleanup request.
/// </summary>
public sealed record DictationFormatPlan(
    DictationTextFormat TextFormat,
    NewlineInjectionMode NewlineHandling,
    InjectionMethod InjectionMethod,
    bool ShiftEnterLineBreaks,
    string? TargetProcessName,
    DictationFormatDecision Decision)
{
    // Known Markdown-aware desktop apps (compared case-insensitively without .exe). When app-aware
    // formatting is enabled and no explicit profile matches, these targets default to MarkdownSource
    // with ClipboardPaste so multi-line lists and paragraphs are pasted as a unit rather than typed
    // line-by-line (where Shift+Enter triggers the editor's list auto-continuation and duplicates
    // bullet markers as "- - ").
    private static readonly HashSet<string> MarkdownProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "github",
        "GitHubCopilot",
        "copilotapp",
        "claude",
        "ChatGPT",
        "M365Copilot",
        "Microsoft Scout",
        "scout",
        "Perplexity",
        "slack",
        "Discord",
        "ms-teams",
        "Teams",
        "Code",
        "Code - Insiders",
        "VSCodium",
        "cursor",
        "windsurf",
        "devenv",
        "rider64",
        "idea64",
        "pycharm64",
        "goland64",
        "clion64",
        "webstorm64",
        "sublime_text",
        "notepad++",
        "zed",
        "Obsidian",
        "Notion",
        "Linear",
        "Typora",
        "MarkText",
        "Joplin",
        "Logseq",
    };

    /// <summary>
    /// Whether this plan's paste keeps everything on the clipboard (an image, formatted text, copied files) and puts it
    /// back, rather than typing when the clipboard holds more than plain text. Only literal Markdown source pasted asks
    /// for it: typed line by line, that text makes a Markdown app continue each list with a marker of its own ("2. 2."),
    /// so a screenshot left on the clipboard must not turn the paste into typing.
    /// </summary>
    public bool KeepsWholeClipboard =>
        TextFormat == DictationTextFormat.MarkdownSource && InjectionMethod == InjectionMethod.ClipboardPaste;

    /// <summary>True when <paramref name="processName"/> is a known Markdown-aware desktop application.</summary>
    public static bool IsMarkdownProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var trimmed = processName.Trim();
        var normalized = trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
        return MarkdownProcesses.Contains(normalized);
    }

    /// <summary>
    /// Binds the one-shot consumption to the plan inside the lifecycle's admitted capture factory.
    /// The caller publishes <paramref name="plainTextOnceChange"/> only after admission releases the lifecycle gate.
    /// </summary>
    public static DictationFormatPlan CaptureAdmitted(
        AppSettings settings, string? targetProcessName, PlainTextOnce plainTextOnce,
        out PlainTextOnceState? plainTextOnceChange)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(plainTextOnce);
        return Capture(settings, targetProcessName, plainTextOnce.ConsumeForAdmittedCapture(out plainTextOnceChange));
    }

    public static DictationFormatPlan Capture(AppSettings settings, string? targetProcessName, bool plainOnce = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var profile = AppProfileMatcher.Match(settings.Profiles, targetProcessName);
        var newline = profile?.NewlineHandling ?? settings.NewlineHandling;
        var legacy = new DictationFormatPlan(
            DictationTextFormat.Plain, newline, settings.InjectionMethod, settings.ShiftEnterLineBreaks, targetProcessName,
            settings.AppAwareFormattingEnabled ? DictationFormatDecision.NoMatchingProfile : DictationFormatDecision.FormattingOff);

        if (!settings.AppAwareFormattingEnabled)
        {
            return legacy;
        }

        if (profile is null)
        {
            if (!IsMarkdownProcess(targetProcessName) ||
                InjectionTextFormatter.ShouldFlatten(newline, targetProcessName))
            {
                return legacy;
            }

            return legacy with
            {
                TextFormat = plainOnce ? DictationTextFormat.Plain : DictationTextFormat.MarkdownSource,
                InjectionMethod = plainOnce ? settings.InjectionMethod : InjectionMethod.ClipboardPaste,
                Decision = plainOnce ? DictationFormatDecision.PlainOnce : DictationFormatDecision.MarkdownSource,
            };
        }

        var format = SafeFormat(profile.TextFormat ?? settings.DefaultTextFormat);
        // Refuse the new preferences as a unit before selecting a representation. Never label flattened source Markdown,
        // or keep a new delivery override after a conflict. Existing profile newline and writing-style rules still apply.
        if (!plainOnce && ConflictsWithNewlines(format, newline, targetProcessName))
        {
            return legacy with { Decision = DictationFormatDecision.MarkdownNewlineConflict };
        }

        var method = profile.InjectionMethod is { } chosen && Enum.IsDefined(chosen)
            ? chosen
            : settings.InjectionMethod;
        var formatDecision = format == DictationTextFormat.MarkdownSource
            ? DictationFormatDecision.MarkdownSource
            : DictationFormatDecision.Plain;
        return legacy with
        {
            TextFormat = plainOnce ? DictationTextFormat.Plain : format,
            InjectionMethod = method,
            ShiftEnterLineBreaks = profile.ShiftEnterLineBreaks ?? settings.ShiftEnterLineBreaks,
            Decision = plainOnce ? DictationFormatDecision.PlainOnce : formatDecision,
        };
    }

    /// <summary>
    /// R: the actual postprocessed characters, after the existing target newline policy. Both representations are identity
    /// renderers in this MVP: no markers added or removed, no Markdown-to-rich conversion, no parsing of code or URLs.
    /// Delivery, including a refused paste's typing fallback, receives this one result without recalculating it.
    /// </summary>
    public string Represent(string postprocessedText)
    {
        ArgumentNullException.ThrowIfNull(postprocessedText);
        return InjectionTextFormatter.Apply(postprocessedText, NewlineHandling, TargetProcessName);
    }

    public static DictationTextFormat SafeFormat(DictationTextFormat value) =>
        value == DictationTextFormat.MarkdownSource ? value : DictationTextFormat.Plain;

    public static bool ConflictsWithNewlines(
        DictationTextFormat format, NewlineInjectionMode newline, string? targetProcessName) =>
        SafeFormat(format) == DictationTextFormat.MarkdownSource &&
        InjectionTextFormatter.ShouldFlatten(newline, targetProcessName);
}

public static class DictationFormattingText
{
    public const string NewlineConflict =
        "Literal Markdown source needs line breaks. Choose Plain text, or Keep line breaks and check the typing method " +
        "in the app yourself. A program name cannot tell a message box from a document or a command window.";

    public const string RuntimeNewlineConflict =
        "Scribe used plain text and the usual typing method because this app's line-break rule uses one line. " +
        "Choose Plain text, or Keep line breaks and check delivery in the app yourself.";

    public const string SourceLimits =
        "Literal Markdown source keeps the characters already in your text, including a snippet's markers. It doesn't " +
        "turn speech into lists, links or code blocks, and doesn't correct changes made by AI cleanup. No rich text is sent.";

    public static string Describe(DictationFormatPlan plan)
    {
        var format = plan.TextFormat == DictationTextFormat.MarkdownSource ? "Literal Markdown source" : "Plain text";
        var method = plan.InjectionMethod == InjectionMethod.ClipboardPaste ? "Paste requested" : "Typing requested";
        var lineBreak = plan.ShiftEnterLineBreaks ? "Shift+Enter for typed line breaks" : "Enter for typed line breaks";
        return $"{format}. {method}; {lineBreak}.";
    }
}
