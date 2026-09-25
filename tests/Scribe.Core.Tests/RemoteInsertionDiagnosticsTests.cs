using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.TextInjection;

namespace Scribe.Core.Tests;

/// <summary>
/// What the log says about an insertion into a Remote Desktop or virtual machine client, shapes only (AGENTS.md, "Logging
/// mandate"): whether the target was one, how typing was paced (the batch size and the number of SendInput calls), and
/// how many line breaks, surrogate pairs and control characters the text held. Never the text, never a window title.
/// </summary>
public class RemoteInsertionDiagnosticsTests
{
    private static readonly nint Target = 0x4242;

    [Theory]
    [InlineData("", 0, 0, 0)]
    [InlineData("plain words only", 0, 0, 0)]
    [InlineData("one\ntwo", 1, 0, 0)]
    [InlineData("one\r\ntwo", 1, 0, 0)] // a CRLF is one line break, as typing sends it
    [InlineData("one\rtwo\r\n\r\nthree\n", 4, 0, 0)]
    [InlineData("ok \U0001F600 and \U0001F680\U0001F680", 0, 3, 0)]
    [InlineData("tab\there and bell\u0007", 0, 0, 2)]
    [InlineData("lone \uD83D surrogate", 0, 0, 0)] // an unpaired half is neither a pair nor a control character
    public void The_shape_of_the_text_is_counted_the_way_typing_sends_it(string text, int lineBreaks, int pairs, int controls) =>
        Assert.Equal(new InjectedTextShape(lineBreaks, pairs, controls), InjectedTextShape.Of(text));

    [Fact]
    public void Typing_into_a_remote_client_says_so_with_its_pace_and_the_text_s_shape()
    {
        // A distinctive length marks this test's span: listeners are process wide and other tests inject too.
        var text = "Remote pace probe 7f3a, line one.\nLine two \U0001F600 with a\ttab and more words to fill it out nicely.";
        var tags = Capture(text, () => Type(text, "msrdc"));

        Assert.True(tags[ScribeTelemetry.TagInjectRemote] is true);
        Assert.Equal(16, tags[ScribeTelemetry.TagInjectBatchUnits]);
        Assert.Equal(TypedBatches(text, "msrdc"), tags[ScribeTelemetry.TagInjectBatches]);
        Assert.Equal(1, tags[ScribeTelemetry.TagInjectLineBreaks]);
        Assert.Equal(1, tags[ScribeTelemetry.TagInjectSurrogatePairs]);
        Assert.Equal(1, tags[ScribeTelemetry.TagInjectControlCharacters]);
    }

    [Fact]
    public void Typing_into_any_other_target_says_it_is_not_remote_and_keeps_its_pace()
    {
        var text = "Local pace probe 2b9e: nothing remote about this text at all, just words and a full stop.";
        var tags = Capture(text, () => Type(text, "notepad"));

        Assert.True(tags[ScribeTelemetry.TagInjectRemote] is false);
        Assert.Equal(50, tags[ScribeTelemetry.TagInjectBatchUnits]);
        Assert.Equal(TypedBatches(text, "notepad"), tags[ScribeTelemetry.TagInjectBatches]);
        Assert.Equal(0, tags[ScribeTelemetry.TagInjectLineBreaks]);
    }

    [Fact]
    public void A_paste_says_whether_its_target_was_remote_and_carries_no_typing_pace()
    {
        var text = "Paste probe 91c4 into a remote session, delivered by Ctrl+V.";
        var clipboard = new TextInjectionFakes.Clipboard();
        clipboard.SeedText("before");
        var tags = Capture(text, () =>
        {
            var platform = new TextInjectionFakes.Platform { Foreground = Target };
            new TextInjector(NullLogger<TextInjector>.Instance, platform, clipboard)
                .Inject(text, InjectionMethod.ClipboardPaste, Target, targetProcessName: "mstsc");
        });

        Assert.True(tags[ScribeTelemetry.TagInjectRemote] is true);
        Assert.False(tags.ContainsKey(ScribeTelemetry.TagInjectBatchUnits));
        Assert.False(tags.ContainsKey(ScribeTelemetry.TagInjectBatches));
    }

    [Fact]
    public void The_trace_line_shows_every_new_tag_and_nothing_of_the_text()
    {
        var text = "Trace probe 5d11 with secret words\nand a second line.";
        var tags = Capture(text, () => Type(text, "vmconnect"));

        var line = TraceTagPolicy.FormatSpan(ScribeTelemetry.InjectActivity, tags, TimeSpan.FromMilliseconds(3));

        Assert.Contains("inject.remote=True", line, StringComparison.Ordinal);
        Assert.Contains("inject.batch_units=16", line, StringComparison.Ordinal);
        Assert.Contains("inject.batches=", line, StringComparison.Ordinal);
        Assert.Contains("inject.line_breaks=1", line, StringComparison.Ordinal);
        Assert.Contains("inject.surrogate_pairs=0", line, StringComparison.Ordinal);
        Assert.Contains("inject.control_chars=0", line, StringComparison.Ordinal);
        Assert.DoesNotContain("omitted", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", line, StringComparison.Ordinal);
        Assert.DoesNotContain("vmconnect", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_recording_start_line_says_whether_the_target_is_a_remote_client()
    {
        var controller = ReadSource("src", "Scribe.App", "Dictation", "DictationController.cs");
        var start = controller.IndexOf("recording started: trigger={Trigger}", StringComparison.Ordinal);
        Assert.True(start > 0);
        var call = controller[start..controller.IndexOf(");", start, StringComparison.Ordinal)];

        Assert.Contains("target={App} remote={Remote}", call, StringComparison.Ordinal);
        Assert.Contains("RemoteClientProcesses.IsRemoteClient(capture.TargetApp)", call, StringComparison.Ordinal);
    }

    private static int TypedBatches(string text, string target)
    {
        var platform = new TextInjectionFakes.Platform { Foreground = Target };
        new TextInjector(NullLogger<TextInjector>.Instance, platform, new TextInjectionFakes.Clipboard())
            .Inject(text, InjectionMethod.UnicodeType, Target, shiftEnterLineBreaks: true, targetProcessName: target);
        return platform.Batches.Count;
    }

    private static void Type(string text, string target) =>
        new TextInjector(
                NullLogger<TextInjector>.Instance,
                new TextInjectionFakes.Platform { Foreground = Target },
                new TextInjectionFakes.Clipboard())
            .Inject(text, InjectionMethod.UnicodeType, Target, shiftEnterLineBreaks: true, targetProcessName: target);

    // The tags of the one text.inject span whose character count is this text's.
    private static Dictionary<string, object?> Capture(string text, Action inject)
    {
        var tags = new Dictionary<string, object?>();
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
                        foreach (var tag in activity.TagObjects)
                        {
                            tags[tag.Key] = tag.Value;
                        }
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        inject();
        lock (tags)
        {
            Assert.NotEmpty(tags);
            return new Dictionary<string, object?>(tags);
        }
    }

    private static string ReadSource(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine([root.FullName, .. parts]));
    }
}
