using Scribe.Core.TextInjection;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// The clipboard borrow against a scripted clipboard. Native behavior these tests cannot settle, whether
/// CloseClipboard or an on-demand synthesized format moves the sequence number, is run both ways, so
/// the borrow must be correct whichever Windows actually does.
/// </summary>
public sealed class ClipboardBorrowerTests
{
    private const string Previous = "the text the user had copied";
    private const string Dictation = "the dictation Scribe pastes";

    private static (TextInjectionFakes.Clipboard Clipboard, ClipboardBorrower Borrower, List<int> Sleeps) Create()
    {
        var clipboard = new TextInjectionFakes.Clipboard();
        var sleeps = new List<int>();
        return (clipboard, new ClipboardBorrower(clipboard, sleeps.Add), sleeps);
    }

    [Fact]
    public void Snapshot_replacement_markers_and_receipt_share_one_open_session()
    {
        var (clipboard, borrower, _) = Create();
        clipboard.SeedText(Previous);

        var borrow = borrower.TryBorrow(Dictation);

        Assert.Equal(ClipboardBorrowStatus.Borrowed, borrow.Status);
        Assert.Equal(Previous, borrow.Lease!.Previous);
        Assert.False(borrow.Lease.WasEmpty);
        Assert.Equal(Dictation, clipboard.Text);
        Assert.Equal(
            new[]
            {
                "open",
                "read-text",
                "empty",
                "set-text",
                "set-data:ExcludeClipboardContentFromMonitorProcessing",
                "set-data:CanIncludeInClipboardHistory",
                "set-data:CanUploadToCloudClipboard",
                "set-data:" + ClipboardBorrower.ReceiptFormatName,
                "close",
            },
            clipboard.Trace);
    }

    [Fact]
    public void The_receipt_is_read_after_the_last_write_and_before_the_clipboard_is_released()
    {
        var (clipboard, borrower, _) = Create();
        clipboard.CloseMovesSequence = true;
        clipboard.SeedText(Previous);

        var lease = borrower.TryBorrow(Dictation).Lease!;

        // The close moved the number once more, after the receipt was taken inside the session. That move
        // is only noted for the log; the number read while held stays the proof.
        Assert.Equal(clipboard.Sequence - 1, lease.Sequence);
        Assert.True(lease.CloseMovedSequence);
        Assert.NotNull(lease.Receipt);
        Assert.Equal(ClipboardBorrower.ReceiptBytes, lease.Receipt.Length);
    }

    [Fact]
    public void A_close_that_leaves_the_number_alone_is_recorded_as_such()
    {
        var (clipboard, borrower, _) = Create();
        clipboard.SeedText(Previous);

        var lease = borrower.TryBorrow(Dictation).Lease!;

        Assert.Equal(clipboard.Sequence, lease.Sequence);
        Assert.False(lease.CloseMovedSequence);
    }

    [Fact]
    public void A_copy_made_while_the_borrow_waits_for_the_clipboard_is_what_gets_restored()
    {
        var (clipboard, borrower, _) = Create();
        clipboard.SeedText("stale text");
        clipboard.OpenAttemptSucceeds = attempt => attempt > 2;
        clipboard.WhileOpenFails = attempt =>
        {
            if (attempt == 2)
            {
                clipboard.OtherAppCopies(Previous);
            }
        };

        var borrow = borrower.TryBorrow(Dictation);
        var restore = borrower.Restore(borrow.Lease!);

        Assert.Equal(Previous, borrow.Lease!.Previous);
        Assert.Equal(ClipboardRestoreOutcome.Restored, restore);
        Assert.Equal(Previous, clipboard.Text);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Restore_puts_the_previous_text_back_whichever_way_the_sequence_number_behaves(
        bool closeMovesSequence, bool synthesizedReadMovesSequence)
    {
        var (clipboard, borrower, _) = Create();
        clipboard.CloseMovesSequence = closeMovesSequence;
        clipboard.SynthesizedReadMovesSequence = synthesizedReadMovesSequence;
        clipboard.SeedText(Previous);

        var lease = borrower.TryBorrow(Dictation).Lease!;
        Assert.Equal(Dictation, clipboard.TargetReads());
        var restore = borrower.Restore(lease);

        Assert.Equal(ClipboardRestoreOutcome.Restored, restore);
        Assert.Equal(Previous, clipboard.Text);
        Assert.True(clipboard.Has("ExcludeClipboardContentFromMonitorProcessing"));
        Assert.True(clipboard.Has("CanIncludeInClipboardHistory"));
        Assert.True(clipboard.Has("CanUploadToCloudClipboard"));
        Assert.False(clipboard.Has(ClipboardBorrower.ReceiptFormatName));
    }

    [Fact]
    public void Restoring_a_clipboard_that_was_empty_empties_it_again()
    {
        var (clipboard, borrower, _) = Create();
        clipboard.SeedEmpty();

        var lease = borrower.TryBorrow(Dictation).Lease!;
        var restore = borrower.Restore(lease);

        Assert.True(lease.WasEmpty);
        Assert.Null(lease.Previous);
        Assert.Equal(ClipboardRestoreOutcome.Restored, restore);
        Assert.Equal(0, clipboard.FormatCount);
    }

    [Fact]
    public void A_copy_made_during_the_restore_retries_is_never_overwritten()
    {
        // The defect this pins: the old restore compared the sequence number BEFORE its open retry loop,
        // so a copy made while it retried was overwritten with the stale snapshot.
        var (clipboard, borrower, _) = Create();
        clipboard.SeedText(Previous);
        var lease = borrower.TryBorrow(Dictation).Lease!;
        int before = clipboard.OpenAttempts;
        clipboard.OpenAttemptSucceeds = attempt => attempt > before + 2;
        clipboard.WhileOpenFails = attempt =>
        {
            if (attempt == before + 1)
            {
                clipboard.OtherAppCopies("newer text from another app");
            }
        };
        int traceStart = clipboard.Trace.Count;

        var restore = borrower.Restore(lease);

        var restoreCalls = clipboard.Trace.Skip(traceStart).ToList();
        Assert.Equal(ClipboardRestoreOutcome.Superseded, restore);
        Assert.Equal("newer text from another app", clipboard.Text);
        Assert.Contains("open", restoreCalls);
        Assert.DoesNotContain("empty", restoreCalls);
        Assert.DoesNotContain("set-text", restoreCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_later_copy_of_the_identical_text_is_not_mistaken_for_Scribes_write(bool closeMovesSequence)
    {
        // For example the user selects the pasted dictation in the target and copies it: same text,
        // but a new item the user asked for, so it must survive.
        var (clipboard, borrower, _) = Create();
        clipboard.CloseMovesSequence = closeMovesSequence;
        clipboard.SeedText(Previous);
        var lease = borrower.TryBorrow(Dictation).Lease!;

        clipboard.OtherAppCopies(Dictation);

        Assert.Equal(ClipboardRestoreOutcome.Superseded, borrower.Restore(lease));
        Assert.Equal(Dictation, clipboard.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Text_rewritten_in_place_counts_as_superseded(bool closeMovesSequence)
    {
        var (clipboard, borrower, _) = Create();
        clipboard.CloseMovesSequence = closeMovesSequence;
        clipboard.SeedText(Previous);
        var lease = borrower.TryBorrow(Dictation).Lease!;

        clipboard.OtherAppRewritesTextInPlace("rewritten by a clipboard tool");

        Assert.Equal(ClipboardRestoreOutcome.Superseded, borrower.Restore(lease));
        Assert.Equal("rewritten by a clipboard tool", clipboard.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_receipt_that_does_not_match_the_lease_is_not_taken_for_Scribes_write(bool closeMovesSequence)
    {
        // The exact dictation text beside a receipt of the right format but other bytes, for example an
        // item cloned from an earlier borrow: only this borrow's own receipt may authorize a restore.
        var (clipboard, borrower, _) = Create();
        clipboard.CloseMovesSequence = closeMovesSequence;
        clipboard.SeedText(Previous);
        var lease = borrower.TryBorrow(Dictation).Lease!;
        var foreign = lease.Receipt!.Select(b => (byte)~b).ToArray();
        int traceStart = clipboard.Trace.Count;

        clipboard.OtherAppCopies(Dictation, clipboard.RegisterFormat(ClipboardBorrower.ReceiptFormatName), foreign);

        Assert.Equal(ClipboardRestoreOutcome.Superseded, borrower.Restore(lease));
        Assert.Equal(Dictation, clipboard.Text);
        Assert.Contains(clipboard.Trace.Skip(traceStart), call => call.StartsWith("read-data", StringComparison.Ordinal));
        Assert.DoesNotContain(clipboard.Trace.Skip(traceStart), call => call is "empty" or "set-text");
    }

    [Fact]
    public void A_replacement_without_the_receipt_is_rejected_before_its_text_is_read()
    {
        // Reading another application's text could make it render a delayed format, so a missing
        // receipt must settle the question first.
        var (clipboard, borrower, _) = Create();
        clipboard.SeedText(Previous);
        var lease = borrower.TryBorrow(Dictation).Lease!;
        clipboard.OtherAppCopies("newer text from another app");
        int traceStart = clipboard.Trace.Count;

        Assert.Equal(ClipboardRestoreOutcome.Superseded, borrower.Restore(lease));
        Assert.DoesNotContain("read-text", clipboard.Trace.Skip(traceStart));
    }

    [Fact]
    public void A_restore_after_the_release_moved_the_number_proves_ownership_by_receipt()
    {
        // A number read after the release can belong to another application's change, so only the held
        // number or the receipt may authorize a mutation.
        var (clipboard, borrower, _) = Create();
        clipboard.CloseMovesSequence = true;
        clipboard.SeedText(Previous);
        var lease = borrower.TryBorrow(Dictation).Lease!;
        Assert.NotEqual(lease.Sequence, clipboard.Sequence);
        int traceStart = clipboard.Trace.Count;

        Assert.Equal(ClipboardRestoreOutcome.Restored, borrower.Restore(lease));
        Assert.Contains("read-data:" + ClipboardBorrower.ReceiptFormatName, clipboard.Trace.Skip(traceStart));
    }

    [Fact]
    public void A_busy_clipboard_is_retried_a_bounded_number_of_times_and_left_untouched()
    {
        var (clipboard, borrower, sleeps) = Create();
        clipboard.SeedText(Previous);
        uint sequence = clipboard.Sequence;
        clipboard.OpenAttemptSucceeds = _ => false;

        var borrow = borrower.TryBorrow(Dictation);

        Assert.Equal(ClipboardBorrowStatus.Busy, borrow.Status);
        Assert.Null(borrow.Lease);
        Assert.Equal(ClipboardBorrower.OpenAttempts, clipboard.OpenAttempts);
        Assert.Equal(ClipboardBorrower.OpenAttempts, borrow.OpenAttempts);
        Assert.Equal(Enumerable.Repeat(ClipboardBorrower.OpenRetryDelayMs, ClipboardBorrower.OpenAttempts - 1), sleeps);
        Assert.Equal(sequence, clipboard.Sequence);
        Assert.Equal(Previous, clipboard.Text);
    }

    [Fact]
    public void A_restore_that_cannot_open_fails_after_bounded_retries_and_changes_nothing()
    {
        var (clipboard, borrower, sleeps) = Create();
        clipboard.SeedText(Previous);
        var lease = borrower.TryBorrow(Dictation).Lease!;
        int before = clipboard.OpenAttempts;
        uint sequence = clipboard.Sequence;
        clipboard.OpenAttemptSucceeds = _ => false;

        var restore = borrower.Restore(lease);

        Assert.Equal(ClipboardRestoreOutcome.Failed, restore);
        Assert.Equal(ClipboardBorrower.OpenAttempts, clipboard.OpenAttempts - before);
        Assert.Equal(ClipboardBorrower.OpenAttempts - 1, sleeps.Count);
        Assert.Equal(sequence, clipboard.Sequence);
        Assert.Equal(Dictation, clipboard.Text);
    }

    [Fact]
    public void Non_text_content_is_refused_inside_the_session_without_any_change()
    {
        var (clipboard, borrower, _) = Create();
        clipboard.SeedFormats(TextInjectionFakes.CF_DIB);
        uint sequence = clipboard.Sequence;

        var borrow = borrower.TryBorrow(Dictation);

        Assert.Equal(ClipboardBorrowStatus.NonTextContent, borrow.Status);
        Assert.Equal(ClipboardRestoreOutcome.NotApplicable, borrow.Rollback);
        Assert.Equal(new[] { "open", "close" }, clipboard.Trace);
        Assert.Equal(sequence, clipboard.Sequence);
    }

    [Fact]
    public void A_rich_copy_with_many_companions_is_refused()
    {
        var (clipboard, borrower, _) = Create();
        clipboard.SeedFormats(
            InjectionNativeMethods.CF_UNICODETEXT,
            clipboard.RegisterFormat("HTML Format"),
            clipboard.RegisterFormat("Rich Text Format"));

        Assert.Equal(ClipboardBorrowStatus.NonTextContent, borrower.TryBorrow(Dictation).Status);
    }

    [Fact]
    public void Scribes_own_markers_and_receipt_do_not_make_its_text_look_unrestorable()
    {
        var (clipboard, borrower, _) = Create();
        clipboard.SeedText(Previous);
        borrower.TryBorrow(Dictation);

        // Text, Windows' three synthesized companions, three privacy markers and the receipt.
        Assert.Equal(8, clipboard.FormatCount);
        Assert.False(borrower.HasNonTextContent());

        // A restore that never happened leaves Scribe's item behind; the next dictation still borrows.
        var next = borrower.TryBorrow("the next dictation");
        Assert.Equal(ClipboardBorrowStatus.Borrowed, next.Status);
        Assert.Equal(Dictation, next.Lease!.Previous);
    }

    [Fact]
    public void A_write_that_fails_after_emptying_puts_the_snapshot_back_before_releasing()
    {
        var (clipboard, borrower, _) = Create();
        clipboard.SeedText(Previous);
        clipboard.SetTextSucceeds = text => text != Dictation;

        var borrow = borrower.TryBorrow(Dictation);

        Assert.Equal(ClipboardBorrowStatus.WriteFailed, borrow.Status);
        Assert.Equal(ClipboardRestoreOutcome.Restored, borrow.Rollback);
        Assert.Null(borrow.Lease);
        Assert.Equal(Previous, clipboard.Text);
        Assert.Single(clipboard.Trace, call => call == "open");
        Assert.Single(clipboard.Trace, call => call == "close");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_borrow_whose_receipt_cannot_be_written_is_rolled_back_in_the_same_session(bool previousWasEmpty)
    {
        // Without a receipt a moved sequence number could never be settled while held, so no lease may
        // exist without one: the snapshot goes back before the release, and the caller types instead.
        var (clipboard, borrower, _) = Create();
        if (previousWasEmpty)
        {
            clipboard.SeedEmpty();
        }
        else
        {
            clipboard.SeedText(Previous);
        }

        uint receiptFormat = clipboard.RegisterFormat(ClipboardBorrower.ReceiptFormatName);
        clipboard.SetDataSucceeds = format => format != receiptFormat;

        var borrow = borrower.TryBorrow(Dictation);

        Assert.Equal(ClipboardBorrowStatus.ReceiptFailed, borrow.Status);
        Assert.Null(borrow.Lease);
        Assert.Equal(ClipboardRestoreOutcome.Restored, borrow.Rollback);
        Assert.Equal(previousWasEmpty ? null : Previous, clipboard.Text);
        Assert.False(clipboard.Has(ClipboardBorrower.ReceiptFormatName));

        var markers = ClipboardBorrower.PrivacyMarkerFormats.Select(name => "set-data:" + name).ToArray();
        var snapshot = previousWasEmpty ? [] : new[] { "read-text" };
        var write = new[] { "empty", "set-text" }.Concat(markers).Append("set-data:" + ClipboardBorrower.ReceiptFormatName);
        var rollback = previousWasEmpty ? ["empty"] : new[] { "empty", "set-text" }.Concat(markers);
        Assert.Equal(
            new[] { "open" }.Concat(snapshot).Concat(write).Concat(rollback).Append("close"),
            clipboard.Trace);
    }

    [Fact]
    public void A_receipt_rollback_that_cannot_empty_is_reported_as_failed_and_still_leases_nothing()
    {
        var (clipboard, borrower, _) = Create();
        clipboard.SeedText(Previous);
        uint receiptFormat = clipboard.RegisterFormat(ClipboardBorrower.ReceiptFormatName);
        clipboard.SetDataSucceeds = format =>
        {
            if (format != receiptFormat)
            {
                return true;
            }

            clipboard.EmptyFails = true;
            return false;
        };

        var borrow = borrower.TryBorrow(Dictation);

        Assert.Equal(ClipboardBorrowStatus.ReceiptFailed, borrow.Status);
        Assert.Null(borrow.Lease);
        Assert.Equal(ClipboardRestoreOutcome.Failed, borrow.Rollback);
        Assert.False(clipboard.IsHeldByScribe);
    }

    [Fact]
    public void A_failed_empty_changes_nothing()
    {
        var (clipboard, borrower, _) = Create();
        clipboard.SeedText(Previous);
        clipboard.EmptyFails = true;

        var borrow = borrower.TryBorrow(Dictation);

        Assert.Equal(ClipboardBorrowStatus.WriteFailed, borrow.Status);
        Assert.Equal(ClipboardRestoreOutcome.NotApplicable, borrow.Rollback);
        Assert.DoesNotContain("set-text", clipboard.Trace);
        Assert.Equal(Previous, clipboard.Text);
    }

    [Fact]
    public void Unreadable_text_is_refused_without_emptying()
    {
        var (clipboard, borrower, _) = Create();
        clipboard.SeedText(Previous);
        clipboard.ReadTextFails = true;

        var borrow = borrower.TryBorrow(Dictation);

        Assert.Equal(ClipboardBorrowStatus.SnapshotUnreadable, borrow.Status);
        Assert.DoesNotContain("empty", clipboard.Trace);
        Assert.Equal(Previous, clipboard.Text);
    }

    [Fact]
    public void Confirming_an_untouched_write_needs_no_second_open()
    {
        var (clipboard, borrower, _) = Create();
        clipboard.SeedText(Previous);
        var lease = borrower.TryBorrow(Dictation).Lease!;
        int opens = clipboard.OpenAttempts;

        Assert.Equal(ClipboardCheck.Ours, borrower.Confirm(lease, out bool opened));
        Assert.False(opened);
        Assert.Equal(opens, clipboard.OpenAttempts);
    }

    [Fact]
    public void Confirm_settles_a_number_moved_only_by_the_release_through_the_receipt()
    {
        // A number read after the release is never proof, so a move made by CloseClipboard alone costs
        // one open, in which the receipt and the exact text show the write is still Scribe's.
        var (clipboard, borrower, _) = Create();
        clipboard.CloseMovesSequence = true;
        clipboard.SeedText(Previous);
        var lease = borrower.TryBorrow(Dictation).Lease!;
        Assert.True(lease.CloseMovedSequence);
        int opens = clipboard.OpenAttempts;
        int traceStart = clipboard.Trace.Count;

        Assert.Equal(ClipboardCheck.Ours, borrower.Confirm(lease, out bool opened));
        Assert.True(opened);
        Assert.Equal(opens + 1, clipboard.OpenAttempts);
        Assert.Equal(
            new[] { "open", "read-data:" + ClipboardBorrower.ReceiptFormatName, "read-text", "close" },
            clipboard.Trace.Skip(traceStart));
        Assert.Equal(clipboard.Sequence, lease.Sequence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Confirm_catches_a_copy_made_before_the_paste(bool closeMovesSequence)
    {
        var (clipboard, borrower, _) = Create();
        clipboard.CloseMovesSequence = closeMovesSequence;
        clipboard.SeedText(Previous);
        var lease = borrower.TryBorrow(Dictation).Lease!;
        clipboard.OtherAppCopies("copied during the settle");
        int traceStart = clipboard.Trace.Count;

        Assert.Equal(ClipboardCheck.Superseded, borrower.Confirm(lease, out bool opened));
        Assert.True(opened);
        Assert.Equal("copied during the settle", clipboard.Text);
        Assert.DoesNotContain(clipboard.Trace.Skip(traceStart), call => call is "empty" or "set-text");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_copy_landing_between_the_release_and_the_next_sequence_read_is_never_confirmed(bool closeMovesSequence)
    {
        // OpenClipboard keeps other writers out only while the clipboard is held, so a number read after
        // the borrow releases it can already belong to another application's copy. Accepting that
        // number would authorize a Ctrl+V that pastes the other application's text.
        var (clipboard, borrower, _) = Create();
        clipboard.CloseMovesSequence = closeMovesSequence;
        clipboard.SeedText(Previous);
        clipboard.AfterClose = close =>
        {
            if (close == 1)
            {
                clipboard.OtherAppCopies("newer text from another app");
            }
        };

        var lease = borrower.TryBorrow(Dictation).Lease!;
        int traceStart = clipboard.Trace.Count;

        Assert.Equal(ClipboardCheck.Superseded, borrower.Confirm(lease, out bool opened));
        Assert.True(opened);
        Assert.Equal(ClipboardRestoreOutcome.Superseded, borrower.Restore(lease));
        Assert.Equal("newer text from another app", clipboard.Text);
        Assert.DoesNotContain(clipboard.Trace.Skip(traceStart), call => call is "empty" or "set-text");
    }

    [Fact]
    public void Confirm_proves_identity_under_the_lock_when_the_number_moved_and_rebaselines()
    {
        var (clipboard, borrower, _) = Create();
        clipboard.CloseMovesSequence = true;
        clipboard.SynthesizedReadMovesSequence = true;
        clipboard.SeedText(Previous);
        var lease = borrower.TryBorrow(Dictation).Lease!;

        // A monitor's read moves the number again, so only the receipt can tell.
        clipboard.MonitorReads();
        Assert.NotEqual(clipboard.Sequence, lease.Sequence);

        Assert.Equal(ClipboardCheck.Ours, borrower.Confirm(lease, out bool opened));
        Assert.True(opened);
        Assert.Equal(clipboard.Sequence, lease.Sequence);

        // The rebaselined number lets the restore prove identity without reading the receipt again.
        int traceStart = clipboard.Trace.Count;
        Assert.Equal(ClipboardRestoreOutcome.Restored, borrower.Restore(lease));
        Assert.DoesNotContain(clipboard.Trace.Skip(traceStart), call => call.StartsWith("read-data", StringComparison.Ordinal));
    }

    [Fact]
    public void Confirm_reports_busy_rather_than_guessing()
    {
        var (clipboard, borrower, _) = Create();
        clipboard.SynthesizedReadMovesSequence = true;
        clipboard.SeedText(Previous);
        var lease = borrower.TryBorrow(Dictation).Lease!;
        clipboard.MonitorReads();
        clipboard.OpenAttemptSucceeds = _ => false;

        Assert.Equal(ClipboardCheck.Busy, borrower.Confirm(lease, out bool opened));
        Assert.True(opened);
        Assert.Equal(Dictation, clipboard.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_restore_reads_decides_and_rewrites_in_one_uninterrupted_session(bool numberMoved)
    {
        // Pins the single-session guarantee: a close and reopen anywhere between the ownership check and
        // the EmptyClipboard would let another application's copy land in between and be overwritten.
        var (clipboard, borrower, _) = Create();
        clipboard.CloseMovesSequence = numberMoved;
        clipboard.SeedText(Previous);
        var lease = borrower.TryBorrow(Dictation).Lease!;
        int traceStart = clipboard.Trace.Count;

        Assert.Equal(ClipboardRestoreOutcome.Restored, borrower.Restore(lease));

        var markers = ClipboardBorrower.PrivacyMarkerFormats.Select(name => "set-data:" + name);
        var proof = numberMoved
            ? new[] { "read-data:" + ClipboardBorrower.ReceiptFormatName, "read-text" }
            : [];
        Assert.Equal(
            new[] { "open" }.Concat(proof).Concat(["empty", "set-text"]).Concat(markers).Append("close"),
            clipboard.Trace.Skip(traceStart));
    }

    [Fact]
    public void A_lease_never_prints_the_text_it_holds()
    {
        var (clipboard, borrower, _) = Create();
        clipboard.SeedText(Previous);
        var borrow = borrower.TryBorrow(Dictation);

        Assert.DoesNotContain(Previous, borrow.Lease!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Dictation, borrow.Lease.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Previous, borrow.ToString(), StringComparison.Ordinal);
    }
}
