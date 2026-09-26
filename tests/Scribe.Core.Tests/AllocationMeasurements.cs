using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using Scribe.Core.Hotkeys;

namespace Scribe.Core.Tests;

/// <summary>
/// Every test that measures <see cref="GC.GetAllocatedBytesForCurrentThread"/> belongs to this collection, which xUnit runs
/// alone, after every parallel collection has finished (stream TR, item 1): no other test runs in the process while one of
/// these measures. Work an earlier test left on the thread pool can still run then, but it cannot add to the measuring
/// thread's count. Their zero assertions are exactly as strict as before, each measured once and asserted as measured; a
/// failure says what the runtime did on the measuring thread (<see cref="AllocationMeasurement"/>).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationMeasurementCollection
{
    public const string Name = "Allocation measurements";
}

/// <summary>
/// What the runtime did on this thread between two readings, read without allocating: the methods the JIT compiled here and
/// the time that took, the collections of each generation, the time the process was paused for them, and the first-chance
/// exceptions thrown on this thread. Read around a measurement, outside its window, it names the runtime's own work when
/// the bytes are not zero (stream TR, item 2).
/// </summary>
internal readonly record struct RuntimeWork(
    long Methods, long JitTicks, int Gen0, int Gen1, int Gen2, long PauseTicks, long Exceptions)
{
    [ThreadStatic]
    private static long t_exceptions;

    // Counted on the throwing thread; subscribed once, when this type is first used, before any window opens.
    static RuntimeWork() => AppDomain.CurrentDomain.FirstChanceException += static (_, _) => t_exceptions++;

    /// <summary>The number of values <see cref="Write"/> puts in an array, for a scenario that returns them across load contexts.</summary>
    public const int Width = 7;

    public static RuntimeWork Now() => new(
        JitInfo.GetCompiledMethodCount(currentThread: true),
        JitInfo.GetCompilationTime(currentThread: true).Ticks,
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2),
        GC.GetTotalPauseDuration().Ticks,
        t_exceptions);

    public RuntimeWork Since(RuntimeWork earlier) => new(
        Methods - earlier.Methods,
        JitTicks - earlier.JitTicks,
        Gen0 - earlier.Gen0,
        Gen1 - earlier.Gen1,
        Gen2 - earlier.Gen2,
        PauseTicks - earlier.PauseTicks,
        Exceptions - earlier.Exceptions);

    /// <summary>Into <paramref name="values"/> at <paramref name="offset"/>, as plain numbers, which any load context can read.</summary>
    public void Write(long[] values, int offset)
    {
        values[offset] = Methods;
        values[offset + 1] = JitTicks;
        values[offset + 2] = Gen0;
        values[offset + 3] = Gen1;
        values[offset + 4] = Gen2;
        values[offset + 5] = PauseTicks;
        values[offset + 6] = Exceptions;
    }

    public static RuntimeWork Read(long[] values, int offset) => new(
        values[offset],
        values[offset + 1],
        (int)values[offset + 2],
        (int)values[offset + 3],
        (int)values[offset + 4],
        values[offset + 5],
        values[offset + 6]);

    public override string ToString() =>
        $"{Methods} method(s) jitted on this thread ({TimeSpan.FromTicks(JitTicks).TotalMilliseconds:0.###} ms), " +
        $"collections gen0 {Gen0}, gen1 {Gen1}, gen2 {Gen2}, GC pause {TimeSpan.FromTicks(PauseTicks).TotalMilliseconds:0.###} ms, " +
        $"{Exceptions} first-chance exception(s) on this thread";
}

/// <summary>
/// The failure path of a zero-allocation measurement (stream TR, item 2). A passing measurement never reaches it. A failing
/// one reruns the measured region under a <see cref="RuntimeEventCapture"/> and fails with what the runtime did on the
/// measuring thread, in the measurement itself (<see cref="RuntimeWork"/>, read around it) and in the rerun (its events).
/// </summary>
internal static class AllocationMeasurement
{
    /// <summary>Passes when <paramref name="bytes"/> is 0, exactly as <c>Assert.Equal(0, bytes)</c> did; otherwise fails naming the cause.</summary>
    /// <param name="rerun">The measured region, run again (and measured again) under the capture on a failure.</param>
    /// <param name="prepare">Run before the rerun and outside its window, to put the state back the region needs.</param>
    public static void AssertZero(long bytes, RuntimeWork during, string what, Action rerun, Action? prepare = null)
    {
        if (bytes == 0)
        {
            return;
        }

        Assert.Fail(Describe(what, bytes, during, rerun, prepare));
    }

    /// <summary>The message for a failed measurement: the measurement, then a rerun of the region under the capture.</summary>
    public static string Describe(string what, long bytes, RuntimeWork during, Action rerun, Action? prepare = null)
    {
        var text = new StringBuilder()
            .Append(what).Append(": ").Append(bytes).Append(" bytes allocated on this thread, expected 0. During it: ")
            .Append(during).Append('.');
        try
        {
            prepare?.Invoke();
            using var capture = RuntimeEventCapture.Start();
            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            rerun();
            var again = GC.GetAllocatedBytesForCurrentThread() - before;
            var rerunWork = RuntimeWork.Now().Since(work);
            text.Append(" Rerun under a runtime event listener: ").Append(again).Append(" bytes; ").Append(rerunWork)
                .Append(". ").Append(RuntimeEventCapture.Describe(capture.Stop().Select(e => e.Line)));
        }
        catch (Exception rerunFailure)
        {
            text.Append(" The rerun could not be made: ").Append(rerunFailure.GetType().Name).Append(": ").Append(rerunFailure.Message);
        }

        return text.ToString();
    }
}

/// <summary>
/// Window boundaries a cold-path scenario marks in the runtime's own event stream: each mark is a method called once, so
/// its first call makes the JIT compile it there, and a capture sees "method jitted WindowMarks::Open3" (or "Close3") in
/// order with everything else the thread raised (a thread's events arrive in order), which splits the events into windows
/// exactly where the scenario put them. Called just outside each window; compiling a method allocates nothing on the
/// managed heap.
/// </summary>
public static class WindowMarks
{
    /// <summary>Just before window <paramref name="index"/> (0 to 7) opens.</summary>
    public static void Open(int index)
    {
        switch (index)
        {
            case 0: Open0(); break;
            case 1: Open1(); break;
            case 2: Open2(); break;
            case 3: Open3(); break;
            case 4: Open4(); break;
            case 5: Open5(); break;
            case 6: Open6(); break;
            default: Open7(); break;
        }
    }

    /// <summary>Just after window <paramref name="index"/> (0 to 7) closes.</summary>
    public static void Close(int index)
    {
        switch (index)
        {
            case 0: Close0(); break;
            case 1: Close1(); break;
            case 2: Close2(); break;
            case 3: Close3(); break;
            case 4: Close4(); break;
            case 5: Close5(); break;
            case 6: Close6(); break;
            default: Close7(); break;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)] private static void Open0() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Open1() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Open2() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Open3() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Open4() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Open5() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Open6() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Open7() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Close0() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Close1() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Close2() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Close3() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Close4() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Close5() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Close6() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Close7() { }
}
/// <summary>
/// A cold-path scenario, a public static <c>Run()</c> returning <c>[measured, work]</c> (the scenario's values, and
/// <see cref="RuntimeWork"/> per window), run in a load context of its own with fresh copies of this assembly and
/// Scribe.Core, so their statics, type initializers and first calls are cold whatever the process ran before; the
/// framework is shared with the rest of the process (stream TR, item 1). The scenario calls <see cref="WindowMarks.Open"/>
/// just before each window and <see cref="WindowMarks.Close"/> just after it.
/// </summary>
internal static class ColdPathMeasurement
{
    /// <summary>
    /// The scenario, run once in the default load context and then, measured, in a fresh load context named
    /// <paramref name="context"/>. The default-context run is not measured: it takes the process-wide first uses of framework
    /// types that the scenario's path makes (measured in stream TR: run first in a process, the keyboard route's window
    /// loaded <c>EqualityComparer&lt;RegisteredWaitHandle&gt;</c> and three more framework generics over framework types,
    /// and after this run none), which otherwise land in whichever test runs first. It cannot warm Scribe's own first calls
    /// in the fresh copy: its types, statics, type initializers and the framework generics over its types are the fresh
    /// context's own, so they stay cold and measured. Both assemblies the measured run used, the tests and Scribe.Core, are
    /// asserted to be the fresh context's own copies.
    /// </summary>
    public static (long[][] Result, AssemblyLoadContext Context) RunFresh(string context, Type scenario)
    {
        _ = scenario.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);

        var folder = Path.GetDirectoryName(typeof(ColdPathMeasurement).Assembly.Location)!;
        var fresh = new FreshCopies(context, folder);
        var tests = fresh.LoadFromAssemblyName(typeof(ColdPathMeasurement).Assembly.GetName());
        Assert.NotSame(typeof(ColdPathMeasurement).Assembly, tests);
        var type = tests.GetType(scenario.FullName!, throwOnError: true)!;
        var result = (long[][])type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;

        // The scenario ran against this context's own Scribe.Core, not the one the rest of the run uses.
        var core = fresh.Assemblies.SingleOrDefault(assembly => assembly.GetName().Name == "Scribe.Core");
        Assert.True(
            core is not null && !ReferenceEquals(core, typeof(HotkeyService).Assembly),
            "The scenario ran against the default context's Scribe.Core, not a fresh copy.");
        return (result, fresh);
    }

    /// <summary>
    /// Passes when the scenario's values are <paramref name="expected"/>, exactly as <c>Assert.Equal(expected, values)</c>
    /// did. Otherwise fails with each window's bytes and runtime work, and a second fresh run under a
    /// <see cref="RuntimeEventCapture"/>, its events placed in the windows they fell in (stream TR, item 2).
    /// </summary>
    public static void AssertAsExpected(long[] expected, long[][] result, IReadOnlyList<string> windows, string context, Type scenario)
    {
        if (result[0].SequenceEqual(expected))
        {
            return;
        }

        var text = new StringBuilder()
            .Append("Expected [").AppendJoin(", ", expected).Append("], measured [").AppendJoin(", ", result[0]).Append("].");
        AppendWindows(text, result, windows, capture: null, events: null);
        try
        {
            IReadOnlyList<(DateTime At, string Line)> events;
            long[][] again;
            using (var capture = RuntimeEventCapture.Start())
            {
                again = RunFresh(context + "-rerun", scenario).Result;
                events = capture.Stop();
                text.Append(" Rerun in another fresh load context under a runtime event listener: [").AppendJoin(", ", again[0]).Append("].");
                AppendWindows(text, again, windows, capture, events);
            }
        }
        catch (Exception rerunFailure)
        {
            text.Append(" The rerun could not be made: ").Append(rerunFailure.GetType().Name).Append(": ").Append(rerunFailure.Message);
        }

        Assert.Fail(text.ToString());
    }

    private static void AppendWindows(
        StringBuilder text, long[][] result, IReadOnlyList<string> windows, RuntimeEventCapture? capture,
        IReadOnlyList<(DateTime At, string Line)>? events)
    {
        var split = events is null ? null : SplitAtMarks(events, windows.Count);
        for (var i = 0; i < windows.Count; i++)
        {
            text.Append(' ').Append(windows[i]).Append(": ").Append(result[0][i]).Append(" bytes; ")
                .Append(RuntimeWork.Read(result[1], i * RuntimeWork.Width)).Append('.');
            if (capture is not null && split is not null)
            {
                text.Append(' ').Append(RuntimeEventCapture.Describe(split[i]));
            }
        }
    }

    // The events of each window: those after the JIT of WindowMarks.Open{i} and before that of Close{i} (WindowMarks).
    private static List<List<string>> SplitAtMarks(IReadOnlyList<(DateTime At, string Line)> events, int windows)
    {
        var split = Enumerable.Range(0, windows).Select(_ => new List<string>()).ToList();
        var current = -1;
        foreach (var (_, line) in events)
        {
            if (Mark(line, "WindowMarks::Open") is { } opened)
            {
                current = opened;
            }
            else if (Mark(line, "WindowMarks::Close") is not null)
            {
                current = -1;
            }
            else if (current >= 0 && current < windows && !line.Contains("WindowMarks::", StringComparison.Ordinal))
            {
                split[current].Add(line);
            }
        }

        return split;

        static int? Mark(string line, string name)
        {
            var at = line.IndexOf(name, StringComparison.Ordinal);
            return at >= 0 && int.TryParse(line.AsSpan(at + name.Length), out var index) ? index : null;
        }
    }

    private sealed class FreshCopies(string name, string folder) : AssemblyLoadContext(name)
    {
        protected override Assembly? Load(AssemblyName assembly) =>
            assembly.Name is "Scribe.Core" or "Scribe.Core.Tests"
                ? LoadFromAssemblyPath(Path.Combine(folder, assembly.Name + ".dll"))
                : null;
    }
}

/// <summary>
/// The runtime's own events for the thread that starts it, while it is open, through an in-process listener on
/// Microsoft-Windows-DotNETRuntime: the types loaded and the methods the JIT compiled on that thread, the exceptions thrown
/// there, the collections it took part in, and the allocations the runtime sampled there. The sampling picks about one
/// allocation per 100 KB, so a small allocation is named only by chance, and the runtime raises no per-object allocation
/// events for a listener that starts after the process (measured in stream TR); what it names is the runtime work around an
/// allocation. Used only by the failure path of a measurement: never around a passing one.
/// </summary>
internal sealed class RuntimeEventCapture : EventListener
{
    // GC, Loader, Jit, Exception, TypeDiagnostic, AllocationSampling.
    private const long Keywords = 0x1 | 0x8 | 0x10 | 0x8000 | 0x8000000000 | 0x80000000000;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    // The listener's base constructor calls OnEventSourceCreated before a constructor body runs, so what the callbacks read
    // is initialized here, and the thread is read from a static the starting thread sets under the lock below.
    private static readonly object s_starting = new();
    private static int s_thread;

    private readonly int _thread = Volatile.Read(ref s_thread);
    private readonly ConcurrentQueue<(DateTime At, string Line)> _events = new();
    private readonly ManualResetEventSlim _enabled = new(false);
    private readonly ManualResetEventSlim _marked = new(false);

    // 0 while capturing; 1 once Stop asks for the end marker; 2 once the marker's collection has started on this thread.
    private int _state;

    private RuntimeEventCapture()
    {
    }

    /// <summary>Starts capturing for this thread, and returns once the runtime's source is enabled (or after a bound).</summary>
    public static RuntimeEventCapture Start()
    {
        lock (s_starting)
        {
            Volatile.Write(ref s_thread, (int)NativeMethods.GetCurrentThreadId());
            var capture = new RuntimeEventCapture();
            capture._enabled.Wait(Bound);
            return capture;
        }
    }

    /// <summary>
    /// Ends the capture: a collection made on this thread marks the end, and everything this thread raised before it has
    /// been delivered once the collection's end event arrives (a thread's events arrive in order). Returns what was captured.
    /// </summary>
    public IReadOnlyList<(DateTime At, string Line)> Stop()
    {
        // An induced collection of generation 0 on this thread: the measured regions make none of their own.
        Volatile.Write(ref _state, 1);
        GC.Collect(0, GCCollectionMode.Forced, blocking: true);
        var complete = _marked.Wait(Bound);
        Dispose();
        var events = _events.ToList();
        if (!complete)
        {
            events.Add((DateTime.UtcNow, "(the end marker never arrived: the events may be incomplete)"));
        }

        return events;
    }

    /// <summary>Captured event lines as one sentence, repeated lines counted.</summary>
    public static string Describe(IEnumerable<string> lines)
    {
        var chosen = lines.ToList();
        if (chosen.Count == 0)
        {
            return "Runtime events on this thread: none.";
        }

        var shown = chosen.GroupBy(line => line).Select(g => g.Count() == 1 ? g.Key : $"{g.Key} (x{g.Count()})").Take(60).ToList();
        return $"Runtime events on this thread ({chosen.Count}): " + string.Join("; ", shown) +
            (shown.Count < chosen.GroupBy(line => line).Count() ? "; ..." : "") + ".";
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)Keywords);
            _enabled.Set();
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.OSThreadId != _thread || e.EventName is null)
        {
            return;
        }

        var state = Volatile.Read(ref _state);
        if (state >= 1 && e.EventName == "GCStart_V2" && Value(e, "Reason") is { } reason &&
            Convert.ToInt64(reason, System.Globalization.CultureInfo.InvariantCulture) == 1)
        {
            Volatile.Write(ref _state, 2); // the end marker: nothing after it is the region's
            return;
        }

        if (state == 2)
        {
            if (e.EventName.StartsWith("GCEnd", StringComparison.Ordinal))
            {
                _marked.Set();
            }

            return;
        }

        if (Summarize(e) is { } line)
        {
            _events.Enqueue((e.TimeStamp, line));
        }
    }

    // One line per event that says what the runtime did, or null for the ones that say nothing about it.
    private static string? Summarize(EventWrittenEventArgs e) => e.EventName switch
    {
        "TypeLoadStop" => $"type loaded {Value(e, "TypeName")}",
        "MethodLoadVerbose_V1" or "MethodLoadVerbose_V2" =>
            $"method jitted {Value(e, "MethodNamespace")}::{Value(e, "MethodName")}",
        "ExceptionThrown_V1" => $"exception thrown {Value(e, "ExceptionType")}: {Value(e, "ExceptionMessage")}",
        "GCStart_V2" => $"collection of gen{Value(e, "Depth")} (reason {Value(e, "Reason")})",
        "AllocationSampled" => $"allocation sampled {Value(e, "TypeName")} ({Value(e, "ObjectSize")} bytes)",
        "GCAllocationTick_V4" => $"allocation tick {Value(e, "TypeName")} ({Value(e, "AllocationAmount")} bytes)",
        "ModuleLoad_V2" => $"module loaded {Value(e, "ModuleILPath")}",
        _ => null,
    };

    private static object? Value(EventWrittenEventArgs e, string name)
    {
        var index = e.PayloadNames?.IndexOf(name) ?? -1;
        return index < 0 || e.Payload is null ? null : e.Payload[index];
    }
}
