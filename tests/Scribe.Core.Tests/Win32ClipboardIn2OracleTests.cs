using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Scribe.Core.TextInjection;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.Tests;

/// <summary>
/// The seeded IN2 equivalence gate: the frozen 0.5.0 memory code decides strings and bytes for the shipping helpers.
/// </summary>
/// <remarks>
/// Only private GlobalAlloc/GlobalLock blocks are used. This test never opens, empties, reads or publishes to the
/// real clipboard, creates no window, installs no hook and sends no input. It can run in a locked or remote session.
/// Logical lengths are supplied separately because GlobalAlloc may round its physical allocation up; odd logical
/// lengths must ignore the trailing half-character. All padding is initialized and compared, not allocator residue.
/// Surrogates are constructed in the test body, not passed through xUnit's UTF-8 theory serialization.
/// </remarks>
public sealed class Win32ClipboardIn2OracleTests
{
    private const int SeedOne = 0x051C1102;
    private const int SeedTwo = 0x07123456;
    private const int PrefixBytes = 16;
    private const int GuardBytes = 64;
    private const int RandomCases = 256;

    private static readonly int[] ReadEdges =
    [
        0, 1, 2, 3, 4, 5, 7, 8, 9, 14, 15, 16, 17, 30, 31, 32, 33,
        62, 63, 64, 65, 126, 127, 128, 129, 254, 255, 256, 257,
        84_999, 85_000, 85_001, 85_002, 131_072, 131_073, 200_000, 200_001,
    ];

    private static readonly int[] WriteEdges =
    [
        0, 1, 2, 3, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65,
        42_499, 42_500, 42_501, 65_536, 65_537, 100_000,
    ];

    [Theory]
    [InlineData(SeedOne)]
    [InlineData(SeedTwo)]
    public void IN2_seeded_reads_match_the_frozen_050_reader(int seed)
    {
        var random = new Random(seed);
        var lengths = ReadEdges.Concat(Enumerable.Range(0, RandomCases).Select(_ => random.Next(4098))).ToArray();
        var caseNumber = 0;
        foreach (var logicalBytes in lengths)
        {
            foreach (var shape in Enum.GetValues<ReadShape>())
            {
                var context = Context(seed, caseNumber++, $"read bytes={logicalBytes} shape={shape}");
                using var block = new PrivateBlock(PrefixBytes + logicalBytes + GuardBytes, context);
                var initial = NonzeroBytes(random, block.Length);
                var chars = logicalBytes / sizeof(char);
                for (var index = 0; index < chars; index++)
                {
                    PutChar(initial, index, (char)random.Next(1, 0x10000));
                }

                if (chars > 0)
                {
                    switch (shape)
                    {
                        case ReadShape.LeadingTerminator:
                            PutChar(initial, 0, '\0');
                            break;
                        case ReadShape.RandomTerminator:
                            PutChar(initial, random.Next(chars), '\0');
                            break;
                        case ReadShape.LastTerminator:
                            PutChar(initial, chars - 1, '\0');
                            break;
                        case ReadShape.SplitSurrogateAtEnd:
                            PutChar(initial, chars - 1, '\uD83D');
                            PutChar(initial, chars, '\uDE00');
                            break;
                    }
                }
                else if (shape == ReadShape.SplitSurrogateAtEnd)
                {
                    PutChar(initial, 0, '\uD83D');
                    PutChar(initial, 1, '\uDE00');
                }

                block.Reset(initial);
                var pointer = block.Pointer + PrefixBytes;
                var expected = Win32Clipboard050TextOracle.ReadUnicodeText(pointer, (nuint)logicalBytes);
                var actual = Win32Clipboard.ReadUnicodeText(pointer, (nuint)logicalBytes);

                EqualText(expected, actual, context);
                Assert.True(block.ReadBytes().AsSpan().SequenceEqual(initial), context + ": a reader changed the block");
            }
        }
    }

    [Theory]
    [InlineData(SeedOne)]
    [InlineData(SeedTwo)]
    public void IN2_seeded_writes_match_the_frozen_050_writer_byte_for_byte(int seed)
    {
        var random = new Random(seed);
        var lengths = WriteEdges.Concat(Enumerable.Range(0, RandomCases).Select(_ => random.Next(2049))).ToArray();
        var caseNumber = 0;
        foreach (var length in lengths)
        {
            foreach (var embeddedTerminator in new[] { false, true })
            {
                var context = Context(seed, caseNumber++, $"write chars={length} embeddedNul={embeddedTerminator}");
                var chars = new char[length];
                for (var index = 0; index < chars.Length; index++)
                {
                    chars[index] = (char)random.Next(1, 0x10000);
                }
                if (chars.Length > 1)
                {
                    chars[^2] = '\uD83D';
                    chars[^1] = '\uDE00';
                }
                else if (chars.Length == 1)
                {
                    chars[0] = '\uD83D';
                }
                if (embeddedTerminator && chars.Length > 0)
                {
                    chars[random.Next(chars.Length)] = '\0';
                }

                var text = new string(chars);
                var payloadBytes = checked((length + 1) * sizeof(char));
                using var block = new PrivateBlock(PrefixBytes + payloadBytes + GuardBytes + (caseNumber & 1), context);
                var initial = NonzeroBytes(random, block.Length);
                var pointer = block.Pointer + PrefixBytes;
                block.Reset(initial);
                Win32Clipboard050TextOracle.WriteUnicodeText(pointer, text);
                var expected = block.ReadBytes();
                block.Reset(initial);
                Win32Clipboard.WriteUnicodeText(pointer, text);
                var actual = block.ReadBytes();

                Assert.True(actual.AsSpan().SequenceEqual(expected), context + ": bytes differ from the 0.5.0 writer");
                var terminator = PrefixBytes + length * sizeof(char);
                Assert.True(actual[terminator] == 0 && actual[terminator + 1] == 0, context + ": final NUL missing");
                Assert.True(actual.AsSpan(0, PrefixBytes).SequenceEqual(initial.AsSpan(0, PrefixBytes)),
                    context + ": prefix changed");
                var tail = PrefixBytes + payloadBytes;
                Assert.True(actual.AsSpan(tail).SequenceEqual(initial.AsSpan(tail)), context + ": suffix changed");
                EqualText(
                    Win32Clipboard050TextOracle.ReadUnicodeText(pointer, (nuint)payloadBytes),
                    Win32Clipboard.ReadUnicodeText(pointer, (nuint)payloadBytes), context + ": readback");
            }
        }
    }

    [Fact]
    public void IN2_private_block_read_status_matches_the_frozen_050_path()
    {
        var context = Context(SeedOne, 0, "discarded empty allocation");
        var empty = GlobalAlloc(GMEM_MOVEABLE, 0);
        Assert.True(empty != 0, context + ": allocation failed");
        try
        {
            Assert.True(GlobalSize(empty) == 0, context + ": expected a discarded block");
            var expected = Win32Clipboard050TextOracle.TryReadUnicodeText(empty, out var expectedText);
            var actual = Win32Clipboard.TryReadUnicodeText(empty, out var actualText);
            Assert.True(expected == actual && !actual && expectedText is null && actualText is null,
                context + ": empty block status changed");
        }
        finally
        {
            GlobalFree(empty);
        }

        var random = new Random(SeedOne);
        foreach (var bytes in new[] { 1, 2, 3, 31, 85_001, 200_001 })
        {
            context = Context(SeedOne, bytes, "physical allocation size");
            using var block = new PrivateBlock(bytes, context);
            block.Reset(NonzeroBytes(random, block.Length));
            var expected = Win32Clipboard050TextOracle.TryReadUnicodeText(block.Handle, out var expectedText);
            var actual = Win32Clipboard.TryReadUnicodeText(block.Handle, out var actualText);
            Assert.True(expected == actual, context + ": status changed");
            Assert.True(string.Equals(expectedText, actualText, StringComparison.Ordinal), context + ": text changed");
        }
    }

    private static string Context(int seed, int caseNumber, string detail) =>
        $"IN2 seed=0x{seed:X8} case={caseNumber} {detail}";

    private static void EqualText(string expected, string actual, string context) =>
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"{context}: text differs, expected units={expected.Length}, actual units={actual.Length}");

    private static void PutChar(byte[] bytes, int index, char value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(PrefixBytes + index * sizeof(char), sizeof(char)), value);

    private static byte[] NonzeroBytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == 0)
            {
                bytes[index] = 0xA5;
            }
        }
        return bytes;
    }

    private enum ReadShape
    {
        Unterminated,
        LeadingTerminator,
        RandomTerminator,
        LastTerminator,
        SplitSurrogateAtEnd,
    }

    private sealed class PrivateBlock : IDisposable
    {
        public PrivateBlock(int minimumBytes, string context)
        {
            Handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)minimumBytes);
            Assert.True(Handle != 0, context + ": allocation failed");
            try
            {
                Length = checked((int)GlobalSize(Handle));
                Assert.True(Length >= minimumBytes, context + ": allocation is too short");
                Pointer = GlobalLock(Handle);
                Assert.True(Pointer != 0, context + ": lock failed");
            }
            catch
            {
                GlobalFree(Handle);
                throw;
            }
        }

        public nint Handle { get; }
        public nint Pointer { get; }
        public int Length { get; }

        public void Reset(byte[] bytes) => Marshal.Copy(bytes, 0, Pointer, bytes.Length);

        public byte[] ReadBytes()
        {
            var bytes = new byte[Length];
            Marshal.Copy(Pointer, bytes, 0, bytes.Length);
            return bytes;
        }

        public void Dispose()
        {
            GlobalUnlock(Handle);
            GlobalFree(Handle);
        }
    }
}
