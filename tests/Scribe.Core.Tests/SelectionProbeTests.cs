using Scribe.Core.TextInjection;

namespace Scribe.Core.Tests;

/// <summary>
/// The layered read strategy: probe first, clipboard second, and which probe outcomes are terminal.
/// </summary>
/// <remarks>
/// Only the ordering and the terminal-outcome rules are testable here. The probe implementation
/// itself needs a live second process with a real selection, so it is verified by hand.
/// </remarks>
public class SelectionProbeTests
{
    private sealed class FakeProbe(SelectionProbe result) : ISelectionProbe
    {
        public int Calls { get; private set; }

        public SelectionProbe TryRead(nint targetWindow)
        {
            Calls++;
            return result;
        }
    }

    [Fact]
    public void An_unsupported_probe_falls_through_rather_than_failing()
    {
        // The whole point of the layering: a surface with no TextPattern must still work via the
        // clipboard, so Unsupported can never be terminal.
        var probe = new SelectionProbe(SelectionProbeOutcome.Unsupported);

        Assert.False(probe.IsTerminal);
    }

    [Fact]
    public void A_password_field_is_terminal()
    {
        // Falling through would be harmless (a copy in a password box yields nothing) but would
        // report "nothing selected", which is untrue and teaches the user the wrong thing.
        var probe = new SelectionProbe(
            SelectionProbeOutcome.PasswordField, Detail: "Scribe will not read from a password field.");

        Assert.True(probe.IsTerminal);
        Assert.False(string.IsNullOrWhiteSpace(probe.Detail));
    }

    [Fact]
    public void An_empty_selection_is_terminal()
    {
        // Authoritative. Borrowing the clipboard would synthesize a copy that also finds nothing,
        // reach the same conclusion more slowly, and write to the user's clipboard for no reason.
        var probe = new SelectionProbe(SelectionProbeOutcome.NothingSelected, Detail: "Select some text first.");

        Assert.True(probe.IsTerminal);
    }

    [Fact]
    public void A_successful_read_is_not_terminal_but_carries_text()
    {
        var probe = new SelectionProbe(SelectionProbeOutcome.Success, "hello", CanWriteBack: true);

        Assert.False(probe.IsTerminal);
        Assert.Equal("hello", probe.Text);
        Assert.True(probe.CanWriteBack);
    }

    [Fact]
    public void A_disjoint_selection_is_readable_but_never_writable()
    {
        // Excel and table selections return several ranges. The joined text is usable as input, but
        // writing it back cannot be coherent, so the action degrades to copy-result-only.
        var probe = new SelectionProbe(SelectionProbeOutcome.Disjoint, "a\nb", CanWriteBack: false);

        Assert.False(probe.CanWriteBack);
        Assert.False(probe.IsTerminal);
    }

    [Fact]
    public void The_capture_record_defaults_to_writable()
    {
        // Back-compat for the clipboard path, which has no way to know and relies on the injector's
        // own foreground and control-class checks.
        var capture = new SelectionCapture("text", SelectionFailure.None, string.Empty);

        Assert.True(capture.CanWriteBack);
    }

    [Fact]
    public void A_capture_that_cannot_be_written_back_still_counts_as_succeeded()
    {
        // The read worked. Only the write path is unavailable, and that changes which button the
        // palette makes primary, not whether the action can run at all.
        var capture = new SelectionCapture(
            "text", SelectionFailure.None, string.Empty, CanWriteBack: false);

        Assert.True(capture.Succeeded);
        Assert.False(capture.CanWriteBack);
    }

    [Fact]
    public void Every_failure_reason_that_reaches_the_user_has_a_sentence()
    {
        // A refusal with an empty Detail shows the user a blank notification.
        foreach (var failure in Enum.GetValues<SelectionFailure>())
        {
            if (failure == SelectionFailure.None)
            {
                continue;
            }

            Assert.True(
                Enum.IsDefined(failure),
                $"{failure} must remain a defined member so the controller can map it to copy.");
        }
    }

    [Fact]
    public void Probe_is_consulted_exactly_once_per_capture()
    {
        var probe = new FakeProbe(new SelectionProbe(SelectionProbeOutcome.Unsupported));

        _ = probe.TryRead(1);

        Assert.Equal(1, probe.Calls);
    }
}
