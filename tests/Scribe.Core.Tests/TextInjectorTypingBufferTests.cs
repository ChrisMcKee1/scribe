using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.TextInjection;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.Tests;

/// <summary>
/// Typing writes every batch of an insertion into one buffer and hands SendInput a span of it. These pin that the events
/// are the ones the per-batch List builder made before, batch for batch, that a short send resends only its own batch,
/// that the buffer holds the largest batch there can be, and that nothing is allocated per batch.
/// </summary>
public class TextInjectorTypingBufferTests
{
    private const nint Target = 0x4242;

    private static readonly InjectionKeys UsKeys = new(
        TextInjectionFakes.Platform.UsLayout(VK_SHIFT),
        TextInjectionFakes.Platform.UsLayout(VK_RETURN),
        TextInjectionFakes.Platform.UsLayout(VK_CONTROL),
        TextInjectionFakes.Platform.UsLayout(VK_V));

    [Theory]
    [InlineData(true, "notepad")]
    [InlineData(false, "notepad")]
    [InlineData(true, "msrdc")]
    [InlineData(false, "msrdc")]
    public void Every_batch_carries_the_events_the_list_builder_made(bool shiftEnter, string target)
    {
        var pace = TypingPace.For(target);
        var index = 0;
        foreach (var text in Corpus())
        {
            var platform = new TextInjectionFakes.Platform { Foreground = Target };
            var injector = new TextInjector(NullLogger<TextInjector>.Instance, platform, new TextInjectionFakes.Clipboard());

            var result = injector.Inject(text, InjectionMethod.UnicodeType, Target, shiftEnter, target);

            var expected = ExpectedBatches(text, pace, shiftEnter);
            var context = $"text {index} ({text.Length} units)";
            Assert.True(result.Succeeded, $"{context}: {result.Error}");
            Assert.Equal(expected.Sum(batch => batch.Length), result.Total);
            Assert.True(
                expected.Count == platform.Batches.Count,
                $"{context}: {platform.Batches.Count} batches, expected {expected.Count}.");
            for (var batch = 0; batch < expected.Count; batch++)
            {
                AssertSameEvents(expected[batch], platform.Batches[batch], $"{context}, batch {batch}");
                Assert.InRange(expected[batch].Length, 1, TextInjector.MaxEventsPerBatch(pace.BatchUnits, shiftEnter));
            }

            index++;
        }
    }

    [Fact]
    public void A_short_send_of_a_later_shorter_batch_resends_only_that_batch()
    {
        // Fifty units of 'a' fill the buffer with 100 events; the ten of 'b' after them use its first 20. A send cut short
        // there must resend the rest of those 20, never the 'a' events still further along the buffer.
        var platform = new TextInjectionFakes.Platform
        {
            Foreground = Target,
            Deliver = (index, inputs) => index == 1 ? 7u : (uint)inputs.Length,
        };
        var injector = new TextInjector(NullLogger<TextInjector>.Instance, platform, new TextInjectionFakes.Clipboard());

        var result = injector.Inject(
            new string('a', 50) + new string('b', 10), InjectionMethod.UnicodeType, Target, shiftEnterLineBreaks: true, "notepad");

        Assert.True(result.Succeeded);
        Assert.Equal(120, result.Sent);
        Assert.Equal([100, 20, 13], platform.Batches.Select(batch => batch.Length));
        AssertSameEvents(platform.Batches[1][7..], platform.Batches[2], "the resent remainder");
        Assert.All(platform.Batches[2], input => Assert.Equal('b', (char)input.U.ki.wScan));
    }

    [Theory]
    [InlineData("notepad", '\n', 60, new[] { 200, 40 })]
    [InlineData("notepad", '\r', 60, new[] { 200, 40 })]
    [InlineData("msrdc", '\n', 20, new[] { 64, 16 })]
    public void A_batch_of_nothing_but_line_breaks_fills_the_buffer_exactly(
        string target, char lineBreak, int count, int[] expectedLengths)
    {
        // Every lone CR or LF is a Shift+Enter of four events, the most one code unit can make, so the first batch is the
        // largest a batch can be and the buffer holds exactly that many.
        var platform = new TextInjectionFakes.Platform { Foreground = Target };
        var injector = new TextInjector(NullLogger<TextInjector>.Instance, platform, new TextInjectionFakes.Clipboard());

        var result = injector.Inject(new string(lineBreak, count), InjectionMethod.UnicodeType, Target, shiftEnterLineBreaks: true, target);

        Assert.True(result.Succeeded);
        Assert.Equal(expectedLengths, platform.Batches.Select(batch => batch.Length));
        Assert.Equal(TextInjector.MaxEventsPerBatch(TypingPace.For(target).BatchUnits, shiftEnter: true), platform.Batches[0].Length);
        var shiftedReturn = new[] { (VK_SHIFT, 0u), (VK_RETURN, 0u), (VK_RETURN, KEYEVENTF_KEYUP), (VK_SHIFT, KEYEVENTF_KEYUP) };
        foreach (var batch in platform.Batches)
        {
            for (var i = 0; i < batch.Length; i++)
            {
                Assert.Equal(shiftedReturn[i % 4], (batch[i].U.ki.wVk, batch[i].U.ki.dwFlags));
            }
        }
    }

    // In the collection that runs alone (stream TR, item 1): no other test runs while it measures.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Theory]
        [InlineData("notepad")]
        [InlineData("msrdc")]
        public void Typing_a_dictation_allocates_its_one_buffer_and_nothing_per_batch(string target)
        {
            var platform = new QuietPlatform();
            var injector = new TextInjector(NullLogger<TextInjector>.Instance, platform, new TextInjectionFakes.Clipboard());
            var pace = TypingPace.For(target);
            var text = FourParagraphs();
            var total = TextInjector.CountKeyEvents(text, 0, text.Length, shiftEnter: true);
            var batches = ExpectedBatches(text, pace, shiftEnter: true).Count;

            // Warm the JIT for every call measured below, and the readings around the window.
            for (var warm = 0; warm < 3; warm++)
            {
                _ = injector.TypeUnicode(text, Target, shiftEnter: true, UsKeys, pace);
            }

            _ = RuntimeWork.Now().Since(RuntimeWork.Now());
            _ = BytesOfOneBuffer(1);

            // What the one buffer costs on this runtime, measured the same way: the typing below may allocate that and
            // nothing else, however many batches it sends.
            var bufferBytes = BytesOfOneBuffer(Math.Min(total, TextInjector.MaxEventsPerBatch(pace.BatchUnits, shiftEnter: true)));

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var typed = injector.TypeUnicode(text, Target, shiftEnter: true, UsKeys, pace);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            AllocationMeasurement.AssertZero(
                allocated - bufferBytes,
                during,
                $"Typing {text.Length} characters into {target}, beyond its one buffer of {bufferBytes} bytes",
                () => injector.TypeUnicode(text, Target, shiftEnter: true, UsKeys, pace));
            Assert.Equal((total, total, batches), typed);
            Assert.True(batches > 1, "The text must take more than one batch, or nothing is reused.");
        }

        private static long BytesOfOneBuffer(int length)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var buffer = new INPUT[length];
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(buffer);
            return bytes;
        }

        // Answers every call without allocating, so the measurement sees only the injector.
        private sealed class QuietPlatform : IInjectionPlatform
        {
            public nint GetForegroundWindow() => Target;

            public uint SendInput(ReadOnlySpan<INPUT> inputs) => (uint)inputs.Length;

            public bool TryInsertIntoStandardEdit(string text, nint expectedForegroundWindow) => false;

            public void Sleep(int milliseconds)
            {
            }

            public KeyScanCode ScanCodeOf(ushort virtualKey) => TextInjectionFakes.Platform.UsLayout(virtualKey);
        }
    }

    // The batches typing sends: the chunking is unchanged, and each chunk's events come from the builder below.
    private static List<INPUT[]> ExpectedBatches(string text, TypingPace pace, bool shiftEnter)
    {
        var batches = new List<INPUT[]>();
        for (var start = 0; start < text.Length;)
        {
            var count = TextInjector.ChunkLength(text, start, pace.BatchUnits, pace.PreferWordBoundary);
            batches.Add(ListBuiltChunk(text, start, count, shiftEnter));
            start += count;
        }

        return batches;
    }

    // The builder every release up to 0.5.0 used, kept here as the reference: a List of the batch's events, a line break
    // added as its own array, and the whole copied out.
    private static INPUT[] ListBuiltChunk(string text, int start, int count, bool shiftEnter)
    {
        var inputs = new List<INPUT>(count * 2);
        var end = start + count;
        for (var i = start; i < end; i++)
        {
            var ch = text[i];
            if (ch is '\r' or '\n')
            {
                inputs.AddRange(shiftEnter
                    ? new[]
                    {
                        Key(VK_SHIFT, UsKeys.Shift, up: false),
                        Key(VK_RETURN, UsKeys.Return, up: false),
                        Key(VK_RETURN, UsKeys.Return, up: true),
                        Key(VK_SHIFT, UsKeys.Shift, up: true),
                    }
                    : new[] { Key(VK_RETURN, UsKeys.Return, up: false), Key(VK_RETURN, UsKeys.Return, up: true) });
                if (ch == '\r' && i + 1 < end && text[i + 1] == '\n')
                {
                    i++;
                }

                continue;
            }

            inputs.Add(Event(0, ch, KEYEVENTF_UNICODE));
            inputs.Add(Event(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }

        return [.. inputs];
    }

    private static INPUT Key(ushort virtualKey, KeyScanCode scan, bool up) =>
        Event(virtualKey, scan.Code, KeyScanCodes.Flags(scan, up));

    private static INPUT Event(ushort virtualKey, ushort scanCode, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = scanCode,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = SyntheticInputMarker.Value,
            },
        },
    };

    // Field by field: the padding inside INPUT is not reliably zeroed by the JIT (it differed between two identical
    // builds), and Windows never reads it.
    private static void AssertSameEvents(INPUT[] expected, INPUT[] actual, string context)
    {
        Assert.True(expected.Length == actual.Length, $"{context}: {actual.Length} events, expected {expected.Length}.");
        for (var i = 0; i < expected.Length; i++)
        {
            var (e, a) = (expected[i], actual[i]);
            Assert.True(
                e.type == a.type && e.U.ki.wVk == a.U.ki.wVk && e.U.ki.wScan == a.U.ki.wScan &&
                e.U.ki.dwFlags == a.U.ki.dwFlags && e.U.ki.time == a.U.ki.time && e.U.ki.dwExtraInfo == a.U.ki.dwExtraInfo,
                $"{context}: event {i} differs.");
        }
    }

    private static IEnumerable<string> Corpus()
    {
        yield return "a";
        yield return "hello world";
        yield return "first line.\nsecond line.";
        yield return "one\r\ntwo";
        yield return "one\rtwo";
        yield return "one\r\n\r\ntwo";
        yield return "trailing\r\n";
        yield return "\r";
        yield return "\n\n\n";
        yield return "x\uD83D";
        yield return "\uDE00y";
        yield return "ok \U0001F600 done";
        yield return "tab\tseparated\tvalues";
        yield return Sentences(184);
        yield return Sentences(150) + "\n" + Sentences(149);
        yield return Sentences(98) + "\n\n" + Sentences(98) + "\n\n" + Sentences(98);
        yield return FourParagraphs();
        yield return new string('x', 120) + "\n" + new string('y', 120);
        yield return string.Concat(Enumerable.Repeat("\U0001F600", 40));
        yield return string.Concat(Enumerable.Repeat("\r\n", 40));

        var random = new Random(20260927);
        string[] pieces = ["a", "b", " ", "\r", "\n", "\r\n", "\t", "\u00e9", "\U0001F600", "xyz", "  "];
        for (var t = 0; t < 40; t++)
        {
            var length = random.Next(1, 400);
            var parts = new List<string>();
            for (var i = 0; i < length; i++)
            {
                parts.Add(pieces[random.Next(pieces.Length)]);
            }

            yield return string.Concat(parts);
        }
    }

    private static string FourParagraphs() =>
        Sentences(190) + "\r\n" + Sentences(190) + "\r\n" + Sentences(190) + "\r\n" + Sentences(190);

    private static string Sentences(int length)
    {
        const string sentence = "The quick brown fox jumps over the lazy dog while the team reviews the release notes. ";
        var text = string.Concat(Enumerable.Repeat(sentence, (length / sentence.Length) + 1));
        return text[..length].TrimEnd() + ".";
    }
}
