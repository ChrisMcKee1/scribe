using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.TextInjection;

namespace Scribe.Core.Tests;

public sealed class SnapshotInjectionLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_flag_selects_one_snapshot_per_insertion_and_never_keeps_it_for_the_next(bool enabled)
    {
        var platform = new Platform();
        var injector = new TextInjector(NullLogger<TextInjector>.Instance, platform, new TextInjectionFakes.Clipboard(),
            enabled ? PerfFlags.Parse(PerfFlags.SnapshotInjectionLayout) : PerfFlags.None);

        injector.Inject("a\nb", InjectionMethod.UnicodeType, 0x4242);
        platform.Mapping = 2;
        injector.Inject("a\nb", InjectionMethod.UnicodeType, 0x4242);

        Assert.Equal(enabled ? 2 : 0, platform.Snapshots);
        Assert.Equal(enabled ? 0 : 8, platform.IndividualReads);
        Assert.Equal(1, platform.Inner.Batches[0][2].U.ki.wScan);
        Assert.Equal(2, platform.Inner.Batches[1][2].U.ki.wScan);
        Assert.Equal(0u, platform.Inner.Batches[0][2].U.ki.dwFlags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_standard_edit_or_rejected_foreground_reads_no_layout(bool enabled)
    {
        var platform = new Platform();
        platform.Inner.StandardEdit = true;
        var injector = new TextInjector(NullLogger<TextInjector>.Instance, platform, new TextInjectionFakes.Clipboard(),
            enabled ? PerfFlags.Parse(PerfFlags.SnapshotInjectionLayout) : PerfFlags.None);
        Assert.True(injector.Inject("hello", expectedForegroundWindow: 0x4242).Succeeded);
        platform.Inner.Foreground = 9;
        Assert.False(injector.Inject("hello", expectedForegroundWindow: 0x4242).Succeeded);
        Assert.Equal(0, platform.Snapshots);
        Assert.Equal(0, platform.IndividualReads);
    }

    private sealed class Platform : IInjectionPlatform
    {
        public TextInjectionFakes.Platform Inner { get; } = new();
        public ushort Mapping { get; set; } = 1;
        public int IndividualReads { get; private set; }
        public int Snapshots { get; private set; }
        public nint GetForegroundWindow() => Inner.GetForegroundWindow();
        public uint SendInput(ReadOnlySpan<InjectionNativeMethods.INPUT> inputs) => Inner.SendInput(inputs);
        public bool TryInsertIntoStandardEdit(string text, nint expectedForegroundWindow) => Inner.TryInsertIntoStandardEdit(text, expectedForegroundWindow);
        public void Sleep(int milliseconds) => Inner.Sleep(milliseconds);
        public KeyScanCode ScanCodeOf(ushort virtualKey)
        {
            IndividualReads++;
            return new(Mapping, false);
        }
        public InjectionKeys SnapshotKeys()
        {
            Snapshots++;
            var key = new KeyScanCode(Mapping, false);
            return new(key, key, key, key);
        }
    }
}
