using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.TextInjection;

/// <summary>
/// Default <see cref="ITextInjector"/>. Clipboard-paste runs the whole save → set → Ctrl+V →
/// restore sequence on a dedicated STA thread, with small delays so the target app reads the
/// clipboard before it is restored. Falls back to Unicode keystroke typing when the clipboard
/// cannot be acquired, and exposes typing directly via <see cref="InjectionMethod.UnicodeType"/>.
/// </summary>
public sealed class TextInjector : ITextInjector
{
    // Let the target consume the paste before we restore the prior clipboard text.
    private const int PasteSettleDelayMs = 130;

    // Brief pause so the clipboard is committed before Ctrl+V is delivered.
    private const int ClipboardSettleDelayMs = 30;

    // Unicode typing is sent in small batches so a long dictation can't overrun the target's input
    // queue (which silently drops keystrokes). Each batch is UnicodeChunkChars code units; we pause
    // InterChunkSettleMs between batches and resend a short SendInput remainder up to MaxChunkRetries.
    private const int UnicodeChunkChars = 50;
    private const int InterChunkSettleMs = 5;
    private const int ChunkRetryDelayMs = 12;
    private const int MaxChunkRetries = 5;

    private readonly ILogger<TextInjector> _logger;

    public TextInjector(ILogger<TextInjector> logger) => _logger = logger;

    public InjectionResult Inject(
        string text,
        InjectionMethod method = InjectionMethod.ClipboardPaste,
        nint expectedForegroundWindow = 0)
    {
        if (string.IsNullOrEmpty(text))
        {
            return InjectionResult.Empty;
        }

        if (expectedForegroundWindow != 0 && GetForegroundWindow() != expectedForegroundWindow)
        {
            return new InjectionResult(false, "none", 0, text.Length, "The focused window changed while processing.");
        }

        // Capture the ambient dictation span on the calling thread; the STA worker is a fresh
        // Thread, so Activity.Current would not flow to it. We re-parent the child span explicitly.
        var parent = Activity.Current;

        return RunOnStaThread(() =>
        {
            using var activity = ScribeTelemetry.Source.StartActivity(
                ScribeTelemetry.InjectActivity, ActivityKind.Internal, parent?.Context ?? default);
            activity?.SetTag(ScribeTelemetry.TagInjectChars, text.Length);

            if (!IsExpectedForeground(expectedForegroundWindow))
            {
                return FocusChanged(text.Length);
            }

            if (TryInsertIntoStandardEdit(text, expectedForegroundWindow))
            {
                ReportInjection(activity, "win32-edit", text.Length, text.Length, fallback: false);
                return new InjectionResult(true, "win32-edit", text.Length, text.Length);
            }

            if (method == InjectionMethod.UnicodeType)
            {
                var typed = TypeUnicode(text, expectedForegroundWindow);
                ReportInjection(activity, "unicode", typed.Sent, typed.Total, fallback: false);
                return new InjectionResult(
                    typed.Sent == typed.Total, "unicode", typed.Sent, typed.Total,
                    typed.Sent == typed.Total ? null : "Only part of the text was accepted by Windows.");
            }

            if (PasteViaClipboard(text, expectedForegroundWindow, out var ctrl, out var focusChanged))
            {
                ReportInjection(activity, "clipboard", ctrl.Sent, ctrl.Total, fallback: false);
                return new InjectionResult(
                    true, "clipboard", ctrl.Sent, ctrl.Total,
                    ctrl.Sent == ctrl.Total ? null : "Paste completed, but modifier cleanup was partial.");
            }

            if (focusChanged)
            {
                return FocusChanged(text.Length);
            }

            _logger.LogWarning("Clipboard paste failed; falling back to Unicode typing.");
            var fallback = TypeUnicode(text, expectedForegroundWindow);
            ReportInjection(activity, "unicode", fallback.Sent, fallback.Total, fallback: true);
            return new InjectionResult(
                fallback.Sent == fallback.Total, "unicode", fallback.Sent, fallback.Total,
                fallback.Sent == fallback.Total ? null : "Only part of the text was accepted by Windows.");
        });
    }

    private static InjectionResult FocusChanged(int total) =>
        new(false, "none", 0, total, "The focused window changed while processing.");

    private static bool IsExpectedForeground(nint expected) =>
        expected == 0 || GetForegroundWindow() == expected;

    private static void ReportInjection(Activity? activity, string method, int sent, int total, bool fallback)
    {
        if (activity is null)
        {
            return;
        }

        var complete = sent == total;
        activity.SetTag(ScribeTelemetry.TagInjectMethod, method);
        activity.SetTag(ScribeTelemetry.TagInjectSent, sent);
        activity.SetTag(ScribeTelemetry.TagInjectTotal, total);
        activity.SetTag(ScribeTelemetry.TagInjectComplete, complete);
        activity.SetTag(ScribeTelemetry.TagInjectFallback, fallback);

        // A partial SendInput is the smoking gun for "I spoke but nothing (or only part) appeared" —
        // mark the span as an error so it stands out in the log and any OTLP backend.
        if (!complete)
        {
            activity.SetStatus(ActivityStatusCode.Error, $"SendInput delivered {sent}/{total} events.");
        }
    }

    private bool PasteViaClipboard(
        string text,
        nint expectedForegroundWindow,
        out (int Sent, int Total) ctrlV,
        out bool focusChanged)
    {
        ctrlV = (0, 0);
        focusChanged = false;

        // An image, copied files or other non-text content can't be saved and restored by
        // Win32Clipboard (text-only by design), so pasting would silently destroy it. Fall back to
        // typing — slower, but the user's screenshot or file copy survives the dictation.
        if (Win32Clipboard.HasNonTextContent())
        {
            _logger.LogInformation("Clipboard holds non-text content; typing instead of pasting to preserve it.");
            return false;
        }

        var wasEmpty = Win32Clipboard.FormatCount == 0;
        string? previous = Win32Clipboard.TryGetText();
        if (!wasEmpty && previous is null)
        {
            return false;
        }

        if (!Win32Clipboard.SetText(text))
        {
            return false;
        }

        var injectedSequence = Win32Clipboard.SequenceNumber;

        Thread.Sleep(ClipboardSettleDelayMs);
        if (!IsExpectedForeground(expectedForegroundWindow))
        {
            focusChanged = true;
            if (Win32Clipboard.SequenceNumber == injectedSequence)
            {
                RestoreClipboard(wasEmpty, previous);
            }

            return false;
        }

        if (Win32Clipboard.SequenceNumber != injectedSequence)
        {
            return false;
        }

        ctrlV = SendCtrlV();

        // A short send can leave Ctrl (or V) logically held down; release both before anything
        // else so a typing fallback can't turn the dictation into accidental keyboard shortcuts.
        if (ctrlV.Sent < ctrlV.Total)
        {
            ReleaseCtrlV();
        }

        // The paste fires on the V-down (the chord's second event). Fewer than two delivered means
        // no paste happened at all: put the user's clipboard back and report failure so the caller
        // types the text instead of losing the dictation.
        if (ctrlV.Sent < 2)
        {
            if (Win32Clipboard.SequenceNumber == injectedSequence)
            {
                RestoreClipboard(wasEmpty, previous);
            }

            return false;
        }

        Thread.Sleep(PasteSettleDelayMs);

        if (Win32Clipboard.SequenceNumber == injectedSequence)
        {
            RestoreClipboard(wasEmpty, previous);
        }

        return true;
    }

    private static void RestoreClipboard(bool wasEmpty, string? previous)
    {
        if (wasEmpty)
        {
            Win32Clipboard.Clear();
        }
        else if (previous is not null)
        {
            Win32Clipboard.SetText(previous);
        }
    }

    private static bool TryInsertIntoStandardEdit(string text, nint expectedForegroundWindow)
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0 || (expectedForegroundWindow != 0 && foreground != expectedForegroundWindow))
        {
            return false;
        }

        var threadId = GetWindowThreadProcessId(foreground, out _);
        var info = new GUITHREADINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
        if (threadId == 0 || !GetGUIThreadInfo(threadId, ref info) || info.hwndFocus == 0)
        {
            return false;
        }

        var className = new char[128];
        var length = GetClassName(info.hwndFocus, className, className.Length);
        if (length == 0)
        {
            return false;
        }

        var controlClass = new string(className, 0, length);
        if (!string.Equals(controlClass, "Edit", StringComparison.OrdinalIgnoreCase) &&
            !controlClass.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return SendMessageTimeout(
            info.hwndFocus, EM_REPLACESEL, (nint)1, text, SMTO_ABORTIFHUNG, 1000, out _) != 0;
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

        uint sent = SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            _logger.LogWarning("SendInput delivered {Sent}/{Total} Ctrl+V events.", sent, inputs.Length);
        }

        return ((int)sent, inputs.Length);
    }

    private (int Sent, int Total) TypeUnicode(string text, nint expectedForegroundWindow)
    {
        int total = text.Length * 2;
        if (total == 0)
        {
            return (0, 0);
        }

        // Windows silently drops synthetic keystrokes when a single SendInput batch is larger than the
        // focused app's input queue can drain — which is why a short dictation types fine but a long
        // one appears partially or not at all. Type in small chunks with a brief settle between them,
        // and resend the unsent remainder of a chunk before giving up. The values favour reliability
        // over raw speed: a few hundred milliseconds on a rare long paragraph is imperceptible next to
        // dropped text.
        int sent = 0;
        for (int start = 0; start < text.Length; start += UnicodeChunkChars)
        {
            if (!IsExpectedForeground(expectedForegroundWindow))
            {
                break;
            }

            int count = Math.Min(UnicodeChunkChars, text.Length - start);
            var inputs = BuildUnicodeChunk(text, start, count);

            int delivered = SendWithRetry(inputs);
            sent += delivered;

            // The target stopped accepting input mid-stream even after retries; stop and let the caller
            // report the partial send (the text.inject span is marked errored when sent != total).
            if (delivered < inputs.Length)
            {
                break;
            }

            // Let the focused app process this batch's WM_CHAR messages before the next one arrives.
            if (start + count < text.Length)
            {
                Thread.Sleep(InterChunkSettleMs);
            }
        }

        if (sent != total)
        {
            _logger.LogWarning("Unicode typing delivered {Sent}/{Total} events; text may be truncated.", sent, total);
        }

        return (sent, total);
    }

    // Two INPUT events (down/up) per UTF-16 code unit. Surrogate pairs are handled naturally because
    // each surrogate half is sent as its own KEYEVENTF_UNICODE event.
    private static INPUT[] BuildUnicodeChunk(string text, int start, int count)
    {
        var inputs = new INPUT[count * 2];
        int index = 0;
        for (int i = start; i < start + count; i++)
        {
            inputs[index++] = UnicodeKey(text[i], keyUp: false);
            inputs[index++] = UnicodeKey(text[i], keyUp: true);
        }

        return inputs;
    }

    // Sends a batch, resending only the unsent remainder when SendInput reports a short count (the
    // input stream was momentarily blocked by other input). Returns how many events were delivered.
    private int SendWithRetry(INPUT[] inputs)
    {
        int size = System.Runtime.InteropServices.Marshal.SizeOf<INPUT>();
        int offset = 0;
        int attempts = 0;
        while (offset < inputs.Length)
        {
            var slice = offset == 0 ? inputs : inputs[offset..];
            uint sent = SendInput((uint)slice.Length, slice, size);
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

            Thread.Sleep(ChunkRetryDelayMs);
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
}
