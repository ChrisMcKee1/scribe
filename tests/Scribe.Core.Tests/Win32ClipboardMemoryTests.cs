using System.Runtime.InteropServices;
using Scribe.Core.TextInjection;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.Tests;

/// <summary>
/// <see cref="Win32Clipboard"/>'s memory handling, on private global memory blocks that never reach the clipboard, so these
/// run on any desktop: text is read where it lies, up to its terminator and never past the block, text is written
/// null-terminated, and small data blocks round-trip. The real clipboard round trip stays in <c>Win32ClipboardTests</c>,
/// which only CI runs.
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
}
