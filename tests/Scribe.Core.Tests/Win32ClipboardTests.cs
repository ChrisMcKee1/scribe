using Scribe.Core.TextInjection;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Exercises the production borrow and restore path against the REAL system clipboard. CI runs these on
/// the hosted x64 and ARM64 runners, where they measure what the fake-based <c>ClipboardBorrowerTests</c>
/// cannot: that the NULL-owner writes (text, privacy markers, receipt) work and that the receipt reads
/// back. Never run them on someone's interactive desktop: exclude them there with
/// <c>--filter "FullyQualifiedName!~Win32ClipboardTests"</c>, because the clipboard may hold content,
/// such as an image, that a text-only restore cannot put back. Each test puts back what it borrowed
/// through the production restore.
/// </summary>
public class Win32ClipboardTests
{
    // Deliberately carries an em dash: it proves Unicode survives the clipboard round trip.
    private const string UnicodeFixture = "Scribe café — 测试 🎤";

    [Fact]
    public void Borrow_round_trips_unicode_text_and_restore_puts_the_previous_clipboard_back()
    {
        ClipboardBorrowStatus status = default;
        bool untouchedWhenRefused = false;
        string? pasted = null;
        bool markersPresent = false;
        bool receiptReadsBack = false;
        ClipboardRestoreOutcome restore = default;
        Snapshot before = default;
        Snapshot after = default;

        RunOnSta(() =>
        {
            var native = Win32Clipboard.Instance;
            var borrower = new ClipboardBorrower(native, Thread.Sleep);
            before = Read(native);
            uint sequence = native.SequenceNumber;

            var borrow = borrower.TryBorrow(UnicodeFixture);
            status = borrow.Status;
            if (borrow.Lease is not { } lease)
            {
                // Non-text content on the developer's clipboard cannot be put back, so the borrow must
                // refuse it without touching anything, and the round trip cannot be exercised.
                untouchedWhenRefused = native.SequenceNumber == sequence;
                return;
            }

            // These registered-format writes are made after EmptyClipboard in a session opened with a
            // NULL owner, the combination the SetClipboardData documentation says fails. A borrow only gets
            // this far when its receipt was written (otherwise it ends as ReceiptFailed); asserting the
            // markers and reading the receipt back in a separate session is what turns that open question
            // into a measured answer on each CI architecture. The restore sits in a finally so a failed
            // read can never leave the fixture on the clipboard.
            try
            {
                markersPresent = ClipboardBorrower.PrivacyMarkerFormats.All(
                    name => native.IsFormatAvailable(native.RegisterFormat(name)));
                receiptReadsBack = ReadReceipt(native).AsSpan().SequenceEqual(lease.Receipt);
                pasted = Read(native).Text;
            }
            finally
            {
                restore = borrower.Restore(lease);
            }

            after = Read(native);
        });

        if (status == ClipboardBorrowStatus.NonTextContent)
        {
            Assert.True(untouchedWhenRefused);
            return;
        }

        Assert.Equal(ClipboardBorrowStatus.Borrowed, status);
        Assert.Equal(UnicodeFixture, pasted);
        Assert.True(markersPresent, "The privacy markers were not on the clipboard after the borrow.");
        Assert.True(receiptReadsBack, "The receipt did not read back byte for byte.");
        Assert.Equal(ClipboardRestoreOutcome.Restored, restore);
        Assert.Equal(before, after);
    }

    [Fact]
    public void The_receipt_alone_proves_ownership_on_the_real_clipboard()
    {
        // Nothing on a quiet machine moves the sequence number between a borrow and its restore, so the
        // lease is handed a stale number instead. That forces the restore down the receipt branch: the
        // receipt and the text are read back from the real clipboard, under the lock, before it mutates.
        ClipboardBorrowStatus status = default;
        ClipboardRestoreOutcome restore = default;
        Snapshot before = default;
        Snapshot after = default;

        RunOnSta(() =>
        {
            var native = Win32Clipboard.Instance;
            var borrower = new ClipboardBorrower(native, Thread.Sleep);
            before = Read(native);

            var borrow = borrower.TryBorrow(UnicodeFixture);
            status = borrow.Status;
            if (borrow.Lease is not { } lease)
            {
                return;
            }

            lease.Rebaseline(unchecked(lease.Sequence - 1));
            restore = borrower.Restore(lease);
            after = Read(native);
        });

        if (status == ClipboardBorrowStatus.NonTextContent)
        {
            return;
        }

        Assert.Equal(ClipboardBorrowStatus.Borrowed, status);
        Assert.Equal(ClipboardRestoreOutcome.Restored, restore);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Scribes_own_clipboard_text_is_not_reported_as_non_text()
    {
        // Guards the paste-preservation gate: with Scribe's text, privacy markers and receipt on the
        // clipboard, the injector must still be willing to paste. The image/files case needs real
        // non-text clipboard data, which a unit test can't stage without clobbering the developer's
        // clipboard with formats that can't be put back.
        ClipboardBorrowStatus status = default;
        bool nonText = true;
        ClipboardRestoreOutcome restore = default;

        RunOnSta(() =>
        {
            var borrower = new ClipboardBorrower(Win32Clipboard.Instance, Thread.Sleep);
            var borrow = borrower.TryBorrow("plain text");
            status = borrow.Status;
            if (borrow.Lease is { } lease)
            {
                nonText = borrower.HasNonTextContent();
                restore = borrower.Restore(lease);
            }
        });

        if (status == ClipboardBorrowStatus.NonTextContent)
        {
            return;
        }

        Assert.Equal(ClipboardBorrowStatus.Borrowed, status);
        Assert.False(nonText);
        Assert.Equal(ClipboardRestoreOutcome.Restored, restore);
    }

    private readonly record struct Snapshot(bool Empty, string? Text);

    // The test's own reads, which only observe what the borrow and the restore left: they keep asking for the clipboard
    // until a deadline of the test's own, because another program (a clipboard manager, the shell) can hold it briefly at
    // any moment, and the borrower's six attempts 15 ms apart, which these used to reuse, fail a read that a hold of about
    // 100 ms delays even when the borrow and the restore worked (review round 4 of stream TR, A6). The borrower's own
    // TryBorrow and Restore keep production's retries.
    private static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(30);
    private const int ReadRetryDelayMs = 15;

    private static byte[] ReadReceipt(IClipboardNative native)
    {
        var receipt = new byte[ClipboardBorrower.ReceiptBytes];
        uint format = native.RegisterFormat(ClipboardBorrower.ReceiptFormatName);
        return WhileOpen(native, () => format != 0 && native.TryReadData(format, receipt) ? receipt : []);
    }

    private static Snapshot Read(IClipboardNative native) =>
        WhileOpen(native, () => new Snapshot(native.FormatCount == 0, native.TryReadText(out var text) ? text : null));

    private static T WhileOpen<T>(IClipboardNative native, Func<T> read)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!native.TryOpen())
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > ReadDeadline)
            {
                throw new InvalidOperationException($"The clipboard stayed busy for {ReadDeadline.TotalSeconds:0} s.");
            }

            Thread.Sleep(ReadRetryDelayMs);
        }

        try
        {
            return read();
        }
        finally
        {
            native.Close();
        }
    }

    // Clipboard work follows the injector's convention: a joined STA thread.
    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException("Clipboard test body failed.", failure);
        }
    }
}
