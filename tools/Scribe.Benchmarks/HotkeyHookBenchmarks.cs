using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;

namespace Scribe.Benchmarks;

[MemoryDiagnoser]
public class HotkeyMouseFilterBenchmarks
{
    private const int Operations = 1024;

    [Params(
        MouseHookFilter.WM_MOUSEMOVE,
        MouseHookFilter.WM_MOUSEWHEEL,
        MouseHookFilter.WM_MBUTTONDOWN,
        MouseHookFilter.WM_XBUTTONUP)]
    public int Message { get; set; }

    [Benchmark(Baseline = true, OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Hotkeys")]
    public int CurrentBitMask()
    {
        var count = 0;
        for (var index = 0; index < Operations; index++)
        {
            count += MouseHookFilter.IsButtonMessage(Message) ? 1 : 0;
        }

        return count;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Hotkeys")]
    public int SwitchPattern()
    {
        var count = 0;
        for (var index = 0; index < Operations; index++)
        {
            count += Message is MouseHookFilter.WM_MBUTTONDOWN or MouseHookFilter.WM_MBUTTONUP or
                MouseHookFilter.WM_XBUTTONDOWN or MouseHookFilter.WM_XBUTTONUP ? 1 : 0;
        }

        return count;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Hotkeys")]
    public int TwoRangeChecks()
    {
        var count = 0;
        for (var index = 0; index < Operations; index++)
        {
            count += (Message >= MouseHookFilter.WM_MBUTTONDOWN && Message <= MouseHookFilter.WM_MBUTTONUP) ||
                (Message >= MouseHookFilter.WM_XBUTTONDOWN && Message <= MouseHookFilter.WM_XBUTTONUP) ? 1 : 0;
        }

        return count;
    }
}

[MemoryDiagnoser]
public class HotkeyEngineBenchmarks
{
    private const uint PageDown = 0x22;
    private const uint F13 = 0x7C;

    private HotkeyTransitionQueue _keyTransitions = null!;
    private HotkeyEngine _keyEngine = null!;
    private HotkeyTransitionQueue _mouseTransitions = null!;
    private HotkeyEngine _mouseEngine = null!;
    private HotkeyTransitionQueue _mixedTransitions = null!;
    private HotkeyEngine _mixedEngine = null!;

    [GlobalSetup]
    public void Setup()
    {
        _keyTransitions = new HotkeyTransitionQueue();
        _keyEngine = new HotkeyEngine(HotkeyBinding.DefaultDictation, null, captureMode: false, paused: false, generation: 1, _keyTransitions);

        _mouseTransitions = new HotkeyTransitionQueue();
        _mouseEngine = new HotkeyEngine(
            new HotkeyBinding(MouseButtons.Back, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Mouse Back"),
            null,
            captureMode: false,
            paused: false,
            generation: 1,
            _mouseTransitions,
            buttonDownInWindows: static _ => false);

        _mixedTransitions = new HotkeyTransitionQueue();
        _mixedEngine = new HotkeyEngine(HotkeyBinding.DefaultDictation, null, captureMode: false, paused: false, generation: 1, _mixedTransitions);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Hotkeys")]
    public int KeyboardUnboundDownUp()
    {
        var down = _keyEngine.OnKeyEvent(F13, isDown: true);
        var up = _keyEngine.OnKeyEvent(F13, isDown: false);
        return Score(down) + Score(up);
    }

    [Benchmark]
    [BenchmarkCategory("Hotkeys")]
    public int KeyboardDefaultActivationCycle()
    {
        var down = _keyEngine.OnKeyEvent(PageDown, isDown: true);
        var up = _keyEngine.OnKeyEvent(PageDown, isDown: false);
        return Score(down) + Score(up) + Drain(_keyTransitions);
    }

    [Benchmark]
    [BenchmarkCategory("Hotkeys")]
    public int MouseUnboundButtonClick()
    {
        var down = _mixedEngine.OnMouseButtonEvent(MouseButtons.Back, isDown: true);
        var up = _mixedEngine.OnMouseButtonEvent(MouseButtons.Back, isDown: false);
        return Score(down) + Score(up);
    }

    [Benchmark]
    [BenchmarkCategory("Hotkeys")]
    public int MouseBoundButtonClick()
    {
        var down = _mouseEngine.OnMouseButtonEvent(MouseButtons.Back, isDown: true);
        var up = _mouseEngine.OnMouseButtonEvent(MouseButtons.Back, isDown: false);
        return Score(down) + Score(up) + Drain(_mouseTransitions);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _keyTransitions.Dispose();
        _mouseTransitions.Dispose();
        _mixedTransitions.Dispose();
    }

    private static int Score(HookDecision decision) =>
        (decision.Suppress ? 1 : 0) + (decision.RequestReconcile ? 2 : 0) + (decision.RequestMouseHookSync ? 4 : 0);

    private static int Drain(HotkeyTransitionQueue queue)
    {
        var count = 0;
        foreach (var _ in queue.TakeAll())
        {
            count++;
        }

        return count;
    }
}

[MemoryDiagnoser]
public class HotkeyQueueBenchmarks
{
    private static readonly HotkeyService.QueuedTransition Transition =
        new(HotkeyTransition.Activated, HotkeyTrigger.Standard, Generation: 1, AllowReconcile: true);

    private HotkeyTransitionQueue _currentQueue = null!;
    private LockFreeInbox<HotkeyService.QueuedTransition> _inbox = null!;
    private ConcurrentQueue<HotkeyService.QueuedTransition> _concurrentQueue = null!;
    private Queue<HotkeyService.QueuedTransition> _lockedQueue = null!;
    private object _lockedQueueGate = null!;
    private SingleProducerRing _ring = null!;
    private AutoResetEvent _wake = null!;

    [GlobalSetup]
    public void Setup()
    {
        _currentQueue = new HotkeyTransitionQueue();
        _inbox = new LockFreeInbox<HotkeyService.QueuedTransition>();
        _concurrentQueue = new ConcurrentQueue<HotkeyService.QueuedTransition>();
        _lockedQueue = new Queue<HotkeyService.QueuedTransition>();
        _lockedQueueGate = new object();
        _ring = new SingleProducerRing(capacity: 1024);
        _wake = new AutoResetEvent(false);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Hotkeys")]
    public int CurrentQueueEnqueueSignalDrain()
    {
        _ = _currentQueue.TryEnqueue(Transition);
        var count = 0;
        foreach (var _ in _currentQueue.TakeAll())
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    [BenchmarkCategory("Hotkeys")]
    public int CurrentInboxOnlyDrain()
    {
        _inbox.Push(Transition);
        var count = 0;
        foreach (var _ in _inbox.TakeAll())
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    [BenchmarkCategory("Hotkeys")]
    public int ConcurrentQueueSignalDrain()
    {
        _concurrentQueue.Enqueue(Transition);
        _wake.Set();
        var count = 0;
        while (_concurrentQueue.TryDequeue(out _))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    [BenchmarkCategory("Hotkeys")]
    public int LockedQueueSignalDrain()
    {
        lock (_lockedQueueGate)
        {
            _lockedQueue.Enqueue(Transition);
        }

        _wake.Set();
        var count = 0;
        lock (_lockedQueueGate)
        {
            while (_lockedQueue.Count != 0)
            {
                _ = _lockedQueue.Dequeue();
                count++;
            }
        }

        return count;
    }

    [Benchmark]
    [BenchmarkCategory("Hotkeys")]
    public int SingleProducerRingSignalDrain()
    {
        _ = _ring.TryEnqueue(Transition);
        _wake.Set();
        var count = 0;
        while (_ring.TryDequeue(out _))
        {
            count++;
        }

        return count;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _currentQueue.Dispose();
        _wake.Dispose();
    }

    private sealed class SingleProducerRing
    {
        private readonly HotkeyService.QueuedTransition[] _items;
        private int _head;
        private int _tail;

        public SingleProducerRing(int capacity) => _items = new HotkeyService.QueuedTransition[capacity];

        public bool TryEnqueue(HotkeyService.QueuedTransition item)
        {
            var next = (_tail + 1) & (_items.Length - 1);
            if (next == Volatile.Read(ref _head))
            {
                return false;
            }

            _items[_tail] = item;
            Volatile.Write(ref _tail, next);
            return true;
        }

        public bool TryDequeue(out HotkeyService.QueuedTransition item)
        {
            var head = _head;
            if (head == Volatile.Read(ref _tail))
            {
                item = default;
                return false;
            }

            item = _items[head];
            Volatile.Write(ref _head, (head + 1) & (_items.Length - 1));
            return true;
        }
    }
}

[MemoryDiagnoser]
public class HotkeyWakeAndDispatchBenchmarks
{
    private AutoResetEvent _autoResetEvent = null!;
    private ManualResetEventSlim _manualResetEventSlim = null!;
    private HotkeyService _service = null!;
    private HotkeyService.QueuedTransition _activated;
    private HotkeyService.QueuedTransition _deactivated;
    private int _events;

    [GlobalSetup]
    public void Setup()
    {
        _autoResetEvent = new AutoResetEvent(false);
        _manualResetEventSlim = new ManualResetEventSlim(false);

        var router = new HotkeyCommandRouter(HotkeyBinding.DefaultDictation);
        var transitions = new HotkeyTransitionQueue();
        var engine = router.BeginEngine(transitions).Engine;
        _service = new HotkeyService(NullLogger<HotkeyService>.Instance, router, () => true, scheduleReconcilePass: _ => { });
        _service.Activated += (_, _) => _events++;
        _service.Deactivated += (_, _) => _events++;
        _activated = new HotkeyService.QueuedTransition(
            HotkeyTransition.Activated,
            HotkeyTrigger.Standard,
            router.CurrentGeneration,
            AllowReconcile: false,
            ActivationEpoch: engine.ActivationEpoch,
            Engine: engine,
            Activation: 1,
            KeyViewEpoch: engine.KeyViewEpoch);
        _deactivated = new HotkeyService.QueuedTransition(
            HotkeyTransition.Deactivated,
            HotkeyTrigger.Standard,
            router.CurrentGeneration,
            AllowReconcile: true,
            KeyViewEpoch: engine.KeyViewEpoch);
        transitions.Dispose();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Hotkeys")]
    public bool AutoResetEventSet() => _autoResetEvent.Set();

    [Benchmark]
    [BenchmarkCategory("Hotkeys")]
    public void ManualResetEventSlimSetReset()
    {
        _manualResetEventSlim.Set();
        _manualResetEventSlim.Reset();
    }

    [Benchmark]
    [BenchmarkCategory("Hotkeys")]
    public int DispatchActivated() => Dispatch(_activated);

    [Benchmark]
    [BenchmarkCategory("Hotkeys")]
    public int DispatchDeactivatedWithRepairRequest() => Dispatch(_deactivated);

    [GlobalCleanup]
    public void Cleanup()
    {
        _service.Dispose();
        _autoResetEvent.Dispose();
        _manualResetEventSlim.Dispose();
    }

    private int Dispatch(HotkeyService.QueuedTransition transition)
    {
        _service.DispatchTransition(transition);
        return _events;
    }
}
