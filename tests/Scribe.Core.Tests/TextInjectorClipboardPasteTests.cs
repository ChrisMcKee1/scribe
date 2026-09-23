using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.TextInjection;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// The clipboard paste path end to end against scripted clipboard and input boundaries. Paste delivery
/// and the restore of the user's clipboard are separate outcomes, and a delivered paste must never be
/// followed by typing the same text again, whatever happened to the restore.
/// </summary>
public sealed class TextInjectorClipboardPasteTests
{
    private const string Previous = "PREVIOUS-CLIPBOARD-7f3a";
    private const string Dictation = "DICTATED-TEXT-c41e";
    private const string Newer = "NEWER-COPY-9d2b";
    private const nint Target = 0x4242;

    private sealed class Rig
    {
        public TextInjectionFakes.Clipboard Clipboard { get; } = new();

        public TextInjectionFakes.Platform Platform { get; } = new() { Foreground = Target };

        public TextInjectionFakes.CapturingLogger<TextInjector> Log { get; init; } = new();

        public InjectionResult Paste(string text = Dictation) =>
            new TextInjector(Log, Platform, Clipboard).Inject(text, InjectionMethod.ClipboardPaste, Target);
    }

    private static int TypedEventsFor(string text) => TextInjector.CountKeyEvents(text, 0, text.Length);

    [Fact]
    public void A_delivered_paste_whose_restore_fails_is_never_typed_a_second_time()
    {
        var rig = new Rig();
        rig.Clipboard.SeedText(Previous);

        // The borrow opens on the first attempt; every later open fails, so only the restore is hit.
        rig.Clipboard.OpenAttemptSucceeds = attempt => attempt == 1;

        var result = rig.Paste();

        Assert.True(result.Succeeded);
        Assert.Equal("clipboard", result.Method);
        Assert.Equal(PasteDelivery.ChordInserted, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.Failed, result.ClipboardRestore);
        Assert.Single(rig.Platform.Batches);
        Assert.Equal(1, rig.Platform.CtrlVChords);
        Assert.Equal(0, rig.Platform.UnicodeEvents);
        Assert.All(
            rig.Platform.Batches.SelectMany(batch => batch),
            input => Assert.Equal(SyntheticInputMarker.Value, input.U.ki.dwExtraInfo));
    }

    [Fact]
    public void A_delivered_paste_puts_the_previous_clipboard_back_after_the_target_read_it()
    {
        var rig = new Rig();
        rig.Clipboard.SeedText(Previous);
        string? pasted = null;
        rig.Platform.OnSleep = ms =>
        {
            if (ms == TextInjector.PasteSettleDelayMs)
            {
                pasted = rig.Clipboard.TargetReads();
            }
        };

        var result = rig.Paste();

        Assert.Equal(Dictation, pasted);
        Assert.True(result.Succeeded);
        Assert.Equal(PasteDelivery.ChordInserted, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.Restored, result.ClipboardRestore);
        Assert.Equal(Previous, rig.Clipboard.Text);
        Assert.Equal(0, rig.Platform.UnicodeEvents);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void The_clipboard_is_never_held_across_a_wait_or_a_keystroke(
        bool closeMovesSequence, bool synthesizedReadMovesSequence)
    {
        var rig = new Rig();
        rig.Clipboard.CloseMovesSequence = closeMovesSequence;
        rig.Clipboard.SynthesizedReadMovesSequence = synthesizedReadMovesSequence;
        rig.Clipboard.SeedText(Previous);
        var heldDuring = new List<string>();
        rig.Platform.OnSleep = ms =>
        {
            if (rig.Clipboard.IsHeldByScribe)
            {
                heldDuring.Add($"sleep {ms}");
            }

            if (ms == TextInjector.PasteSettleDelayMs)
            {
                rig.Clipboard.TargetReads();
            }
        };
        rig.Platform.Deliver = (_, inputs) =>
        {
            if (rig.Clipboard.IsHeldByScribe)
            {
                heldDuring.Add("SendInput");
            }

            return (uint)inputs.Length;
        };

        var result = rig.Paste();

        Assert.Empty(heldDuring);
        Assert.Equal(PasteDelivery.ChordInserted, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.Restored, result.ClipboardRestore);
        Assert.Equal(Previous, rig.Clipboard.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_copy_made_during_the_settle_is_neither_pasted_nor_overwritten(bool closeMovesSequence)
    {
        var rig = new Rig();
        rig.Clipboard.CloseMovesSequence = closeMovesSequence;
        rig.Clipboard.SeedText(Previous);
        rig.Platform.OnSleep = ms =>
        {
            if (ms == TextInjector.ClipboardSettleDelayMs)
            {
                rig.Clipboard.OtherAppCopies(Newer);
            }
        };

        var result = rig.Paste();

        // Nothing was pasted, so typing is the only delivery and cannot duplicate anything.
        Assert.Equal(PasteDelivery.Superseded, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.Superseded, result.ClipboardRestore);
        Assert.Equal("unicode", result.Method);
        Assert.True(result.Succeeded);
        Assert.Equal(0, rig.Platform.CtrlVChords);
        Assert.Equal(TypedEventsFor(Dictation), rig.Platform.UnicodeEvents);
        Assert.Equal(Newer, rig.Clipboard.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_copy_landing_right_after_the_borrow_releases_the_clipboard_is_never_pasted(bool closeMovesSequence)
    {
        // The copy lands between the borrow's CloseClipboard and the next sequence number read, the one
        // instant a number read after the release could be mistaken for Scribe's own.
        var rig = new Rig();
        rig.Clipboard.CloseMovesSequence = closeMovesSequence;
        rig.Clipboard.SeedText(Previous);
        rig.Clipboard.AfterClose = close =>
        {
            if (close == 1)
            {
                rig.Clipboard.OtherAppCopies(Newer);
            }
        };

        var result = rig.Paste();

        Assert.Equal(0, rig.Platform.CtrlVChords);
        Assert.Equal(PasteDelivery.Superseded, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.Superseded, result.ClipboardRestore);
        Assert.Equal("unicode", result.Method);
        Assert.True(result.Succeeded);
        Assert.Equal(TypedEventsFor(Dictation), rig.Platform.UnicodeEvents);
        Assert.Equal(Newer, rig.Clipboard.Text);
    }

    [Fact]
    public void A_receipt_that_cannot_be_written_puts_the_previous_text_back_and_types_instead()
    {
        var rig = new Rig();
        rig.Clipboard.SeedText(Previous);
        uint receiptFormat = rig.Clipboard.RegisterFormat(ClipboardBorrower.ReceiptFormatName);
        rig.Clipboard.SetDataSucceeds = format => format != receiptFormat;

        var result = rig.Paste();

        Assert.Equal(PasteDelivery.ReceiptFailed, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.Restored, result.ClipboardRestore);
        Assert.Equal("unicode", result.Method);
        Assert.True(result.Succeeded);
        Assert.Equal(0, rig.Platform.CtrlVChords);
        Assert.Equal(TypedEventsFor(Dictation), rig.Platform.UnicodeEvents);
        Assert.Equal(Previous, rig.Clipboard.Text);

        // The rollback happened inside the borrow's own session: no second open to confirm or restore.
        Assert.Single(rig.Clipboard.Trace, call => call == "open");
        var line = Assert.Single(rig.Log.Entries, entry => entry.Contains("Clipboard paste", StringComparison.Ordinal));
        Assert.StartsWith("Warning:", line, StringComparison.Ordinal);
        Assert.Contains("Delivery=ReceiptFailed", line, StringComparison.Ordinal);
        Assert.Contains("Restore=Restored", line, StringComparison.Ordinal);
        Assert.Contains("TypingInstead=True", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_receipt_failure_never_pastes_or_overwrites_a_copy_made_right_after_the_borrow()
    {
        // With no receipt nothing held could tell Scribe's item from a copy that lands right after the
        // release, so a borrow that fails to write its receipt must neither paste nor restore over it.
        var rig = new Rig();
        rig.Clipboard.SeedText(Previous);
        uint receiptFormat = rig.Clipboard.RegisterFormat(ClipboardBorrower.ReceiptFormatName);
        rig.Clipboard.SetDataSucceeds = format => format != receiptFormat;
        rig.Clipboard.AfterClose = close =>
        {
            if (close == 1)
            {
                rig.Clipboard.OtherAppCopies(Newer);
            }
        };

        var result = rig.Paste();

        Assert.Equal(0, rig.Platform.CtrlVChords);
        Assert.Equal(PasteDelivery.ReceiptFailed, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.Restored, result.ClipboardRestore);
        Assert.Equal("unicode", result.Method);
        Assert.True(result.Succeeded);
        Assert.Equal(TypedEventsFor(Dictation), rig.Platform.UnicodeEvents);
        Assert.Equal(Newer, rig.Clipboard.Text);
        Assert.Single(rig.Clipboard.Trace, call => call == "open");
    }

    [Fact]
    public void A_copy_made_after_the_paste_is_left_alone_and_nothing_is_retyped()
    {
        var rig = new Rig();
        rig.Clipboard.SeedText(Previous);
        rig.Platform.OnSleep = ms =>
        {
            if (ms == TextInjector.PasteSettleDelayMs)
            {
                rig.Clipboard.OtherAppCopies(Newer);
            }
        };

        var result = rig.Paste();

        Assert.True(result.Succeeded);
        Assert.Equal(PasteDelivery.ChordInserted, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.Superseded, result.ClipboardRestore);
        Assert.Equal(Newer, rig.Clipboard.Text);
        Assert.Equal(0, rig.Platform.UnicodeEvents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_copy_made_while_the_restore_retries_the_open_survives_a_delivered_paste(bool closeMovesSequence)
    {
        // The original defect end to end: the restore used to decide before its open retry loop, so a
        // copy made while it retried was overwritten with the stale snapshot.
        var rig = new Rig();
        rig.Clipboard.CloseMovesSequence = closeMovesSequence;
        rig.Clipboard.SeedText(Previous);
        int restoreFirstAttempt = 0;
        rig.Platform.OnSleep = ms =>
        {
            if (ms == TextInjector.PasteSettleDelayMs)
            {
                rig.Clipboard.TargetReads();
                restoreFirstAttempt = rig.Clipboard.OpenAttempts + 1;
            }
        };
        rig.Clipboard.OpenAttemptSucceeds = attempt =>
            restoreFirstAttempt == 0 || attempt > restoreFirstAttempt + 1;
        rig.Clipboard.WhileOpenFails = attempt =>
        {
            if (attempt == restoreFirstAttempt)
            {
                rig.Clipboard.OtherAppCopies(Newer);
            }
        };

        var result = rig.Paste();

        Assert.True(result.Succeeded);
        Assert.Equal("clipboard", result.Method);
        Assert.Equal(PasteDelivery.ChordInserted, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.Superseded, result.ClipboardRestore);
        Assert.Equal(Newer, rig.Clipboard.Text);
        Assert.Equal(1, rig.Platform.CtrlVChords);
        Assert.Equal(0, rig.Platform.UnicodeEvents);
        Assert.False(rig.Clipboard.IsHeldByScribe);
    }

    [Fact]
    public void A_restore_that_throws_after_a_delivered_paste_is_reported_as_failed_and_never_retyped()
    {
        var rig = new Rig();
        rig.Clipboard.SeedText(Previous);
        rig.Clipboard.SetTextSucceeds = text =>
            text == Previous ? throw new InvalidOperationException("EXCEPTION-DETAIL-51c0") : true;

        var result = rig.Paste();

        Assert.True(result.Succeeded);
        Assert.Equal("clipboard", result.Method);
        Assert.Equal(PasteDelivery.ChordInserted, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.Failed, result.ClipboardRestore);
        Assert.Equal(0, rig.Platform.UnicodeEvents);
        Assert.False(rig.Clipboard.IsHeldByScribe);
        Assert.Contains(rig.Log.Entries, entry =>
            entry.StartsWith("Warning:", StringComparison.Ordinal) &&
            entry.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Log.Entries, entry => entry.Contains("51c0", StringComparison.Ordinal));
    }

    [Fact]
    public void A_log_sink_fault_while_reporting_a_failed_restore_does_not_fail_the_delivered_paste()
    {
        var rig = new Rig();
        rig.Log.ThrowOn = message => message.StartsWith("Restoring the clipboard failed", StringComparison.Ordinal);
        rig.Clipboard.SeedText(Previous);
        rig.Clipboard.SetTextSucceeds = text =>
            text == Previous ? throw new InvalidOperationException("restore fault") : true;

        var result = rig.Paste();

        Assert.True(result.Succeeded);
        Assert.Equal(PasteDelivery.ChordInserted, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.Failed, result.ClipboardRestore);
        Assert.Equal(0, rig.Platform.UnicodeEvents);
        Assert.Contains(rig.Log.Entries, entry => entry.Contains("Restoring the clipboard failed", StringComparison.Ordinal));
    }

    [Fact]
    public void An_incomplete_chord_releases_the_keys_restores_the_clipboard_and_types_instead()
    {
        var rig = new Rig();
        rig.Clipboard.SeedText(Previous);
        rig.Platform.Deliver = (index, inputs) => index == 0 ? 1u : (uint)inputs.Length;

        var result = rig.Paste();

        Assert.Equal(PasteDelivery.ChordIncomplete, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.Restored, result.ClipboardRestore);
        Assert.Equal("unicode", result.Method);
        Assert.True(result.Succeeded);
        Assert.Equal(Previous, rig.Clipboard.Text);

        // The key-up pair goes out before any typing, so Ctrl cannot turn the text into shortcuts.
        var release = TextInjector.BuildCtrlVReleaseInputs();
        Assert.Equal(release.Select(i => (i.U.ki.wVk, i.U.ki.dwFlags)), rig.Platform.Batches[1].Select(i => (i.U.ki.wVk, i.U.ki.dwFlags)));
        Assert.Equal(TypedEventsFor(Dictation), rig.Platform.UnicodeEvents);
    }

    [Fact]
    public void A_clipboard_that_stays_busy_is_left_untouched_and_the_text_is_typed()
    {
        var rig = new Rig();
        rig.Clipboard.SeedText(Previous);
        uint sequence = rig.Clipboard.Sequence;
        rig.Clipboard.OpenAttemptSucceeds = _ => false;

        var result = rig.Paste();

        Assert.Equal(PasteDelivery.ClipboardBusy, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.NotApplicable, result.ClipboardRestore);
        Assert.Equal("unicode", result.Method);
        Assert.Equal(TypedEventsFor(Dictation), rig.Platform.UnicodeEvents);
        Assert.Equal(sequence, rig.Clipboard.Sequence);
        Assert.Equal(ClipboardBorrower.OpenAttempts, rig.Clipboard.OpenAttempts);
        Assert.Equal(
            ClipboardBorrower.OpenAttempts - 1,
            rig.Platform.Sleeps.Count(ms => ms == ClipboardBorrower.OpenRetryDelayMs));
    }

    [Fact]
    public void Focus_moving_during_the_settle_restores_the_clipboard_and_sends_nothing()
    {
        var rig = new Rig();
        rig.Clipboard.SeedText(Previous);
        rig.Platform.OnSleep = ms =>
        {
            if (ms == TextInjector.ClipboardSettleDelayMs)
            {
                rig.Platform.Foreground = 0x9999;
            }
        };

        var result = rig.Paste();

        Assert.False(result.Succeeded);
        Assert.Equal("none", result.Method);
        Assert.Equal("The focused window changed while processing.", result.Error);
        Assert.Equal(PasteDelivery.FocusChanged, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.Restored, result.ClipboardRestore);
        Assert.Empty(rig.Platform.Batches);
        Assert.Equal(Previous, rig.Clipboard.Text);
    }

    [Fact]
    public void Non_text_clipboard_content_is_preserved_by_typing_instead()
    {
        var rig = new Rig();
        rig.Clipboard.SeedFormats(TextInjectionFakes.CF_DIB);

        var result = rig.Paste();

        Assert.Equal(PasteDelivery.NonTextContent, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.NotApplicable, result.ClipboardRestore);
        Assert.Equal("unicode", result.Method);
        Assert.Equal(new[] { "open", "close" }, rig.Clipboard.Trace);
    }

    [Fact]
    public void A_failed_write_is_rolled_back_and_the_text_is_typed()
    {
        var rig = new Rig();
        rig.Clipboard.SeedText(Previous);
        rig.Clipboard.SetTextSucceeds = text => text != Dictation;

        var result = rig.Paste();

        Assert.Equal(PasteDelivery.WriteFailed, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.Restored, result.ClipboardRestore);
        Assert.Equal("unicode", result.Method);
        Assert.Equal(0, rig.Platform.CtrlVChords);
        Assert.Equal(Previous, rig.Clipboard.Text);
    }

    [Fact]
    public void A_write_that_cannot_be_reconfirmed_is_not_pasted()
    {
        var rig = new Rig();
        rig.Clipboard.SynthesizedReadMovesSequence = true;
        rig.Clipboard.SeedText(Previous);

        // A monitor's read during the settle moves the number, so Confirm has to open the clipboard; only
        // the borrow's open succeeds, so the confirm and the restore find it busy.
        rig.Platform.OnSleep = ms =>
        {
            if (ms == TextInjector.ClipboardSettleDelayMs)
            {
                rig.Clipboard.MonitorReads();
            }
        };
        rig.Clipboard.OpenAttemptSucceeds = attempt => attempt == 1;

        var result = rig.Paste();

        Assert.Equal(PasteDelivery.Unconfirmed, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.Failed, result.ClipboardRestore);
        Assert.Equal("unicode", result.Method);
        Assert.Equal(0, rig.Platform.CtrlVChords);
        Assert.Equal(TypedEventsFor(Dictation), rig.Platform.UnicodeEvents);
    }

    [Fact]
    public void Focus_moving_while_Confirm_waits_for_the_clipboard_sends_nothing_and_restores()
    {
        // The window was right after the settle, but Confirm then spent a retry waiting for the
        // clipboard, and the user switched windows meanwhile: Ctrl+V would have landed in the wrong one.
        var rig = new Rig();
        rig.Clipboard.CloseMovesSequence = true;
        rig.Clipboard.SynthesizedReadMovesSequence = true;
        rig.Clipboard.SeedText(Previous);
        rig.Platform.OnSleep = ms =>
        {
            if (ms == TextInjector.ClipboardSettleDelayMs)
            {
                // Any move from the number read while held makes Confirm open; this one comes after the
                // release's own.
                rig.Clipboard.MonitorReads();
            }
            else if (ms == ClipboardBorrower.OpenRetryDelayMs)
            {
                rig.Platform.Foreground = 0x9999;
            }
        };

        // Attempt 1 is the borrow; attempt 2, Confirm's first, fails; the rest succeed.
        rig.Clipboard.OpenAttemptSucceeds = attempt => attempt != 2;

        var result = rig.Paste();

        Assert.Equal(0, rig.Platform.CtrlVChords);
        Assert.Empty(rig.Platform.Batches);
        Assert.False(result.Succeeded);
        Assert.Equal(InjectionResult.FocusChangedError, result.Error);
        Assert.Equal(PasteDelivery.FocusChanged, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.Restored, result.ClipboardRestore);
        Assert.Equal(Previous, rig.Clipboard.Text);
        Assert.Contains(rig.Log.Entries, entry => entry.Contains("ConfirmOpened=True", StringComparison.Ordinal));
    }

    [Fact]
    public void The_standard_edit_fast_path_never_touches_the_clipboard()
    {
        var rig = new Rig();
        rig.Clipboard.SeedText(Previous);
        rig.Platform.StandardEdit = true;

        var result = rig.Paste();

        Assert.Equal("win32-edit", result.Method);
        Assert.Equal(PasteDelivery.NotUsed, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.NotApplicable, result.ClipboardRestore);
        Assert.Empty(rig.Clipboard.Trace);
        Assert.Empty(rig.Platform.Batches);
    }

    [Theory]
    [InlineData(PasteDelivery.ChordInserted, ClipboardRestoreOutcome.Restored, LogLevel.Debug)]
    [InlineData(PasteDelivery.ChordInserted, ClipboardRestoreOutcome.Superseded, LogLevel.Information)]
    [InlineData(PasteDelivery.ChordInserted, ClipboardRestoreOutcome.Failed, LogLevel.Warning)]
    [InlineData(PasteDelivery.Superseded, ClipboardRestoreOutcome.Superseded, LogLevel.Information)]
    [InlineData(PasteDelivery.FocusChanged, ClipboardRestoreOutcome.Restored, LogLevel.Information)]
    [InlineData(PasteDelivery.FocusChanged, ClipboardRestoreOutcome.Failed, LogLevel.Warning)]
    [InlineData(PasteDelivery.NonTextContent, ClipboardRestoreOutcome.NotApplicable, LogLevel.Information)]
    [InlineData(PasteDelivery.ClipboardBusy, ClipboardRestoreOutcome.NotApplicable, LogLevel.Warning)]
    [InlineData(PasteDelivery.SnapshotUnreadable, ClipboardRestoreOutcome.NotApplicable, LogLevel.Warning)]
    [InlineData(PasteDelivery.WriteFailed, ClipboardRestoreOutcome.Restored, LogLevel.Warning)]
    [InlineData(PasteDelivery.ReceiptFailed, ClipboardRestoreOutcome.Restored, LogLevel.Warning)]
    [InlineData(PasteDelivery.ReceiptFailed, ClipboardRestoreOutcome.Failed, LogLevel.Warning)]
    [InlineData(PasteDelivery.Unconfirmed, ClipboardRestoreOutcome.Restored, LogLevel.Warning)]
    [InlineData(PasteDelivery.ChordIncomplete, ClipboardRestoreOutcome.Restored, LogLevel.Warning)]
    public void Clipboard_outcomes_are_logged_at_a_level_matching_their_severity(
        PasteDelivery delivery, ClipboardRestoreOutcome restore, LogLevel expected) =>
        Assert.Equal(expected, TextInjector.ClipboardLogLevel(delivery, restore));

    [Fact]
    public void Each_paste_attempt_writes_one_clipboard_line_of_enum_names()
    {
        var rig = new Rig();
        rig.Clipboard.SeedFormats(TextInjectionFakes.CF_DIB);

        rig.Paste();

        var line = Assert.Single(rig.Log.Entries, entry => entry.Contains("Clipboard paste", StringComparison.Ordinal));
        Assert.StartsWith("Information:", line, StringComparison.Ordinal);
        Assert.Contains("Delivery=NonTextContent", line, StringComparison.Ordinal);
        Assert.Contains("Restore=NotApplicable", line, StringComparison.Ordinal);
        Assert.Contains("TypingInstead=True", line, StringComparison.Ordinal);
        Assert.Contains("CloseMovedSequence=False", line, StringComparison.Ordinal);
        Assert.Contains("ConfirmOpened=False", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_paste_line_shows_how_the_sequence_number_behaved(bool closeMovesSequence)
    {
        // A release that moves the number leaves Confirm nothing held to compare with, so it has to open
        // the clipboard and prove the write by receipt before the paste.
        var rig = new Rig();
        rig.Clipboard.CloseMovesSequence = closeMovesSequence;
        rig.Clipboard.SeedText(Previous);

        rig.Paste();

        var line = Assert.Single(rig.Log.Entries, entry => entry.Contains("Clipboard paste", StringComparison.Ordinal));
        Assert.Contains("Delivery=ChordInserted", line, StringComparison.Ordinal);
        Assert.Contains($"CloseMovedSequence={closeMovesSequence}", line, StringComparison.Ordinal);
        Assert.Contains($"ConfirmOpened={closeMovesSequence}", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_throwing_log_sink_or_trace_listener_never_fails_a_delivered_paste()
    {
        // Every log call throws, and so does the span's stop, including the warnings a partial Ctrl+V
        // raises after its V key-down already went out.
        const string text = "DELIVERED-DESPITE-FAULTS-0e71";
        var faults = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ScribeTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == ScribeTelemetry.InjectActivity &&
                    activity.GetTagItem(ScribeTelemetry.TagInjectChars) is int chars && chars == text.Length)
                {
                    Interlocked.Increment(ref faults);
                    throw new InvalidOperationException("Simulated trace listener fault.");
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var rig = new Rig();
        rig.Log.ThrowOn = _ => true;
        rig.Clipboard.SeedText(Previous);
        rig.Platform.Deliver = (index, inputs) => index == 0 ? 3u : (uint)inputs.Length;

        var result = rig.Paste(text);

        Assert.True(result.Succeeded);
        Assert.Equal("clipboard", result.Method);
        Assert.Equal(PasteDelivery.ChordInserted, result.Paste);
        Assert.Equal(ClipboardRestoreOutcome.Restored, result.ClipboardRestore);
        Assert.Equal(0, rig.Platform.UnicodeEvents);
        Assert.Equal(Previous, rig.Clipboard.Text);
        Assert.Contains(rig.Log.Entries, entry => entry.Contains("Ctrl+V events", StringComparison.Ordinal));
        Assert.Contains(rig.Log.Entries, entry => entry.Contains("Clipboard paste", StringComparison.Ordinal));
        Assert.Equal(1, Volatile.Read(ref faults));
    }

    [Fact]
    public void Every_tag_an_injection_span_carries_is_allowlisted_and_shown_in_its_declared_shape()
    {
        // A distinctive length marks this test's spans: listeners are process wide and other tests inject too.
        const string text = "TAG-SHAPE-PROBE-5c0d-of-a-length-no-other-test-uses-0417";
        var tags = new List<KeyValuePair<string, object?>>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ScribeTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == ScribeTelemetry.InjectActivity &&
                    activity.GetTagItem(ScribeTelemetry.TagInjectChars) is int chars && chars == text.Length)
                {
                    lock (tags)
                    {
                        tags.AddRange(activity.TagObjects);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        // A delivered paste, a paste refused over rich clipboard content (typed instead), one refused over
        // a receipt that could not be written, Unicode typing, and the standard edit control fast path:
        // between them every tag the injector sets, in every shape a delivery takes.
        var arrangements = new Action<Rig>[]
        {
            rig => rig.Clipboard.SeedText(Previous),
            rig => rig.Clipboard.SeedFormats(TextInjectionFakes.CF_DIB),
            rig =>
            {
                rig.Clipboard.SeedText(Previous);
                uint receiptFormat = rig.Clipboard.RegisterFormat(ClipboardBorrower.ReceiptFormatName);
                rig.Clipboard.SetDataSucceeds = format => format != receiptFormat;
            },
            rig => rig.Platform.StandardEdit = true,
        };

        foreach (var arrange in arrangements)
        {
            var rig = new Rig();
            arrange(rig);
            rig.Paste(text);
        }

        var typist = new TextInjector(
            new TextInjectionFakes.CapturingLogger<TextInjector>(),
            new TextInjectionFakes.Platform(),
            new TextInjectionFakes.Clipboard());
        typist.Inject(text, InjectionMethod.UnicodeType, 0);

        List<KeyValuePair<string, object?>> seen;
        lock (tags)
        {
            seen = [.. tags];
        }

        Assert.Contains(seen, tag => tag.Key == ScribeTelemetry.TagPasteDelivery);
        Assert.Contains(seen, tag =>
            tag.Key == ScribeTelemetry.TagPasteDelivery && Equals(tag.Value, nameof(PasteDelivery.ReceiptFailed)));
        Assert.Contains(seen, tag => tag.Key == ScribeTelemetry.TagClipboardRestore);
        Assert.Contains(seen, tag => tag.Key == ScribeTelemetry.TagInjectFallback && tag.Value is true);
        Assert.All(seen, tag =>
        {
            Assert.True(TraceTagPolicy.TryFormatValue(tag.Key, tag.Value, out var formatted), $"{tag.Key} is not on the allowlist");
            Assert.NotEqual(TraceTagPolicy.OmittedValue, formatted);
        });
    }

    [Fact]
    public void No_clipboard_or_dictated_text_reaches_the_log_or_the_trace()
    {
        var spans = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ScribeTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (spans)
                {
                    spans.AddRange(activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"));
                    spans.Add($"status={activity.StatusDescription}");
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var log = new TextInjectionFakes.CapturingLogger<TextInjector>();
        var scenarios = new Action<Rig>[]
        {
            rig => rig.Platform.OnSleep = ms =>
            {
                if (ms == TextInjector.PasteSettleDelayMs)
                {
                    rig.Clipboard.TargetReads();
                }
            },
            rig => rig.Clipboard.OpenAttemptSucceeds = attempt => attempt == 1,
            rig => rig.Platform.OnSleep = ms =>
            {
                if (ms == TextInjector.ClipboardSettleDelayMs)
                {
                    rig.Clipboard.OtherAppCopies(Newer);
                }
            },
            rig => rig.Clipboard.OpenAttemptSucceeds = _ => false,
            rig => rig.Clipboard.SeedFormats(TextInjectionFakes.CF_DIB),
            rig => rig.Platform.Deliver = (index, inputs) => index == 0 ? 1u : (uint)inputs.Length,
            rig => rig.Clipboard.SetTextSucceeds = text => text != Dictation,
            rig =>
            {
                uint receiptFormat = rig.Clipboard.RegisterFormat(ClipboardBorrower.ReceiptFormatName);
                rig.Clipboard.SetDataSucceeds = format => format != receiptFormat;
            },
            rig =>
            {
                rig.Clipboard.SynthesizedReadMovesSequence = true;
                rig.Clipboard.OpenAttemptSucceeds = attempt => attempt == 1;
                rig.Platform.OnSleep = ms =>
                {
                    if (ms == TextInjector.ClipboardSettleDelayMs)
                    {
                        rig.Clipboard.MonitorReads();
                    }
                };
            },
        };

        foreach (var arrange in scenarios)
        {
            var rig = new Rig { Log = log };
            rig.Clipboard.SeedText(Previous);
            arrange(rig);
            rig.Paste();
        }

        var everything = log.Entries.Concat(spans).ToList();
        Assert.Contains(log.Entries, entry => entry.Contains("Delivery=ChordInserted", StringComparison.Ordinal));
        Assert.Contains(log.Entries, entry => entry.Contains("Delivery=Unconfirmed", StringComparison.Ordinal));
        Assert.Contains(log.Entries, entry => entry.Contains("Delivery=ReceiptFailed", StringComparison.Ordinal));
        Assert.Contains(log.Entries, entry => entry.Contains("Restore=Failed", StringComparison.Ordinal));
        Assert.Contains(spans, tag => tag == $"{ScribeTelemetry.TagClipboardRestore}=Restored");
        Assert.Contains(spans, tag => tag == $"{ScribeTelemetry.TagPasteDelivery}=Superseded");
        foreach (var secret in new[] { Previous, Dictation, Newer, "7f3a", "c41e", "9d2b" })
        {
            Assert.DoesNotContain(everything, entry => entry.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }
}
