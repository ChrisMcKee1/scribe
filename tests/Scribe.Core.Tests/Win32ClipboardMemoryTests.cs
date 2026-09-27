using System.Runtime.InteropServices;
using Scribe.Core.TextInjection;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.Tests;

/// <summary>
/// <see cref="Win32Clipboard"/>'s memory handling, on private global memory blocks and on pinned memory of the test's own,
/// never the clipboard, so these run on any desktop: text is read where it lies, up to its terminator and never past the
/// block, text is written with exactly one terminator, and small data blocks round-trip. The reads and writes that must
/// not stray are also run on memory poisoned past its logical end, so the result never depends on what an allocator left
/// there. The real clipboard round trip stays in <c>Win32ClipboardTests</c>, which only CI runs.
/// </summary>
public class Win32ClipboardMemoryTests
{
    private const string Fixture = "Scribe caf\u00e9, \u6d4b\u8bd5 \U0001F3A4";

    [Fact]
    public void A_terminated_block_is_read_up_to_its_terminator()
    {
        var block = TextBlock("hello world\0" + new string('Z', 64));
        try
        {
            Assert.True(Win32Clipboard.TryReadUnicodeText(block, out var text));
            Assert.Equal("hello world", text);
        }
        finally
        {
            GlobalFree(block);
        }
    }

    [Fact]
    public void A_block_without_a_terminator_is_read_to_its_end()
    {
        var block = FilledBlock(minimumChars: 40, 'q');
        try
        {
            var chars = (int)((ulong)GlobalSize(block) / sizeof(char));
            Assert.True(Win32Clipboard.TryReadUnicodeText(block, out var text));
            Assert.Equal(new string('q', chars), text);
        }
        finally
        {
            GlobalFree(block);
        }
    }

    [Fact]
    public void A_block_that_starts_with_its_terminator_reads_as_empty()
    {
        var block = TextBlock("\0" + new string('Z', 8));
        try
        {
            Assert.True(Win32Clipboard.TryReadUnicodeText(block, out var text));
            Assert.Equal(string.Empty, text);
        }
        finally
        {
            GlobalFree(block);
        }
    }

    [Fact]
    public void A_block_with_no_room_for_a_character_is_not_text()
    {
        // For zero bytes GlobalAlloc hands back a block marked as discarded, whose size is zero.
        var block = GlobalAlloc(GMEM_MOVEABLE, 0);
        Assert.NotEqual(nint.Zero, block);
        try
        {
            Assert.Equal((nuint)0, GlobalSize(block));
            Assert.False(Win32Clipboard.TryReadUnicodeText(block, out var text));
            Assert.Null(text);
        }
        finally
        {
            GlobalFree(block);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData(Fixture)]
    public void Text_written_for_the_clipboard_is_terminated_and_reads_back_whole(string text)
    {
        var block = Win32Clipboard.AllocUnicodeText(text);
        Assert.NotEqual(nint.Zero, block);
        try
        {
            Assert.True(GlobalSize(block) >= (nuint)((text.Length + 1) * sizeof(char)));
            Assert.Equal(text + "\0", ReadChars(block, text.Length + 1));
            Assert.True(Win32Clipboard.TryReadUnicodeText(block, out var read));
            Assert.Equal(text, read);
        }
        finally
        {
            GlobalFree(block);
        }
    }

    [Fact]
    public void A_long_text_reads_back_whole()
    {
        var text = string.Concat(Enumerable.Repeat(Fixture, 5_000));
        var block = Win32Clipboard.AllocUnicodeText(text);
        Assert.NotEqual(nint.Zero, block);
        try
        {
            Assert.True(Win32Clipboard.TryReadUnicodeText(block, out var read));
            Assert.Equal(text, read);
        }
        finally
        {
            GlobalFree(block);
        }
    }

    [Fact]
    public void A_data_block_round_trips_and_a_destination_larger_than_the_block_is_refused()
    {
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        var block = Win32Clipboard.AllocData(data);
        Assert.NotEqual(nint.Zero, block);
        try
        {
            var read = new byte[data.Length];
            Assert.True(Win32Clipboard.TryReadBlock(block, read));
            Assert.Equal(data, read);

            var size = checked((int)GlobalSize(block));
            Assert.False(Win32Clipboard.TryReadBlock(block, new byte[size + 1]));
        }
        finally
        {
            GlobalFree(block);
        }
    }

    [Fact]
    public void An_empty_payload_still_gets_a_real_block()
    {
        var block = Win32Clipboard.AllocData([]);
        Assert.NotEqual(nint.Zero, block);
        try
        {
            Assert.True(GlobalSize(block) >= 1);
            Assert.True(Win32Clipboard.TryReadBlock(block, []));
        }
        finally
        {
            GlobalFree(block);
        }
    }

    // Regions whose logical length is shorter than the memory behind them, with poison after the logical end: whatever
    // the allocator would have left there, a read one character too far sees poison. Lengths around 8, 16, 32 and 64
    // characters meet the vectorized IndexOf's widths, 81 and 83 bytes (and 1 and 3) end in half a character, and the
    // last two are large.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(14)]
    [InlineData(16)]
    [InlineData(18)]
    [InlineData(30)]
    [InlineData(32)]
    [InlineData(34)]
    [InlineData(62)]
    [InlineData(64)]
    [InlineData(66)]
    [InlineData(81)]
    [InlineData(83)]
    [InlineData(126)]
    [InlineData(128)]
    [InlineData(130)]
    [InlineData(200_000)]
    [InlineData(200_001)]
    public void An_unterminated_region_is_read_to_its_logical_end_and_never_past_it(int logicalBytes)
    {
        var logical = Text(logicalBytes / sizeof(char));

        var read = ReadPoisonedRegion(logical, logicalBytes, Win32Clipboard.ReadUnicodeText);

        Assert.Equal(-1, read.IndexOf(Poison));
        Assert.Equal(logical, read);
    }

    [Theory]
    [InlineData(16, 0)]
    [InlineData(16, 7)]
    [InlineData(81, 39)]
    [InlineData(130, 64)]
    [InlineData(200_000, 99_999)]
    public void A_terminated_region_is_read_up_to_its_terminator(int logicalBytes, int terminatorAt)
    {
        var chars = Text(logicalBytes / sizeof(char)).ToCharArray();
        chars[terminatorAt] = '\0';

        var read = ReadPoisonedRegion(new string(chars), logicalBytes, Win32Clipboard.ReadUnicodeText);

        Assert.Equal(new string(chars, 0, terminatorAt), read);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(80)]
    [InlineData(81)]
    [InlineData(83)]
    [InlineData(200_000)]
    public void A_reader_that_reads_one_character_too_many_brings_the_poison_back(int logicalBytes)
    {
        // What the region tests rely on: the defect they guard against, a read of one character past the end, shows.
        var read = ReadPoisonedRegion(Text(logicalBytes / sizeof(char)), logicalBytes, OneCharacterTooMany);

        Assert.NotEqual(-1, read.IndexOf(Poison));

        static string OneCharacterTooMany(nint pointer, nuint byteLength)
        {
            var chars = Marshal.PtrToStringUni(pointer, (int)(byteLength / sizeof(char)) + 1);
            var end = chars.IndexOf('\0');
            return end < 0 ? chars : chars[..end];
        }
    }

    // Writes into caller-provided memory set to 0xFFFF everywhere, past the end included, so a missing terminator or a
    // write past it shows whatever the allocator would have left there.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(33)]
    [InlineData(64)]
    [InlineData(90_000)]
    public void Text_is_written_with_exactly_one_terminator_and_nothing_after_it_touched(int length)
    {
        var text = Text(length);

        var memory = WriteIntoPoison(text, Win32Clipboard.WriteUnicodeText);

        Assert.Equal(text, new string(memory, 0, length));
        Assert.Equal('\0', memory[length]);
        Assert.Equal(new string(Untouched, UntouchedTail), new string(memory, length + 1, UntouchedTail));
    }

    [Fact]
    public void A_writer_that_leaves_out_the_terminator_is_caught()
    {
        // What the write test relies on: a text written without its terminator leaves the poison where the NUL belongs.
        var text = Text(90_000);

        var memory = WriteIntoPoison(text, static (target, value) => Marshal.Copy(value.ToCharArray(), 0, target, value.Length));

        Assert.Equal(text, new string(memory, 0, text.Length));
        Assert.Equal(Untouched, memory[text.Length]);
    }

    // In the collection that runs alone (stream TR, item 1): no other test runs while it measures.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Fact]
        public void Reading_text_allocates_only_the_text()
        {
            // 400 characters, then 64 of slack after the terminator, as another application's block can have.
            var block = TextBlock(new string('q', 400) + "\0" + new string('Z', 64));
            try
            {
                // Warm the JIT and the imports' stubs for every call measured below, and the readings around the window.
                for (var warm = 0; warm < 3; warm++)
                {
                    _ = Win32Clipboard.TryReadUnicodeText(block, out _);
                }

                _ = RuntimeWork.Now().Since(RuntimeWork.Now());
                _ = BytesOfAString(1);

                // What a string of the text's length costs on this runtime, measured the same way: the read below may
                // allocate that and nothing else, whatever the block holds past the text.
                var textBytes = BytesOfAString(400);

                var work = RuntimeWork.Now();
                var before = GC.GetAllocatedBytesForCurrentThread();
                var read = Win32Clipboard.TryReadUnicodeText(block, out var text);
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                var during = RuntimeWork.Now().Since(work);

                AllocationMeasurement.AssertZero(
                    allocated - textBytes,
                    during,
                    $"Reading 400 characters from a block of {GlobalSize(block)} bytes, beyond the text of {textBytes} bytes",
                    () => Win32Clipboard.TryReadUnicodeText(block, out _));
                Assert.True(read);
                Assert.Equal(400, text?.Length);
            }
            finally
            {
                GlobalFree(block);
            }
        }

        [Fact]
        public void Writing_blocks_and_reading_data_allocate_nothing()
        {
            byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
            var destination = new byte[data.Length];
            var text = new string('w', 400);

            // Warm the JIT and the imports' stubs for every call measured below, and the readings around the window.
            for (var warm = 0; warm < 3; warm++)
            {
                RoundTrip(text, data, destination);
            }

            _ = RuntimeWork.Now().Since(RuntimeWork.Now());

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var textBlock = Win32Clipboard.AllocUnicodeText(text);
            var dataBlock = Win32Clipboard.AllocData(data);
            var read = Win32Clipboard.TryReadBlock(dataBlock, destination);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);
            GlobalFree(textBlock);
            GlobalFree(dataBlock);

            AllocationMeasurement.AssertZero(
                allocated,
                during,
                "Writing a text block and a data block and reading the data back",
                () => RoundTrip(text, data, destination));
            Assert.NotEqual(nint.Zero, textBlock);
            Assert.True(read);
            Assert.Equal(data, destination);

            static void RoundTrip(string text, byte[] data, byte[] destination)
            {
                var textBlock = Win32Clipboard.AllocUnicodeText(text);
                var dataBlock = Win32Clipboard.AllocData(data);
                _ = Win32Clipboard.TryReadBlock(dataBlock, destination);
                GlobalFree(textBlock);
                GlobalFree(dataBlock);
            }
        }

        private static long BytesOfAString(int length)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var text = new string('x', length);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(text);
            return bytes;
        }
    }

    // A block holding exactly these characters. Anything GlobalAlloc adds past them is left as it is; the terminated cases
    // never read it.
    private static nint TextBlock(string content)
    {
        var chars = content.ToCharArray();
        var block = GlobalAlloc(GMEM_MOVEABLE, (nuint)(chars.Length * sizeof(char)));
        Assert.NotEqual(nint.Zero, block);
        var pointer = GlobalLock(block);
        Assert.NotEqual(nint.Zero, pointer);
        try
        {
            Marshal.Copy(chars, 0, pointer, chars.Length);
        }
        finally
        {
            GlobalUnlock(block);
        }

        return block;
    }

    // A block of at least this many characters, with every character up to the size GlobalSize reports set to fill.
    private static nint FilledBlock(int minimumChars, char fill)
    {
        var block = GlobalAlloc(GMEM_MOVEABLE, (nuint)(minimumChars * sizeof(char)));
        Assert.NotEqual(nint.Zero, block);
        var chars = (int)((ulong)GlobalSize(block) / sizeof(char));
        var pointer = GlobalLock(block);
        Assert.NotEqual(nint.Zero, pointer);
        try
        {
            Marshal.Copy(new string(fill, chars).ToCharArray(), 0, pointer, chars);
        }
        finally
        {
            GlobalUnlock(block);
        }

        return block;
    }

    private static string ReadChars(nint block, int count)
    {
        var pointer = GlobalLock(block);
        Assert.NotEqual(nint.Zero, pointer);
        try
        {
            var chars = new char[count];
            Marshal.Copy(pointer, chars, 0, count);
            return new string(chars);
        }
        finally
        {
            GlobalUnlock(block);
        }
    }

    // After a region's logical end: 0x5858, never a NUL and never a character Text makes.
    private const char Poison = '\u5858';

    // Everywhere in a write's destination before the write.
    private const char Untouched = '\uFFFF';

    // How many characters of each poisoned region or destination lie past its logical end.
    private const int UntouchedTail = 64;

    // Neither NUL nor poison: letters, an accented and a CJK character, and a surrogate pair that a length can cut in two.
    private static string Text(int length)
    {
        const string pattern = "abcdefg h\u00e9\u6d4b\uD83C\uDFA4 xyz";
        return string.Create(length, pattern, static (chars, pattern) =>
        {
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = pattern[i % pattern.Length];
            }
        });
    }

    // Pinned memory whose first logicalBytes bytes hold logical (logicalBytes / 2 characters), and whose every character
    // after them is poison, including the one an odd logicalBytes ends inside; read with the pointer and logicalBytes.
    private static string ReadPoisonedRegion(string logical, int logicalBytes, Func<nint, nuint, string> read)
    {
        Assert.Equal(logicalBytes / sizeof(char), logical.Length);
        var memory = new char[logical.Length + UntouchedTail];
        memory.AsSpan().Fill(Poison);
        logical.AsSpan().CopyTo(memory);
        var pin = GCHandle.Alloc(memory, GCHandleType.Pinned);
        try
        {
            return read(pin.AddrOfPinnedObject(), (nuint)logicalBytes);
        }
        finally
        {
            pin.Free();
        }
    }

    // Pinned memory with room for text and its terminator and UntouchedTail characters more, all of it Untouched, handed
    // to write; the memory as the write left it.
    private static char[] WriteIntoPoison(string text, Action<nint, string> write)
    {
        var memory = new char[text.Length + 1 + UntouchedTail];
        memory.AsSpan().Fill(Untouched);
        var pin = GCHandle.Alloc(memory, GCHandleType.Pinned);
        try
        {
            write(pin.AddrOfPinnedObject(), text);
        }
        finally
        {
            pin.Free();
        }

        return memory;
    }
}
