using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.TextInjection;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.Tests;

/// <summary>
/// Typing into a Remote Desktop or virtual machine client is paced for the remote session: every injected keystroke is
/// sent on to a remote input stack, and the user's 0.4.3 log shows 184 characters (368 events) typed into msrdc in 99 ms,
/// in four SendInput calls of up to 100 events. Remote targets get small batches with a longer settle; every other target
/// keeps today's pacing exactly. No batch ever splits a CRLF pair or a surrogate pair.
/// </summary>
public class TypingPaceTests
{
    private static readonly nint Target = 0x4242;

    // Plain dictation-like text of the two lengths the brief asks the extra time for: the one typed just before the
    // freeze (184 characters) and the longest of the user's session (766). Words, spaces and full stops, no line breaks.
    private static readonly string Text184 = Words(184);
    private static readonly string Text766 = Words(766);

    [Fact]
    public void A_remote_client_is_paced_for_the_remote_session_and_everything_else_as_before()
    {
        Assert.Equal(new TypingPace(16, 20, PreferWordBoundary: false, PacedForRemoteSession: true), TypingPace.For("msrdc"));
        Assert.Equal(TypingPace.RemoteSession, TypingPace.For("mstsc.exe"));
        Assert.Equal(TypingPace.RemoteSession, TypingPace.For("VirtualBoxVM"));
        Assert.Equal(new TypingPace(50, 5, PreferWordBoundary: true, PacedForRemoteSession: false), TypingPace.For("notepad"));
        Assert.Equal(TypingPace.Local, TypingPace.For(null));
        Assert.Equal(TypingPace.Local, TypingPace.For(""));
    }

    [Fact]
    public void Typing_into_a_remote_client_sends_at_most_sixteen_code_units_per_call_with_twenty_ms_between()
    {
        var platform = Type(Text184, "msrdc");

        Assert.Equal(Text184, Replay(platform));
        Assert.All(platform.Batches, batch => Assert.InRange(batch.Length, 1, 32));
        Assert.Equal(12, platform.Batches.Count); // 11 batches of 16 and one of 8
        Assert.Equal(Enumerable.Repeat(20, 11), platform.Sleeps);
    }

    [Theory]
    [InlineData(184, 12, 220)]
    [InlineData(766, 48, 940)]
    public void The_extra_time_for_a_remote_client_is_bounded_by_its_settles(int characters, int batches, int settleMs)
    {
        var text = characters == 184 ? Text184 : Text766;
        var remote = Type(text, "msrdc");
        var local = Type(text, "notepad");

        Assert.Equal(text, Replay(remote));
        Assert.Equal(batches, remote.Batches.Count);
        Assert.Equal(settleMs, remote.Sleeps.Sum());

        // The same events either way: pacing changes only how they are grouped and spaced.
        Assert.Equal(local.Batches.Sum(batch => batch.Length), remote.Batches.Sum(batch => batch.Length));
        Assert.All(local.Sleeps, sleep => Assert.Equal(5, sleep));
    }

    [Fact]
    public void Typing_into_any_other_target_keeps_today_s_batches_and_settle()
    {
        // Worked by hand from the batching rule every release has used: 50 code units, backing up to the last space in
        // the batch's second half ("and " ends at 48), then the remaining 20, with one 5 ms settle between them.
        const string text = "the quick brown fox jumps over the lazy dog and keeps running onward";
        foreach (var target in new string?[] { null, "notepad", "WINWORD", "Code" })
        {
            var platform = Type(text, target);

            Assert.Equal(text, Replay(platform));
            Assert.Equal([96, 40], platform.Batches.Select(batch => batch.Length));
            Assert.Equal([5], platform.Sleeps);
        }
    }

    [Fact]
    public void A_remote_batch_does_not_wait_for_a_word_boundary()
    {
        const string text = "the quick brown fox jumps over the lazy dog and keeps running onward";
        var platform = Type(text, "msrdc");

        // 68 code units in fives: four full batches of 16 and the last 4, whatever word they cut.
        Assert.Equal([32, 32, 32, 32, 8], platform.Batches.Select(batch => batch.Length));
        Assert.Equal([20, 20, 20, 20], platform.Sleeps);
    }

    [Theory]
    [InlineData(16, false)]
    [InlineData(16, true)]
    [InlineData(50, true)]
    [InlineData(50, false)]
    public void No_batch_ends_inside_a_surrogate_pair(int max, bool preferWordBoundary)
    {
        // An emoji walked across every position of a batch, with no space for the word-boundary rule to back up to.
        for (var before = 0; before < 2 * max; before++)
        {
            var text = new string('x', before) + "\U0001F600" + new string('y', max);
            for (int start = 0; start < text.Length;)
            {
                int count = TextInjector.ChunkLength(text, start, max, preferWordBoundary);
                int end = start + count;
                Assert.True(count > 0, "chunking must always make progress");
                Assert.False(
                    end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]),
                    $"a batch of {max} ended inside the pair at {end} ({before} units before it)");
                start = end;
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(16)]
    [InlineData(17)]
    public void A_remote_batch_never_splits_a_crlf_pair(int max)
    {
        const string text = "alpha\r\nbravo\r\ncharlie\r\ndelta and a longer tail\r\nend";
        for (int start = 0; start < text.Length;)
        {
            int count = TextInjector.ChunkLength(text, start, max, preferWordBoundary: false);
            Assert.False(start + count < text.Length && text[start + count - 1] == '\r' && text[start + count] == '\n');
            start += count;
        }
    }

    [Fact]
    public void Surrogate_pairs_and_line_breaks_arrive_whole_in_a_paced_remote_session()
    {
        var text = "Ship it \U0001F680 now.\r\nNext line \U0001F600\U0001F600 and more text to fill a batch.";
        var platform = Type(text, "msrdc", shiftEnter: true);

        Assert.Equal(text.Replace("\r\n", "\u21B5"), Replay(platform));
        foreach (var batch in platform.Batches)
        {
            var units = batch.Where(input => (input.U.ki.dwFlags & KEYEVENTF_UNICODE) != 0 &&
                (input.U.ki.dwFlags & KEYEVENTF_KEYUP) == 0).Select(input => (char)input.U.ki.wScan).ToArray();
            Assert.False(units.Length > 0 && char.IsHighSurrogate(units[^1]), "a batch ended on a high surrogate");
            Assert.False(units.Length > 0 && char.IsLowSurrogate(units[0]), "a batch began with a low surrogate");
        }
    }

    [Fact]
    public void A_paste_that_falls_back_to_typing_is_paced_for_a_remote_client_too()
    {
        var clipboard = new TextInjectionFakes.Clipboard { OpenAttemptSucceeds = _ => false };
        var platform = new TextInjectionFakes.Platform { Foreground = Target };
        var injector = new TextInjector(NullLogger<TextInjector>.Instance, platform, clipboard);

        var result = injector.Inject(Text184, InjectionMethod.ClipboardPaste, Target, targetProcessName: "msrdc");

        Assert.Equal(PasteDelivery.ClipboardBusy, result.Paste);
        Assert.True(result.Succeeded);
        Assert.Equal(12, platform.Batches.Count);
        Assert.Contains(20, platform.Sleeps);
        Assert.DoesNotContain(5, platform.Sleeps);
    }

    private static TextInjectionFakes.Platform Type(string text, string? target, bool shiftEnter = false)
    {
        var platform = new TextInjectionFakes.Platform { Foreground = Target };
        var injector = new TextInjector(NullLogger<TextInjector>.Instance, platform, new TextInjectionFakes.Clipboard());
        var result = injector.Inject(text, InjectionMethod.UnicodeType, Target, shiftEnter, target);
        Assert.True(result.Succeeded, result.Error);
        return platform;
    }

    // What the target received: each character as itself, a Shift+Enter as U+21B5.
    private static string Replay(TextInjectionFakes.Platform platform)
    {
        var text = new StringBuilder();
        foreach (var input in platform.Batches.SelectMany(batch => batch))
        {
            var key = input.U.ki;
            if ((key.dwFlags & KEYEVENTF_KEYUP) != 0)
            {
                continue;
            }

            if ((key.dwFlags & KEYEVENTF_UNICODE) != 0)
            {
                text.Append((char)key.wScan);
            }
            else if (key.wVk == VK_RETURN)
            {
                text.Append('\u21B5');
            }
        }

        return text.ToString();
    }

    private static string Words(int length)
    {
        const string sentence = "Please send the revised figures to the team before the review on Friday. ";
        var text = new StringBuilder();
        while (text.Length < length)
        {
            text.Append(sentence);
        }

        return text.ToString(0, length);
    }
}
