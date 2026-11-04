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
    MarkdownWithFormatting,
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
    // bullet markers as "- - "). Apps in FormattedMarkdownProcesses get the same paste plus an HTML copy.
    private static readonly HashSet<string> MarkdownProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "github",
        "GitHubCopilot",
        "claude",
        "M365Copilot",
        "Microsoft Scout",
        "scout",
        "Perplexity",
        "slack",
        "Discord",
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

    // Apps that read the clipboard's HTML but leave pasted Markdown as typed, measured by pasting into each on 2026-11-04:
    // Teams showed "**Bold**" and "- item" literally and turned the HTML copy into real formatting; Microsoft Copilot
    // (copilotapp) half-converted pasted Markdown and ran a fenced code block into the lines after it, while the HTML copy
    // lost no line; Codex (the ChatGPT app) kept Markdown literal and formatted the HTML copy. They default to
    // MarkdownWithFormatting. The GitHub Copilot app is deliberately not here: it converts pasted Markdown fully (task boxes
    // and a code block's language included), and given both it takes the HTML, which loses those two.
    private static readonly HashSet<string> FormattedMarkdownProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ms-teams",
        "Teams",
        "copilotapp",
        "ChatGPT",
    };

    /// <summary>
    /// Whether this plan's paste keeps everything on the clipboard (an image, formatted text, copied files) and puts it
    /// back, rather than typing when the clipboard holds more than plain text. Only literal Markdown source pasted asks
    /// for it: typed line by line, that text makes a Markdown app continue each list with a marker of its own ("2. 2."),
    /// so a screenshot left on the clipboard must not turn the paste into typing.
    /// </summary>
    public bool KeepsWholeClipboard =>
        IsMarkdown(TextFormat) && InjectionMethod == InjectionMethod.ClipboardPaste;

    /// <summary>True when <paramref name="processName"/> is a known Markdown-aware desktop application.</summary>
    public static bool IsMarkdownProcess(string? processName) => BuiltInFormat(processName) is not null;

    /// <summary>
    /// The format a known app gets when no profile matches it: <see cref="DictationTextFormat.MarkdownWithFormatting"/> for
    /// an app that reads the clipboard's HTML but not pasted Markdown, <see cref="DictationTextFormat.MarkdownSource"/> for
    /// the other Markdown-aware apps, and null for every other app.
    /// </summary>
    public static DictationTextFormat? BuiltInFormat(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        var trimmed = processName.Trim();
        var normalized = trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
        if (FormattedMarkdownProcesses.Contains(normalized))
        {
            return DictationTextFormat.MarkdownWithFormatting;
        }

        return MarkdownProcesses.Contains(normalized) ? DictationTextFormat.MarkdownSource : null;
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
            if (BuiltInFormat(targetProcessName) is not { } builtIn ||
                InjectionTextFormatter.ShouldFlatten(newline, targetProcessName))
            {
                return legacy;
            }

            return legacy with
            {
                TextFormat = plainOnce ? DictationTextFormat.Plain : builtIn,
                InjectionMethod = plainOnce ? settings.InjectionMethod : InjectionMethod.ClipboardPaste,
                Decision = plainOnce ? DictationFormatDecision.PlainOnce : DecisionFor(builtIn),
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
        var formatDecision = DecisionFor(format);
        return legacy with
        {
            TextFormat = plainOnce ? DictationTextFormat.Plain : format,
            InjectionMethod = method,
            ShiftEnterLineBreaks = profile.ShiftEnterLineBreaks ?? settings.ShiftEnterLineBreaks,
            Decision = plainOnce ? DictationFormatDecision.PlainOnce : formatDecision,
        };
    }

    /// <summary>
    /// R: the actual postprocessed characters, after the existing target newline policy. Every format's text is an identity
    /// rendering: no markers added or removed, no parsing of code or URLs. Delivery, including a refused paste's typing
    /// fallback, receives this one result without recalculating it, and history keeps it.
    /// </summary>
    public string Represent(string postprocessedText)
    {
        ArgumentNullException.ThrowIfNull(postprocessedText);
        return InjectionTextFormatter.Apply(postprocessedText, NewlineHandling, TargetProcessName);
    }

    /// <summary>
    /// The HTML fragment "Markdown with formatting" puts on the clipboard beside <paramref name="represented"/> (R, the text
    /// <see cref="Represent"/> returned), or null: for every other format, for a dictation that is typed rather than pasted,
    /// and for text with no Markdown structure (see <see cref="MarkdownClipboardHtml"/>). It is only ever an addition to the
    /// clipboard; nothing keeps it.
    /// </summary>
    public string? ClipboardHtml(string represented)
    {
        ArgumentNullException.ThrowIfNull(represented);
        return TextFormat == DictationTextFormat.MarkdownWithFormatting && InjectionMethod == InjectionMethod.ClipboardPaste
            ? MarkdownClipboardHtml.TryRender(represented)
            : null;
    }

    public static DictationTextFormat SafeFormat(DictationTextFormat value) =>
        value is DictationTextFormat.MarkdownSource or DictationTextFormat.MarkdownWithFormatting
            ? value
            : DictationTextFormat.Plain;

    /// <summary>Whether <paramref name="format"/> pastes Markdown source, with or without its formatted copy.</summary>
    public static bool IsMarkdown(DictationTextFormat format) => SafeFormat(format) != DictationTextFormat.Plain;

    public static bool ConflictsWithNewlines(
        DictationTextFormat format, NewlineInjectionMode newline, string? targetProcessName) =>
        IsMarkdown(format) && InjectionTextFormatter.ShouldFlatten(newline, targetProcessName);

    private static DictationFormatDecision DecisionFor(DictationTextFormat format) => SafeFormat(format) switch
    {
        DictationTextFormat.MarkdownSource => DictationFormatDecision.MarkdownSource,
        DictationTextFormat.MarkdownWithFormatting => DictationFormatDecision.MarkdownWithFormatting,
        _ => DictationFormatDecision.Plain,
    };
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
        "turn speech into lists, links or code blocks, and doesn't correct changes made by AI cleanup. Markdown with " +
        "formatting pastes that same text and adds a formatted copy of it, for apps that show pasted Markdown as typed.";

    public static string Describe(DictationFormatPlan plan)
    {
        var format = plan.TextFormat switch
        {
            DictationTextFormat.MarkdownSource => "Literal Markdown source",
            DictationTextFormat.MarkdownWithFormatting => "Markdown with formatting",
            _ => "Plain text",
        };
        var method = plan.InjectionMethod == InjectionMethod.ClipboardPaste ? "Paste requested" : "Typing requested";
        var lineBreak = plan.ShiftEnterLineBreaks ? "Shift+Enter for typed line breaks" : "Enter for typed line breaks";
        return $"{format}. {method}; {lineBreak}.";
    }
}
