using Scribe.Core.TextInjection;
using Xunit;
using static Scribe.Core.Tests.TextInjectionFakes;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.Tests;

/// <summary>
/// The whole-clipboard borrow a formatting paste asks for: a screenshot, formatted text or copied files on the
/// clipboard are kept and put back instead of turning the paste into typing, which in a Markdown app doubles every list
/// marker. What cannot be put back exactly is refused inside the session, untouched, so the caller types as before.
/// </summary>
public sealed class WholeClipboardBorrowTests
{
    private const string Dictation = "Steps:\r\n1. One\r\n2. Two";

    private static readonly byte[] Bitmap = [40, 0, 0, 0, 1, 2, 3, 4, 5, 6];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 9, 9, 9];
    private static readonly byte[] Html = "Version:0.9\r\n<b>formatted</b>"u8.ToArray();

    private static (Clipboard Clipboard, ClipboardBorrower Borrower) Create(long maxKeptBytes = ClipboardBorrower.MaxKeptBytes)
    {
        var clipboard = new Clipboard();
        return (clipboard, new ClipboardBorrower(clipboard, _ => { }, maxKeptBytes));
    }

    [Fact]
    public void A_screenshot_is_kept_through_the_paste_and_put_back_exactly()
    {
        var (clipboard, borrower) = Create();
        uint png = clipboard.RegisterFormat("PNG");
        clipboard.SeedContent((CF_DIBV5, Bitmap), (png, Png), (CF_BITMAP, [7]));

        var borrow = borrower.TryBorrow(Dictation, keepWholeClipboard: true);

        Assert.Equal(ClipboardBorrowStatus.Borrowed, borrow.Status);
        var kept = borrow.Lease!.Kept!;
        Assert.Equal([CF_DIBV5, png], kept.Formats.Select(f => f.Format));
        Assert.Equal(Bitmap.Length + Png.Length, kept.Bytes);
        Assert.Null(borrow.Lease.Previous);
        Assert.Equal(Dictation, clipboard.TargetReads());

        Assert.Equal(ClipboardRestoreOutcome.Restored, borrower.Restore(borrow.Lease));
        Assert.Equal(Bitmap, clipboard.DataOf(CF_DIBV5));
        Assert.Equal(Png, clipboard.DataOf(png));
        Assert.Null(clipboard.Text);
        Assert.Null(clipboard.DataOf(CF_BITMAP));
        Assert.False(clipboard.Has(ClipboardBorrower.ReceiptFormatName));
        Assert.All(ClipboardBorrower.PrivacyMarkerFormats, name => Assert.True(clipboard.Has(name)));
    }

    [Fact]
    public void Formatted_text_keeps_the_format_its_application_placed_and_its_companions()
    {
        var (clipboard, borrower) = Create();
        uint html = clipboard.RegisterFormat("HTML Format");
        uint source = clipboard.RegisterFormat("Chromium internal source URL");
        byte[] url = "https://example.test/"u8.ToArray();
        clipboard.SeedContent((CF_UNICODETEXT, Clipboard.Encode("formatted")), (html, Html), (source, url));

        var lease = borrower.TryBorrow(Dictation, keepWholeClipboard: true).Lease!;

        // Windows lists the ANSI and OEM forms after the Unicode text it makes them from; they come back with it.
        Assert.Equal([CF_UNICODETEXT, html, source, CF_LOCALE], lease.Kept!.Formats.Select(f => f.Format));
        Assert.DoesNotContain("copy:" + CF_TEXT, clipboard.Trace);
        Assert.DoesNotContain("copy:" + CF_OEMTEXT, clipboard.Trace);

        Assert.Equal(ClipboardRestoreOutcome.Restored, borrower.Restore(lease));
        Assert.Equal("formatted", clipboard.Text);
        Assert.Equal(Html, clipboard.DataOf(html));
        Assert.Equal(url, clipboard.DataOf(source));
    }

    [Fact]
    public void Copied_files_are_kept_like_any_other_block()
    {
        var (clipboard, borrower) = Create();
        uint effect = clipboard.RegisterFormat("Preferred DropEffect");
        byte[] files = [20, 0, 0, 0, 1, 0, 0, 0];
        clipboard.SeedContent((CF_HDROP, files), (effect, [1, 0, 0, 0]));

        var lease = borrower.TryBorrow(Dictation, keepWholeClipboard: true).Lease!;
        Assert.Equal(ClipboardRestoreOutcome.Restored, borrower.Restore(lease));

        Assert.Equal(files, clipboard.DataOf(CF_HDROP));
        Assert.Equal([1, 0, 0, 0], clipboard.DataOf(effect));
    }

    [Fact]
    public void Only_asked_for_does_the_borrow_keep_more_than_text()
    {
        var (clipboard, borrower) = Create();
        clipboard.SeedContent((CF_DIB, Bitmap));

        var borrow = borrower.TryBorrow(Dictation);

        Assert.Equal(ClipboardBorrowStatus.NonTextContent, borrow.Status);
        Assert.DoesNotContain("list-formats", clipboard.Trace);
        Assert.Equal(Bitmap, clipboard.DataOf(CF_DIB));
    }

    [Fact]
    public void Plain_text_and_an_empty_clipboard_take_the_text_borrow_as_before()
    {
        var (clipboard, borrower) = Create();
        clipboard.SeedText("plain");
        var text = borrower.TryBorrow(Dictation, keepWholeClipboard: true).Lease!;
        Assert.Null(text.Kept);
        Assert.Equal("plain", text.Previous);
        Assert.Equal(ClipboardRestoreOutcome.Restored, borrower.Restore(text));
        Assert.Equal("plain", clipboard.Text);

        clipboard.SeedEmpty();
        var empty = borrower.TryBorrow(Dictation, keepWholeClipboard: true).Lease!;
        Assert.True(empty.WasEmpty);
        Assert.Null(empty.Kept);
        Assert.DoesNotContain("list-formats", clipboard.Trace);
    }

    [Theory]
    [InlineData(0x0080u)] // CF_OWNERDISPLAY: only its owner can draw it
    [InlineData(0x0082u)] // CF_DSPBITMAP
    [InlineData(0x008Eu)] // CF_DSPENHMETAFILE
    [InlineData(0x0200u)] // CF_PRIVATEFIRST: a handle that can be anything
    [InlineData(0x02FFu)] // CF_PRIVATELAST
    public void Content_that_cannot_be_put_back_exactly_is_left_untouched(uint format)
    {
        var (clipboard, borrower) = Create();
        clipboard.SeedContent((CF_DIB, Bitmap), (format, [1]));
        var before = clipboard.Content;

        var borrow = borrower.TryBorrow(Dictation, keepWholeClipboard: true);

        AssertRefusedUntouched(clipboard, borrow, before);
    }

    [Fact]
    public void A_bitmap_handle_with_no_device_independent_bitmap_is_left_untouched()
    {
        var (clipboard, borrower) = Create();
        clipboard.SeedContent((CF_BITMAP, [7]));
        var before = clipboard.Content;

        AssertRefusedUntouched(clipboard, borrower.TryBorrow(Dictation, keepWholeClipboard: true), before);
    }

    [Fact]
    public void An_enhanced_metafile_is_kept_as_its_bits_and_its_picture_form_comes_back_with_it()
    {
        var (clipboard, borrower) = Create();
        byte[] metafile = [1, 0, 0, 0, 108, 0, 0, 0];
        clipboard.SeedContent((CF_ENHMETAFILE, metafile), (CF_METAFILEPICT, [5]));

        var lease = borrower.TryBorrow(Dictation, keepWholeClipboard: true).Lease!;

        Assert.Equal([CF_ENHMETAFILE], lease.Kept!.Formats.Select(f => f.Format));
        Assert.Contains("copy-emf", clipboard.Trace);
        Assert.Equal(ClipboardRestoreOutcome.Restored, borrower.Restore(lease));
        Assert.Contains("set-emf", clipboard.Trace);
        Assert.Equal(metafile, clipboard.DataOf(CF_ENHMETAFILE));
    }

    [Fact]
    public void A_picture_metafile_the_application_placed_itself_is_left_untouched()
    {
        var (clipboard, borrower) = Create();
        clipboard.SeedContent((CF_METAFILEPICT, [5]), (CF_ENHMETAFILE, [1, 0, 0, 0]));
        var before = clipboard.Content;

        AssertRefusedUntouched(clipboard, borrower.TryBorrow(Dictation, keepWholeClipboard: true), before);
    }

    [Fact]
    public void An_owner_that_does_not_answer_is_never_asked_for_its_data()
    {
        var (clipboard, borrower) = Create();
        clipboard.OwnerResponding = false;
        clipboard.SeedContent((CF_DIB, Bitmap));
        var before = clipboard.Content;

        var borrow = borrower.TryBorrow(Dictation, keepWholeClipboard: true);

        AssertRefusedUntouched(clipboard, borrow, before);
        Assert.Contains("owner-responds", clipboard.Trace);
        Assert.DoesNotContain(clipboard.Trace, call => call.StartsWith("copy", StringComparison.Ordinal));
    }

    [Fact]
    public void More_than_the_cap_is_left_untouched()
    {
        var (clipboard, borrower) = Create(maxKeptBytes: Bitmap.Length + Png.Length - 1);
        uint png = clipboard.RegisterFormat("PNG");
        clipboard.SeedContent((CF_DIB, Bitmap), (png, Png));
        var before = clipboard.Content;

        AssertRefusedUntouched(clipboard, borrower.TryBorrow(Dictation, keepWholeClipboard: true), before);
    }

    [Fact]
    public void Exactly_the_cap_is_kept()
    {
        var (clipboard, borrower) = Create(maxKeptBytes: Bitmap.Length + Png.Length);
        uint png = clipboard.RegisterFormat("PNG");
        clipboard.SeedContent((CF_DIB, Bitmap), (png, Png));

        Assert.Equal(ClipboardBorrowStatus.Borrowed, borrower.TryBorrow(Dictation, keepWholeClipboard: true).Status);
    }

    [Fact]
    public void A_format_that_cannot_be_read_or_a_list_that_stops_short_is_left_untouched()
    {
        var (clipboard, borrower) = Create();
        uint png = clipboard.RegisterFormat("PNG");
        clipboard.SeedContent((CF_DIB, Bitmap), (png, Png));
        var before = clipboard.Content;
        clipboard.CopySucceeds = format => format != png;

        AssertRefusedUntouched(clipboard, borrower.TryBorrow(Dictation, keepWholeClipboard: true), before);

        clipboard.CopySucceeds = null;
        clipboard.ListFormatsFails = true;
        AssertRefusedUntouched(clipboard, borrower.TryBorrow(Dictation, keepWholeClipboard: true), before);
    }

    [Fact]
    public void Scribes_own_markers_and_receipt_are_written_again_not_copied()
    {
        var (clipboard, borrower) = Create();
        uint receipt = clipboard.RegisterFormat(ClipboardBorrower.ReceiptFormatName);
        uint marker = clipboard.RegisterFormat(ClipboardBorrower.PrivacyMarkerFormats[0]);
        clipboard.SeedContent((CF_DIB, Bitmap), (marker, [0, 0, 0, 0]), (receipt, new byte[ClipboardBorrower.ReceiptBytes]));

        var lease = borrower.TryBorrow(Dictation, keepWholeClipboard: true).Lease!;

        Assert.Equal([CF_DIB], lease.Kept!.Formats.Select(f => f.Format));
    }

    [Fact]
    public void Oles_reference_to_the_application_that_copied_is_left_out_and_the_rendered_formats_kept()
    {
        var (clipboard, borrower) = Create();
        uint rtf = clipboard.RegisterFormat("Rich Text Format");
        uint dataObject = clipboard.RegisterFormat("DataObject");
        uint olePrivate = clipboard.RegisterFormat("Ole Private Data");
        clipboard.SeedContent((dataObject, [9, 9]), (rtf, "{\\rtf1 x}"u8.ToArray()), (olePrivate, [8]));

        var lease = borrower.TryBorrow(Dictation, keepWholeClipboard: true).Lease!;

        Assert.Equal([rtf], lease.Kept!.Formats.Select(f => f.Format));
        Assert.Equal(ClipboardRestoreOutcome.Restored, borrower.Restore(lease));
        Assert.False(clipboard.IsFormatAvailable(dataObject));
        Assert.False(clipboard.IsFormatAvailable(olePrivate));
        Assert.True(clipboard.IsFormatAvailable(rtf));
    }

    [Fact]
    public void A_copy_made_during_the_paste_is_never_overwritten_by_the_kept_screenshot()
    {
        var (clipboard, borrower) = Create();
        clipboard.SeedContent((CF_DIB, Bitmap));
        var lease = borrower.TryBorrow(Dictation, keepWholeClipboard: true).Lease!;

        clipboard.OtherAppCopies("newer copy");

        Assert.Equal(ClipboardRestoreOutcome.Superseded, borrower.Restore(lease));
        Assert.Equal("newer copy", clipboard.Text);
        Assert.Null(clipboard.DataOf(CF_DIB));
    }

    [Fact]
    public void A_text_write_that_fails_after_emptying_puts_every_kept_format_back_in_the_same_session()
    {
        var (clipboard, borrower) = Create();
        uint png = clipboard.RegisterFormat("PNG");
        clipboard.SeedContent((CF_DIBV5, Bitmap), (png, Png));
        clipboard.SetTextSucceeds = _ => false;

        var borrow = borrower.TryBorrow(Dictation, keepWholeClipboard: true);

        Assert.Equal(ClipboardBorrowStatus.WriteFailed, borrow.Status);
        Assert.Equal(ClipboardRestoreOutcome.Restored, borrow.Rollback);
        Assert.Equal(Bitmap, clipboard.DataOf(CF_DIBV5));
        Assert.Equal(Png, clipboard.DataOf(png));
        Assert.Equal(1, clipboard.Closes);
    }

    [Fact]
    public void A_kept_format_that_cannot_be_written_back_fails_the_restore_but_the_rest_still_return()
    {
        var (clipboard, borrower) = Create();
        uint png = clipboard.RegisterFormat("PNG");
        clipboard.SeedContent((CF_DIBV5, Bitmap), (png, Png));
        var lease = borrower.TryBorrow(Dictation, keepWholeClipboard: true).Lease!;
        clipboard.RestoreWriteSucceeds = format => format != CF_DIBV5;

        Assert.Equal(ClipboardRestoreOutcome.Failed, borrower.Restore(lease));
        Assert.Null(clipboard.DataOf(CF_DIBV5));
        Assert.Equal(Png, clipboard.DataOf(png));
    }

    [Fact]
    public void The_whole_clipboard_is_read_decided_and_replaced_in_one_open_session()
    {
        var (clipboard, borrower) = Create();
        clipboard.SeedContent((CF_DIB, Bitmap));

        borrower.TryBorrow(Dictation, keepWholeClipboard: true);

        Assert.Equal(
            new[]
            {
                "open",
                "owner-responds",
                "list-formats",
                "copy:8",
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

    private static void AssertRefusedUntouched(
        Clipboard clipboard, ClipboardBorrowResult borrow, IReadOnlyList<(uint Format, byte[] Data)> before)
    {
        Assert.Equal(ClipboardBorrowStatus.NonTextContent, borrow.Status);
        Assert.Null(borrow.Lease);
        Assert.DoesNotContain("empty", clipboard.Trace);
        Assert.Equal(before.Select(f => f.Format), clipboard.Content.Select(f => f.Format));
        Assert.Equal(before.Select(f => f.Data), clipboard.Content.Select(f => f.Data));
    }
}
