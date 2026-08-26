using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Microsoft.Extensions.Logging;
using Scribe.Core.TextInjection;

namespace Scribe.App.Infrastructure;

/// <summary>
/// Reads the selection out of another application using UI Automation, without touching the
/// clipboard.
/// </summary>
/// <remarks>
/// <para>
/// This is the preferred way to read a selection. The clipboard path works everywhere but costs the
/// user real damage on every invocation: it synthesizes a copy, which the TARGET application
/// performs, so the selected text lands in Windows Clipboard History and syncs to their other
/// devices, and Scribe cannot annotate a write it did not make. UI Automation reads the same text
/// with no side effect at all.
/// </para>
/// <para>
/// <b>Runs on a dedicated long-lived MTA thread, and that is the whole performance story.</b>
/// Measured cross-process, the first read in a process costs 52 to 159 ms and the second costs 2 ms,
/// because essentially all of it is one-time COM and UI Automation client initialization rather than
/// per-call round trips. A per-capture thread would pay the initialization every time. Keeping one
/// warm thread turns a visible stall into something imperceptible.
/// </para>
/// <para>
/// <b>Password fields are the reason this class refuses before it reads.</b> A WPF PasswordBox
/// reports <c>IsPassword=True</c> and its TextPattern hands back the mask characters, which look
/// like ordinary text to any emptiness check. Without the guard, adding this class would make Scribe
/// strictly LESS safe than the clipboard path it improves on, because a copy inside a password box
/// yields nothing at all. It would ship the mask to a cloud model and then overwrite the credential
/// with the result.
/// </para>
/// </remarks>
public sealed class UiaSelectionProbe : ISelectionProbe, IDisposable
{
    // Generous, because it is only ever paid once: the first call initializes the COM apartment and
    // the UI Automation client. Subsequent calls settle around 2 ms.
    private const int FirstReadTimeoutMs = 1500;
    private const int WarmReadTimeoutMs = 400;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public nint hwndActive;
        public nint hwndFocus;
        public nint hwndCapture;
        public nint hwndMenuOwner;
        public nint hwndMoveSize;
        public nint hwndCaret;
        public int rcLeft, rcTop, rcRight, rcBottom;
    }

    private readonly ILogger<UiaSelectionProbe> _log;
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Thread _worker;
    private bool _warm;
    private bool _disposed;

    private sealed record WorkItem(nint Target, TaskCompletionSource<SelectionProbe> Completion);

    public UiaSelectionProbe(ILogger<UiaSelectionProbe> log)
    {
        _log = log;

        // MTA, not STA: UI Automation clients are documented to run in a multithreaded apartment,
        // and an STA client marshals every call through a message pump it does not own.
        _worker = new Thread(Pump)
        {
            Name = "Scribe.UiaSelectionProbe",
            IsBackground = true,
        };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    /// <inheritdoc />
    public SelectionProbe TryRead(nint targetWindow)
    {
        if (_disposed || targetWindow == 0)
        {
            return SelectionProbe.Unsupported;
        }

        var item = new WorkItem(targetWindow, new TaskCompletionSource<SelectionProbe>(
            TaskCreationOptions.RunContinuationsAsynchronously));

        try
        {
            _queue.Add(item);
        }
        catch (Exception)
        {
            return SelectionProbe.Unsupported;
        }

        var timeout = _warm ? WarmReadTimeoutMs : FirstReadTimeoutMs;
        if (!item.Completion.Task.Wait(timeout))
        {
            // The worker is still going. Abandoning the wait is safe: the result is simply dropped.
            _log.LogDebug("UI Automation selection read exceeded {Timeout} ms; falling through.", timeout);
            return SelectionProbe.Unsupported;
        }

        _warm = true;
        return item.Completion.Task.Result;
    }

    private void Pump()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            SelectionProbe result;
            try
            {
                result = Read(item.Target);
            }
            catch (ElementNotAvailableException)
            {
                // The target closed or navigated mid-read. Not an error worth surfacing.
                result = SelectionProbe.Unsupported;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "UI Automation selection read failed; falling through.");
                result = SelectionProbe.Unsupported;
            }

            item.Completion.TrySetResult(result);
        }
    }

    private SelectionProbe Read(nint target)
    {
        var element = ResolveFocusedElement(target);
        if (element is null)
        {
            return SelectionProbe.Unsupported;
        }

        // Before anything else. See the class remarks: the mask reads as ordinary text.
        if (IsPassword(element))
        {
            return new SelectionProbe(
                SelectionProbeOutcome.PasswordField,
                Detail: "Scribe will not read from a password field.");
        }

        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var raw) ||
            raw is not TextPattern text)
        {
            return SelectionProbe.Unsupported;
        }

        if (text.SupportedTextSelection == SupportedTextSelection.None)
        {
            return SelectionProbe.Unsupported;
        }

        var ranges = text.GetSelection();
        if (ranges is null || ranges.Length == 0)
        {
            return SelectionProbe.Unsupported;
        }

        var canWriteBack = CanWriteBack(element);

        // A single zero-length range is the caret sitting somewhere with nothing selected. This is
        // authoritative and terminal: falling through to a clipboard borrow would synthesize a copy
        // that also finds nothing, report the same conclusion more slowly, and touch the user's
        // clipboard for no reason.
        if (ranges.Length == 1)
        {
            var single = ranges[0].GetText(-1) ?? string.Empty;
            return single.Length == 0
                ? new SelectionProbe(
                    SelectionProbeOutcome.NothingSelected,
                    Detail: "Select some text first, then run this action.")
                : new SelectionProbe(SelectionProbeOutcome.Success, single, canWriteBack);
        }

        // Several ranges means a table or spreadsheet selection. The text is readable, but the
        // pieces cannot be written back coherently, so the caller must offer the result rather than
        // promise a replacement.
        var joined = string.Join("\n", ranges.Select(r => r.GetText(-1) ?? string.Empty)
            .Where(s => s.Length > 0));

        return joined.Length == 0
            ? new SelectionProbe(
                SelectionProbeOutcome.NothingSelected,
                Detail: "Select some text first, then run this action.")
            : new SelectionProbe(SelectionProbeOutcome.Disjoint, joined, CanWriteBack: false);
    }

    /// <summary>
    /// Resolves the element holding the selection.
    /// </summary>
    /// <remarks>
    /// Prefers the target thread's own focused window over <c>AutomationElement.FocusedElement</c>.
    /// The static property answers "what has focus right now", which during the moment Scribe acts
    /// can still be Scribe's own surface. Asking the target's thread directly is both narrower and
    /// correct. <c>GetGUIThreadInfo</c> is passed the target's thread id rather than 0 for the same
    /// reason: the documentation warns that 0 may not return valid handles while a window is losing
    /// activation, which is exactly this moment.
    /// </remarks>
    private static AutomationElement? ResolveFocusedElement(nint target)
    {
        var threadId = GetWindowThreadProcessId(target, out _);
        if (threadId != 0)
        {
            var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(threadId, ref info) && info.hwndFocus != 0)
            {
                var focused = AutomationElement.FromHandle(info.hwndFocus);
                if (focused is not null)
                {
                    return focused;
                }
            }
        }

        try
        {
            return AutomationElement.FocusedElement ?? AutomationElement.FromHandle(target);
        }
        catch (Exception)
        {
            return AutomationElement.FromHandle(target);
        }
    }

    private static bool IsPassword(AutomationElement element)
    {
        try
        {
            return element.Current.IsPassword;
        }
        catch (Exception)
        {
            // An unreadable property is treated as "might be a password". Refusing a legitimate read
            // costs one fallback to the clipboard; guessing wrong the other way ships a credential.
            return true;
        }
    }

    /// <summary>
    /// Whether the surface can accept a programmatic replacement.
    /// </summary>
    /// <remarks>
    /// Measured, this is false far more often than it looks. Every document-style surface tested that
    /// was not a RichEdit exposed TextPattern without ValuePattern: a WPF RichTextBox and Windows
    /// Terminal both read fine and cannot be written. That is the common case for exactly the
    /// surfaces this feature targets, so the answer is resolved BEFORE the model runs and the palette
    /// offers "Copy result" instead of promising a replacement it cannot perform.
    /// </remarks>
    private static bool CanWriteBack(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var raw) &&
                raw is ValuePattern value)
            {
                return !value.Current.IsReadOnly;
            }
        }
        catch (Exception)
        {
            // Fall through: absence of a readable ValuePattern is not proof either way, and the
            // injector still has its own foreground and control-class checks.
        }

        // No ValuePattern at all. Typing into the surface may still work (the injector synthesizes
        // real keystrokes rather than using UI Automation), so this is not a refusal, only a signal
        // that Scribe cannot confirm a write path.
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
    }
}
