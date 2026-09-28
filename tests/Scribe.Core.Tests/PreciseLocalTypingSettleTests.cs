using System.ComponentModel;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.TextInjection;

namespace Scribe.Core.Tests;

public sealed class PreciseLocalTypingSettleTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Only_local_inter_batch_waits_use_the_private_timer(bool enabled, bool remote)
    {
        var platform = new Platform();
        var result = Inject(platform, enabled, remote ? "msrdc" : "local");

        Assert.True(result.Succeeded);
        Assert.Equal(enabled && !remote ? 1 : 0, platform.Creates);
        Assert.Equal(enabled && !remote ? 2 : 0, platform.Waiter.Waits.Count);
        Assert.All(platform.Inner.Sleeps, ms => Assert.Equal(remote ? 20 : 5, ms));
        Assert.Equal(enabled && !remote, platform.Waiter.Disposed);
    }

    [Fact]
    public void Retry_sleeps_stay_on_the_old_path()
    {
        var platform = new Platform();
        platform.Inner.Deliver = (attempt, inputs) => attempt == 0 ? 2u : (uint)inputs.Length;
        Assert.True(Inject(platform, true).Succeeded);
        Assert.Equal([12], platform.Inner.Sleeps);
        Assert.Equal([5, 5], platform.Waiter.Waits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_or_wait_failure_falls_back_for_the_whole_insertion(bool creationFails)
    {
        var platform = new Platform { CreateFails = creationFails };
        platform.Waiter.Succeeds = false;
        Assert.True(Inject(platform, true).Succeeded);
        Assert.Equal(1, platform.Creates);
        Assert.Equal([5, 5], platform.Inner.Sleeps);
        Assert.Equal(!creationFails, platform.Waiter.Disposed);
    }

    [Fact]
    public void A_single_batch_and_clipboard_paste_create_no_timer()
    {
        var platform = new Platform();
        var clipboard = new TextInjectionFakes.Clipboard();
        var injector = new TextInjector(NullLogger<TextInjector>.Instance, platform, clipboard,
            PerfFlags.Parse(PerfFlags.PreciseLocalTypingSettle));
        Assert.True(injector.Inject("short", InjectionMethod.UnicodeType).Succeeded);
        Assert.True(injector.Inject("paste", InjectionMethod.ClipboardPaste).Succeeded);
        Assert.Equal(0, platform.Creates);
        Assert.Equal([30, 130], platform.Inner.Sleeps);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_unexpected_nonfatal_timer_exception_falls_back_for_the_rest_of_the_insertion(bool duringCreation)
    {
        var platform = new Platform
        {
            CreationException = duringCreation ? new InvalidOperationException("PRIVATE-CREATE") : null,
        };
        platform.Waiter.WaitException = duringCreation ? null : new InvalidOperationException("PRIVATE-WAIT");
        var log = new TextInjectionFakes.CapturingLogger<TextInjector>();
        var injector = new TextInjector(log, platform, new TextInjectionFakes.Clipboard(),
            PerfFlags.Parse(PerfFlags.PreciseLocalTypingSettle));

        Assert.True(injector.Inject(new string('x', 130), InjectionMethod.UnicodeType).Succeeded);
        Assert.Equal(1, platform.Creates);
        Assert.Equal([5, 5], platform.Inner.Sleeps);
        Assert.Equal(!duringCreation, platform.Waiter.Disposed);
        Assert.Contains(log.Entries, line => line.Contains("Precise typing settle unavailable", StringComparison.Ordinal));
        Assert.All(log.Entries, line => Assert.DoesNotContain("PRIVATE-", line));
    }

    [Fact]
    public void A_fatal_timer_exception_is_not_replaced_with_a_successful_fallback()
    {
        var platform = new Platform { CreationException = new OutOfMemoryException() };

        var failure = Assert.Throws<InvalidOperationException>(() => Inject(platform, true));

        Assert.IsType<OutOfMemoryException>(failure.InnerException);
        Assert.Empty(platform.Inner.Sleeps);
    }

    [Fact]
    public void A_timer_is_disposed_when_a_later_native_send_throws()
    {
        var platform = new Platform();
        platform.Inner.Deliver = (attempt, inputs) => attempt == 1
            ? throw new InvalidOperationException()
            : (uint)inputs.Length;

        Assert.Throws<InvalidOperationException>(() => Inject(platform, true));
        Assert.True(platform.Waiter.Disposed);
    }

    [Fact]
    public void A_timer_disposal_failure_does_not_turn_delivered_text_into_a_failure()
    {
        var platform = new Platform();
        platform.Waiter.DisposeFails = true;
        var log = new TextInjectionFakes.CapturingLogger<TextInjector>();
        var injector = new TextInjector(log, platform, new TextInjectionFakes.Clipboard(),
            PerfFlags.Parse(PerfFlags.PreciseLocalTypingSettle));

        Assert.True(injector.Inject(new string('x', 130), InjectionMethod.UnicodeType).Succeeded);
        Assert.Contains(log.Entries, line => line.Contains("Releasing the precise typing timer failed", StringComparison.Ordinal));
    }

    private static InjectionResult Inject(Platform platform, bool enabled, string target = "local") =>
        new TextInjector(NullLogger<TextInjector>.Instance, platform, new TextInjectionFakes.Clipboard(),
            enabled ? PerfFlags.Parse(PerfFlags.PreciseLocalTypingSettle) : PerfFlags.None)
            .Inject(new string('x', 130), InjectionMethod.UnicodeType, targetProcessName: target);

    private sealed class Platform : IInjectionPlatform
    {
        public TextInjectionFakes.Platform Inner { get; } = new();
        public Waiter Waiter { get; } = new();
        public bool CreateFails { get; init; }
        public Exception? CreationException { get; init; }
        public int Creates { get; private set; }
        public nint GetForegroundWindow() => Inner.GetForegroundWindow();
        public uint SendInput(ReadOnlySpan<InjectionNativeMethods.INPUT> inputs) => Inner.SendInput(inputs);
        public bool TryInsertIntoStandardEdit(string text, nint expectedForegroundWindow) => false;
        public void Sleep(int milliseconds) => Inner.Sleep(milliseconds);
        public KeyScanCode ScanCodeOf(ushort virtualKey) => Inner.ScanCodeOf(virtualKey);
        public IPreciseTypingWait CreatePreciseWait()
        {
            Creates++;
            if (CreationException is { } exception)
            {
                throw exception;
            }
            return CreateFails ? throw new Win32Exception(5) : Waiter;
        }
    }

    private sealed class Waiter : IPreciseTypingWait
    {
        public bool Succeeds { get; set; } = true;
        public bool Disposed { get; private set; }
        public bool DisposeFails { get; set; }
        public Exception? WaitException { get; set; }
        public List<int> Waits { get; } = [];
        public bool TryWait(int milliseconds)
        {
            Waits.Add(milliseconds);
            if (WaitException is { } exception)
            {
                throw exception;
            }
            return Succeeds;
        }
        public void Dispose()
        {
            Disposed = true;
            if (DisposeFails)
            {
                throw new InvalidOperationException("PRIVATE-TIMER");
            }
        }
    }
}
