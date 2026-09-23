using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.TextInjection;

/// <summary>
/// Default <see cref="ITextInjector"/>. Clipboard-paste runs the whole borrow, Ctrl+V, restore sequence
/// on a dedicated STA thread, with small delays so the target app reads the clipboard before it is
/// restored. Falls back to Unicode keystroke typing whenever no paste can have fired, and exposes typing
/// directly via <see cref="InjectionMethod.UnicodeType"/>.
/// </summary>
public sealed class TextInjector : ITextInjector
{
    // Let the target consume the paste before we restore the prior clipboard text.
    internal const int PasteSettleDelayMs = 130;

    // Brief pause so the clipboard is committed before Ctrl+V is delivered.
    internal const int ClipboardSettleDelayMs = 30;

    // Unicode typing is sent in small batches so a long dictation can't overrun the target's input
    // queue (which silently drops keystrokes). Each batch is UnicodeChunkChars code units; we pause
    // InterChunkSettleMs between batches and resend a short SendInput remainder up to MaxChunkRetries.
    private const int UnicodeChunkChars = 50;
    private const int InterChunkSettleMs = 5;
    private const int ChunkRetryDelayMs = 12;
    private const int MaxChunkRetries = 5;

    private readonly ILogger _logger;
    private readonly IInjectionPlatform _platform;
    private readonly ClipboardBorrower _clipboard;

    public TextInjector(ILogger<TextInjector> logger)
        : this(logger, Win32InjectionPlatform.Instance, Win32Clipboard.Instance)
    {
    }

    internal TextInjector(ILogger<TextInjector> logger, IInjectionPlatform platform, IClipboardNative clipboard)
    {
        _logger = new NonThrowingLogger(logger);
        _platform = platform;
        _clipboard = new ClipboardBorrower(clipboard, platform.Sleep);
    }

    public InjectionResult Inject(
        string text,
        InjectionMethod method = InjectionMethod.ClipboardPaste,
        nint expectedForegroundWindow = 0,
        bool shiftEnterLineBreaks = true)
    {
        if (string.IsNullOrEmpty(text))
        {
            return InjectionResult.Empty;
        }

        if (!IsExpectedForeground(expectedForegroundWindow))
        {
            return FocusChanged(text.Length);
        }

        // Capture the ambient dictation span on the calling thread; the STA worker is a fresh
        // Thread, so Activity.Current would not flow to it. We re-parent the child span explicitly.
        var parent = Activity.Current;

        return RunOnStaThread(() =>
        {
            var activity = TryStartActivity(parent, text.Length);
            try
            {
                return InjectOnStaThread(activity, text, method, expectedForegroundWindow, shiftEnterLineBreaks);
            }
            finally
            {
                // Listeners run synchronously when the span stops, after the text may already have
                // reached the target, so a faulting one must not turn that into a failed injection.
                TryStop(activity);
            }
        });
    }

    private InjectionResult InjectOnStaThread(
        Activity? activity, string text, InjectionMethod method, nint expectedForegroundWindow, bool shiftEnterLineBreaks)
    {
        if (!IsExpectedForeground(expectedForegroundWindow))
        {
            return FocusChanged(text.Length);
        }

        if (_platform.TryInsertIntoStandardEdit(text, expectedForegroundWindow))
        {
            ReportInjection(activity, "win32-edit", text.Length, text.Length, fallback: false);
            return new InjectionResult(true, "win32-edit", text.Length, text.Length);
        }

        if (method == InjectionMethod.UnicodeType)
        {
            return TypeAndReport(activity, text, expectedForegroundWindow, shiftEnterLineBreaks, fallback: false);
        }

        var paste = PasteViaClipboard(text, expectedForegroundWindow);
        ReportClipboard(activity, paste);
        LogClipboard(paste);

        if (paste.Delivery == PasteDelivery.ChordInserted)
        {
            // Delivered. Whatever became of the restore, typing now would insert the text twice.
            ReportInjection(activity, "clipboard", paste.Sent, paste.Total, fallback: false);
            return new InjectionResult(
                true, "clipboard", paste.Sent, paste.Total,
                paste.Sent == paste.Total ? null : "Paste completed, but modifier cleanup was partial.")
            {
                Paste = paste.Delivery,
                ClipboardRestore = paste.Restore,
            };
        }

        if (paste.Delivery == PasteDelivery.FocusChanged)
        {
            return FocusChanged(text.Length) with { Paste = paste.Delivery, ClipboardRestore = paste.Restore };
        }

        // No paste can have fired, so typing cannot duplicate anything.
        return TypeAndReport(activity, text, expectedForegroundWindow, shiftEnterLineBreaks, fallback: true) with
        {
            Paste = paste.Delivery,
            ClipboardRestore = paste.Restore,
        };
    }

    // Telemetry is optional and injection is not, so neither end of the span may throw into it.
    private static Activity? TryStartActivity(Activity? parent, int chars)
    {
        try
        {
            var activity = ScribeTelemetry.Source.StartActivity(
                ScribeTelemetry.InjectActivity, ActivityKind.Internal, parent?.Context ?? default);
            activity?.SetTag(ScribeTelemetry.TagInjectChars, chars);
            return activity;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryStop(Activity? activity)
    {
        try
        {
            activity?.Dispose();
        }
        catch (Exception)
        {
            // A trace listener's fault is not the dictation's.
        }
    }

    private InjectionResult TypeAndReport(
        Activity? activity, string text, nint expectedForegroundWindow, bool shiftEnterLineBreaks, bool fallback)
    {
        var typed = TypeUnicode(text, expectedForegroundWindow, shiftEnterLineBreaks);
        ReportInjection(activity, "unicode", typed.Sent, typed.Total, fallback);
        return new InjectionResult(
            typed.Sent == typed.Total, "unicode", typed.Sent, typed.Total,
            typed.Sent == typed.Total ? null : "Only part of the text was accepted by Windows.");
    }

    private static InjectionResult FocusChanged(int total) =>
        new(false, "none", 0, total, InjectionResult.FocusChangedError);

    private bool IsExpectedForeground(nint expected) =>
        expected == 0 || _platform.GetForegroundWindow() == expected;

    // Runs after text may already have reached the target, so it must not throw (see TryStop).
    private static void ReportInjection(Activity? activity, string method, int sent, int total, bool fallback)
    {
        if (activity is null)
        {
            return;
        }

        try
        {
            var complete = sent == total;
            activity.SetTag(ScribeTelemetry.TagInjectMethod, method);
            activity.SetTag(ScribeTelemetry.TagInjectSent, sent);
            activity.SetTag(ScribeTelemetry.TagInjectTotal, total);
            activity.SetTag(ScribeTelemetry.TagInjectComplete, complete);
            activity.SetTag(ScribeTelemetry.TagInjectFallback, fallback);

            // A partial SendInput is the smoking gun for "I spoke but nothing (or only part) appeared";
            // mark the span as an error so it stands out in the log and any OTLP backend.
            if (!complete)
            {
                activity.SetStatus(ActivityStatusCode.Error, $"SendInput delivered {sent}/{total} events.");
            }
        }
        catch (Exception)
        {
            // Telemetry is optional; the injection outcome is not.
        }
    }

    private readonly record struct ClipboardPasteReport(
        PasteDelivery Delivery,
        ClipboardRestoreOutcome Restore,
        int Sent,
        int Total,
        int OpenAttempts,
        bool ConfirmOpened,
        bool CloseMovedSequence);

    private ClipboardPasteReport PasteViaClipboard(string text, nint expectedForegroundWindow)
    {
        // An image, copied files or other non-text content can't be saved and restored (text-only by
        // design), so the borrow refuses it and the caller types instead; slower, but the user's
        // screenshot or file copy survives the dictation. Every other refusal leaves nothing of
        // Scribe's behind either: a text or receipt write that failed after emptying was rolled back in
        // the same session.
        var borrow = _clipboard.TryBorrow(text);
        if (borrow.Lease is not { } lease)
        {
            var refused = borrow.Status switch
            {
                ClipboardBorrowStatus.Busy => PasteDelivery.ClipboardBusy,
                ClipboardBorrowStatus.NonTextContent => PasteDelivery.NonTextContent,
                ClipboardBorrowStatus.SnapshotUnreadable => PasteDelivery.SnapshotUnreadable,
                ClipboardBorrowStatus.ReceiptFailed => PasteDelivery.ReceiptFailed,
                _ => PasteDelivery.WriteFailed,
            };
            return new(refused, borrow.Rollback, 0, 0, borrow.OpenAttempts, false, false);
        }

        bool confirmOpened = false;
        ClipboardPasteReport Report(PasteDelivery delivery, ClipboardRestoreOutcome restore, int sent = 0, int total = 0) =>
            new(delivery, restore, sent, total, borrow.OpenAttempts, confirmOpened, lease.CloseMovedSequence);

        // The clipboard is closed during this wait and stays closed through the paste: the target has
        // to open it to read, so holding it here would break every paste.
        _platform.Sleep(ClipboardSettleDelayMs);
        if (!IsExpectedForeground(expectedForegroundWindow))
        {
            return Report(PasteDelivery.FocusChanged, RestoreClipboard(lease));
        }

        // Another application may have copied at any moment since the borrow released the clipboard, and
        // Ctrl+V would then paste its content. Nothing was sent yet, so typing instead cannot duplicate
        // anything.
        switch (_clipboard.Confirm(lease, out confirmOpened))
        {
            case ClipboardCheck.Superseded:
                return Report(PasteDelivery.Superseded, ClipboardRestoreOutcome.Superseded);
            case ClipboardCheck.Busy:
                return Report(PasteDelivery.Unconfirmed, RestoreClipboard(lease));
        }

        // Confirm can spend its open retries waiting on another process, and focus can move in that
        // time (Alt+Tab, a toast), so the window is checked again right before the keystrokes.
        if (!IsExpectedForeground(expectedForegroundWindow))
        {
            return Report(PasteDelivery.FocusChanged, RestoreClipboard(lease));
        }

        var ctrlV = SendCtrlV();

        // A short send can leave Ctrl (or V) logically held down; release both before anything
        // else so a typing fallback can't turn the dictation into accidental keyboard shortcuts.
        if (ctrlV.Sent < ctrlV.Total)
        {
            ReleaseCtrlV();
        }

        // The paste fires on the V-down (the chord's second event). Fewer than two inserted means
        // no paste happened at all: put the user's clipboard back and report it so the caller
        // types the text instead of losing the dictation.
        if (ctrlV.Sent < 2)
        {
            return Report(PasteDelivery.ChordIncomplete, RestoreClipboard(lease), ctrlV.Sent, ctrlV.Total);
        }

        _platform.Sleep(PasteSettleDelayMs);
        return Report(PasteDelivery.ChordInserted, RestoreClipboard(lease), ctrlV.Sent, ctrlV.Total);
    }

    // The restore can run after the paste was delivered, so a fault in it has to become a Failed
    // outcome: escaping as an exception would report a delivered dictation as failed.
    private ClipboardRestoreOutcome RestoreClipboard(ClipboardLease lease)
    {
        try
        {
            return _clipboard.Restore(lease);
        }
        catch (Exception ex)
        {
            // The type only, never the message: Scribe's native layer does not put clipboard content in
            // exceptions, but nothing guarantees that for every exception a restore could surface. The
            // logger cannot throw here (NonThrowingLogger), so this catch cannot be escaped either.
            _logger.LogWarning("Restoring the clipboard failed with {ExceptionType}.", ex.GetType().Name);
            return ClipboardRestoreOutcome.Failed;
        }
    }

    // Runs after every delivered paste, so it must not throw (see TryStop).
    private static void ReportClipboard(Activity? activity, ClipboardPasteReport paste)
    {
        if (activity is null)
        {
            return;
        }

        try
        {
            // Enum names only, never content.
            activity.SetTag(ScribeTelemetry.TagPasteDelivery, paste.Delivery.ToString());
            activity.SetTag(ScribeTelemetry.TagClipboardRestore, paste.Restore.ToString());
        }
        catch (Exception)
        {
            // Telemetry is optional; the injection outcome is not.
        }
    }

    // One line per paste attempt, enum names, counts and booleans only. The clipboard can hold anything
    // the user copied, so no content, length or format name of it may reach the log. The last two values
    // make native behavior this code could not measure visible from field logs: whether releasing the
    // clipboard moves the sequence number, and how often the number has moved by Ctrl+V so that Confirm
    // has to open the clipboard. A receipt the NULL-owner session could not write shows as its own
    // delivery, ReceiptFailed.
    private void LogClipboard(ClipboardPasteReport paste)
    {
        bool typingInstead = paste.Delivery is not (PasteDelivery.ChordInserted or PasteDelivery.FocusChanged);
        _logger.Log(
            ClipboardLogLevel(paste.Delivery, paste.Restore),
            "Clipboard paste {Delivery}; previous clipboard {Restore}; {OpenAttempts} open attempt(s); " +
            "typing instead: {TypingInstead}; close moved sequence: {CloseMovedSequence}; " +
            "confirm opened clipboard: {ConfirmOpened}.",
            paste.Delivery, paste.Restore, paste.OpenAttempts, typingInstead, paste.CloseMovedSequence,
            paste.ConfirmOpened);
    }

    internal static LogLevel ClipboardLogLevel(PasteDelivery delivery, ClipboardRestoreOutcome restore)
    {
        if (restore == ClipboardRestoreOutcome.Failed)
        {
            return LogLevel.Warning;
        }

        return delivery switch
        {
            PasteDelivery.ChordInserted when restore == ClipboardRestoreOutcome.Restored => LogLevel.Debug,

            // Expected outcomes rather than faults: another application copied, the user switched
            // windows, or the clipboard held content that typing preserves.
            PasteDelivery.ChordInserted or PasteDelivery.Superseded or PasteDelivery.FocusChanged
                or PasteDelivery.NonTextContent => LogLevel.Information,
            _ => LogLevel.Warning,
        };
    }

    // Best-effort key-up pair after a partial Ctrl+V send. Sending an up for a key that was never
    // down is harmless; leaving Ctrl held down is not.
    private void ReleaseCtrlV()
    {
        var inputs = BuildCtrlVReleaseInputs();
        var sent = SendWithRetry(inputs);
        if (sent != inputs.Length)
        {
            _logger.LogWarning("Releasing a partial Ctrl+V delivered {Sent}/{Total} key-up events.", sent, inputs.Length);
        }
    }

    internal static INPUT[] BuildCtrlVReleaseInputs() =>
        [KeyUp(VK_CONTROL), KeyUp(VK_V)];

    private (int Sent, int Total) SendCtrlV()
    {
        INPUT[] inputs =
        [
            KeyDown(VK_CONTROL),
            KeyDown(VK_V),
            KeyUp(VK_V),
            KeyUp(VK_CONTROL),
        ];

        uint sent = _platform.SendInput(inputs);
        if (sent != inputs.Length)
        {
            _logger.LogWarning("SendInput delivered {Sent}/{Total} Ctrl+V events.", sent, inputs.Length);
        }

        return ((int)sent, inputs.Length);
    }

    private (int Sent, int Total) TypeUnicode(string text, nint expectedForegroundWindow, bool shiftEnter)
    {
        int total = CountKeyEvents(text, 0, text.Length, shiftEnter);
        if (total == 0)
        {
            return (0, 0);
        }

        // Windows silently drops synthetic keystrokes when a single SendInput batch is larger than the
        // focused app's input queue can drain; this is why a short dictation types fine but a long
        // one appears partially or not at all. Type in small chunks with a brief settle between them,
        // and resend the unsent remainder of a chunk before giving up. The values favour reliability
        // over raw speed: a few hundred milliseconds on a rare long paragraph is imperceptible next to
        // dropped text.
        int sent = 0;
        try
        {
            for (int start = 0; start < text.Length;)
            {
                if (!IsExpectedForeground(expectedForegroundWindow))
                {
                    break;
                }

                int count = ChunkLength(text, start, UnicodeChunkChars);
                var inputs = BuildUnicodeChunk(text, start, count, shiftEnter);

                int delivered = SendWithRetry(inputs);
                sent += delivered;

                // The target stopped accepting input mid-stream even after retries; stop and let the caller
                // report the partial send (the text.inject span is marked errored when sent != total).
                if (delivered < inputs.Length)
                {
                    // A truncated chunk can leave Shift logically down (the chord's release is its last
                    // event), which would turn anything the user types next into a shortcut. Same reason
                    // ReleaseCtrlV exists; a key-up for a key that was never down is harmless.
                    if (shiftEnter)
                    {
                        ReleaseShift();
                    }

                    break;
                }

                start += count;

                // Let the focused app process this batch's WM_CHAR messages before the next one arrives.
                if (start < text.Length)
                {
                    _platform.Sleep(InterChunkSettleMs);
                }
            }
        }
        catch when (ReleaseShiftOnFault(shiftEnter))
        {
            // Unreachable: the filter always returns false so the exception keeps propagating. The
            // filter exists purely to run the Shift key-up first. Without it, a throw between the
            // chord's key-down and key-up (SendWithRetry, or an allocation failure building a chunk)
            // would leave Shift latched down for the user's next real keystroke, turning it into a
            // shortcut. A filter is used rather than catch/rethrow so the original stack is untouched.
            throw;
        }

        if (sent != total)
        {
            _logger.LogWarning("Unicode typing delivered {Sent}/{Total} events; text may be truncated.", sent, total);
        }

        return (sent, total);
    }

    // Best-effort key-up after a partial chunk that may have ended mid-chord.
    private void ReleaseShift()
    {
        INPUT[] inputs = [KeyUp(VK_SHIFT)];
        if (SendWithRetry(inputs) != inputs.Length)
        {
            _logger.LogWarning("Releasing Shift after a partial Unicode chunk did not deliver.");
        }
    }

    /// <summary>
    /// Exception-filter helper: releases Shift, then returns false so the exception keeps unwinding.
    /// Swallowing a failure here is deliberate. This runs while another exception is already in
    /// flight, and throwing from a filter would replace the real fault with a misleading one.
    /// </summary>
    private bool ReleaseShiftOnFault(bool shiftEnter)
    {
        if (shiftEnter)
        {
            try
            {
                ReleaseShift();
            }
            catch
            {
                // Nothing useful to do; the original exception is the one worth reporting.
            }
        }

        return false;
    }

    // Two INPUT events (down/up) per UTF-16 code unit. Surrogate pairs are handled naturally because
    // each surrogate half is sent as its own KEYEVENTF_UNICODE event. Line breaks are the exception:
    // see BuildLineBreak.
    internal static INPUT[] BuildUnicodeChunk(string text, int start, int count, bool shiftEnter = true)
    {
        var inputs = new List<INPUT>(count * 2);
        int end = start + count;
        for (int i = start; i < end; i++)
        {
            char ch = text[i];
            if (ch is '\r' or '\n')
            {
                inputs.AddRange(BuildLineBreak(shiftEnter));

                // CRLF is one line break, not two. ChunkLength guarantees the pair is never split
                // across batches, so the lookahead never runs past the end of this chunk.
                if (ch == '\r' && i + 1 < end && text[i + 1] == '\n')
                {
                    i++;
                }

                continue;
            }

            inputs.Add(UnicodeKey(ch, keyUp: false));
            inputs.Add(UnicodeKey(ch, keyUp: true));
        }

        return [.. inputs];
    }

    // A line break has to be a real Return keypress. Sent as KEYEVENTF_UNICODE the bare LF control
    // character is discarded by most edit controls, so the two lines ran together with no separator
    // at all ("first line.second line").
    //
    // It is shifted by default because chat apps (Teams, Slack, Discord, and the web clients of all
    // three) bind a bare Enter to "send". AI cleanup is what introduces paragraph breaks in the first
    // place; raw ASR output has none, so a polished two-paragraph dictation typed into a chat box
    // fired the message on the first break and typed the rest into an empty composer. Shift+Enter is
    // the soft-newline chord in every one of those apps, and in a plain edit control, textarea or
    // RichEdit it is indistinguishable from Enter, so the shifted form is safe or strictly better
    // nearly everywhere. Word treats it as a line break rather than a paragraph mark, which is the
    // one visible difference and still renders as the new line the speaker asked for.
    internal static IEnumerable<INPUT> BuildLineBreak(bool shiftEnter)
    {
        if (!shiftEnter)
        {
            return [KeyDown(VK_RETURN), KeyUp(VK_RETURN)];
        }

        // Physical chord order: hold shift, tap Return, release shift. Unicode events are unaffected
        // by modifier state, and shift is released before the next character, so nothing leaks.
        return [KeyDown(VK_SHIFT), KeyDown(VK_RETURN), KeyUp(VK_RETURN), KeyUp(VK_SHIFT)];
    }

    // Keeps a CRLF pair inside a single batch: split across two SendInput calls the CR and the LF
    // would each become their own Return and type an extra blank line.
    //
    // It also prefers to end a batch on a word boundary. Each batch is a separate SendInput followed
    // by a settle, so the target repaints between them and the user watches the text arrive in
    // pieces. Cutting at a fixed character count tore words in half mid-render ("consider" landing as
    // "consi" then "der"); backing up to the last space in the batch means whole words appear at a
    // time, which reads as typing rather than glitching. The backoff never gives up more than half a
    // batch, so a long unbroken token (a URL, a file path) still makes steady progress instead of
    // degrading toward one character per send.
    internal static int ChunkLength(string text, int start, int max)
    {
        int count = Math.Min(max, text.Length - start);
        int end = start + count;

        // Never split a CRLF pair.
        if (count > 1 && end < text.Length && text[end - 1] == '\r' && text[end] == '\n')
        {
            return count - 1;
        }

        // The batch already ends the text, or ends exactly at a break: nothing to adjust.
        if (end >= text.Length || IsBreakBetween(text[end - 1], text[end]))
        {
            return count;
        }

        int floor = start + (count / 2);
        for (int i = end - 1; i > floor; i--)
        {
            if (IsBreakBetween(text[i - 1], text[i]))
            {
                return i - start;
            }
        }

        return count;
    }

    // A batch may end after whitespace or a line break, so the next batch starts a fresh word.
    private static bool IsBreakBetween(char last, char next) =>
        char.IsWhiteSpace(last) || last is '\r' or '\n' || next is '\r' or '\n';

    // The number of INPUT events BuildUnicodeChunk will produce for a span, so a completed send is
    // never misreported as truncated now that a CRLF collapses to a single keypress and a shifted
    // line break costs four events instead of two.
    internal static int CountKeyEvents(string text, int start, int count, bool shiftEnter = true)
    {
        int lineBreakEvents = shiftEnter ? 4 : 2;
        int events = 0;
        int end = start + count;
        for (int i = start; i < end; i++)
        {
            if (text[i] is '\r' or '\n')
            {
                events += lineBreakEvents;
                if (text[i] == '\r' && i + 1 < end && text[i + 1] == '\n')
                {
                    i++;
                }

                continue;
            }

            events += 2;
        }

        return events;
    }

    // Sends a batch, resending only the unsent remainder when SendInput reports a short count (the
    // input stream was momentarily blocked by other input). Returns how many events were delivered.
    private int SendWithRetry(INPUT[] inputs)
    {
        int offset = 0;
        int attempts = 0;
        while (offset < inputs.Length)
        {
            var slice = offset == 0 ? inputs : inputs[offset..];
            uint sent = _platform.SendInput(slice);
            offset += (int)sent;

            if (sent == (uint)slice.Length)
            {
                break;
            }

            if (++attempts > MaxChunkRetries)
            {
                _logger.LogWarning("SendInput stalled at {Offset}/{Total} events after {Attempts} retries.",
                    offset, inputs.Length, attempts);
                break;
            }

            _platform.Sleep(ChunkRetryDelayMs);
        }

        return offset;
    }

    private static INPUT KeyDown(ushort virtualKey) => KeyboardInput(virtualKey, 0, 0);

    private static INPUT KeyUp(ushort virtualKey) => KeyboardInput(virtualKey, 0, KEYEVENTF_KEYUP);

    private static INPUT UnicodeKey(char ch, bool keyUp)
    {
        uint flags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0);
        return KeyboardInput(0, ch, flags);
    }

    private static INPUT KeyboardInput(ushort virtualKey, ushort scanCode, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = scanCode,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = SyntheticInputMarker.Value,
            },
        },
    };

    private static T RunOnStaThread<T>(Func<T> action)
    {
        Exception? captured = null;
        T? result = default;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        })
        {
            Name = "Scribe.TextInjection",
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw new InvalidOperationException("Text injection failed.", captured);
        }

        return result!;
    }

    /// <summary>
    /// Wraps the injector's logger so that no log call can throw. Most of them run after text may
    /// already have reached the target (a partial Ctrl+V, the restore, the per-paste line), where an
    /// escaping exception would report a delivered dictation as failed. Wrapping the sink once keeps
    /// that true for every call site, including ones added later.
    /// </summary>
    private sealed class NonThrowingLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            try
            {
                return inner.BeginScope(state);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            try
            {
                return inner.IsEnabled(logLevel);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try
            {
                inner.Log(logLevel, eventId, state, exception, formatter);
            }
            catch (Exception)
            {
                // Diagnostics must never turn a delivered paste into a failed one.
            }
        }
    }
}
