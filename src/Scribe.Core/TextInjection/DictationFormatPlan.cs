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

        if (!settings.AppAwareFormattingEnabled || profile is null)
        {
            return legacy;
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
