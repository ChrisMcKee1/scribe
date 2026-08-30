using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.TextActions;
using Scribe.Core.TextInjection;

namespace Scribe.App.TextActions;

/// <summary>
/// Drives the text action feature end to end: capture the selection, show the palette, run the
/// chosen action, and write the result back over the selection.
/// </summary>
/// <remarks>
/// Ordering here is load-bearing. The selection is captured to a string BEFORE the palette is shown,
/// because the palette takes focus and the target app will grey (and in some apps drop) its
/// selection highlight the moment it loses foreground. Everything after that point operates on the
/// captured string and the captured window handle, never on live UI state.
/// </remarks>
internal sealed class TextActionController : IDisposable
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private readonly SelectionReader _selection;
    private readonly ITextCleanupService _cleanup;
    private readonly ITextPostProcessor _postProcessor;
    private readonly IDictionaryRepository _dictionary;
    private readonly IDictionaryLibraryService? _libraries;
    private readonly ITextInjector _injector;
    private readonly ILogger<TextActionController> _log;

    private AppSettings _settings = AppSettings.CreateDefault();
    private TextActionPaletteWindow? _window;
    private CancellationTokenSource? _runCts;
    private SelectionCapture? _capture;
    private bool _disposed;

    public TextActionController(
        SelectionReader selection,
        ITextCleanupService cleanup,
        ITextPostProcessor postProcessor,
        IDictionaryRepository dictionary,
        ITextInjector injector,
        ILogger<TextActionController> log,
        IDictionaryLibraryService? libraries = null)
    {
        _selection = selection;
        _cleanup = cleanup;
        _postProcessor = postProcessor;
        _dictionary = dictionary;
        _injector = injector;
        _log = log;
        _libraries = libraries;
    }

    /// <summary>Raised with a short sentence when the feature cannot proceed, for a tray notice.</summary>
    public event Action<string>? Notice;

    /// <summary>
    /// Supplies the last non-Scribe foreground window. Set by the host so the tray and dock routes
    /// can read a selection even though reaching them moved focus.
    /// </summary>
    public Func<nint>? ForegroundTargetProvider { get; set; }

    /// <summary>Called as the dock's visible state should change, so the host can drive the face.</summary>
    public event Action<DockState>? StateChanged;

    /// <summary>Replaces the live settings after a save.</summary>
    public void ApplySettings(AppSettings settings) => _settings = settings.Clone();

    /// <summary>
    /// Entry point from the hotkey, the dock, or the tray. Captures whatever is selected and opens
    /// the palette. Safe to call repeatedly: a second invocation while the palette is open closes it.
    /// </summary>
    /// <param name="useRememberedTarget">
    /// True when the caller may have taken focus on the way here. The tray menu definitely does, so
    /// by the time its handler runs the selection's owner has been deactivated and reading the live
    /// foreground window would read Scribe's own menu. The hotkey does not, and neither does the
    /// dock (it is WS_EX_NOACTIVATE), so both pass false and read what is genuinely in front.
    /// </param>
    public void Invoke(bool useRememberedTarget = false)
    {
        if (_disposed)
        {
            return;
        }

        if (_window is not null)
        {
            _window.Close();
            return;
        }

        var target = useRememberedTarget ? ForegroundTargetProvider?.Invoke() ?? 0 : 0;

        StateChanged?.Invoke(DockState.Reading);
        var capture = _selection.Capture(target);
        if (!capture.Succeeded)
        {
            _log.LogInformation("Text action capture failed: {Failure}.", capture.Failure);
            StateChanged?.Invoke(DockState.Failed);
            Notice?.Invoke(capture.Detail);
            ResetStateSoon();
            return;
        }

        StateChanged?.Invoke(DockState.Idle);
        _capture = capture;
        ShowPalette(capture);
    }

    // Transient states hold briefly, then the dock goes back to sleep. Sleep is the resting state
    // precisely because it is the honest one: Scribe is reading nothing between invocations.
    private void ResetStateSoon()
    {
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(
            _ => StateChanged?.Invoke(DockState.Idle),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ShowPalette(SelectionCapture capture)
    {
        var selection = capture.Text!;

        // Precomputed so the vocabulary row can show a real count, and disappear when it would do
        // nothing at all rather than sitting there as a button that never changes anything.
        var vocabularyFixes = 0;
        try
        {
            vocabularyFixes = _postProcessor.ProcessSelection(selection).Replacements.Count;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Vocabulary preview failed; offering the action anyway.");
        }

        var window = new TextActionPaletteWindow(
            TextActionCatalog.All,
            selection,
            DescribeDestination(),
            vocabularyFixes);

        window.ActionChosen += action => _ = RunActionAsync(action, selection, capture);
        window.ReplaceRequested += text => Complete(text, capture, copyInstead: false);
        window.CopyRequested += text => Complete(text, capture, copyInstead: true);
        window.Closed += (_, _) =>
        {
            _runCts?.Cancel();
            _runCts = null;
            _window = null;
            _capture = null;
        };

        _window = window;
        window.Show();
        window.PositionNearCursor();

        // Not window.Activate(). The dock is WS_EX_NOACTIVATE, so clicking it leaves Scribe as a
        // background process, and WPF's Activate cannot win the foreground from there. The palette
        // then renders on top with logical focus only, and every click on an action row is ignored.
        window.ForceForeground();
    }

    private async Task RunActionAsync(TextAction action, string selection, SelectionCapture capture)
    {
        var window = _window;
        if (window is null)
        {
            return;
        }

        window.ShowRunning(action);
        StateChanged?.Invoke(DockState.Working);

        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;

        try
        {
            string result;

            if (action.Kind == TextActionKind.Deterministic)
            {
                result = _postProcessor.ProcessSelection(selection).Text;
            }
            else
            {
                var profile = AppProfileMatcher.Match(_settings.Profiles, capture.TargetProcess);
                var singleLine = InjectionTextFormatter.ShouldFlatten(
                    profile?.NewlineHandling ?? _settings.NewlineHandling, capture.TargetProcess);

                // Per-app profile first, then the user's global cleanup style. Passing only the
                // profile (as this did originally) meant that on a machine with no matching profile
                // the configured writing style never reached the model at all, so the number, date
                // and acronym conventions users had set for dictation silently did not apply here.
                var houseStyle = string.IsNullOrWhiteSpace(profile?.WritingStyle)
                    ? _settings.AiCleanupWritingStyle
                    : profile!.WritingStyle;

                var outcome = await _cleanup.ApplyActionAsync(
                    selection,
                    action,
                    EffectiveGlossary(),
                    spokenInstruction: null,
                    writingStyleOverride: houseStyle,
                    requireSingleLine: singleLine,
                    cancellationToken: token).ConfigureAwait(true);

                if (!outcome.Succeeded)
                {
                    if (outcome.Failure != TextActionFailure.Cancelled)
                    {
                        // Logged here and not only where the exception is caught. NotEnabled,
                        // NotReady, Rejected, TooLarge and EmptySelection never throw: they return a
                        // failed result from a guard, so this branch was the one failure path that
                        // left no trace at all. That silence is why a run of dead-looking clicks was
                        // diagnosed as a focus bug and fixed in the wrong layer; one reproduction now
                        // names the failure instead.
                        _log.LogInformation(
                            "Text action {ActionId} failed: {Failure}. {Detail}",
                            action.Id,
                            outcome.Failure,
                            outcome.Detail);

                        window.ShowFailure(outcome.Detail);
                        StateChanged?.Invoke(DockState.Failed);
                        ResetStateSoon();
                    }
                    else
                    {
                        StateChanged?.Invoke(DockState.Idle);
                    }

                    return;
                }

                result = outcome.Text!;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            StateChanged?.Invoke(DockState.Done);
            ResetStateSoon();

            // Resolved at capture time by the selection probe, not guessed here. Measured, every
            // document-style surface that is not a RichEdit reads fine and cannot be written: a WPF
            // RichTextBox and Windows Terminal both expose TextPattern without ValuePattern. That is
            // the common case for exactly the surfaces this feature targets, so the palette must not
            // promise a replacement it cannot perform.
            var editable = capture.CanWriteBack && IsProbablyEditable(capture.TargetWindow);
            window.ShowResult(
                action,
                result,
                canReplace: editable,
                replaceBlockedReason: editable
                    ? null
                    : "Scribe can read this text but cannot replace it here, so use Copy instead.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Text action {ActionId} failed.", action.Id);
            window.ShowFailure("Something went wrong. Your text was left alone.");
        }
    }

    private void Complete(string text, SelectionCapture capture, bool copyInstead)
    {
        _window?.Close();

        if (copyInstead)
        {
            CopyAsFallback(text, "Copied the result to your clipboard.");
            return;
        }

        // Off the UI thread: restoring the foreground polls for up to 700 ms and the injector adds
        // its own settle delays, so doing this on the dispatcher would freeze the app visibly at the
        // exact moment the user is watching for their text to change.
        _ = Task.Run(() => WriteBack(text, capture));
    }

    private void WriteBack(string text, SelectionCapture capture)
    {

        // Legal here and nowhere else: Scribe owns the foreground because the palette just had it,
        // which is one of the documented conditions under which SetForegroundWindow is permitted.
        //
        // And it must be WAITED FOR. SetForegroundWindow only requests activation; the change lands
        // when the target thread processes it. Injecting immediately sent the opening keystrokes into
        // the activation gap, where they went to the outgoing window instead of the document, which
        // is why the first character of every rewrite went missing. SelectionReader already polls
        // for this on the read side; the write side has to do the same.
        if (capture.TargetWindow != 0 && !RestoreForeground(capture.TargetWindow))
        {
            _log.LogWarning("Target window never came back to the foreground; not injecting.");
            CopyAsFallback(text, "Scribe could not switch back to your app, so the result is on your clipboard.");
            return;
        }

        var profile = AppProfileMatcher.Match(_settings.Profiles, capture.TargetProcess);
        var formatted = InjectionTextFormatter.Apply(
            text, profile?.NewlineHandling ?? _settings.NewlineHandling, capture.TargetProcess);

        // Multi-line results go in by paste rather than by typing, whatever the configured method.
        // An editor that auto-indents adds its own leading whitespace after every newline, so typed
        // pretty-printed JSON arrives with the indentation doubling on each level, and a typed
        // Markdown list gets the editor's own auto-continued bullet on top of ours. A paste is one
        // atomic insert that no auto-formatter gets to interfere with. TextInjector already falls
        // back to typing when the clipboard cannot be acquired, so this cannot strand the result.
        var multiLine = formatted.AsSpan().IndexOfAny('\n', '\r') >= 0;
        var method = multiLine ? InjectionMethod.ClipboardPaste : _settings.InjectionMethod;

        // The selection is still live in the target: the palette never took it away, it only greyed
        // the highlight. Injecting replaces it, exactly as typing over a selection would.
        var result = _injector.Inject(
            formatted,
            method,
            capture.TargetWindow,
            _settings.ShiftEnterLineBreaks);

        if (!result.Succeeded)
        {
            _log.LogWarning("Text action write-back failed: {Error}.", result.Error);
            CopyAsFallback(text, "Scribe could not type the result, so it is on your clipboard instead.");
        }
    }

    /// <summary>
    /// Brings <paramref name="target"/> back to the foreground and waits until Windows has actually
    /// made the change, returning false if it never does.
    /// </summary>
    /// <remarks>
    /// Two asynchronous transitions are racing at this point: the palette closing (which hands focus
    /// back to whatever Windows picks) and this explicit activation. Polling is what makes the pair
    /// deterministic. The settle delay afterwards matters separately: a window can be foreground
    /// before its control has rebuilt its caret and selection state, and input delivered in that
    /// window is silently dropped.
    /// </remarks>
    private static bool RestoreForeground(nint target)
    {
        // Ask for activation even when the window already looks foreground. Closing the palette hands
        // focus back asynchronously, so "already foreground" here can mean "mid-restoration", and the
        // request is harmless when it is genuinely settled.
        _ = SetForegroundWindow(target);

        // Then wait on the real signal rather than a timer. WaitForInputReady polls until the target
        // thread reports a focused control, which is what actually determines whether a keystroke
        // will land, and settles on every success path including the already-foreground one.
        return ForegroundReadiness.WaitForInputReady(target);
    }

    /// <summary>
    /// Puts the result on the clipboard and tells the user. Marshals to the UI thread because WPF's
    /// <see cref="Clipboard"/> requires STA and the write-back path now runs on a worker.
    /// </summary>
    private void CopyAsFallback(string text, string notice)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.Invoke(() =>
        {
            try
            {
                Clipboard.SetText(text);
                Notice?.Invoke(notice);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Clipboard fallback failed.");
                Notice?.Invoke("Scribe could not replace the text.");
            }
        });
    }

    // Best-effort: the standard-edit fast path is the only case Scribe can positively confirm is
    // writable. Everything else is offered as a copy, which is honest rather than probabilistic.
    private static bool IsProbablyEditable(nint window) => window != 0;

    private IReadOnlyList<DictionaryEntry> EffectiveGlossary()
    {
        try
        {
            var own = _dictionary.GetEnabled();
            if (_libraries is null)
            {
                return own;
            }

            // The user's own entries first: they are the terms a model cannot guess, so they must
            // survive truncation when a small model's budget cuts the list short.
            var libraryEntries = _libraries.GetEnabledLibraryEntries();
            return libraryEntries.Count == 0
                ? own
                : DictionaryLibraryComposer.Merge(own, libraryEntries);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Building the action glossary failed; continuing without one.");
            return [];
        }
    }

    private string DescribeDestination() => _settings switch
    {
        { EnableAiCleanup: false } => "No model",
        { AiCleanupProvider: CleanupProvider.FoundryLocal } => "On this device",
        { AiCleanupProvider: CleanupProvider.AzureFoundry } => HostOf(_settings.AiCleanupAzureEndpoint),
        { AiCleanupProvider: CleanupProvider.OpenAiCompatible } => HostOf(_settings.AiCleanupCustomEndpoint),
        _ => "Unknown",
    };

    private static string HostOf(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return "Not configured";
        }

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "Configured";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runCts?.Cancel();
        _runCts?.Dispose();
        _window?.Close();
    }
}
