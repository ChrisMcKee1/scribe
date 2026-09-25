using Scribe.Core.PostProcessing;

namespace Scribe.Core.TextInjection;

/// <summary>
/// The insertion step of a dictation, decided here so a test can reach it (the controller has none): what is typed into
/// the target, and what is kept. With <see cref="Models.AppSettings.AddSpaceAfterDictation"/> on, the default, the
/// target is given one space after the dictation so the next dictation does not run into it (issue #78). Only the target
/// gets it: history, the tray's recent dictations, the recovery notice's copy, quick add and the playground's report keep
/// the text as it was dictated.
/// </summary>
public static class DictationInsertion
{
    /// <summary>
    /// What to type for a dictation that produced <paramref name="text"/>. With <paramref name="addSpaceAfterDictation"/>
    /// on, the text followed by one space, unless it is empty or already ends in white space: any character
    /// <see cref="char.IsWhiteSpace(char)"/> accepts (a space, a tab, a line break, a no-break space and every other
    /// Unicode space) counts as already there, so a snippet ending in a line break gets nothing, and a dictation that
    /// inserts nothing still inserts nothing. Otherwise <paramref name="text"/> itself.
    /// </summary>
    public static string TextToType(string text, bool addSpaceAfterDictation)
    {
        ArgumentNullException.ThrowIfNull(text);

        return addSpaceAfterDictation && text.Length > 0 && !char.IsWhiteSpace(text[^1])
            ? text + " "
            : text;
    }

    /// <summary>
    /// Inserts a finished dictation in the order the controller has always used: the text is kept for recovery first, so
    /// an insertion that fails, stops part way or never runs leaves it copyable from the tray, then cancellation is
    /// checked, and only then is it typed, with the space when <see cref="TextToType"/> adds one.
    /// </summary>
    /// <param name="text">
    /// The dictation as the pipeline finished it, after AI cleanup, the dictionary and snippets and the line-break
    /// handling for the target. The space comes after all of them: flattening trims the text for a single-line target,
    /// so a space added before it would never reach one.
    /// </param>
    /// <param name="addSpaceAfterDictation">The setting of the capture being inserted.</param>
    /// <param name="recovery">The tray's recent dictations, which the recovery notice and quick add read.</param>
    /// <param name="inject">Types the text it is given into the target: the controller's <c>ITextInjector.Inject</c>.</param>
    /// <param name="cancellationToken">The controller's lifetime token: nothing is typed once shutdown has begun.</param>
    /// <exception cref="OperationCanceledException">The token was canceled; the text was kept and nothing was typed.</exception>
    public static DictationInsertionResult Insert(
        string text,
        bool addSpaceAfterDictation,
        LastTranscriptStore recovery,
        Func<string, InjectionResult> inject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(inject);

        recovery.Set(text);
        cancellationToken.ThrowIfCancellationRequested();

        var typed = TextToType(text, addSpaceAfterDictation);
        return new DictationInsertionResult(text, typed, inject(typed));
    }
}

/// <summary>How one dictation's insertion went (<see cref="DictationInsertion.Insert"/>).</summary>
public sealed class DictationInsertionResult
{
    internal DictationInsertionResult(string recorded, string typed, InjectionResult injection)
    {
        Recorded = recorded;
        Typed = typed;
        Injection = injection;
    }

    /// <summary>
    /// The dictation as it is kept: in history, the tray's recent dictations, the recovery notice's copy, quick add and
    /// the playground's report. It never carries the space added for the target.
    /// </summary>
    public string Recorded { get; }

    /// <summary>How typing into the target went.</summary>
    public InjectionResult Injection { get; }

    /// <summary>
    /// Whether the text handed to the target carried a space after the dictation; whether it arrived is
    /// <see cref="Injection"/>'s to say. A shape the log and the playground may show.
    /// </summary>
    public bool SpaceAdded => Typed.Length != Recorded.Length;

    /// <summary>
    /// What the target was given. Internal on purpose: the shell cannot reach it, so it cannot hand the spaced text to
    /// anything that keeps text. Only the inject callback ever sees it.
    /// </summary>
    internal string Typed { get; }
}
