using System.Text;
using Scribe.Core.TextInjection;
using Xunit;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.Tests;

/// <summary>
/// Unicode typing is the default injection method, so a line break has to survive it. Sent as a
/// KEYEVENTF_UNICODE control character a bare LF is discarded by most edit controls, which silently
/// joined the two lines with no separator; these tests pin the real Return keypress instead. They
/// also pin the <b>shifted</b> Return, because a bare Enter is "send" in every major chat app.
/// </summary>
public class TextInjectorUnicodeChunkTests
{
    // Replays a built batch the way the target app would see it: printable characters come back as
    // themselves, a Return keypress comes back as "\n", and a shifted Return as "\u21B5" so a test
    // can tell the two apart.
    private static string Replay(INPUT[] inputs)
    {
        var text = new StringBuilder();
        for (int i = 0; i < inputs.Length;)
        {
            var down = inputs[i].U.ki;

            if ((down.dwFlags & KEYEVENTF_UNICODE) != 0)
            {
                var up = inputs[i + 1].U.ki;
                Assert.Equal(0u, down.dwFlags & KEYEVENTF_KEYUP);
                Assert.Equal(KEYEVENTF_KEYUP, up.dwFlags & KEYEVENTF_KEYUP);
                Assert.Equal(0, (int)down.wVk);
                Assert.Equal(down.wScan, up.wScan);
                text.Append((char)down.wScan);
                i += 2;
                continue;
            }

            if (down.wVk == VK_SHIFT)
            {
                // Shift+Enter: hold shift, tap Return, release shift.
                Assert.Equal(0u, down.dwFlags & KEYEVENTF_KEYUP);
                Assert.Equal(VK_RETURN, inputs[i + 1].U.ki.wVk);
                Assert.Equal(0u, inputs[i + 1].U.ki.dwFlags & KEYEVENTF_KEYUP);
                Assert.Equal(VK_RETURN, inputs[i + 2].U.ki.wVk);
                Assert.Equal(KEYEVENTF_KEYUP, inputs[i + 2].U.ki.dwFlags & KEYEVENTF_KEYUP);
                Assert.Equal(VK_SHIFT, inputs[i + 3].U.ki.wVk);
                Assert.Equal(KEYEVENTF_KEYUP, inputs[i + 3].U.ki.dwFlags & KEYEVENTF_KEYUP);
                text.Append('\u21B5');
                i += 4;
                continue;
            }

            Assert.Equal(VK_RETURN, down.wVk);
            Assert.Equal(0u, down.dwFlags & KEYEVENTF_KEYUP);
            Assert.Equal(VK_RETURN, inputs[i + 1].U.ki.wVk);
            Assert.Equal(KEYEVENTF_KEYUP, inputs[i + 1].U.ki.dwFlags & KEYEVENTF_KEYUP);
            text.Append('\n');
            i += 2;
        }

        return text.ToString();
    }

    private static string TypeAll(string text, int chunkChars, bool shiftEnter = false)
    {
        var typed = new StringBuilder();
        for (int start = 0; start < text.Length;)
        {
            int count = TextInjector.ChunkLength(text, start, chunkChars);
            Assert.True(count > 0, "chunking must always make progress");
            typed.Append(Replay(TextInjector.BuildUnicodeChunk(text, start, count, shiftEnter)));
            start += count;
        }

        return typed.ToString();
    }

    [Fact]
    public void Plain_text_is_typed_verbatim() =>
        Assert.Equal("hello world", TypeAll("hello world", 50));

    [Theory]
    [InlineData("first line.\nsecond line.")]
    [InlineData("first line.\r\nsecond line.")]
    [InlineData("first line.\rsecond line.")]
    public void A_line_break_becomes_one_return_keypress(string text)
    {
        var typed = TypeAll(text, 50);

        Assert.Equal("first line.\nsecond line.", typed);
        Assert.DoesNotContain('\r', typed);
    }

    [Fact]
    public void A_paragraph_break_keeps_both_line_breaks() =>
        Assert.Equal("one\n\ntwo", TypeAll("one\r\n\r\ntwo", 50));

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(50)]
    public void A_crlf_pair_survives_every_chunk_boundary(int chunkChars)
    {
        // Small batch sizes walk the CR/LF pair across every position in a chunk; splitting it would
        // type two Returns and insert a blank line the user never dictated.
        const string text = "alpha\r\nbravo\r\ncharlie\r\ndelta";

        Assert.Equal("alpha\nbravo\ncharlie\ndelta", TypeAll(text, chunkChars));
    }

    [Fact]
    public void Surrogate_pairs_are_typed_as_two_code_units() =>
        Assert.Equal("ok \U0001F600", TypeAll("ok \U0001F600", 50));

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a\r\n\r\nb")]
    [InlineData("trailing\r\n")]
    public void The_event_total_matches_what_is_actually_built(string text)
    {
        foreach (var shiftEnter in new[] { false, true })
        {
            int expected = TextInjector.CountKeyEvents(text, 0, text.Length, shiftEnter);

            int built = 0;
            for (int start = 0; start < text.Length;)
            {
                int count = TextInjector.ChunkLength(text, start, 4);
                built += TextInjector.BuildUnicodeChunk(text, start, count, shiftEnter).Length;
                start += count;
            }

            Assert.Equal(expected, built);
        }
    }

    // A bare Enter is bound to "send" in Teams, Slack and Discord, so a cleaned two-paragraph
    // dictation fired the message on the first break. Shift+Enter is the soft-newline chord there
    // and behaves like Enter in a plain edit control, so it is the default.
    [Theory]
    [InlineData("one\ntwo")]
    [InlineData("one\r\ntwo")]
    [InlineData("one\rtwo")]
    public void A_line_break_is_shifted_by_default(string text) =>
        Assert.Equal("one\u21B5two", TypeAll(text, 50, shiftEnter: true));

    [Fact]
    public void A_paragraph_break_shifts_both_line_breaks() =>
        Assert.Equal("one\u21B5\u21B5two", TypeAll("one\r\n\r\ntwo", 50, shiftEnter: true));

    [Fact]
    public void Shift_is_released_after_every_line_break()
    {
        // A shift left logically down would turn the following characters into a shortcut stream.
        var inputs = TextInjector.BuildUnicodeChunk("a\nb\nc", 0, 5, shiftEnter: true);

        int depth = 0;
        foreach (var input in inputs)
        {
            var key = input.U.ki;
            if (key.wVk != VK_SHIFT)
            {
                continue;
            }

            depth += (key.dwFlags & KEYEVENTF_KEYUP) == 0 ? 1 : -1;
            Assert.InRange(depth, 0, 1);
        }

        Assert.Equal(0, depth);
    }

    [Fact]
    public void Opting_out_sends_a_plain_return() =>
        Assert.Equal("one\ntwo", TypeAll("one\r\ntwo", 50, shiftEnter: false));

    // Each batch is a separate SendInput the target repaints after, so a fixed-width cut was visible
    // as a torn word. Batches should end on whitespace instead.
    [Fact]
    public void A_batch_ends_on_a_word_boundary()
    {
        const string text = "the quick brown fox jumps over the lazy dog and keeps running onward";

        for (int start = 0; start < text.Length;)
        {
            int count = TextInjector.ChunkLength(text, start, 20);
            int end = start + count;
            if (end < text.Length)
            {
                Assert.True(
                    char.IsWhiteSpace(text[end - 1]),
                    $"batch [{start}..{end}) ended mid-word: '{text[start..end]}'");
            }

            start = end;
        }
    }

    [Fact]
    public void A_long_unbroken_token_still_fills_a_batch()
    {
        // No whitespace to back up to; the batch must not shrink toward one character per send.
        var text = new string('x', 200);

        Assert.Equal(50, TextInjector.ChunkLength(text, 0, 50));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(50)]
    public void Word_boundary_batching_never_loses_or_reorders_text(int chunkChars)
    {
        const string text = "first sentence here.\r\n\r\nsecond sentence with a https://example.com/very/long/path in it";

        Assert.Equal(text.Replace("\r\n", "\u21B5"), TypeAll(text, chunkChars, shiftEnter: true));
    }
}
