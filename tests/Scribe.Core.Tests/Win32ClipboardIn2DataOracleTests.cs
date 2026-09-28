using System.Runtime.InteropServices;
using Scribe.Core.TextInjection;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.Tests;

/// <summary>
/// The seeded IN2 gate for the clipboard receipt's byte path (Win32Clipboard.AllocData and TryReadBlock, which IN2 moved
/// from byte-array copies to spans over locked memory), beside <see cref="Win32ClipboardIn2OracleTests"/> for the text
/// path: release 0.5.0's statements, frozen below from Win32Clipboard.cs at 10c9a0b (SetData and TryReadData), decide the
/// allocation size, the bytes written, and every read's status and bytes.
/// </summary>
/// <remarks>
/// Only private GlobalAlloc/GlobalLock blocks are used: this never opens, empties, reads or publishes to the real
/// clipboard, creates no window, installs no hook and sends no input, so it runs in a locked or remote session. Every byte
/// compared was written by the test; allocator residue past a payload is never compared.
/// </remarks>
public sealed class Win32ClipboardIn2DataOracleTests
{
    private static readonly int[] Edges =
    [
        0, 1, 2, 3, 15, 16, 17, 31, 32, 33, 255, 256, 257, 4_095, 4_096, 4_097, 84_999, 85_000, 85_001, 200_000,
    ];

    [Theory]
    [InlineData(0x051C1102)]
    [InlineData(0x07123456)]
    public void IN2_seeded_receipt_writes_match_the_frozen_050_writer(int seed)
    {
        var random = new Random(seed);
        var caseNumber = 0;
        foreach (var length in Edges.Concat(Enumerable.Range(0, 128).Select(_ => random.Next(4_097))))
        {
            var context = $"IN2 data seed=0x{seed:X8} case={caseNumber++} write bytes={length}";
            var data = new byte[length];
            random.NextBytes(data);

            var shipped = Win32Clipboard.AllocData(data);
            Assert.True(shipped != 0, context + ": the shipping allocation failed");
            var frozen = FrozenAllocate(length);
            Assert.True(frozen != 0, context + ": the frozen allocation failed");
            try
            {
                // Both ask GlobalAlloc for the same size, so both blocks are the same physical size.
                Assert.True(GlobalSize(shipped) == GlobalSize(frozen), context + ": allocation sizes differ");
                FrozenWrite(frozen, data);

                Assert.True(Payload(shipped, length).AsSpan().SequenceEqual(data), context + ": shipping bytes differ");
                Assert.True(Payload(frozen, length).AsSpan().SequenceEqual(data), context + ": frozen bytes differ");
            }
            finally
            {
                GlobalFree(shipped);
                GlobalFree(frozen);
            }
        }
    }

    [Theory]
    [InlineData(0x051C1102)]
    [InlineData(0x07123456)]
    public void IN2_seeded_receipt_reads_match_the_frozen_050_reader(int seed)
    {
        var random = new Random(seed);
        var caseNumber = 0;
        foreach (var requested in Edges.Where(e => e > 0).Concat(Enumerable.Range(0, 64).Select(_ => random.Next(1, 4_097))))
        {
            var handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)requested);
            Assert.True(handle != 0, $"IN2 data seed=0x{seed:X8}: allocation failed");
            try
            {
                var physical = checked((int)GlobalSize(handle));
                var contents = new byte[physical];
                random.NextBytes(contents);
                Fill(handle, contents);

                foreach (var wanted in new[] { 0, 1, 16, physical - 1, physical, physical + 1, random.Next(physical + 9) }.Distinct())
                {
                    if (wanted < 0)
                    {
                        continue;
                    }

                    var context = $"IN2 data seed=0x{seed:X8} case={caseNumber++} block={physical} read={wanted}";
                    var expected = Enumerable.Repeat((byte)0x5A, wanted).ToArray();
                    var actual = Enumerable.Repeat((byte)0x5A, wanted).ToArray();

                    var expectedStatus = FrozenRead(handle, expected);
                    var actualStatus = Win32Clipboard.TryReadBlock(handle, actual);

                    Assert.True(expectedStatus == actualStatus, context + ": read status differs from the 0.5.0 reader");
                    Assert.True(actual.AsSpan().SequenceEqual(expected), context + ": destination differs from the 0.5.0 reader");
                    Assert.True(Payload(handle, physical).AsSpan().SequenceEqual(contents), context + ": a reader changed the block");
                }
            }
            finally
            {
                GlobalFree(handle);
            }
        }
    }

    // Release 0.5.0's SetData allocation (10c9a0b), verbatim.
    private static nint FrozenAllocate(int length) => GlobalAlloc(GMEM_MOVEABLE, (nuint)Math.Max(length, 1));

    // Release 0.5.0's SetData copy under the lock (10c9a0b), verbatim inside the lock.
    private static void FrozenWrite(nint global, ReadOnlySpan<byte> data)
    {
        nint target = GlobalLock(global);
        Assert.True(target != 0, "lock failed");
        try
        {
            if (data.Length > 0)
            {
                Marshal.Copy(data.ToArray(), 0, target, data.Length);
            }
        }
        finally
        {
            GlobalUnlock(global);
        }
    }

    // Release 0.5.0's TryReadData after GetClipboardData (10c9a0b), verbatim.
    private static bool FrozenRead(nint handle, Span<byte> destination)
    {
        if (handle == 0 || GlobalSize(handle) < (nuint)destination.Length)
        {
            return false;
        }

        nint pointer = GlobalLock(handle);
        if (pointer == 0)
        {
            return false;
        }

        try
        {
            var buffer = new byte[destination.Length];
            Marshal.Copy(pointer, buffer, 0, buffer.Length);
            buffer.CopyTo(destination);
            return true;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static byte[] Payload(nint handle, int length)
    {
        var bytes = new byte[length];
        nint pointer = GlobalLock(handle);
        Assert.True(pointer != 0, "lock failed");
        try
        {
            Marshal.Copy(pointer, bytes, 0, length);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return bytes;
    }

    private static void Fill(nint handle, byte[] contents)
    {
        nint pointer = GlobalLock(handle);
        Assert.True(pointer != 0, "lock failed");
        try
        {
            Marshal.Copy(contents, 0, pointer, contents.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }
}
