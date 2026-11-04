using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.Settings;
using Scribe.Core.TextInjection;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// "Markdown with formatting": which apps get it, what the clipboard holds while the target pastes (the Markdown text
/// plus an HTML rendering of it, in Scribe's one borrow session), and that nothing else changes: the text, the receipt,
/// the restore and a typed fallback are exactly what a Markdown source paste does.
/// </summary>
public sealed class FormattedClipboardPasteTests
{
    private const string Previous = "PREVIOUS-CLIPBOARD-5be1";
    private const string Markdown = "Notes\n- First item\n- Second item";
    private const nint Target = 0x4242;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static AppSettings Settings() => new()
    {
        AppAwareFormattingEnabled = true,
        DefaultTextFormat = DictationTextFormat.Plain,
        InjectionMethod = InjectionMethod.UnicodeType,
        NewlineHandling = NewlineInjectionMode.SmartFlatten,
        ShiftEnterLineBreaks = true,
        Profiles = [],
    };

    // --- Which apps get it ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ms-teams")]
    [InlineData("MS-TEAMS.EXE")]
    [InlineData("Teams")]
    [InlineData("copilotapp")]
    [InlineData("ChatGPT")]
    public void Apps_that_read_html_but_not_pasted_markdown_get_markdown_with_formatting_pasted(string process)
    {
        var settings = Settings();

        var plan = DictationFormatPlan.Capture(settings, process);

        Assert.Equal(DictationFormatDecision.MarkdownWithFormatting, plan.Decision);
        Assert.Equal(DictationTextFormat.MarkdownWithFormatting, plan.TextFormat);
        Assert.Equal(InjectionMethod.ClipboardPaste, plan.InjectionMethod);
        Assert.True(plan.KeepsWholeClipboard);
        Assert.True(DictationFormatPlan.IsMarkdownProcess(process));
        Assert.NotNull(plan.ClipboardHtml(Markdown));

        var plainOnce = DictationFormatPlan.Capture(settings, process, plainOnce: true);
        Assert.Equal(DictationTextFormat.Plain, plainOnce.TextFormat);
        Assert.Null(plainOnce.ClipboardHtml(Markdown));

        settings.NewlineHandling = NewlineInjectionMode.AlwaysFlatten;
        var flattened = DictationFormatPlan.Capture(settings, process);
        Assert.Equal(DictationTextFormat.Plain, flattened.TextFormat);
        Assert.Null(flattened.ClipboardHtml(Markdown));
    }

    [Theory]
    [InlineData("github")]
    [InlineData("claude")]
    [InlineData("scout")]
    [InlineData("Code")]
    public void Apps_that_convert_pasted_markdown_themselves_keep_markdown_source_alone(string process)
    {
        var plan = DictationFormatPlan.Capture(Settings(), process);

        Assert.Equal(DictationTextFormat.MarkdownSource, plan.TextFormat);
        Assert.Null(plan.ClipboardHtml(Markdown));
    }

    [Fact]
    public void Formatting_off_or_another_app_adds_no_html()
    {
        var settings = Settings();
        Assert.Null(DictationFormatPlan.Capture(settings, "notepad").ClipboardHtml(Markdown));

        settings.AppAwareFormattingEnabled = false;
        var off = DictationFormatPlan.Capture(settings, "ms-teams");
        Assert.Equal(DictationTextFormat.Plain, off.TextFormat);
        Assert.Null(off.ClipboardHtml(Markdown));
    }

    [Fact]
    public void A_profile_can_choose_it_and_typing_it_types_the_markdown_with_no_html()
    {
        var settings = Settings();
        settings.Profiles =
        [
            new AppProfile
            {
                Name = "Mail",
                ProcessNames = ["olk"],
                NewlineHandling = NewlineInjectionMode.KeepNewlines,
                TextFormat = DictationTextFormat.MarkdownWithFormatting,
                InjectionMethod = InjectionMethod.ClipboardPaste,
            },
        ];

        var pasted = DictationFormatPlan.Capture(settings, "olk");
        Assert.Equal(DictationFormatDecision.MarkdownWithFormatting, pasted.Decision);
        Assert.NotNull(pasted.ClipboardHtml(Markdown));
        Assert.Equal("Markdown with formatting. Paste requested; Shift+Enter for typed line breaks.", DictationFormattingText.Describe(pasted));

        settings.Profiles[0].InjectionMethod = InjectionMethod.UnicodeType;
        var typed = DictationFormatPlan.Capture(settings, "olk");
        Assert.Equal(DictationTextFormat.MarkdownWithFormatting, typed.TextFormat);
        Assert.Null(typed.ClipboardHtml(Markdown));
    }

    [Fact]
    public void It_needs_line_breaks_like_markdown_source()
    {
        Assert.True(DictationFormatPlan.ConflictsWithNewlines(
            DictationTextFormat.MarkdownWithFormatting, NewlineInjectionMode.AlwaysFlatten, "anything"));
        Assert.False(DictationFormatPlan.ConflictsWithNewlines(
            DictationTextFormat.MarkdownWithFormatting, NewlineInjectionMode.KeepNewlines, "anything"));

        var settings = Settings();
        settings.Profiles =
        [
            new AppProfile
            {
                Name = "Shell",
                ProcessNames = ["pwsh"],
                TextFormat = DictationTextFormat.MarkdownWithFormatting,
            },
        ];
        var plan = DictationFormatPlan.Capture(settings, "pwsh");
        Assert.Equal(DictationFormatDecision.MarkdownNewlineConflict, plan.Decision);
        Assert.Null(plan.ClipboardHtml(Markdown));
    }

    [Fact]
    public void Plain_prose_pastes_exactly_as_before_with_no_html()
    {
        var plan = DictationFormatPlan.Capture(Settings(), "ms-teams");

        Assert.Null(plan.ClipboardHtml("Thanks, I'll look at it this afternoon."));
        Assert.Null(plan.ClipboardHtml("First paragraph.\n\nSecond paragraph with https://example.com."));
    }

    // --- The saved setting --------------------------------------------------------------------------------------------

    [Fact]
    public void The_format_saves_by_name_and_reads_back()
    {
        var settings = new AppSettings
        {
            DefaultTextFormat = DictationTextFormat.MarkdownWithFormatting,
            Profiles = [new AppProfile { Name = "Chat", ProcessNames = ["x"], TextFormat = DictationTextFormat.MarkdownWithFormatting }],
        };

        var json = JsonSerializer.Serialize(settings, Json);
        Assert.Contains("\"defaultTextFormat\":\"MarkdownWithFormatting\"", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize<AppSettings>(json, Json)!;
        Assert.Equal(DictationTextFormat.MarkdownWithFormatting, back.DefaultTextFormat);
        Assert.Equal(DictationTextFormat.MarkdownWithFormatting, back.Profiles[0].TextFormat);
    }

    [Theory]
    [InlineData("\"markdownwithformatting\"", DictationTextFormat.MarkdownWithFormatting)]
    [InlineData("\"MarkdownSource\"", DictationTextFormat.MarkdownSource)]
    [InlineData("\"RichSomethingNewer\"", DictationTextFormat.Plain)]
    [InlineData("2", DictationTextFormat.Plain)]
    public void A_name_this_build_does_not_know_reads_as_plain(string token, DictationTextFormat expected)
    {
        var back = JsonSerializer.Deserialize<AppSettings>("{\"defaultTextFormat\":" + token + "}", Json)!;

        Assert.Equal(expected, back.DefaultTextFormat);
    }

    // --- The borrow -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_html_is_written_in_the_borrow_session_after_the_text_and_before_the_receipt()
    {
        var clipboard = new TextInjectionFakes.Clipboard();
        clipboard.SeedText(Previous);
        var borrower = new ClipboardBorrower(clipboard, _ => { });
        var html = ClipboardHtmlFormat.Encode("<ul><li>First item</li></ul>")!;

        var borrow = borrower.TryBorrow(Markdown, keepWholeClipboard: true, html);

        Assert.Equal(ClipboardBorrowStatus.Borrowed, borrow.Status);
        Assert.True(borrow.Lease!.HtmlPlaced);
        Assert.Equal(Markdown, clipboard.Text);
        Assert.Equal(html, clipboard.DataOf(ClipboardHtmlFormat.FormatName));
        int text = clipboard.Trace.IndexOf("set-text");
        int htmlWrite = clipboard.Trace.IndexOf("set-data:" + ClipboardHtmlFormat.FormatName);
        int receipt = clipboard.Trace.IndexOf("set-data:" + ClipboardBorrower.ReceiptFormatName);
        Assert.True(text < htmlWrite && htmlWrite < receipt);
        Assert.Equal(1, clipboard.Trace.Count(entry => entry == "open"));
        Assert.Equal("close", clipboard.Trace[^1]);

        Assert.Equal(ClipboardRestoreOutcome.Restored, borrower.Restore(borrow.Lease));
        Assert.Equal(Previous, clipboard.Text);
        Assert.False(clipboard.Has(ClipboardHtmlFormat.FormatName));
    }

    [Fact]
    public void A_failed_html_write_still_borrows_with_the_text_and_its_receipt()
    {
        var clipboard = new TextInjectionFakes.Clipboard();
        clipboard.SeedText(Previous);
        uint htmlFormat = clipboard.RegisterFormat(ClipboardHtmlFormat.FormatName);
        clipboard.SetDataSucceeds = format => format != htmlFormat;
        var borrower = new ClipboardBorrower(clipboard, _ => { });

        var borrow = borrower.TryBorrow(Markdown, keepWholeClipboard: true, ClipboardHtmlFormat.Encode("<p>x</p>"));

        Assert.Equal(ClipboardBorrowStatus.Borrowed, borrow.Status);
        Assert.False(borrow.Lease!.HtmlPlaced);
        Assert.Equal(Markdown, clipboard.Text);
        Assert.True(clipboard.Has(ClipboardBorrower.ReceiptFormatName));
        Assert.Equal(ClipboardCheck.Ours, borrower.Confirm(borrow.Lease, out _));
    }

    [Fact]
    public void A_borrow_without_html_writes_no_html()
    {
        var clipboard = new TextInjectionFakes.Clipboard();
        var borrow = new ClipboardBorrower(clipboard, _ => { }).TryBorrow(Markdown, keepWholeClipboard: true);

        Assert.False(borrow.Lease!.HtmlPlaced);
        Assert.DoesNotContain("set-data:" + ClipboardHtmlFormat.FormatName, clipboard.Trace);
    }

    // --- The paste ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_target_reads_the_markdown_and_the_html_together_and_the_user_s_clipboard_comes_back()
    {
        var clipboard = new TextInjectionFakes.Clipboard();
        clipboard.SeedText(Previous);
        var platform = new TextInjectionFakes.Platform { Foreground = Target };
        string? pastedText = null;
        byte[]? pastedHtml = null;
        platform.OnSleep = ms =>
        {
            if (ms == TextInjector.PasteSettleDelayMs)
            {
                pastedText = clipboard.TargetReads();
                pastedHtml = clipboard.DataOf(ClipboardHtmlFormat.FormatName);
            }
        };
        var log = new TextInjectionFakes.CapturingLogger<TextInjector>();
        var fragment = MarkdownClipboardHtml.TryRender(Markdown)!;

        var result = new TextInjector(log, platform, clipboard, PerfFlags.None)
            .Inject(Markdown, InjectionMethod.ClipboardPaste, Target, keepWholeClipboard: true, htmlFragment: fragment);

        Assert.True(result.Succeeded);
        Assert.Equal(PasteDelivery.ChordInserted, result.Paste);
        Assert.Equal(Markdown, pastedText);
        Assert.NotNull(pastedHtml);
        Assert.Contains(fragment, Encoding.UTF8.GetString(pastedHtml!), StringComparison.Ordinal);
        Assert.Equal(Previous, clipboard.Text);
        Assert.False(clipboard.Has(ClipboardHtmlFormat.FormatName));
        Assert.Contains(log.Entries, message =>
            message.Contains("formatted copy asked: True, placed: True", StringComparison.Ordinal) &&
            !message.Contains(fragment, StringComparison.Ordinal));
    }

    [Fact]
    public void A_refused_paste_types_the_markdown_text_never_the_html()
    {
        var clipboard = new TextInjectionFakes.Clipboard();
        var platform = new TextInjectionFakes.Platform { Foreground = Target };
        clipboard.OpenAttemptSucceeds = _ => false;
        var typed = new StringBuilder();
        platform.Deliver = (_, inputs) =>
        {
            foreach (var input in inputs)
            {
                var key = input.U.ki;
                if ((key.dwFlags & InjectionNativeMethods.KEYEVENTF_UNICODE) != 0 &&
                    (key.dwFlags & InjectionNativeMethods.KEYEVENTF_KEYUP) == 0)
                {
                    typed.Append((char)key.wScan);
                }
            }

            return (uint)inputs.Length;
        };

        var result = new TextInjector(new TextInjectionFakes.CapturingLogger<TextInjector>(), platform, clipboard, PerfFlags.None)
            .Inject("- a **b**", InjectionMethod.ClipboardPaste, Target, keepWholeClipboard: true,
                htmlFragment: "<ul><li>a <strong>b</strong></li></ul>");

        Assert.True(result.Succeeded);
        Assert.Equal(PasteDelivery.ClipboardBusy, result.Paste);
        Assert.Equal("- a **b**", typed.ToString());
    }

    // --- Excel stays on one line ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("EXCEL", NewlineInjectionMode.SmartFlatten, true)]
    [InlineData("excel", NewlineInjectionMode.SmartFlatten, true)]
    [InlineData("EXCEL", NewlineInjectionMode.KeepNewlines, false)]
    [InlineData("WINWORD", NewlineInjectionMode.SmartFlatten, false)]
    [InlineData("POWERPNT", NewlineInjectionMode.SmartFlatten, false)]
    public void Automatic_keeps_an_excel_dictation_in_one_cell(string process, NewlineInjectionMode mode, bool flattened)
    {
        Assert.Equal(flattened, InjectionTextFormatter.ShouldFlatten(mode, process));
        Assert.Equal(flattened ? "Line one Line two" : "Line one\nLine two", InjectionTextFormatter.Apply("Line one\nLine two", mode, process));
        Assert.False(InjectionTextFormatter.IsTerminalProcess(process));
    }

    [Fact]
    public void The_documents_preset_leaves_excel_out()
    {
        var profile = ProfilePresets.Instantiate(ProfilePresets.Documents);

        Assert.DoesNotContain("EXCEL", profile.ProcessNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Excel", ProfilePresets.Documents.Description, StringComparison.Ordinal);
        Assert.Contains("WINWORD", profile.ProcessNames);
    }
}
