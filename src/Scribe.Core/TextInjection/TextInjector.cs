using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;
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

    public void Inject(string text, InjectionMethod method = InjectionMethod.ClipboardPaste)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Capture the ambient dictation span on the calling thread; the STA worker is a fresh
        // Thread, so Activity.Current would not flow to it. We re-parent the child span explicitly.
        var parent = Activity.Current;

        RunOnStaThread(() =>
        {
            using var activity = ScribeTelemetry.Source.StartActivity(
                ScribeTelemetry.InjectActivity, ActivityKind.Internal, parent?.Context ?? default);
            activity?.SetTag(ScribeTelemetry.TagInjectChars, text.Length);

            if (method == InjectionMethod.UnicodeType)
            {
                var typed = TypeUnicode(text);
                ReportInjection(activity, "unicode", typed.Sent, typed.Total, fallback: false);
                return;
            }

            if (PasteViaClipboard(text, out var ctrl))
            {
                ReportInjection(activity, "clipboard", ctrl.Sent, ctrl.Total, fallback: false);
            }
            else
            {
                _logger.LogWarning("Clipboard paste failed; falling back to Unicode typing.");
                var typed = TypeUnicode(text);
                ReportInjection(activity, "unicode", typed.Sent, typed.Total, fallback: true);
            }
        });
    }

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

    private bool PasteViaClipboard(string text, out (int Sent, int Total) ctrlV)
    {
        ctrlV = (0, 0);
        string? previous = Win32Clipboard.TryGetText();

        if (!Win32Clipboard.SetText(text))
        {
            return false;
        }

        Thread.Sleep(ClipboardSettleDelayMs);
        ctrlV = SendCtrlV();
        Thread.Sleep(PasteSettleDelayMs);

        if (previous is not null)
        {
            Win32Clipboard.SetText(previous);
        }

        return true;
    }

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

    private (int Sent, int Total) TypeUnicode(string text)
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
                dwExtraInfo = 0,
            },
        },
    };

    private static void RunOnStaThread(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
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
    }
}
