using System.Text;
using Scribe.Core.TextInjection;
using Xunit;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.Tests;

/// <summary>
/// Unicode typing is the default injection method, so a line break has to survive it. Sent as a
/// KEYEVENTF_UNICODE control character a bare LF is discarded by most edit controls, which silently
/// joined the two lines with no separator; these tests pin the real Return keypress instead.
/// </summary>
public class TextInjectorUnicodeChunkTests
{
    // Replays a built batch the way the target app would see it: printable characters come back as
    // themselves, a Return keypress comes back as "\n".
    private static string Replay(INPUT[] inputs)
    {
        var text = new StringBuilder();
        for (int i = 0; i < inputs.Length; i += 2)
        {
            var down = inputs[i].U.ki;
            var up = inputs[i + 1].U.ki;

            Assert.Equal(0u, down.dwFlags & KEYEVENTF_KEYUP);
            Assert.Equal(KEYEVENTF_KEYUP, up.dwFlags & KEYEVENTF_KEYUP);

            if ((down.dwFlags & KEYEVENTF_UNICODE) != 0)
            {
                Assert.Equal(0, (int)down.wVk);
                text.Append((char)down.wScan);
            }
            else
            {
                Assert.Equal(VK_RETURN, down.wVk);
                Assert.Equal(VK_RETURN, up.wVk);
                text.Append('\n');
            }
        }

        return text.ToString();
    }

    private static string TypeAll(string text, int chunkChars)
    {
        var typed = new StringBuilder();
        for (int start = 0; start < text.Length;)
        {
            int count = TextInjector.ChunkLength(text, start, chunkChars);
            Assert.True(count > 0, "chunking must always make progress");
            typed.Append(Replay(TextInjector.BuildUnicodeChunk(text, start, count)));
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
        int expected = TextInjector.CountKeyEvents(text, 0, text.Length);

        int built = 0;
        for (int start = 0; start < text.Length;)
        {
            int count = TextInjector.ChunkLength(text, start, 4);
            built += TextInjector.BuildUnicodeChunk(text, start, count).Length;
            start += count;
        }

        Assert.Equal(expected, built);
    }
}
