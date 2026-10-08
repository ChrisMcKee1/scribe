using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.TextInjection;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.Tests;

public sealed class LiteralFormattingDeliveryTests
{
    private const string Source =
        "# supplied heading\r\n- existing bullet *kept*\r\n```csharp\r\n" +
        "var u = \"https://example.test/a%20b?x=1&y=2#frag\";\r\n" +
        "    Console.WriteLine(u); // \u041F \u03C2 \U0001F680 cafe\u0301\r\n```\r\n" +
        "<b>literal, not HTML</b> [given link](https://example.test/path) \u2014 \u2013";
    private const nint Target = 0x4242;

    [Theory]
    [InlineData(DictationTextFormat.Plain)]
    [InlineData(DictationTextFormat.MarkdownSource)]
    public void Both_renderers_preserve_every_supplied_code_url_marker_punctuation_and_unicode_character(DictationTextFormat format)
    {
        var settings = Settings(format, InjectionMethod.UnicodeType, false);
        var plan = DictationFormatPlan.Capture(settings, "Code");
        Assert.Same(Source, plan.Represent(Source));
        Assert.Same(string.Empty, plan.Represent(string.Empty));
        Assert.Equal(Source + " ", DictationInsertion.TextToType(Source, true));
    }

    [Theory]
    [InlineData(DictationTextFormat.Plain, InjectionMethod.UnicodeType)]
    [InlineData(DictationTextFormat.Plain, InjectionMethod.ClipboardPaste)]
    [InlineData(DictationTextFormat.MarkdownSource, InjectionMethod.UnicodeType)]
    [InlineData(DictationTextFormat.MarkdownSource, InjectionMethod.ClipboardPaste)]
    public void Representation_recorded_recovery_and_delivered_text_follow_R_and_T_without_rich_payloads(
        DictationTextFormat format, InjectionMethod method)
    {
        var rig = new DeliveryRig();
        var settings = Settings(format, method, false);
        var plan = DictationFormatPlan.Capture(settings, "Code");
        var represented = plan.Represent(Source);
        var insertion = rig.Insert(represented, plan, settings.AddSpaceAfterDictation);

        Assert.True(insertion.Injection.Succeeded);
        Assert.Equal(Source, insertion.Recorded);
        Assert.Equal(Source, rig.Recovery.Get());
        Assert.Equal(Source + " ", Assert.Single(rig.HandedToInjector));
        if (method == InjectionMethod.ClipboardPaste)
        {
            Assert.Equal(Source + " ", rig.Pasted);
            Assert.False(rig.Clipboard.Has("HTML Format"));
            Assert.False(rig.Clipboard.Has("Rich Text Format"));
        }
        else
        {
            Assert.Equal((Source + " ").Replace("\r\n", "\n", StringComparison.Ordinal), rig.Typed());
        }
    }

    [Fact]
    public void Paste_refusal_falls_back_to_typing_the_same_R_never_reformatting_or_flattening_it()
    {
        var rig = new DeliveryRig();
        rig.Clipboard.SeedFormats(TextInjectionFakes.CF_DIB);
        var plan = DictationFormatPlan.Capture(Settings(DictationTextFormat.MarkdownSource, InjectionMethod.ClipboardPaste, true), "Code");
        var represented = plan.Represent(Source);
        var insertion = rig.Insert(represented, plan);

        Assert.Equal(PasteDelivery.NonTextContent, insertion.Injection.Paste);
        Assert.True(insertion.Injection.Succeeded);
        Assert.Equal(Source, insertion.Recorded);
        Assert.Equal(Source, rig.Recovery.Get());
        Assert.Equal(Source + " ", Assert.Single(rig.HandedToInjector));
        Assert.Equal((Source + " ").Replace("\r\n", "\n", StringComparison.Ordinal), rig.Typed());
        Assert.Equal(0, rig.Platform.CtrlVChords);
    }

    [Theory]
    [InlineData("msrdc")]
    [InlineData("mstsc")]
    [InlineData("vmconnect")]
    public void Profile_paste_preferences_cannot_authorize_clipboard_delivery_to_remote_clients(string process)
    {
        var rig = new DeliveryRig();
        var settings = Settings(DictationTextFormat.MarkdownSource, InjectionMethod.ClipboardPaste, false, process);
        var plan = DictationFormatPlan.Capture(settings, process);
        var insertion = rig.Insert(Source, plan);

        Assert.True(insertion.Injection.Succeeded);
        Assert.Equal("unicode", insertion.Injection.Method);
        Assert.Equal(0, rig.Clipboard.OpenAttempts);
        Assert.Equal(0, rig.Platform.CtrlVChords);
        Assert.Equal(Source, insertion.Recorded);
        Assert.Equal((Source + " ").Replace("\r\n", "\n", StringComparison.Ordinal), rig.Typed());
        Assert.All(rig.Platform.Sleeps, delay => Assert.Equal(TextInjector.RemoteSettleMs, delay));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Profile_typed_newline_choices_reach_the_actual_injector_without_changing_recorded_source(bool shiftEnter)
    {
        var rig = new DeliveryRig();
        var plan = DictationFormatPlan.Capture(Settings(DictationTextFormat.MarkdownSource, InjectionMethod.UnicodeType, shiftEnter), "Code");
        var insertion = rig.Insert("one\ntwo\nthree", plan, addSpace: false);
        Assert.True(insertion.Injection.Succeeded);
        Assert.Equal("one\ntwo\nthree", insertion.Recorded);
        var keys = rig.Platform.Batches.SelectMany(batch => batch).Select(input => input.U.ki).ToList();
        Assert.Equal(2, keys.Count(key => key.wVk == VK_RETURN && (key.dwFlags & KEYEVENTF_KEYUP) == 0));
        Assert.Equal(shiftEnter ? 2 : 0, keys.Count(key => key.wVk == VK_SHIFT && (key.dwFlags & KEYEVENTF_KEYUP) == 0));
        Assert.Equal("one\ntwo\nthree", rig.Typed());
    }

    [Fact]
    public void Long_literal_source_survives_many_native_delivery_chunks_without_added_fences_or_rejoined_lines()
    {
        var text = string.Join("\r\n", Enumerable.Range(0, 500).Select(index =>
            $"{index}. `x_{index}` https://example.test/a/{index}?q=1 \U0001F680 \u041F"));
        var rig = new DeliveryRig();
        var plan = DictationFormatPlan.Capture(Settings(DictationTextFormat.MarkdownSource, InjectionMethod.UnicodeType, false), "Code");
        Assert.Same(text, plan.Represent(text));
        var insertion = rig.Insert(text, plan, addSpace: false);
        Assert.True(insertion.Injection.Succeeded);
        Assert.True(rig.Platform.Batches.Count > 100);
        Assert.Equal(text, insertion.Recorded);
        Assert.Equal(text, rig.Recovery.Get());
        Assert.Equal(text.Replace("\r\n", "\n", StringComparison.Ordinal), rig.Typed());
    }

    [Fact]
    public void Existing_upstream_chunk_space_joins_are_not_reconstructed_into_lost_markdown_structure()
    {
        string[] upstreamSegments = ["a paragraph", "a lost list item", "another item", "https://example.test/"];
        var alreadyJoined = string.Join(" ", upstreamSegments);
        var plan = DictationFormatPlan.Capture(Settings(DictationTextFormat.MarkdownSource, InjectionMethod.UnicodeType, false), "Code");
        Assert.Same(alreadyJoined, plan.Represent(alreadyJoined));
        Assert.DoesNotContain('\n', plan.Represent(alreadyJoined));
        Assert.DoesNotContain("- ", plan.Represent(alreadyJoined), StringComparison.Ordinal);
    }

    [Fact]
    public void Dictionary_and_snippet_output_is_the_source_even_with_plain_once_not_an_instruction_for_another_rewrite()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var dictionary = new DictionaryRepository(db);
        dictionary.AddRange([DictionaryEntry.New("dot net", ".NET")]);
        var snippets = new SnippetRepository(db);
        snippets.SaveAll([Snippet.New("insert source", Source)]);
        var processor = new TextPostProcessor(dictionary, NullLogger<TextPostProcessor>.Instance, snippets);
        var plan = DictationFormatPlan.Capture(Settings(DictationTextFormat.MarkdownSource, InjectionMethod.UnicodeType, false), "Code", plainOnce: true);
        var p = processor.Process("insert source");
        Assert.Equal(Source, p);
        Assert.Equal(Source, plan.Represent(p));
        Assert.Equal("use .NET", plan.Represent(processor.Process("use dot net")));
    }

    [Fact]
    public void Cancellation_and_focus_loss_keep_R_without_recording_transport_fallback_text()
    {
        var plan = DictationFormatPlan.Capture(Settings(DictationTextFormat.MarkdownSource, InjectionMethod.ClipboardPaste, false), "Code");
        var cancelled = new DeliveryRig();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => cancelled.Insert(Source, plan, token: cancellation.Token));
        Assert.Equal(Source, cancelled.Recovery.Get());
        Assert.Empty(cancelled.HandedToInjector);

        var lostFocus = new DeliveryRig();
        lostFocus.Platform.Foreground = 0x9999;
        var failed = lostFocus.Insert(Source, plan);
        Assert.False(failed.Injection.Succeeded);
        Assert.Equal(InjectionResult.FocusChangedError, failed.Injection.Error);
        Assert.Equal(Source, failed.Recorded);
        Assert.Equal(Source, lostFocus.Recovery.Get());
        Assert.Equal(0, lostFocus.Clipboard.OpenAttempts);
        Assert.Empty(lostFocus.Platform.Batches);
    }

    [Fact]
    public void The_off_path_matches_actual_legacy_injection_events_paste_and_recovery_not_just_source_checks()
    {
        foreach (var method in Enum.GetValues<InjectionMethod>())
        foreach (var shiftEnter in new[] { false, true })
        foreach (var target in new[] { "Code", "pwsh", "msrdc" })
        foreach (var input in new[] { "plain", "one \r\n two", "code `x` https://example.test/ \U0001F680" })
        {
            var settings = Settings(DictationTextFormat.MarkdownSource,
                method == InjectionMethod.UnicodeType ? InjectionMethod.ClipboardPaste : InjectionMethod.UnicodeType,
                !shiftEnter, target);
            settings.AppAwareFormattingEnabled = false;
            settings.InjectionMethod = method;
            settings.ShiftEnterLineBreaks = shiftEnter;
            settings.Profiles[0].NewlineHandling = null;
            var expected = AppAwareFormattingTests.LegacyApply(input, settings.NewlineHandling, target);
            var actualPlan = DictationFormatPlan.Capture(settings, target, plainOnce: true);
            var legacyPlan = new DictationFormatPlan(DictationTextFormat.Plain, settings.NewlineHandling, method,
                shiftEnter, target, DictationFormatDecision.FormattingOff);
            var legacy = new DeliveryRig();
            var actual = new DeliveryRig();
            var oldInsertion = legacy.Insert(expected, legacyPlan);
            var newInsertion = actual.Insert(actualPlan.Represent(input), actualPlan);
            Assert.Equal(oldInsertion.Recorded, newInsertion.Recorded);
            Assert.Equal(oldInsertion.Injection, newInsertion.Injection);
            Assert.Equal(legacy.HandedToInjector, actual.HandedToInjector);
            Assert.Equal(legacy.Pasted, actual.Pasted);
            Assert.Equal(legacy.Typed(), actual.Typed());
            Assert.Equal(legacy.Recovery.Get(), actual.Recovery.Get());
            Assert.Equal(legacy.Clipboard.Trace, actual.Clipboard.Trace);
            Assert.Equal(legacy.Platform.Sleeps, actual.Platform.Sleeps);
            Assert.Equal(EventShapes(legacy.Platform), EventShapes(actual.Platform));
        }
    }

    private static IEnumerable<(uint Type, ushort Key, ushort Scan, uint Flags)> EventShapes(TextInjectionFakes.Platform platform) =>
        platform.Batches.SelectMany(batch => batch).Select(input => (input.type, input.U.ki.wVk, input.U.ki.wScan, input.U.ki.dwFlags));

    private static AppSettings Settings(DictationTextFormat format, InjectionMethod method, bool shiftEnter, string process = "Code")
    {
        var settings = AppAwareFormattingTests.NewSettings();
        settings.Profiles[0].ProcessNames = [process];
        settings.Profiles[0].TextFormat = format;
        settings.Profiles[0].InjectionMethod = method;
        settings.Profiles[0].ShiftEnterLineBreaks = shiftEnter;
        return settings;
    }

    private sealed class DeliveryRig
    {
        public TextInjectionFakes.Platform Platform { get; } = new() { Foreground = Target };
        public TextInjectionFakes.Clipboard Clipboard { get; } = new();
        public LastTranscriptStore Recovery { get; } = new();
        public List<string> HandedToInjector { get; } = [];
        public string? Pasted { get; private set; }

        public DeliveryRig()
        {
            Clipboard.SeedText("previous clipboard");
            Platform.OnSleep = delay =>
            {
                if (delay == TextInjector.PasteSettleDelayMs) Pasted = Clipboard.TargetReads();
            };
        }

        public DictationInsertionResult Insert(string represented, DictationFormatPlan plan, bool addSpace = true, CancellationToken token = default)
        {
            var injector = new TextInjector(NullLogger<TextInjector>.Instance, Platform, Clipboard);
            return DictationInsertion.Insert(represented, addSpace, Recovery, typed =>
            {
                HandedToInjector.Add(typed);
                return injector.Inject(typed, plan.InjectionMethod, Target, plan.ShiftEnterLineBreaks, plan.TargetProcessName);
            }, token);
        }

        public string Typed()
        {
            var text = new StringBuilder();
            foreach (var input in Platform.Batches.SelectMany(batch => batch))
            {
                var key = input.U.ki;
                if ((key.dwFlags & KEYEVENTF_KEYUP) != 0) continue;
                if ((key.dwFlags & KEYEVENTF_UNICODE) != 0) text.Append((char)key.wScan);
                else if (key.wVk == VK_RETURN) text.Append('\n');
            }

            return text.ToString();
        }
    }
}
