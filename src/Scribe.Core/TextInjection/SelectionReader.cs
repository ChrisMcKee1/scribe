using Microsoft.Extensions.Logging;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.TextInjection;

/// <summary>Why a selection could not be read.</summary>
public enum SelectionFailure
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>There is no foreground window to read from.</summary>
    NoTarget,

    /// <summary>
    /// The focused app is a console host, where a synthesized Ctrl+C is an interrupt signal rather
    /// than a copy and would kill whatever the user is running.
    /// </summary>
    UnsafeTarget,

    /// <summary>
    /// The clipboard holds something that cannot be saved and restored as text (an image, files, a
    /// spreadsheet range), so copying over it would destroy content Scribe cannot put back.
    /// </summary>
    ClipboardNotRestorable,

    /// <summary>The copy produced nothing, which almost always means nothing was selected.</summary>
    NothingSelected,

    /// <summary>
    /// The focused control is a password field. Refused before reading: a password mask reads as
    /// ordinary text, so without this Scribe would ship a credential to a model and then overwrite
    /// the field with the result.
    /// </summary>
    PasswordField,
}

/// <summary>What a selection read produced.</summary>
/// <param name="Text">The selected text on success, otherwise null.</param>
/// <param name="Failure">Why it failed, or <see cref="SelectionFailure.None"/>.</param>
/// <param name="Detail">A sentence for the palette to show. Empty on success.</param>
/// <param name="TargetWindow">The window the selection came from, for the write-back focus check.</param>
/// <param name="TargetProcess">Process name (no .exe) of the target, for profile matching.</param>
/// <param name="CanWriteBack">
/// False when the surface was read but cannot accept a programmatic replacement, so the palette must
/// offer the result rather than promise to replace the selection. Resolved before the model runs.
/// </param>
public sealed record SelectionCapture(
    string? Text,
    SelectionFailure Failure,
    string Detail,
    nint TargetWindow = 0,
    string? TargetProcess = null,
    bool CanWriteBack = true)
{
    /// <summary>True when <see cref="Text"/> holds a usable selection.</summary>
    public bool Succeeded => Failure == SelectionFailure.None && !string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Reads the text currently selected in the foreground application by synthesizing Ctrl+C and
/// reading the clipboard, then putting the user's previous clipboard back.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors <see cref="TextInjector"/>'s clipboard paste path in reverse and shares its main
/// constraint: everything runs on a dedicated STA thread, because <see cref="Win32Clipboard"/>
/// requires a thread that owns a message queue.
/// </para>
/// <para>
/// <b>Proving the copy landed.</b> The clipboard is emptied immediately before the synthesized
/// Ctrl+C, so any text that appears afterwards is by definition what the target just wrote. Two
/// weaker tests were tried first and both failed in production.
/// <c>GetClipboardSequenceNumber</c> is machine-wide and any process can bump it, so a change proves
/// nothing on its own. Adding "and the payload differs from what was there before" then broke the
/// most ordinary cases of all: a user who had already copied that text themselves, or who ran a
/// second action over text Scribe had just pasted, got told that something else had changed their
/// clipboard when in fact the copy had worked perfectly.
/// </para>
/// <para>
/// A console host is refused outright, because Ctrl+C there is an interrupt signal rather than a
/// copy and would stop whatever the user is running.
/// </para>
/// </remarks>
public sealed class SelectionReader
{
    // Give the target time to service the synthesized keystroke and publish the clipboard. Polled
    // rather than slept flat out so a fast app returns quickly.
    private const int CopyPollIntervalMs = 15;
    private const int CopyTimeoutMs = 450;
    private const int RestoreSettleDelayMs = 60;


    private const ushort VK_C = 0x43;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    private readonly ILogger<SelectionReader> _logger;
    private readonly ISelectionProbe? _probe;

    /// <param name="probe">
    /// Optional non-destructive reader, tried before the clipboard. Null falls straight to the
    /// clipboard, which is what the unit tests use.
    /// </param>
    public SelectionReader(ILogger<SelectionReader> logger, ISelectionProbe? probe = null)
    {
        _logger = logger;
        _probe = probe;
    }

    /// <summary>
    /// Captures the current selection. Never throws. On any failure the user's clipboard is left as
    /// it was found.
    /// </summary>
    /// <param name="preferredTarget">
    /// The window to read from. Pass the last non-Scribe foreground window when the palette was
    /// reached by a route that stole focus, such as the tray menu: by then
    /// <c>GetForegroundWindow</c> returns Scribe, and copying from Scribe's own menu yields nothing.
    /// Zero means "use whatever is in front", which is correct for the hotkey path, since
    /// <c>RegisterHotKey</c> delivers to a message-only window without changing activation.
    /// </param>
    public SelectionCapture Capture(nint preferredTarget = 0)
    {
        var target = preferredTarget != 0 ? preferredTarget : GetForegroundWindow();
        if (target == 0)
        {
            return new SelectionCapture(null, SelectionFailure.NoTarget, "Scribe could not tell which app is in front.");
        }

        var process = ProcessNameForWindow(target);

        // Rung 1: read without touching anything, and do it BEFORE any activation work.
        //
        // UI Automation reads a selection cross-process without the target being foreground, so on
        // the path that succeeds there is no SetForegroundWindow, no readiness wait, no focus bounce
        // and no clipboard write at all. Only the clipboard fallback below needs the target in front,
        // because only a synthesized keystroke does.
        //
        // It runs for every target INCLUDING terminals: the terminal refusal further down exists
        // because a synthesized Ctrl+C there is an interrupt signal, which is a clipboard-path
        // hazard that does not apply to a passive read.
        if (_probe is { } probe)
        {
            var result = probe.TryRead(target);
            switch (result.Outcome)
            {
                case SelectionProbeOutcome.Success:
                case SelectionProbeOutcome.Disjoint:
                    _logger.LogDebug(
                        "Selection read via UI Automation ({Outcome}, writable={Writable}).",
                        result.Outcome,
                        result.CanWriteBack);
                    return new SelectionCapture(
                        result.Text, SelectionFailure.None, string.Empty, target, process, result.CanWriteBack);

                case SelectionProbeOutcome.PasswordField:
                    return new SelectionCapture(
                        null, SelectionFailure.PasswordField, result.Detail ?? string.Empty, target, process);

                case SelectionProbeOutcome.NothingSelected:
                    // Authoritative. Borrowing the clipboard would reach the same conclusion more
                    // slowly while writing to it for nothing.
                    return new SelectionCapture(
                        null, SelectionFailure.NothingSelected, result.Detail ?? string.Empty, target, process);
            }
        }

        if (InjectionTextFormatter.IsTerminalProcess(process))
        {
            return new SelectionCapture(
                null,
                SelectionFailure.UnsafeTarget,
                "In a terminal, Scribe can only read text you selected with the mouse. Ctrl+C would stop the running command.",
                target,
                process);
        }

        // Rung 2 needs a real keystroke, so now the target has to actually BE foreground and have
        // restored focus to a control. Issued unconditionally rather than only when the window looks
        // like it is not foreground: "already foreground" can mean "mid-restoration", and a Ctrl+C
        // delivered in that gap copies nothing, which surfaced as a spurious "select some text first".
        _ = SetForegroundWindow(target);

        if (!ForegroundReadiness.WaitForInputReady(target))
        {
            return new SelectionCapture(
                null,
                SelectionFailure.NoTarget,
                "Scribe could not switch back to the app your text is in. Try the hotkey instead.",
                target,
                process);
        }

        try
        {
            return RunOnStaThread(() => CaptureCore(target, process));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Selection capture failed.");
            return new SelectionCapture(
                null, SelectionFailure.NothingSelected, "Scribe could not read the selected text.", target, process);
        }
    }

    private SelectionCapture CaptureCore(nint target, string? process)
    {
        // Only refuse when the clipboard genuinely cannot be restored, meaning an image, copied
        // files, or a spreadsheet range with no text form. Ordinary text, including rich text
        // carrying HTML and RTF companions, round-trips fine and must not block the feature: people
        // have something copied almost all of the time.
        if (!Win32Clipboard.CanBorrow())
        {
            return new SelectionCapture(
                null,
                SelectionFailure.ClipboardNotRestorable,
                "Your clipboard holds an image or files, which Scribe cannot put back afterwards. Paste or clear it first.",
                target,
                process);
        }

        var previous = Win32Clipboard.TryGetText();

        // Empty the clipboard BEFORE the copy, so anything text-shaped that appears afterwards is by
        // definition what the target just put there.
        //
        // This replaces a payload comparison against the pre-copy snapshot, which was wrong in a way
        // that failed constantly in the most ordinary situations: the user had already copied that
        // same text themselves, or Scribe's own paste had left it on the clipboard and they ran a
        // second action over the result. In both cases the copy worked perfectly and Scribe reported
        // "something else changed the clipboard". Emptying first removes the comparison entirely, so
        // there is nothing left to get wrong. GetClipboardSequenceNumber is machine-wide and any
        // process can bump it, so it was never sufficient proof on its own either.
        _ = Win32Clipboard.Clear();

        if (SendCtrlC() == 0)
        {
            RestoreClipboard(previous);
            return new SelectionCapture(
                null, SelectionFailure.NothingSelected, "Windows refused the copy. Try again.", target, process);
        }

        var captured = WaitForClipboardText();

        if (string.IsNullOrEmpty(captured))
        {
            RestoreClipboard(previous);
            return new SelectionCapture(
                null,
                SelectionFailure.NothingSelected,
                "Scribe could not read a selection. Highlight some text, then try again.",
                target,
                process);
        }

        RestoreClipboard(previous);
        return new SelectionCapture(captured, SelectionFailure.None, string.Empty, target, process);
    }

    /// <summary>
    /// Polls for text appearing on the freshly emptied clipboard. Any non-empty text is the copy,
    /// because nothing was there to begin with.
    /// </summary>
    private static string? WaitForClipboardText()
    {
        // A real elapsed-time deadline, not an iteration count. The previous loop added a fixed
        // 15 ms per pass while each pass could burn up to 90 ms inside Win32Clipboard's open retry
        // (6 attempts at 15 ms), so a nominal 450 ms budget could run past three seconds whenever
        // another process was contending for the clipboard, which is exactly when it is slow.
        var deadline = Environment.TickCount64 + CopyTimeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(CopyPollIntervalMs);

            var text = Win32Clipboard.TryGetText();
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>Puts the user's clipboard back the way it was found.</summary>
    /// <remarks>
    /// Unconditional, unlike the previous version which skipped the restore whenever the sequence
    /// number had moved. That guard was meant to avoid clobbering a clipboard manager's write, but
    /// the sequence number ALWAYS moves here: Scribe empties the clipboard and the target then
    /// writes to it, which is two bumps of Scribe's own making. So the guard fired on every capture
    /// and the restore never ran, leaving the user holding their selection instead of what they had
    /// copied. That is the "Clipboard changed during capture" line in the log. Restoring
    /// unconditionally is the lesser risk by a wide margin: the exposure window is a few hundred
    /// milliseconds, against losing the clipboard on every single invocation.
    /// </remarks>
    private void RestoreClipboard(string? previous)
    {
        Thread.Sleep(RestoreSettleDelayMs);

        try
        {
            if (string.IsNullOrEmpty(previous))
            {
                _ = Win32Clipboard.Clear();
                return;
            }

            // Retry once and warn rather than swallow. SetText calls EmptyClipboard FIRST and can
            // then fail on the allocation, so a silent failure here does not leave the clipboard
            // unchanged, it leaves it EMPTY. Losing what the user had copied deserves a log line
            // somebody will actually see.
            if (!Win32Clipboard.SetText(previous) && !Win32Clipboard.SetText(previous))
            {
                _logger.LogWarning(
                    "Could not restore the previous clipboard text; the clipboard may now be empty.");
            }
        }
        catch (Exception ex)
        {
            // Losing the previous clipboard is bad, but it must never fail the capture the user asked for.
            _logger.LogDebug(ex, "Restoring the clipboard threw.");
        }
    }

    /// <summary>
    /// Modifiers that must be released before a synthesized Ctrl+C, and restored afterwards.
    /// </summary>
    /// <remarks>
    /// Left and right variants are tested separately because the hook and the target both
    /// distinguish them, and releasing the wrong side leaves the chord half-held.
    /// </remarks>
    private static readonly ushort[] ModifierKeys =
    [
        0xA2, // VK_LCONTROL
        0xA3, // VK_RCONTROL
        0xA0, // VK_LSHIFT
        0xA1, // VK_RSHIFT
        0xA4, // VK_LMENU  (left Alt)
        0xA5, // VK_RMENU  (right Alt)
        0x5B, // VK_LWIN
        0x5C, // VK_RWIN
    ];

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    /// <summary>
    /// Synthesizes Ctrl+C, first releasing any modifier the user is physically still holding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The release step is not defensive tidiness, it is what makes the copy work at all. The palette
    /// is opened with a chord such as Ctrl+Alt+Space, and RegisterHotKey fires on key DOWN while the
    /// user is still holding Ctrl and Alt. Sending a bare Ctrl+C into that state delivers
    /// Ctrl+Alt+C to the target, which is not copy in most applications, so nothing reaches the
    /// clipboard and the capture reports "nothing selected" on a selection that was there all along.
    /// </para>
    /// <para>
    /// The held modifiers are re-pressed afterwards so the user's physical key state and the OS agree
    /// again; leaving them released would make the eventual real key-up look like a stray event. A
    /// final dummy key-up is appended because releasing the Windows key on its own opens the Start
    /// menu, and an intervening unrelated key stops the OS treating it as a solo Win press.
    /// </para>
    /// </remarks>
    private static int SendCtrlC()
    {
        var held = new List<ushort>();
        foreach (var key in ModifierKeys)
        {
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
            {
                held.Add(key);
            }
        }

        var inputs = new List<INPUT>(held.Count * 2 + 5);

        foreach (var key in held)
        {
            inputs.Add(KeyboardInput(key, keyUp: true));
        }

        inputs.Add(KeyboardInput(VK_CONTROL, keyUp: false));
        inputs.Add(KeyboardInput(VK_C, keyUp: false));
        inputs.Add(KeyboardInput(VK_C, keyUp: true));
        inputs.Add(KeyboardInput(VK_CONTROL, keyUp: true));

        foreach (var key in held)
        {
            inputs.Add(KeyboardInput(key, keyUp: false));
        }

        if (held.Contains((ushort)0x5B) || held.Contains((ushort)0x5C))
        {
            // 0xFF is a reserved virtual key that no application maps, so this is inert except for
            // breaking the "Windows key pressed and released with nothing in between" pattern.
            inputs.Add(KeyboardInput(0xFF, keyUp: true));
        }

        var array = inputs.ToArray();
        return (int)SendInput(
            (uint)array.Length, array, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyboardInput(ushort virtualKey, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = 0,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                time = 0,
                // Tagged like every other key Scribe synthesizes, so the push-to-talk hook ignores it
                // rather than treating our own Ctrl as a user keypress.
                dwExtraInfo = Hotkeys.SyntheticInputMarker.Value,
            },
        },
    };

    private static string? ProcessNameForWindow(nint window)
    {
        try
        {
            _ = GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
            {
                return null;
            }

            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception)
        {
            // Process exited, or access denied on a protected process. Not knowing the name only
            // costs profile matching, so it must never fail the capture.
            return null;
        }
    }

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
            Name = "Scribe.SelectionReader",
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return captured is not null ? throw captured : result!;
    }
}
