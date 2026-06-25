using Microsoft.Extensions.Logging;
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

    private readonly ILogger<TextInjector> _logger;

    public TextInjector(ILogger<TextInjector> logger) => _logger = logger;

    public void Inject(string text, InjectionMethod method = InjectionMethod.ClipboardPaste)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        RunOnStaThread(() =>
        {
            if (method == InjectionMethod.UnicodeType)
            {
                TypeUnicode(text);
                return;
            }

            if (!PasteViaClipboard(text))
            {
                _logger.LogWarning("Clipboard paste failed; falling back to Unicode typing.");
                TypeUnicode(text);
            }
        });
    }

    private bool PasteViaClipboard(string text)
    {
        string? previous = Win32Clipboard.TryGetText();

        if (!Win32Clipboard.SetText(text))
        {
            return false;
        }

        Thread.Sleep(ClipboardSettleDelayMs);
        SendCtrlV();
        Thread.Sleep(PasteSettleDelayMs);

        if (previous is not null)
        {
            Win32Clipboard.SetText(previous);
        }

        return true;
    }

    private void SendCtrlV()
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
    }

    private void TypeUnicode(string text)
    {
        // Two INPUT events (down/up) per UTF-16 code unit. Surrogate pairs are handled naturally
        // because each surrogate half is sent as its own KEYEVENTF_UNICODE event.
        var inputs = new INPUT[text.Length * 2];
        int index = 0;
        foreach (char ch in text)
        {
            inputs[index++] = UnicodeKey(ch, keyUp: false);
            inputs[index++] = UnicodeKey(ch, keyUp: true);
        }

        if (inputs.Length == 0)
        {
            return;
        }

        uint sent = SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            _logger.LogWarning("SendInput delivered {Sent}/{Total} Unicode events.", sent, inputs.Length);
        }
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
